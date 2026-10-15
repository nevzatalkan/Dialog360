using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Diyalog360.Api.Services;

/// <summary>
/// eval/questions.json'daki her soruyu sıfır önbellekle çalıştırır ve sonucu beklenen SQL'in sonucuyla karşılaştırır.
/// Karşılaştırma kolon adı ve sırasından bağımsızdır: satır sayısı eşit olmalı ve beklenen her satırın değerleri
/// üretilen bir satırda bulunmalıdır (üretilen sorgu fazladan kolon içerebilir).
/// </summary>
public sealed class EvalRunner(ChatOrchestrator orchestrator, QueryRunner runner)
{
    public sealed record Item(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("soru")] string Soru,
        [property: JsonPropertyName("beklenen_tur")] string BeklenenTur,
        [property: JsonPropertyName("beklenen_sql")] string? BeklenenSql,
        [property: JsonPropertyName("onceki")] List<string>? Onceki);

    public async Task<int> RunAsync(string path, string? onlyId, CancellationToken ct)
    {
        var items = JsonSerializer.Deserialize<List<Item>>(File.ReadAllText(path))!;
        if (onlyId != null) items = items.Where(i => i.Id == onlyId).ToList();
        int pass = 0;
        var results = new List<object>();

        foreach (var item in items)
        {
            var chatId = $"eval-{item.Id}-{DateTime.Now:HHmmss}";
            foreach (var prev in item.Onceki ?? []) await orchestrator.HandleAsync(chatId, prev, ct, useCache: false);
            var res = await orchestrator.HandleAsync(chatId, item.Soru, ct, useCache: false);

            var (ok, reason) = Check(item, res);
            if (ok) pass++;
            Console.WriteLine($"{(ok ? "✅" : "❌")} {item.Id,-4} {res.Meta.TotalMs,6} ms  deneme:{res.Meta.Attempts}  {item.Soru}{(ok ? "" : "\n      → " + reason)}");
            results.Add(new { id = item.Id, soru = item.Soru, ok, reason, kind = res.Kind, sql = res.Report?.Sql, attempts = res.Meta.Attempts, totalMs = res.Meta.TotalMs });
        }

        Console.WriteLine($"\nDoğruluk: {pass}/{items.Count} (%{(items.Count == 0 ? 0 : pass * 100.0 / items.Count):F0})");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(path)!, "results.json"),
            JsonSerializer.Serialize(new { tarih = DateTime.Now, dogru = pass, toplam = items.Count, sonuclar = results },
                new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        return pass == items.Count ? 0 : 1;
    }

    (bool Ok, string Reason) Check(Item item, ChatResponse res)
    {
        var expectedKind = item.BeklenenTur switch { "rapor" => "report", "netlestirme" => "clarify", _ => "blocked" };
        if (res.Kind != expectedKind) return (false, $"beklenen tür {expectedKind}, gelen {res.Kind}: {res.Text}");
        if (expectedKind != "report") return (true, "");

        var gold = runner.Run(item.BeklenenSql!);
        if (gold.Status != GuardStatus.Ok) return (false, "beklenen SQL çalışmadı: " + gold.Error);
        var expected = gold.Result!.Rows;
        var actual = res.Report!.Rows;
        if (expected.Count != actual.Count) return (false, $"satır sayısı farklı: beklenen {expected.Count}, gelen {actual.Count}\n      SQL: {res.Report.Sql}");

        var remaining = actual.Select(Normalize).ToList();
        foreach (var row in expected.Select(Normalize))
        {
            int match = remaining.FindIndex(r => IsSubset(row, r));
            if (match < 0) return (false, $"beklenen satır bulunamadı: [{string.Join(", ", row)}]\n      SQL: {res.Report.Sql}");
            remaining.RemoveAt(match);
        }
        return (true, "");
    }

    static List<string> Normalize(object?[] row) => row.Select(v => v switch
    {
        null => "∅",
        double d => Math.Round(d, 1).ToString(CultureInfo.InvariantCulture),
        long l => ((double)l).ToString(CultureInfo.InvariantCulture),
        _ => Convert.ToString(v, CultureInfo.InvariantCulture)!.Trim(),
    }).ToList();

    static bool IsSubset(List<string> expected, List<string> actual)
    {
        var pool = actual.ToList();
        foreach (var v in expected)
        {
            int i = pool.IndexOf(v);
            if (i < 0) return false;
            pool.RemoveAt(i);
        }
        return true;
    }
}
