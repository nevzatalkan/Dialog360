using System.Text;
using System.Text.Json;

namespace Diyalog360.Api.Services;

/// <summary>
/// Sistem prompt'u = kurallar + şema + iş sözlüğü + örnekler. Açılışta bir kez kurulur ve hiç değişmez;
/// böylece llama-server bu öneki KV önbellekte tutar ve her istekte yalnızca soru ile geçmiş işlenir.
/// </summary>
public sealed class PromptBuilder
{
    const int HistoryMessages = 6;

    public string SystemPrompt { get; }

    /// <summary>Sistem prompt'unun parmak izi: prompt dosyaları değişince önbellekteki eski cevaplar kullanılmaz.</summary>
    public string Version { get; }

    public PromptBuilder(string promptDir, SchemaCatalog catalog)
    {
        string Read(string file) => File.ReadAllText(Path.Combine(promptDir, file)).Trim();
        SystemPrompt = $"""
            {Read("system.md")}

            ## Veritabanı şeması
            {catalog.PromptText.Trim()}

            ## İş sözlüğü
            {Read("glossary.md")}

            ## Örnekler (soru → beklenen JSON)
            {Read("examples.md")}
            """;
        Version = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(SystemPrompt)))[..16];
    }

    public static string UserMessage(string historyText, string message) =>
        $"## Sohbet geçmişi\n{(historyText == "" ? "(yok, bu sohbetin ilk mesajı)" : historyText)}\n\n## Yeni mesaj\n{message}";

    /// <summary>Son birkaç mesajı prompt'a uygun metne çevirir; "anchor" son SQL ya da son netleştirme sorusudur (önbellek anahtarı için).</summary>
    public static (string Text, string Anchor) History(IReadOnlyList<StoredMessage> messages)
    {
        var sb = new StringBuilder();
        string anchor = "";
        foreach (var m in messages.TakeLast(HistoryMessages))
        {
            if (m.Role == "user")
            {
                sb.AppendLine(m.Content.StartsWith("[Elle düzenlenen SQL]") ? "Kullanıcı SQL panelinde sorguyu elle düzenledi." : $"Kullanıcı: {m.Content}");
                continue;
            }
            var r = JsonSerializer.Deserialize<ChatResponse>(m.Content, ChatOrchestrator.Json);
            switch (r?.Kind)
            {
                case "report":
                    sb.AppendLine($"Asistan rapor üretti. SQL: {r.Report!.Sql}");
                    anchor = r.Report.Sql;
                    break;
                case "clarify":
                    sb.AppendLine($"Asistan netleştirme sordu: {r.Clarify!.Question} Seçenekler: {string.Join(" | ", r.Clarify.Options)}");
                    anchor = r.Clarify.Question;
                    break;
                default:
                    sb.AppendLine("Asistan: (istek cevaplanamadı veya engellendi)");
                    break;
            }
        }
        return (sb.ToString().TrimEnd(), anchor);
    }
}
