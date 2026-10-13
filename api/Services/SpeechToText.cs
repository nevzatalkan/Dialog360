using System.Net.Http.Headers;
using System.Text.Json;

namespace Diyalog360.Api.Services;

public sealed class SpeechOptions
{
    /// <summary>Whisper (yerel whisper.cpp sunucusu) | AzureSpeech | None</summary>
    public string Provider { get; set; } = "Whisper";
    public string Language { get; set; } = "tr";
    public string WhisperUrl { get; set; } = "http://127.0.0.1:18090";
    public string? AzureRegion { get; set; }
    public string? AzureKey { get; set; }
    public int TimeoutSeconds { get; set; } = 60;
}

/// <summary>Konuşmayı yazıya döken sağlayıcı. Girdi her zaman 16 kHz mono PCM WAV'dır (tarayıcı üretir).</summary>
public interface ISpeechToText
{
    string Name { get; }
    Task<string> TranscribeAsync(byte[] wav, CancellationToken ct);
    Task<bool> IsHealthyAsync(CancellationToken ct);
}

public sealed class SpeechUnavailableException(string message) : Exception(message);

/// <summary>
/// Tanımayı kolaylaştıran alan terimleri: veri kataloğundaki gerçek değerler (şube, şehir, sektör, segment, kart tipi...).
/// Whisper'a ön metin olarak verilir; "Alsancak", "KOBİ" doğru yazılır.
/// </summary>
public static class SpeechVocabulary
{
    const int MaxLength = 700; // Whisper ön metni ~224 token ile sınırlı; Türkçe kelimeler çok token tutar

    /// <summary>
    /// Whisper ön metnin yazım tarzını taklit eder: büyük harfli düz bir liste verilirse her kelimeyi büyük harfle yazar.
    /// Bu yüzden değerler "Şube adı: Kadıköy, Alsancak." gibi kısa cümlelerle verilir ve sona doğal bir örnek soru eklenir.
    /// </summary>
    public static string Build(SchemaCatalog catalog)
    {
        var sentences = catalog.Tables
            .SelectMany(t => t.Columns
                .Where(c => c.Values is { Count: > 0 })
                .Select(c => (Label: Label(t.Name, c), Values: c.Values!.Where(v => v.Length > 2 && !v.All(char.IsDigit)).ToList())))
            .Where(g => g.Values.Count > 0)
            .Select(g => $"{g.Label}: {string.Join(", ", g.Values)}.");

        var text = "Bankacılık raporu soruları.";
        foreach (var sentence in sentences)
        {
            if (text.Length + sentence.Length > MaxLength) break;
            text += " " + sentence;
        }
        return text + " Örnek soru: geçen ay sektörlere göre kart harcamaları ve iadeleri ne kadar?";
    }

    /// <summary>Kolon açıklamasının ilk parçası ("Şube adı (semt), örn. ..." → "Şube adı"); yoksa "Tablo Kolon".</summary>
    static string Label(string table, ColumnInfo c) =>
        c.Description?.Split('(', ';', ',')[0].Trim() is { Length: > 0 } d ? d : $"{table} {c.Name}";
}

/// <summary>Yerel whisper.cpp sunucusu (whisper-server). Model ve dil sunucuda değil, istekte belirtilir.</summary>
public sealed class WhisperSpeechToText(HttpClient http, SpeechOptions options, string vocabulary) : ISpeechToText
{
    public string Name => "Whisper (yerel)";

    public async Task<string> TranscribeAsync(byte[] wav, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent
        {
            { new ByteArrayContent(wav) { Headers = { ContentType = new MediaTypeHeaderValue("audio/wav") } }, "file", "ses.wav" },
            { new StringContent("json"), "response_format" },
            { new StringContent(options.Language), "language" },
            { new StringContent("0"), "temperature" },
            { new StringContent(vocabulary), "prompt" },
        };
        HttpResponseMessage res;
        try
        {
            res = await http.PostAsync($"{options.WhisperUrl.TrimEnd('/')}/inference", form, ct);
        }
        catch (HttpRequestException)
        {
            throw new SpeechUnavailableException("Ses tanıma sunucusuna (whisper-server) ulaşılamadı; scripts/start-whisper ile başlatın.");
        }
        using (res)
        {
            var body = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode) throw new SpeechUnavailableException($"Whisper hatası {(int)res.StatusCode}: {body[..Math.Min(body.Length, 200)]}");
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("text", out var t) ? t.GetString()?.Trim() ?? "" : "";
        }
    }

    public async Task<bool> IsHealthyAsync(CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            using var res = await http.GetAsync(options.WhisperUrl, cts.Token);
            return res.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// Azure AI Speech, kısa ses REST API'si (60 sn'ye kadar). Anahtar yalnızca sunucuda kalır.
/// https://learn.microsoft.com/azure/ai-services/speech-service/rest-speech-to-text-short
/// </summary>
public sealed class AzureSpeechToText(HttpClient http, SpeechOptions options) : ISpeechToText
{
    public string Name => "Azure AI Speech";

    string Locale => options.Language.Contains('-') ? options.Language : $"{options.Language}-{options.Language.ToUpperInvariant()}";

    public async Task<string> TranscribeAsync(byte[] wav, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(options.AzureRegion) || string.IsNullOrEmpty(options.AzureKey))
            throw new SpeechUnavailableException("Azure Speech için Speech:AzureRegion ve Speech:AzureKey ayarlanmalı.");
        var url = $"https://{options.AzureRegion}.stt.speech.microsoft.com/speech/recognition/conversation/cognitiveservices/v1?language={Locale}&format=simple";
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(wav) };
        req.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/wav; codecs=audio/pcm; samplerate=16000");
        req.Headers.Add("Ocp-Apim-Subscription-Key", options.AzureKey);
        using var res = await http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw new SpeechUnavailableException($"Azure Speech hatası {(int)res.StatusCode}: {body[..Math.Min(body.Length, 200)]}");
        using var doc = JsonDocument.Parse(body);
        var status = doc.RootElement.GetProperty("RecognitionStatus").GetString();
        return status == "Success" ? doc.RootElement.GetProperty("DisplayText").GetString()?.Trim() ?? "" : "";
    }

    public Task<bool> IsHealthyAsync(CancellationToken ct) =>
        Task.FromResult(!string.IsNullOrEmpty(options.AzureRegion) && !string.IsNullOrEmpty(options.AzureKey));
}

public static class Wav
{
    const int HeaderBytes = 44;
    const int MinAudioBytes = 16000 * 2 / 5; // 16 kHz, 16 bit, en az 0,2 sn

    /// <summary>Gelen dosyanın gerçekten WAV olduğunu kaba biçimde doğrular (RIFF....WAVE başlığı).</summary>
    public static bool LooksValid(byte[] data) =>
        data.Length >= HeaderBytes && data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F'
        && data[8] == 'W' && data[9] == 'A' && data[10] == 'V' && data[11] == 'E';

    /// <summary>Başlık var ama ses verisi yok/çok kısa: mikrofondan ses gelmemiş demektir.</summary>
    public static bool HasAudio(byte[] data) => data.Length >= HeaderBytes + MinAudioBytes;
}

/// <summary>Speech:Provider = None: mikrofon butonu pasif görünür.</summary>
public sealed class DisabledSpeechToText : ISpeechToText
{
    public string Name => "Kapalı";
    public Task<string> TranscribeAsync(byte[] wav, CancellationToken ct) =>
        throw new SpeechUnavailableException("Sesle soru bu kurulumda kapalı (Speech:Provider = None).");
    public Task<bool> IsHealthyAsync(CancellationToken ct) => Task.FromResult(false);
}
