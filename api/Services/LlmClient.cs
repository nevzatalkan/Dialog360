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
    /// <summary>Sağlık kontrolü için GET atılacak adres; boşsa {BaseUrl}/models (GitHub Models'te bu uç yok).</summary>
    public string? HealthUrl { get; set; }
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
        var body = new JsonObject
        {
            ["model"] = options.Model,
            ["messages"] = new JsonArray(messages.Select(m => (JsonNode)new JsonObject { ["role"] = m.Role, ["content"] = m.Content }).ToArray()),
            ["temperature"] = options.Temperature,
            ["max_tokens"] = options.MaxTokens,
            ["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject { ["name"] = "diyalog360_cevap", ["strict"] = true, ["schema"] = AnswerSchema.DeepClone() },
            },
        };
        if (options.LlamaCachePrompt) body["cache_prompt"] = true;

        var sw = Stopwatch.StartNew();
        var raw = await PostAsync(body, ct);
        var answer = JsonSerializer.Deserialize<LlmAnswer>(raw)
                     ?? throw new InvalidOperationException("LLM boş cevap döndürdü.");
        answer = answer with
        {
            Sql = answer.Sql?.Trim() ?? "", Baslik = answer.Baslik ?? "", Aciklama = answer.Aciklama ?? "",
            NetlestirmeSorusu = answer.NetlestirmeSorusu ?? "", Varsayimlar = answer.Varsayimlar ?? [], Secenekler = answer.Secenekler ?? [],
        };
        return (answer, raw, sw.ElapsedMilliseconds);
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
            using var req = new HttpRequestMessage(HttpMethod.Get,
                string.IsNullOrEmpty(options.HealthUrl) ? $"{options.BaseUrl.TrimEnd('/')}/models" : options.HealthUrl);
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
        var payload = body.ToJsonString();
        HttpResponseMessage res = await http.SendAsync(req, cts.Token);
        // Bulut servislerinde dakikalık token sınırı (429): servisin önerdiği kadar bekleyip en fazla 2 kez yeniden dene
        for (int retry = 0; res.StatusCode == System.Net.HttpStatusCode.TooManyRequests && retry < 2; retry++)
        {
            var wait = res.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5 * (retry + 1));
            if (wait > TimeSpan.FromSeconds(20)) break;
            log.LogWarning("LLM hız sınırı (429); {Seconds} sn sonra yeniden denenecek", wait.TotalSeconds);
            res.Dispose();
            await Task.Delay(wait, cts.Token);
            using var again = new HttpRequestMessage(HttpMethod.Post, req.RequestUri)
            {
                Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json"),
            };
            AddAuth(again);
            res = await http.SendAsync(again, cts.Token);
        }
        using var _ = res;
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
        if (string.IsNullOrEmpty(options.ApiKey)) return;
        // Azure OpenAI / Foundry anahtarı "api-key" başlığıyla ister; diğer OpenAI uyumlu servisler Bearer
        if (req.RequestUri?.Host.EndsWith(".azure.com", StringComparison.OrdinalIgnoreCase) == true)
            req.Headers.Add("api-key", options.ApiKey);
        else
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
    }
}
