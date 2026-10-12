using Diyalog360.Api.Data;
using Diyalog360.Api.Services;
using Azure.Core;
using Azure.Identity;

// Kullanım:
//   dotnet run                 → API (http://localhost:5080)
//   dotnet run -- seed         → demo veritabanını (Data/bank.db) bugüne göre yeniden üret
//   dotnet run -- eval [id]    → eval/questions.json ile doğruluk ölçümü (model servisi açık olmalı)
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

var llmOptions = builder.Configuration.GetSection("Llm").Get<LlmOptions>() ?? new LlmOptions();
var catalog = new SchemaCatalog(bankDb, Path.Combine(promptDir, "schema.notes.json"));
var promptBuilder = new PromptBuilder(promptDir, catalog);
var guard = new SqlGuard(catalog);
File.WriteAllText(Path.Combine(dataDir, "prompt.generated.md"), promptBuilder.SystemPrompt);

builder.Services.AddSingleton(llmOptions);
builder.Services.AddSingleton<TokenCredential>(_ =>
    llmOptions.UseManagedIdentity
        ? new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned)
        : new DefaultAzureCredential());
builder.Services.AddSingleton(catalog);
builder.Services.AddSingleton(promptBuilder);
builder.Services.AddSingleton(guard);
builder.Services.AddSingleton(new QueryRunner(bankDb, guard, catalog,
    builder.Configuration.GetValue("Query:MaxRows", 1000), builder.Configuration.GetValue("Query:TimeoutSeconds", 15)));
builder.Services.AddSingleton(new AppStore(Path.Combine(dataDir, "app.db")));
builder.Services.AddHttpClient<LlmClient>(c => c.Timeout = Timeout.InfiniteTimeSpan);
builder.Services.AddTransient<ChatOrchestrator>();
builder.Services.AddTransient<EvalRunner>();

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

app.MapPost("/api/chat", async (ChatRequest req, ChatOrchestrator orchestrator, CancellationToken ct) =>
{
    var message = req.Message?.Trim() ?? "";
    if (message.Length is 0 or > 1000) return Results.BadRequest(new { error = "Mesaj 1-1000 karakter olmalı." });
    return Results.Ok(await orchestrator.HandleAsync(req.ChatId, message, ct));
});

app.MapPost("/api/sql", (SqlRequest req, ChatOrchestrator orchestrator) =>
{
    var sql = req.Sql?.Trim() ?? "";
    if (sql.Length is 0 or > 5000) return Results.BadRequest(new { error = "SQL 1-5000 karakter olmalı." });
    return Results.Ok(orchestrator.RunManual(req.ChatId, sql));
});

app.MapGet("/api/history/{chatId}", (string chatId, AppStore store) =>
    store.GetMessages(chatId).Select(m => m.Role == "user"
        ? (object)new { role = "user", text = m.Content }
        : new { role = "assistant", response = System.Text.Json.JsonSerializer.Deserialize<ChatResponse>(m.Content, ChatOrchestrator.Json) }));

app.MapGet("/api/schema", (SchemaCatalog c) => c.Tables);

app.MapGet("/api/health", async (LlmClient llm, SchemaCatalog c, CancellationToken ct) =>
    new { llm = await llm.IsHealthyAsync(ct), model = llm.Model, provider = llm.Provider, tables = c.Tables.Count });

app.MapFallbackToFile("index.html");

// Sabit sistem prompt'unu açılışta modele okut: ilk kullanıcı sorusu da hızlı cevaplansın.
app.Lifetime.ApplicationStarted.Register(() => _ = Task.Run(async () =>
{
    if (llmOptions.UsesFoundryAgent) return;
    try
    {
        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<LlmClient>().WarmUpAsync(promptBuilder.SystemPrompt, CancellationToken.None);
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning("LLM ısıtılamadı ({Message}); model servisi kullanılabilir mi?", ex.Message);
    }
}));

app.Run();

record ChatRequest(string? ChatId, string? Message);

record SqlRequest(string? ChatId, string? Sql);
