using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Diyalog360.Api.Services;

public sealed class LlmOptions
{
    /// <summary>OpenAI uyumlu uç (llama-server, Azure OpenAI, Foundry...).</summary>
    public string BaseUrl { get; set; } = "http://127.0.0.1:18080/v1";
    public string Model { get; set; } = "qwen";
    public string? ApiKey { get; set; }
    public double Temperature { get; set; } = 0;
    public int MaxTokens { get; set; } = 900;
    public int TimeoutSeconds { get; set; } = 240;
    /// <summary>llama-server'a sabit prompt önekini KV önbellekte tutmasını söyler.</summary>
    public bool LlamaCachePrompt { get; set; } = true;
}

public record ChatMessage(string Role, string Content);

/// <summary>LLM'in yapılandırılmış cevabı. Alan adları prompt'taki JSON ile birebir aynıdır.</summary>
public sealed record LlmAnswer(
    [property: JsonPropertyName("tur")] string Tur,
    [property: JsonPropertyName("baslik")] string Baslik,
    [property: JsonPropertyName("sql")] string Sql,
    [property: JsonPropertyName("aciklama")] string Aciklama,
    [property: JsonPropertyName("varsayimlar")] List<string> Varsayimlar,
    [property: JsonPropertyName("netlestirme_sorusu")] string NetlestirmeSorusu,
    [property: JsonPropertyName("secenekler")] List<string> Secenekler);

public sealed class LlmClient(HttpClient http, LlmOptions options, ILogger<LlmClient> log)
{
    static readonly JsonNode AnswerSchema = JsonNode.Parse("""
        {
          "type": "object",
          "properties": {
            "tur": { "type": "string", "enum": ["rapor", "netlestirme", "ret"] },
            "baslik": { "type": "string" },
            "sql": { "type": "string" },
            "aciklama": { "type": "string" },
            "varsayimlar": { "type": "array", "items": { "type": "string" } },
            "netlestirme_sorusu": { "type": "string" },
            "secenekler": { "type": "array", "items": { "type": "string" } }
          },
          "required": ["tur", "baslik", "sql", "aciklama", "varsayimlar", "netlestirme_sorusu", "secenekler"],
          "additionalProperties": false
        }
        """)!;

    public string Model => options.Model;

    public async Task<(LlmAnswer Answer, string Raw, long Ms)> AskAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var raw = await AskJsonAsync(messages, "diyalog360_cevap", AnswerSchema, ct);
        var answer = JsonSerializer.Deserialize<LlmAnswer>(raw)
                     ?? throw new InvalidOperationException("LLM boş cevap döndürdü.");
        answer = answer with
        {
            Sql = answer.Sql?.Trim() ?? "", Baslik = answer.Baslik ?? "", Aciklama = answer.Aciklama ?? "",
            NetlestirmeSorusu = answer.NetlestirmeSorusu ?? "", Varsayimlar = answer.Varsayimlar ?? [], Secenekler = answer.Secenekler ?? [],
        };
        return (answer, raw, sw.ElapsedMilliseconds);
    }

    /// <summary>Verilen JSON şemasına (strict) uyan bir cevap ister ve ham JSON metnini döndürür.</summary>
    public async Task<string> AskJsonAsync(IReadOnlyList<ChatMessage> messages, string schemaName, JsonNode schema, CancellationToken ct, int? maxTokens = null)
    {
        var body = new JsonObject
        {
            ["model"] = options.Model,
            ["messages"] = new JsonArray(messages.Select(m => (JsonNode)new JsonObject { ["role"] = m.Role, ["content"] = m.Content }).ToArray()),
            ["temperature"] = options.Temperature,
            ["max_tokens"] = maxTokens ?? options.MaxTokens,
            ["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject { ["name"] = schemaName, ["strict"] = true, ["schema"] = schema.DeepClone() },
            },
        };
        if (options.LlamaCachePrompt) body["cache_prompt"] = true;
        return await PostAsync(body, ct);
    }

    /// <summary>Sabit sistem prompt'unu modele bir kez okutur; sonraki isteklerde önek KV önbellekten gelir.</summary>
    public async Task WarmUpAsync(string systemPrompt, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = options.Model,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = systemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = "Merhaba" }),
            ["max_tokens"] = 1,
        };
        if (options.LlamaCachePrompt) body["cache_prompt"] = true;
        var sw = Stopwatch.StartNew();
        await PostAsync(body, ct);
        log.LogInformation("LLM ısındı: sistem prompt'u {Ms} ms'de önbelleğe alındı", sw.ElapsedMilliseconds);
    }

    public async Task<bool> IsHealthyAsync(CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{options.BaseUrl.TrimEnd('/')}/models");
            AddAuth(req);
            using var res = await http.SendAsync(req, cts.Token);
            return res.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    async Task<string> PostAsync(JsonObject body, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{options.BaseUrl.TrimEnd('/')}/chat/completions")
        {
            Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
        };
        AddAuth(req);
        using var res = await http.SendAsync(req, cts.Token);
        var text = await res.Content.ReadAsStringAsync(cts.Token);
        if (!res.IsSuccessStatusCode)
            throw new HttpRequestException($"LLM hatası {(int)res.StatusCode}: {text[..Math.Min(text.Length, 300)]}");

        var json = JsonNode.Parse(text)!;
        if (json["timings"] is JsonNode t)
            log.LogInformation("LLM: prompt {P} token ({C} önbellekten) {Pms:F0} ms, üretim {G} token {Gms:F0} ms",
                t["prompt_n"], t["cache_n"], (double?)t["prompt_ms"], t["predicted_n"], (double?)t["predicted_ms"]);
        return json["choices"]?[0]?["message"]?["content"]?.GetValue<string>()
               ?? throw new InvalidOperationException("LLM cevabında içerik yok.");
    }

    void AddAuth(HttpRequestMessage req)
    {
        if (!string.IsNullOrEmpty(options.ApiKey))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
            req.Headers.Add("api-key", options.ApiKey);
        }
    }
}
