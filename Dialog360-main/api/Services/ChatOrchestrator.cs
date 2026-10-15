using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Diyalog360.Api.Services;

public record ReportDto(
    string Sql, string Explanation, IReadOnlyList<string> Assumptions, IReadOnlyList<QueryColumn> Columns,
    List<object?[]> Rows, bool Truncated, ChartSpec Chart, IReadOnlyList<string> Tables, IReadOnlyList<string> UsedColumns,
    IReadOnlyList<string> Plan, IReadOnlyList<string> PlanWarnings, long DbMs)
{
    /// <summary>Tıklanabilir kolonlar ve gidilebilecek seviyeler (drill down/up). Eski kayıtlarda boştur.</summary>
    public IReadOnlyList<DrillColumn>? Drill { get; init; }
}

public record ClarifyDto(string Question, IReadOnlyList<string> Options);

/// <param name="Layer">İsteği hangi güvenlik katmanının durdurduğu.</param>
public record BlockedDto(string Reason, string Layer, string? Sql);

public record CorrectionDto(string Sql, string Error);

public record MetaDto(int Attempts, bool FromCache, long LlmMs, long TotalMs, IReadOnlyList<CorrectionDto> Corrections, string Model);

/// <param name="Kind">report | clarify | blocked | error</param>
public record ChatResponse(
    string ChatId, long MessageId, string Kind, string Title, string Text,
    ReportDto? Report, ClarifyDto? Clarify, BlockedDto? Blocked, MetaDto Meta)
{
    /// <summary>Detay yolu: kök rapor › tıklanan değerler. Yalnızca drill ile üretilen raporlarda dolu.</summary>
    public IReadOnlyList<Crumb>? Crumbs { get; init; }

    /// <summary>Drill raporunun nasıl üretildiği: rewrite (LLM'siz) | llm | back (kayıtlı rapora dönüş).</summary>
    public string? DrillSource { get; init; }
}

/// <summary>
/// Bir mesajı uçtan uca işler: önbellek → LLM (yapılandırılmış JSON) → SqlGuard → çalıştırma →
/// hata varsa hata mesajıyla LLM'e geri dönüp en fazla 2 kez düzeltme → grafik + özet.
/// </summary>
public sealed class ChatOrchestrator(PromptBuilder prompt, LlmClient llm, QueryRunner runner, AppStore store, SchemaCatalog catalog, ILogger<ChatOrchestrator> log)
{
    const int MaxAttempts = 3;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <param name="baseSql">Drill'de LLM'e düşüldüğünde temel alınacak rapor SQL'i (sohbetteki son SQL olmayabilir).</param>
    /// <param name="storedMessage">Geçmişe kullanıcı mesajı olarak yazılacak metin (verilmezse message).</param>
    /// <param name="decorate">Cevap kaydedilmeden önce uygulanır (örn. detay yolu eklemek için).</param>
    public async Task<ChatResponse> HandleAsync(string? chatId, string message, CancellationToken ct, bool useCache = true,
        string? baseSql = null, string? storedMessage = null, Func<ChatResponse, ChatResponse>? decorate = null)
    {
        var total = Stopwatch.StartNew();
        chatId = store.EnsureChat(chatId);
        var history = store.GetMessages(chatId);
        store.AddMessage(chatId, "user", storedMessage ?? message);

        var (historyText, anchor) = PromptBuilder.History(history);
        if (baseSql != null)
        {
            historyText = (historyText + $"\nKullanıcı şu rapordaki bir değere tıkladı; bu SQL'i temel al. SQL: {baseSql}").Trim();
            anchor = baseSql;
        }
        var cacheKey = CacheKey(message, anchor, prompt.Version, llm.Model);
        var messages = new List<ChatMessage>
        {
            new("system", prompt.SystemPrompt),
            new("user", PromptBuilder.UserMessage(historyText, message)),
        };

        var corrections = new List<CorrectionDto>();
        long llmMs = 0;
        bool fromCache = false;
        bool clarifyChallenged = false;
        bool retChallenged = false;
        LlmAnswer answer;
        string raw;

        // KVKK: koruma açıksa (Privacy:ProtectPersonalData) kişisel veri isteyen sorular LLM'e hiç gitmeden durdurulur
        if (catalog.ProtectPersonalData && PiiFilter.IsPiiRequest(message))
            return Finish(Response("blocked", "İstek engellendi", PiiFilter.Reason,
                blocked: new BlockedDto(PiiFilter.Reason, "Kişisel veri filtresi (KVKK)", null)), null, 0, 0);

        try
        {
            var cached = useCache ? store.CacheGet(cacheKey) : null;
            if (cached != null)
            {
                answer = JsonSerializer.Deserialize<LlmAnswer>(cached)!;
                raw = cached;
                fromCache = true;
            }
            else
            {
                (answer, raw, var ms) = await llm.AskAsync(messages, ct);
                llmMs += ms;
            }

            for (int attempt = 1; ; attempt++)
            {
                // Gereksiz netleştirme: soruda belirsiz bir sıfat yoksa modelden bir kez rapor üretmesini iste
                if (answer.Tur == "netlestirme" && !clarifyChallenged && !ClarifyGuard.HasAmbiguousTerm(message))
                {
                    clarifyChallenged = true;
                    log.LogInformation("Gereksiz netleştirme modele geri gönderildi: {Question}", answer.NetlestirmeSorusu);
                    if (fromCache) { store.CacheRemove(cacheKey); fromCache = false; }
                    messages.Add(new ChatMessage("assistant", raw));
                    messages.Add(new ChatMessage("user", ClarifyGuard.Challenge));
                    (answer, raw, var clarifyMs) = await llm.AskAsync(messages, ct);
                    llmMs += clarifyMs;
                }

                // Gereksiz ret: veri değiştiren bir fiil içermeyen, bu veritabanıyla ilgili bir soruyu modelden bir kez rapora çevirmesini iste
                if (answer.Tur == "ret" && !retChallenged && RetGuard.ShouldChallenge(message))
                {
                    retChallenged = true;
                    log.LogInformation("Gereksiz ret modele geri gönderildi: {Reason}", answer.Aciklama);
                    if (fromCache) { store.CacheRemove(cacheKey); fromCache = false; }
                    messages.Add(new ChatMessage("assistant", raw));
                    messages.Add(new ChatMessage("user", RetGuard.Challenge));
                    (answer, raw, var retMs) = await llm.AskAsync(messages, ct);
                    llmMs += retMs;
                }

                switch (answer.Tur)
                {
                    case "netlestirme" when answer.Secenekler.Count > 0 || answer.NetlestirmeSorusu != "":
                        store.CachePut(cacheKey, message, raw);
                        return Finish(Response("clarify", "Netleştirme", answer.NetlestirmeSorusu,
                            clarify: new ClarifyDto(answer.NetlestirmeSorusu, answer.Secenekler)), null, 0, 0);

                    case "ret":
                        store.CachePut(cacheKey, message, raw);
                        return Finish(Response("blocked", "İstek engellendi",
                            answer.Aciklama != "" ? answer.Aciklama : "Bu istek Diyalog 360'ın kapsamı dışında.",
                            blocked: new BlockedDto(answer.Aciklama, "Model niyet kontrolü", null)), null, 0, 0);
                }

                if (!string.IsNullOrWhiteSpace(answer.Sql))
                {
                    var (fixedSql, fixNotes) = SqlAutoFix.Apply(answer.Sql);
                    if (fixNotes.Count > 0)
                        answer = answer with { Sql = fixedSql, Varsayimlar = [.. answer.Varsayimlar, .. fixNotes] };
                }

                var outcome = string.IsNullOrWhiteSpace(answer.Sql)
                    ? new QueryOutcome(GuardStatus.Invalid, "SQL alanı boş; tur 'rapor' ise tek bir SELECT yazmalısın.", null)
                    : SqlLint.Check(answer.Sql, message) is { } lintError
                        ? new QueryOutcome(GuardStatus.Invalid, lintError, null)
                        : runner.Run(answer.Sql);

                if (outcome.Status == GuardStatus.Ok)
                {
                    if (!fromCache || attempt > 1) store.CachePut(cacheKey, message, raw);
                    var (report, summary) = BuildReport(answer.Sql, answer.Aciklama, answer.Varsayimlar, outcome.Result!);
                    return Finish(Response("report", answer.Baslik != "" ? answer.Baslik : message, summary, report: report),
                        answer.Sql, report.Rows.Count, report.DbMs);
                }

                if (outcome.Status == GuardStatus.Forbidden)
                {
                    if (fromCache) store.CacheRemove(cacheKey);
                    return Finish(Response("blocked", "İstek engellendi", outcome.Error!,
                        blocked: new BlockedDto(outcome.Error!, "SQL doğrulayıcı (SQLite authorizer)", answer.Sql)), answer.Sql, 0, 0, outcome.Error);
                }

                corrections.Add(new CorrectionDto(answer.Sql, outcome.Error!));
                log.LogInformation("Deneme {Attempt} başarısız: {Error}", attempt, outcome.Error);
                if (fromCache) { store.CacheRemove(cacheKey); fromCache = false; }
                if (attempt == MaxAttempts)
                    return Finish(Response("error", "Sorgu oluşturulamadı",
                        "Bu soruyu güvenilir bir sorguya çeviremedim. Soruyu biraz daha açık yazmayı ya da daha basit parçalara bölmeyi deneyebilirsiniz."),
                        answer.Sql, 0, 0, outcome.Error);

                var feedback = SqlErrorExplainer.Explain(outcome.Error!, answer.Sql, catalog);
                if (!feedback.Contains("aynen koru"))
                    feedback += "\nYalnızca bu hatayı düzelt; sorgudaki diğer tüm koşulları (Durum, IslemTipi, tarih aralıkları, filtreler) aynen koru.";
                if (corrections.Count >= 2 && corrections[^1].Sql.Trim() == corrections[^2].Sql.Trim())
                    feedback += "\nDİKKAT: Bir önceki denemedeki SQL'in AYNISINI gönderdin ve aynı hatayı aldın. Sorguyu yukarıdaki açıklamaya göre gerçekten değiştir; gerekirse daha basit bir yapı kur.";
                messages.Add(new ChatMessage("assistant", raw));
                messages.Add(new ChatMessage("user",
                    $"Bu SQL çalıştırılamadı.\nHata: {feedback}\nAynı isteği cevaplayan, hatası düzeltilmiş JSON'u döndür."));
                (answer, raw, var retryMs) = await llm.AskAsync(messages, ct);
                llmMs += retryMs;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            log.LogWarning(ex, "LLM çağrısı başarısız");
            return Finish(Response("error", "Model servisine ulaşılamadı",
                "Dil modeli şu anda cevap vermiyor. Daha önce sorulmuş sorular önbellekten cevaplanmaya devam eder; llama-server'ın çalıştığını kontrol edin."),
                null, 0, 0, ex.Message);
        }

        ChatResponse Response(string kind, string title, string text, ReportDto? report = null, ClarifyDto? clarify = null, BlockedDto? blocked = null) =>
            new(chatId, 0, kind, title, text, report, clarify, blocked,
                new MetaDto(corrections.Count + 1, fromCache, llmMs, 0, corrections.ToList(), llm.Model));

        ChatResponse Finish(ChatResponse r, string? sql, int rows, long dbMs, string? error = null)
        {
            r = r with { Meta = r.Meta with { TotalMs = total.ElapsedMilliseconds } };
            if (decorate != null) r = decorate(r);
            var id = store.AddMessage(chatId, "assistant", JsonSerializer.Serialize(r, Json));
            store.Log(chatId, message, r.Kind, sql, rows, r.Meta.Attempts, r.Meta.FromCache, r.Meta.LlmMs, dbMs, r.Meta.TotalMs, error);
            return r with { MessageId = id };
        }
    }

    /// <summary>
    /// Kullanıcının SQL panelinde elle düzenlediği sorguyu LLM'siz, aynı doğrulayıcıdan geçirerek çalıştırır.
    /// Jüri burada DELETE/DROP yazarak deterministik güvenlik katmanını doğrudan görebilir.
    /// </summary>
    public ChatResponse RunManual(string? chatId, string sql)
    {
        var total = Stopwatch.StartNew();
        chatId = store.EnsureChat(chatId);
        store.AddMessage(chatId, "user", "[Elle düzenlenen SQL]\n" + sql);
        var outcome = runner.Run(sql);
        var meta = new MetaDto(1, false, 0, 0, [], "—");

        ChatResponse response = outcome.Status switch
        {
            GuardStatus.Ok => BuildManual(),
            GuardStatus.Forbidden => new(chatId, 0, "blocked", "Sorgu engellendi", outcome.Error!, null, null,
                new BlockedDto(outcome.Error!, "SQL doğrulayıcı (SQLite authorizer)", sql), meta),
            _ => new(chatId, 0, "error", "Sorgu çalıştırılamadı", outcome.Error!, null, null, null, meta),
        };
        response = response with { Meta = meta with { TotalMs = total.ElapsedMilliseconds } };
        var id = store.AddMessage(chatId, "assistant", JsonSerializer.Serialize(response, Json));
        store.Log(chatId, "[Elle SQL]", response.Kind, sql, response.Report?.Rows.Count ?? 0, 1, false, 0,
            response.Report?.DbMs ?? 0, response.Meta.TotalMs, outcome.Error);
        return response with { MessageId = id };

        ChatResponse BuildManual()
        {
            var (report, summary) = BuildReport(sql, "Bu sorgu SQL panelinde elle düzenlendi ve aynı güvenlik kontrollerinden geçirilerek çalıştırıldı.", [], outcome.Result!);
            return new(chatId, 0, "report", "Düzenlenmiş sorgu", summary, report, null, null, meta);
        }
    }

    /// <summary>
    /// Drill down / across / up: rapordaki bir değere tıklanınca önceki SQL'e filtre eklenip kırılım değiştirilir.
    /// Basit sorgularda LLM'siz (DrillPlanner.Rewrite), değilse LLM'e takip mesajıyla. value null → üst seviyeye çıkma.
    /// </summary>
    public async Task<ChatResponse> DrillAsync(string chatId, long messageId, string column, object? value, string target, CancellationToken ct)
    {
        var total = Stopwatch.StartNew();
        var parent = LoadReport(chatId, messageId);
        var options = parent.Report!.Drill ?? DrillPlanner.Options(parent.Report.Sql, parent.Report.Columns, catalog);
        var col = options.FirstOrDefault(c => c.Column == column)
                  ?? throw new ArgumentException($"'{column}' kolonunda detaya inilemez.");
        if (value == null ? col.Up?.Key != target : col.Targets.All(t => t.Key != target))
            throw new ArgumentException("Bu kolon için geçersiz hedef seviye.");

        var level = DrillPlanner.Level(col.Level);
        var goal = DrillPlanner.Level(target);
        var stored = DrillPrefix + DrillPlanner.StepLabel(value, target);
        List<Crumb>? crumbs = null;
        if (value != null)
        {
            crumbs = parent.Crumbs is { Count: > 0 } c ? [.. c] : [new Crumb(parent.Title, null)];
            crumbs[^1] = crumbs[^1] with { MessageId = messageId };
            crumbs.Add(new Crumb(DrillPlanner.DisplayValue(value), null));
        }

        var (sql, reason) = DrillPlanner.Rewrite(parent.Report.Sql, column, value, target, catalog);
        if (sql != null)
        {
            var outcome = runner.Run(sql);
            if (outcome.Status == GuardStatus.Ok)
            {
                store.AddMessage(chatId, "user", stored);
                var tr = CultureInfo.GetCultureInfo("tr-TR");
                var title = value != null ? $"{DrillPlanner.DisplayValue(value)} › {goal.Label} kırılımı" : $"{goal.Label} kırılımı";
                var explanation = value != null
                    ? $"Önceki rapor yalnızca {level.Label.ToLower(tr)} = {DrillPlanner.DisplayValue(value)} için {goal.Label.ToLower(tr)} kırılımında yeniden hesaplandı. Sorgu dil modeli kullanılmadan, önceki SQL'e filtre eklenerek üretildi."
                    : $"Önceki rapor {goal.Label.ToLower(tr)} seviyesine çıkarıldı; alt seviye filtreleri kaldırıldı. Sorgu dil modeli kullanılmadan üretildi.";
                var (report, summary) = BuildReport(sql, explanation, parent.Report.Assumptions, outcome.Result!);
                var response = new ChatResponse(chatId, 0, "report", title, summary, report, null, null,
                    new MetaDto(1, false, 0, total.ElapsedMilliseconds, [], "—")) { Crumbs = crumbs, DrillSource = "rewrite" };
                var id = store.AddMessage(chatId, "assistant", JsonSerializer.Serialize(response, Json));
                store.Log(chatId, stored, "report", sql, report.Rows.Count, 1, false, 0, report.DbMs, response.Meta.TotalMs, null);
                return response with { MessageId = id };
            }
            reason = outcome.Error;
        }

        log.LogInformation("Drill LLM'e düştü: {Reason}", reason);
        return await HandleAsync(chatId, DrillPlanner.FollowUpMessage(level.Key, value, target), ct,
            baseSql: parent.Report.Sql, storedMessage: stored,
            decorate: r => r with { Crumbs = r.Kind == "report" ? crumbs : null, DrillSource = "llm" });
    }

    /// <summary>Detay yolundaki önceki bir rapora geri döner (drill up): kayıtlı rapor LLM'siz ve sorgusuz yeniden gösterilir.</summary>
    public ChatResponse DrillBack(string chatId, long messageId)
    {
        var total = Stopwatch.StartNew();
        var parent = LoadReport(chatId, messageId);
        var stored = DrillPrefix + "↩ " + (parent.Crumbs is { Count: > 0 } c ? c[^1].Label : parent.Title);
        store.AddMessage(chatId, "user", stored);
        var response = parent with
        {
            ChatId = chatId, MessageId = 0, DrillSource = "back",
            Meta = parent.Meta with { FromCache = false, LlmMs = 0, Attempts = 1, Corrections = [], TotalMs = total.ElapsedMilliseconds },
        };
        var id = store.AddMessage(chatId, "assistant", JsonSerializer.Serialize(response, Json));
        store.Log(chatId, stored, "report", parent.Report!.Sql, parent.Report.Rows.Count, 1, false, 0, 0, response.Meta.TotalMs, null);
        return response with { MessageId = id };
    }

    public const string DrillPrefix = "[Detay] ";

    ChatResponse LoadReport(string chatId, long messageId)
    {
        var message = store.GetMessages(chatId).FirstOrDefault(m => m.Id == messageId && m.Role == "assistant")
                      ?? throw new KeyNotFoundException("Rapor bulunamadı.");
        var response = JsonSerializer.Deserialize<ChatResponse>(message.Content, Json);
        return response?.Report != null ? response : throw new ArgumentException("Bu mesaj bir rapor değil.");
    }

    (ReportDto Report, string Summary) BuildReport(string sql, string explanation, IReadOnlyList<string> assumptions, QueryResult result)
    {
        var cols = ReportBuilder.Classify(result.Columns);
        var chart = ReportBuilder.PickChart(cols, result.Rows);
        var summary = ReportBuilder.Summarize(cols, result.Rows, chart, result.Truncated);
        return (new ReportDto(sql, explanation, assumptions, cols, result.Rows, result.Truncated, chart,
            result.Tables, result.UsedColumns, result.Plan, result.PlanWarnings, result.DurationMs)
        { Drill = DrillPlanner.Options(sql, cols, catalog) }, summary);
    }

    /// <summary>
    /// Aynı soru (aynı sohbet bağlamında, aynı prompt sürümü ve aynı modelle) → aynı cevap. Demo sırasında LLM'siz çalışmayı da sağlar.
    /// Prompt dosyaları, ayarlar (örn. KVKK) veya model değişince anahtar da değişir; eski prompt'la üretilmiş cevaplar bir daha dönmez.
    /// </summary>
    static string CacheKey(string message, string anchor, string promptVersion, string model)
    {
        var normalized = Regex.Replace(message.ToLower(CultureInfo.GetCultureInfo("tr-TR")), @"[^\p{L}\p{N}]+", " ").Trim();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{normalized}\n{anchor}\n{promptVersion}\n{model}")))[..32];
    }
}
