using System.Security.Claims;
using Diyalog360.Api.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Diyalog360.Api.Services;

// Kullanım:
//   dotnet run                 → API (http://localhost:5080)
//   dotnet run -- seed         → demo veritabanını (Data/bank.db) bugüne göre yeniden üret
//   dotnet run -- eval [id]    → eval/questions.json ile doğruluk ölçümü (llama-server açık olmalı)
Console.OutputEncoding = System.Text.Encoding.UTF8; // Windows konsolunda Türkçe karakter ve ✅/❌ için
var builder = WebApplication.CreateBuilder(args);
var root = builder.Environment.ContentRootPath;
var dataDir = Path.Combine(root, "Data");
var promptDir = Path.Combine(root, "Prompt");
var bankDb = Path.Combine(dataDir, "bank.db");
Directory.CreateDirectory(dataDir);

if (args.Contains("seed") || !File.Exists(bankDb))
{
    Console.WriteLine("Demo veritabanı üretiliyor (Data/bank.db)...");
    DemoSeeder.Seed(bankDb);
    Console.WriteLine("Tamam.");
    if (args.Contains("seed")) return;
}

if (DemoSeeder.EnsureCinsiyet(bankDb)) Console.WriteLine("Musteri tablosuna Cinsiyet kolonu eklendi (müşteri adından dolduruldu).");

var llmOptions = builder.Configuration.GetSection("Llm").Get<LlmOptions>() ?? new LlmOptions();
var catalog = new SchemaCatalog(bankDb, Path.Combine(promptDir, "schema.notes.json"),
    protectPersonalData: builder.Configuration.GetValue("Privacy:ProtectPersonalData", false));
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
builder.Services.AddSingleton(new FilterCatalog(bankDb, catalog));
builder.Services.AddTransient<ZbAnalyzer>();
builder.Services.AddHttpClient<LlmClient>(c => c.Timeout = Timeout.InfiniteTimeSpan);
builder.Services.AddTransient<ChatOrchestrator>();
builder.Services.AddTransient<EvalRunner>();

// Giriş: tek kullanıcı (app.db → AppUser), HttpOnly çerezle oturum. API 401 döner; yönlendirmeyi önyüz yapar.
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o =>
{
    o.Cookie.Name = "d360.auth";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.ExpireTimeSpan = TimeSpan.FromHours(8);
    o.SlidingExpiration = true;
    o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
    o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
});
builder.Services.AddAuthorization();

var app = builder.Build();

if (args.Contains("eval"))
{
    var only = args.SkipWhile(a => a != "eval").Skip(1).FirstOrDefault();
    var evalPath = Path.GetFullPath(Path.Combine(root, "..", "eval", "questions.json"));
    Environment.ExitCode = await app.Services.GetRequiredService<EvalRunner>().RunAsync(evalPath, only, CancellationToken.None);
    return;
}

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

app.MapPost("/api/auth/login", async (LoginRequest req, AppStore store, HttpContext http) =>
{
    var username = req.Username?.Trim() ?? "";
    if (username.Length == 0 || string.IsNullOrEmpty(req.Password) || !store.ValidateUser(username, req.Password))
    {
        await Task.Delay(400); // kaba kuvvet denemelerini yavaşlat
        return Results.Json(new { error = "Kullanıcı adı veya şifre hatalı." }, statusCode: StatusCodes.Status401Unauthorized);
    }
    var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, username.ToLowerInvariant())], CookieAuthenticationDefaults.AuthenticationScheme);
    await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
    return Results.Ok(new { username = username.ToLowerInvariant() });
});

app.MapPost("/api/auth/logout", async (HttpContext http) =>
{
    await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.NoContent();
});

app.MapGet("/api/auth/me", (ClaimsPrincipal user) =>
    user.Identity?.IsAuthenticated == true ? Results.Ok(new { username = user.Identity.Name }) : Results.Unauthorized());

app.MapPost("/api/chat", async (ChatRequest req, ChatOrchestrator orchestrator, AppStore store, ClaimsPrincipal user, CancellationToken ct) =>
{
    var message = req.Message?.Trim() ?? "";
    if (message.Length is 0 or > 1000) return Results.BadRequest(new { error = "Mesaj 1-1000 karakter olmalı." });
    if (req.ChatId != null && !store.CanAccess(req.ChatId, user.Identity!.Name!)) return Results.NotFound(new { error = "Sohbet bulunamadı." });
    var response = await orchestrator.HandleAsync(req.ChatId, message, ct);
    store.ClaimChat(response.ChatId, user.Identity!.Name!);
    return Results.Ok(response);
}).RequireAuthorization();

app.MapPost("/api/sql", (SqlRequest req, ChatOrchestrator orchestrator, AppStore store, ClaimsPrincipal user) =>
{
    var sql = req.Sql?.Trim() ?? "";
    if (sql.Length is 0 or > 5000) return Results.BadRequest(new { error = "SQL 1-5000 karakter olmalı." });
    if (req.ChatId != null && !store.CanAccess(req.ChatId, user.Identity!.Name!)) return Results.NotFound(new { error = "Sohbet bulunamadı." });
    var response = orchestrator.RunManual(req.ChatId, sql);
    store.ClaimChat(response.ChatId, user.Identity!.Name!);
    return Results.Ok(response);
}).RequireAuthorization();

app.MapGet("/api/history/{chatId}", (string chatId, AppStore store, ClaimsPrincipal user) =>
    !store.CanAccess(chatId, user.Identity!.Name!)
        ? Results.NotFound(new { error = "Sohbet bulunamadı." })
        : Results.Ok(store.GetMessages(chatId).Select(m => m.Role == "user"
            ? (object)new { role = "user", text = m.Content, createdAt = m.CreatedAt }
            : new { role = "assistant", response = System.Text.Json.JsonSerializer.Deserialize<ChatResponse>(m.Content, ChatOrchestrator.Json)! with { MessageId = m.Id }, createdAt = m.CreatedAt })))
    .RequireAuthorization();

// Drill down / across / up: rapordaki bir değere tıklayarak detaya inme ve detay yolunda geri dönme
app.MapPost("/api/drill", async (DrillRequest req, ChatOrchestrator orchestrator, AppStore store, ClaimsPrincipal user, CancellationToken ct) =>
{
    if (string.IsNullOrEmpty(req.ChatId) || string.IsNullOrEmpty(req.Column) || string.IsNullOrEmpty(req.Target))
        return Results.BadRequest(new { error = "chatId, column ve target zorunlu." });
    if (!store.CanAccess(req.ChatId, user.Identity!.Name!)) return Results.NotFound(new { error = "Sohbet bulunamadı." });
    object? value = req.Value?.ValueKind switch
    {
        System.Text.Json.JsonValueKind.Number => req.Value.Value.TryGetInt64(out var l) ? l : req.Value.Value.GetDouble(),
        System.Text.Json.JsonValueKind.String => req.Value.Value.GetString(),
        _ => null,
    };
    try
    {
        var response = await orchestrator.DrillAsync(req.ChatId, req.MessageId, req.Column, value, req.Target, ct);
        store.ClaimChat(response.ChatId, user.Identity!.Name!);
        return Results.Ok(response);
    }
    catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
}).RequireAuthorization();

app.MapPost("/api/drill/back", (DrillBackRequest req, ChatOrchestrator orchestrator, AppStore store, ClaimsPrincipal user) =>
{
    if (string.IsNullOrEmpty(req.ChatId)) return Results.BadRequest(new { error = "chatId zorunlu." });
    if (!store.CanAccess(req.ChatId, user.Identity!.Name!)) return Results.NotFound(new { error = "Sohbet bulunamadı." });
    try { return Results.Ok(orchestrator.DrillBack(req.ChatId, req.MessageId)); }
    catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
}).RequireAuthorization();

// ZB: soru kutusunda "ZB <ifade>" yazılınca ifade analiz edilir ve filtre penceresinin alanları döner
app.MapPost("/api/zb/analyze", async (ZbRequest req, ZbAnalyzer analyzer, CancellationToken ct) =>
{
    var term = req.Term?.Trim() ?? "";
    if (term.Length is 0 or > 200) return Results.BadRequest(new { error = "İfade 1-200 karakter olmalı." });
    return Results.Ok(await analyzer.AnalyzeAsync(term, req.Text, ct, req.Context));
}).RequireAuthorization();

// ZB penceresinde seçim değiştikçe: diğer alanların seçenekleri ve sayıları bağlama göre yeniden hesaplanır (örn. Ankara → yalnızca Ankara şubeleri)
app.MapPost("/api/zb/facets", (ZbFacetRequest req, FilterCatalog filters) =>
    Results.Ok(filters.Facets(req.Context ?? [], filters.Describe(req.Context ?? [], "pencere")))).RequireAuthorization();

// Geçmiş menüsü: sohbet listesi (son etkinliğe göre), arama, yeniden adlandırma, silme
app.MapGet("/api/chats", (string? q, AppStore store, ClaimsPrincipal user) => store.ListChats(user.Identity!.Name!, q))
    .RequireAuthorization();

app.MapPatch("/api/chats/{chatId}", (string chatId, RenameRequest req, AppStore store, ClaimsPrincipal user) =>
{
    var title = req.Title?.Trim() ?? "";
    if (title.Length is 0 or > 120) return Results.BadRequest(new { error = "Başlık 1-120 karakter olmalı." });
    return store.RenameChat(chatId, user.Identity!.Name!, title) ? Results.NoContent() : Results.NotFound(new { error = "Sohbet bulunamadı." });
}).RequireAuthorization();

app.MapDelete("/api/chats/{chatId}", (string chatId, AppStore store, ClaimsPrincipal user) =>
    store.DeleteChat(chatId, user.Identity!.Name!) ? Results.NoContent() : Results.NotFound(new { error = "Sohbet bulunamadı." }))
    .RequireAuthorization();

app.MapGet("/api/schema", (SchemaCatalog c) => c.Tables).RequireAuthorization();

app.MapGet("/api/health", async (LlmClient llm, SchemaCatalog c, CancellationToken ct) =>
    new { llm = await llm.IsHealthyAsync(ct), model = llm.Model, tables = c.Tables.Count });

app.MapFallbackToFile("index.html");

// Sabit sistem prompt'unu açılışta modele okut: ilk kullanıcı sorusu da hızlı cevaplansın.
app.Lifetime.ApplicationStarted.Register(() => _ = Task.Run(async () =>
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

record RenameRequest(string? Title);

record DrillRequest(string? ChatId, long MessageId, string? Column, System.Text.Json.JsonElement? Value, string? Target);

record DrillBackRequest(string? ChatId, long MessageId);

record ZbRequest(string? Term, string? Text, List<FilterDefault>? Context);

record ZbFacetRequest(List<FilterDefault>? Context);
