using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Diyalog360.Api.Data;
using Diyalog360.Api.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

// Kullanım:
//   dotnet run                          → API (http://localhost:5080)
//   dotnet run -- set-password [ad]     → tek kullanıcının adını ve şifresini belirle (Data/auth.json)
//   dotnet run -- seed                  → demo veritabanını (Data/bank.db) bugüne göre yeniden üret
//   dotnet run -- eval [id]             → eval/questions.json ile doğruluk ölçümü (llama-server açık olmalı)
Console.OutputEncoding = System.Text.Encoding.UTF8; // Windows konsolunda Türkçe karakter ve ✅/❌ için
var builder = WebApplication.CreateBuilder(args);
var root = builder.Environment.ContentRootPath;
var dataDir = Path.Combine(root, "Data");
var promptDir = Path.Combine(root, "Prompt");
var bankDb = Path.Combine(dataDir, "bank.db");
var authFile = Path.Combine(dataDir, "auth.json");
Directory.CreateDirectory(dataDir);

if (args.Contains("set-password"))
{
    Environment.ExitCode = SetPasswordCommand.Run(authFile, args.SkipWhile(a => a != "set-password").Skip(1).FirstOrDefault(a => !a.StartsWith('-')));
    return;
}

if (args.Contains("seed") || !File.Exists(bankDb))
{
    Console.WriteLine("Demo veritabanı üretiliyor (Data/bank.db)...");
    DemoSeeder.Seed(bankDb);
    Console.WriteLine("Tamam.");
    if (args.Contains("seed")) return;
}

var llmOptions = builder.Configuration.GetSection("Llm").Get<LlmOptions>() ?? new LlmOptions();
var catalog = new SchemaCatalog(bankDb, Path.Combine(promptDir, "schema.notes.json"));
var promptBuilder = new PromptBuilder(promptDir, catalog);
var guard = new SqlGuard(catalog);
File.WriteAllText(Path.Combine(dataDir, "prompt.generated.md"), promptBuilder.SystemPrompt);

builder.Services.AddSingleton(llmOptions);
builder.Services.AddSingleton(catalog);
builder.Services.AddSingleton(promptBuilder);
builder.Services.AddSingleton(guard);
builder.Services.AddSingleton(new QueryRunner(bankDb, guard, catalog,
    builder.Configuration.GetValue("Query:MaxRows", 1000), builder.Configuration.GetValue("Query:TimeoutSeconds", 15)));
builder.Services.AddSingleton(new AppStore(Path.Combine(dataDir, "app.db")));
builder.Services.AddSingleton(new AuthService(authFile));
builder.Services.AddHttpClient<LlmClient>(c => c.Timeout = Timeout.InfiniteTimeSpan);

// Sesle soru: Speech:Provider = Whisper (yerel whisper.cpp) | AzureSpeech | None
var speechOptions = builder.Configuration.GetSection("Speech").Get<SpeechOptions>() ?? new SpeechOptions();
var speechVocabulary = SpeechVocabulary.Build(catalog);
File.WriteAllText(Path.Combine(dataDir, "speech-prompt.generated.txt"), speechVocabulary);
builder.Services.AddHttpClient("speech", c => c.Timeout = TimeSpan.FromSeconds(speechOptions.TimeoutSeconds));
builder.Services.AddSingleton<ISpeechToText>(sp =>
{
    var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("speech");
    return speechOptions.Provider.ToLowerInvariant() switch
    {
        "whisper" => new WhisperSpeechToText(http, speechOptions, speechVocabulary),
        "azurespeech" => new AzureSpeechToText(http, speechOptions),
        _ => new DisabledSpeechToText(),
    };
});
builder.Services.AddTransient<ChatOrchestrator>();
builder.Services.AddTransient<EvalRunner>();

// Oturum: şifreli, HttpOnly, SameSite=Strict çerez. Anahtarlar Data/keys'te; yeniden başlatmada oturum düşmez.
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDir, "keys")))
    .SetApplicationName("Diyalog360");
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o =>
{
    o.Cookie.Name = "d360.session";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    o.ExpireTimeSpan = TimeSpan.FromHours(builder.Configuration.GetValue("Auth:SessionHours", 8));
    o.SlidingExpiration = true;
    // API çağrılarında giriş sayfasına yönlendirme yerine 401/403
    o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
    o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
    // Her istekte: oturum sunucuda hâlâ kayıtlı mı ve şifre o oturumdan beri değişmedi mi?
    o.Events.OnValidatePrincipal = async ctx =>
    {
        var services = ctx.HttpContext.RequestServices;
        var sid = ctx.Principal?.FindFirstValue("sid");
        var stamp = ctx.Principal?.FindFirstValue("stamp");
        if (sid == null || !services.GetRequiredService<AppStore>().SessionExists(sid) || stamp != services.GetRequiredService<AuthService>().Stamp)
        {
            ctx.RejectPrincipal();
            await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        }
    };
});
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddFixedWindowLimiter("login", w => { w.PermitLimit = 10; w.Window = TimeSpan.FromMinutes(1); w.QueueLimit = 0; });
});

var app = builder.Build();

if (args.Contains("eval"))
{
    var only = args.SkipWhile(a => a != "eval").Skip(1).FirstOrDefault();
    var evalPath = Path.GetFullPath(Path.Combine(root, "..", "eval", "questions.json"));
    Environment.ExitCode = await app.Services.GetRequiredService<EvalRunner>().RunAsync(evalPath, only, CancellationToken.None);
    return;
}

if (!File.Exists(authFile))
    app.Logger.LogWarning("Kullanıcı tanımlı değil; giriş yapılamaz. Tanımlamak için: cd api && dotnet run -- set-password");

app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h.XContentTypeOptions = "nosniff";
    h.XFrameOptions = "DENY";
    h["Referrer-Policy"] = "no-referrer";
    h.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; " +
                              "connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
    if (ctx.Request.Path.StartsWithSegments("/api")) h.CacheControl = "no-store";
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// --- Kimlik doğrulama (anonim erişilebilir) ---
app.MapPost("/api/auth/login", async (LoginRequest req, AuthService auth, AppStore store, HttpContext http) =>
{
    var username = req.Username ?? "";
    var password = req.Password ?? "";
    if (username.Length is 0 or > 100 || password.Length is 0 or > 200)
        return Results.BadRequest(new { error = "Kullanıcı adı ve şifre gerekli." });

    var result = auth.Verify(username, password);
    switch (result.Status)
    {
        case LoginStatus.Success:
            var sid = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            store.AddSession(sid);
            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, username.Trim()), new Claim("sid", sid), new Claim("stamp", auth.Stamp)],
                CookieAuthenticationDefaults.AuthenticationScheme);
            await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
            return Results.Ok(new { username = username.Trim() });
        case LoginStatus.NotConfigured:
            return Results.Json(new { error = "Kullanıcı tanımlı değil. Sunucuda `cd api && dotnet run -- set-password` çalıştırın." }, statusCode: 503);
        case LoginStatus.Locked:
            return Results.Json(new { error = $"Çok fazla hatalı deneme. {Math.Ceiling(result.RetryAfter!.Value.TotalMinutes)} dakika sonra tekrar deneyin." }, statusCode: 429);
        default:
            await Task.Delay(Random.Shared.Next(300, 700)); // deneme hızını düşür
            return Results.Json(new { error = "Kullanıcı adı veya şifre hatalı." }, statusCode: 401);
    }
}).RequireRateLimiting("login");

app.MapPost("/api/auth/logout", async (HttpContext http, AppStore store) =>
{
    if (http.User.FindFirstValue("sid") is { } sid) store.RemoveSession(sid);
    await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.NoContent();
});

app.MapGet("/api/auth/me", (HttpContext http, AuthService auth) =>
    http.User.Identity?.IsAuthenticated == true
        ? Results.Ok(new { username = http.User.Identity.Name })
        : Results.Json(new { configured = auth.IsConfigured }, statusCode: 401));

// --- Uygulama (giriş gerekli) ---
var api = app.MapGroup("/api").RequireAuthorization();

api.MapPost("/chat", async (ChatRequest req, ChatOrchestrator orchestrator, CancellationToken ct) =>
{
    var message = req.Message?.Trim() ?? "";
    if (message.Length is 0 or > 1000) return Results.BadRequest(new { error = "Mesaj 1-1000 karakter olmalı." });
    if (!ChatId.IsValidOrEmpty(req.ChatId)) return Results.BadRequest(new { error = "Geçersiz sohbet kimliği." });
    return Results.Ok(await orchestrator.HandleAsync(req.ChatId, message, ct));
});

api.MapPost("/sql", (SqlRequest req, ChatOrchestrator orchestrator) =>
{
    var sql = req.Sql?.Trim() ?? "";
    if (sql.Length is 0 or > 5000) return Results.BadRequest(new { error = "SQL 1-5000 karakter olmalı." });
    if (!ChatId.IsValidOrEmpty(req.ChatId)) return Results.BadRequest(new { error = "Geçersiz sohbet kimliği." });
    return Results.Ok(orchestrator.RunManual(req.ChatId, sql));
});

api.MapGet("/history/{chatId}", (string chatId, AppStore store) =>
    !ChatId.IsValidOrEmpty(chatId) ? Results.BadRequest() : Results.Ok(store.GetMessages(chatId).Select(m => m.Role == "user"
        ? (object)new { role = "user", text = m.Content }
        : new { role = "assistant", response = System.Text.Json.JsonSerializer.Deserialize<ChatResponse>(m.Content, ChatOrchestrator.Json) })));

api.MapDelete("/chats/{chatId}", (string chatId, AppStore store) =>
{
    if (!ChatId.IsValidOrEmpty(chatId)) return Results.BadRequest();
    store.DeleteChat(chatId);
    return Results.NoContent();
});

api.MapPost("/speech", async (HttpRequest request, ISpeechToText stt, CancellationToken ct) =>
{
    // Çapraz site form gönderimi özel başlık ekleyemez: SameSite çereze ek CSRF katmanı
    if (request.Headers["X-Diyalog360"] != "1") return Results.BadRequest(new { error = "Eksik istek başlığı." });
    if (!request.HasFormContentType) return Results.BadRequest(new { error = "Ses kaydı multipart/form-data olarak gönderilmeli." });
    var file = (await request.ReadFormAsync(ct)).Files["audio"];
    if (file is null || file.Length is 0 or > 5_000_000) return Results.BadRequest(new { error = "Ses kaydı 0-5 MB arasında olmalı." });
    using var buffer = new MemoryStream();
    await file.CopyToAsync(buffer, ct);
    var wav = buffer.ToArray();
    if (!Wav.LooksValid(wav)) return Results.BadRequest(new { error = "Ses kaydı WAV biçiminde olmalı." });
    try
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var text = await stt.TranscribeAsync(wav, ct);
        return Results.Ok(new { text, provider = stt.Name, durationMs = sw.ElapsedMilliseconds });
    }
    catch (SpeechUnavailableException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: 503);
    }
}).WithMetadata(new RequestSizeLimitAttribute(6_000_000));

api.MapGet("/schema", (SchemaCatalog c) => c.Tables);

api.MapGet("/health", async (LlmClient llm, ISpeechToText stt, SchemaCatalog c, CancellationToken ct) =>
    new { llm = await llm.IsHealthyAsync(ct), model = llm.Model, tables = c.Tables.Count, speech = await stt.IsHealthyAsync(ct), speechProvider = stt.Name });

app.MapFallbackToFile("index.html");

// Sabit sistem prompt'unu açılışta modele okut: ilk kullanıcı sorusu da hızlı cevaplansın.
// Uzak API'lerde (LlamaCachePrompt=false) KV önbelleği yok; ısınma yalnızca istek kotası harcar.
if (llmOptions.LlamaCachePrompt) app.Lifetime.ApplicationStarted.Register(() => _ = Task.Run(async () =>
{
    try
    {
        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<LlmClient>().WarmUpAsync(promptBuilder.SystemPrompt, CancellationToken.None);
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning("LLM ısıtılamadı ({Message}); llama-server açık mı?", ex.Message);
    }
}));

app.Run();

record ChatRequest(string? ChatId, string? Message);

record SqlRequest(string? ChatId, string? Sql);

record LoginRequest(string? Username, string? Password);

static partial class ChatId
{
    [GeneratedRegex("^[A-Za-z0-9-]{1,64}$")]
    private static partial Regex Pattern();

    public static bool IsValidOrEmpty(string? id) => string.IsNullOrEmpty(id) || Pattern().IsMatch(id);
}

static class SetPasswordCommand
{
    public static int Run(string authFile, string? username)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(username))
            {
                Console.Write("Kullanıcı adı: ");
                username = Console.ReadLine();
            }
            string password;
            if (Console.IsInputRedirected)
            {
                password = Console.ReadLine() ?? "";
            }
            else
            {
                password = ReadHidden("Şifre: ");
                if (ReadHidden("Şifre (tekrar): ") != password) { Console.Error.WriteLine("Şifreler eşleşmiyor."); return 1; }
            }
            AuthService.SetPassword(authFile, username ?? "", password);
            Console.WriteLine($"Kullanıcı kaydedildi: {username!.Trim()} → {authFile}");
            return 0;
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    static string ReadHidden(string prompt)
    {
        Console.Write(prompt);
        var sb = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
            if (!char.IsControl(key.KeyChar)) sb.Append(key.KeyChar);
        }
        Console.WriteLine();
        return sb.ToString();
    }
}
