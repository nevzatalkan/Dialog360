using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Azure.Core;
using Azure.Identity;

namespace Diyalog360.Api.Services;

public sealed class LlmOptions
{
    public string Provider { get; set; } = "OpenAICompatible";
    /// <summary>OpenAI uyumlu uç (Ollama, llama-server vb.).</summary>
    public string BaseUrl { get; set; } = "http://127.0.0.1:18080/v1";
    public string Model { get; set; } = "qwen";
    public string? ApiKey { get; set; }
    public string? AgentEndpoint { get; set; }
    public bool UseManagedIdentity { get; set; }
    public double Temperature { get; set; } = 0;
    public int MaxTokens { get; set; } = 900;
    public int TimeoutSeconds { get; set; } = 240;
    /// <summary>llama-server'a sabit prompt önekini KV önbellekte tutmasını söyler.</summary>
    public bool LlamaCachePrompt { get; set; } = true;
    public bool UsesFoundryAgent => string.Equals(Provider, "FoundryAgent", StringComparison.OrdinalIgnoreCase);
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

public sealed class LlmClient(HttpClient http, LlmOptions options, TokenCredential credential, ILogger<LlmClient> log)
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
    public string Provider => options.UsesFoundryAgent ? "foundry-agent" : "openai-compatible";
    public string FailureHint => options.UsesFoundryAgent
        ? "Foundry ajanı şu anda cevap vermiyor. Foundry Responses uç noktasını, model dağıtımını ve API'nin Foundry Agent Consumer yetkisini kontrol edin."
        : "Dil modeli şu anda cevap vermiyor. Ollama veya yapılandırılmış OpenAI uyumlu model sunucusunun çalıştığını kontrol edin.";

    public async Task<(LlmAnswer Answer, string Raw, long Ms)> AskAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var raw = options.UsesFoundryAgent
            ? await PostFoundryAsync(messages, ct)
            : await PostAsync(BuildChatCompletionsBody(messages), ct);
        var answer = JsonSerializer.Deserialize<LlmAnswer>(raw)
                     ?? throw new InvalidOperationException("LLM boş cevap döndürdü.");
        answer = answer with
        {
            Sql = answer.Sql?.Trim() ?? "", Baslik = answer.Baslik ?? "", Aciklama = answer.Aciklama ?? "",
            NetlestirmeSorusu = answer.NetlestirmeSorusu ?? "", Varsayimlar = answer.Varsayimlar ?? [], Secenekler = answer.Secenekler ?? [],
        };
        return (answer, raw, sw.ElapsedMilliseconds);
    }

    JsonObject BuildChatCompletionsBody(IReadOnlyList<ChatMessage> messages)
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
        return body;
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
            if (options.UsesFoundryAgent)
            {
                if (!Uri.TryCreate(options.AgentEndpoint, UriKind.Absolute, out _)) return false;
                var token = await credential.GetTokenAsync(new TokenRequestContext(["https://ai.azure.com/.default"]), cts.Token);
                return !string.IsNullOrWhiteSpace(token.Token);
            }

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

    async Task<string> PostFoundryAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct)
    {
        if (!Uri.TryCreate(options.AgentEndpoint, UriKind.Absolute, out var endpoint))
            throw new InvalidOperationException("Llm:AgentEndpoint must be an absolute Foundry Responses URL.");

        var body = new JsonObject
        {
            ["input"] = new JsonArray(messages.Select(m => (JsonNode)new JsonObject
            {
                ["role"] = m.Role,
                ["content"] = m.Content,
            }).ToArray()),
            ["stream"] = false,
            ["max_output_tokens"] = options.MaxTokens,
            ["text"] = new JsonObject
            {
                ["format"] = new JsonObject
                {
                    ["type"] = "json_schema",
                    ["name"] = "diyalog360_cevap",
                    ["strict"] = true,
                    ["schema"] = AnswerSchema.DeepClone(),
                },
            },
        };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
        };
        var token = await credential.GetTokenAsync(new TokenRequestContext(["https://ai.azure.com/.default"]), cts.Token);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

        using var res = await http.SendAsync(req, cts.Token);
        var text = await res.Content.ReadAsStringAsync(cts.Token);
        if (!res.IsSuccessStatusCode)
            throw new HttpRequestException($"Foundry agent hatası {(int)res.StatusCode}: {text[..Math.Min(text.Length, 300)]}");

        var json = JsonNode.Parse(text) as JsonObject
                   ?? throw new InvalidOperationException("Foundry Responses cevabı geçerli bir JSON nesnesi değil.");
        if (json["output_text"]?.GetValue<string>() is { } outputText)
            return outputText;

        if (json["output"] is JsonArray output)
        {
            var textParts = output
                .OfType<JsonObject>()
                .Where(item => item["type"]?.GetValue<string>() == "message")
                .SelectMany(item => item["content"] is JsonArray content ? content : [])
                .Where(item => item?["type"]?.GetValue<string>() == "output_text")
                .Select(item => item?["text"]?.GetValue<string>())
                .Where(part => !string.IsNullOrEmpty(part));
            var combined = string.Concat(textParts);
            if (combined.Length > 0) return combined;
        }

        throw new InvalidOperationException("Foundry Responses cevabında metin içeriği yok.");
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
