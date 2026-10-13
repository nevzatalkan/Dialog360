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
    IReadOnlyList<string> Plan, IReadOnlyList<string> PlanWarnings, long DbMs);

public record ClarifyDto(string Question, IReadOnlyList<string> Options);

/// <param name="Layer">İsteği hangi güvenlik katmanının durdurduğu.</param>
public record BlockedDto(string Reason, string Layer, string? Sql);

public record CorrectionDto(string Sql, string Error);

public record MetaDto(int Attempts, bool FromCache, long LlmMs, long TotalMs, IReadOnlyList<CorrectionDto> Corrections, string Model);

/// <param name="Kind">report | clarify | blocked | error</param>
public record ChatResponse(
    string ChatId, long MessageId, string Kind, string Title, string Text,
    ReportDto? Report, ClarifyDto? Clarify, BlockedDto? Blocked, MetaDto Meta);

/// <summary>
/// Bir mesajı uçtan uca işler: önbellek → LLM (yapılandırılmış JSON) → SqlGuard → çalıştırma →
/// hata varsa hata mesajıyla LLM'e geri dönüp en fazla 2 kez düzeltme → grafik + özet.
/// </summary>
public sealed class ChatOrchestrator(PromptBuilder prompt, LlmClient llm, QueryRunner runner, AppStore store, ILogger<ChatOrchestrator> log)
{
    const int MaxAttempts = 3;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<ChatResponse> HandleAsync(string? chatId, string message, CancellationToken ct, bool useCache = true)
    {
        var total = Stopwatch.StartNew();
        chatId = store.EnsureChat(chatId);
        var history = store.GetMessages(chatId);
        store.AddMessage(chatId, "user", message);

        var (historyText, anchor) = PromptBuilder.History(history);
        var cacheKey = CacheKey(message, anchor);
        var messages = new List<ChatMessage>
        {
            new("system", prompt.SystemPrompt),
            new("user", PromptBuilder.UserMessage(historyText, message)),
        };

        var corrections = new List<CorrectionDto>();
        long llmMs = 0;
        bool fromCache = false;
        LlmAnswer answer;
        string raw;

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

                var outcome = string.IsNullOrWhiteSpace(answer.Sql)
                    ? new QueryOutcome(GuardStatus.Invalid, "SQL alanı boş; tur 'rapor' ise tek bir SELECT yazmalısın.", null)
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

                messages.Add(new ChatMessage("assistant", raw));
                messages.Add(new ChatMessage("user",
                    $"Bu SQL çalıştırılamadı.\nHata: {outcome.Error}\nAynı isteği cevaplayan, hatası düzeltilmiş JSON'u döndür."));
                (answer, raw, var retryMs) = await llm.AskAsync(messages, ct);
                llmMs += retryMs;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            log.LogWarning(ex, "LLM çağrısı başarısız");
            return Finish(Response("error", "Model servisine ulaşılamadı",
                $"Dil modeli şu anda cevap vermiyor ({ex.Message[..Math.Min(ex.Message.Length, 300)]}). " +
                "Daha önce sorulmuş sorular önbellekten cevaplanmaya devam eder; model sunucusunu ve API anahtarını kontrol edin."),
                null, 0, 0, ex.Message);
        }

        ChatResponse Response(string kind, string title, string text, ReportDto? report = null, ClarifyDto? clarify = null, BlockedDto? blocked = null) =>
            new(chatId, 0, kind, title, text, report, clarify, blocked,
                new MetaDto(corrections.Count + 1, fromCache, llmMs, 0, corrections.ToList(), llm.Model));

        ChatResponse Finish(ChatResponse r, string? sql, int rows, long dbMs, string? error = null)
        {
            r = r with { Meta = r.Meta with { TotalMs = total.ElapsedMilliseconds } };
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

    static (ReportDto Report, string Summary) BuildReport(string sql, string explanation, IReadOnlyList<string> assumptions, QueryResult result)
    {
        var cols = ReportBuilder.Classify(result.Columns);
        var chart = ReportBuilder.PickChart(cols, result.Rows);
        var summary = ReportBuilder.Summarize(cols, result.Rows, chart, result.Truncated);
        return (new ReportDto(sql, explanation, assumptions, cols, result.Rows, result.Truncated, chart,
            result.Tables, result.UsedColumns, result.Plan, result.PlanWarnings, result.DurationMs), summary);
    }

    /// <summary>Aynı soru (aynı sohbet bağlamında) → aynı cevap. Demo sırasında LLM'siz çalışmayı da sağlar.</summary>
    static string CacheKey(string message, string anchor)
    {
        var normalized = Regex.Replace(message.ToLower(CultureInfo.GetCultureInfo("tr-TR")), @"[^\p{L}\p{N}]+", " ").Trim();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized + "\n" + anchor)))[..32];
    }
}
