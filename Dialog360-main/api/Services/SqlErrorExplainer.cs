using System.Text.RegularExpressions;

namespace Diyalog360.Api.Services;

/// <summary>
/// SQLite'ın kısa hata mesajlarını ("no such column: k.AcilisTarihi") şemaya ve sorgudaki alias'lara bakarak
/// modelin düzeltebileceği somut bir talimata çevirir. Model aynı hatalı SQL'i tekrar göndermesin diye
/// hatanın gerçek nedenini (tanımsız alias, CTE'de olmayan kolon, yanlış tablo) söyler.
/// </summary>
public static partial class SqlErrorExplainer
{
    [GeneratedRegex(@"no such column:\s*(?:(\w+)\.)?(\w+)", RegexOptions.IgnoreCase)]
    private static partial Regex NoSuchColumn();

    [GeneratedRegex(@"no such table:\s*(?:\w+\.)?(\w+)", RegexOptions.IgnoreCase)]
    private static partial Regex NoSuchTable();

    [GeneratedRegex(@"(?:\bWITH\s+(?:RECURSIVE\s+)?|,\s*)(\w+)\s+AS\s*\(", RegexOptions.IgnoreCase)]
    private static partial Regex CteName();

    [GeneratedRegex(@"\b(?:FROM|JOIN)\s+(\w+)(?:\s+(?:AS\s+)?(?!ON\b|WHERE\b|JOIN\b|LEFT\b|RIGHT\b|INNER\b|OUTER\b|CROSS\b|GROUP\b|ORDER\b|LIMIT\b|USING\b|UNION\b|HAVING\b)(\w+))?", RegexOptions.IgnoreCase)]
    private static partial Regex TableRef();

    static readonly Dictionary<string, string> StandardAlias = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Islem"] = "i", ["Kart"] = "k", ["Musteri"] = "m", ["Sube"] = "sb", ["Sektor"] = "s",
        ["Hesap"] = "h", ["Urun"] = "u", ["MusteriUrun"] = "mu", ["Kampanya"] = "ka",
    };

    public static string Explain(string error, string sql, SchemaCatalog catalog)
    {
        var ctes = CteName().Matches(sql).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Aynı alias sorgunun farklı yerlerinde farklı kaynakları gösterebilir (örn. "FROM Kart k" ve "FROM kartlar k")
        var aliases = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in TableRef().Matches(sql))
        {
            var source = m.Groups[1].Value;
            var alias = m.Groups[2].Success ? m.Groups[2].Value : source;
            if (!aliases.TryGetValue(alias, out var list)) aliases[alias] = list = [];
            if (!list.Contains(source, StringComparer.OrdinalIgnoreCase)) list.Add(source);
        }

        var col = NoSuchColumn().Match(error);
        if (col.Success)
        {
            var qualifier = col.Groups[1].Success ? col.Groups[1].Value : null;
            var column = col.Groups[2].Value;
            var owners = catalog.Tables.Where(t => t.Columns.Any(c => c.Name.Equals(column, StringComparison.OrdinalIgnoreCase))).Select(t => t.Name).ToList();
            var where = owners.Count > 0 ? $"'{column}' kolonu şu tablo(lar)da var: {string.Join(", ", owners)}." : NearestHint(column, catalog);

            if (qualifier == null)
                return $"{error}\nAçıklama: '{column}' bu sorgu kapsamında tanımlı değil. {where} Kolonu her zaman tablo alias'ıyla yaz (örn. k.{column}) ve o tabloyu FROM/JOIN'e ekle.";

            if (!aliases.TryGetValue(qualifier, out var sources))
                return $"{error}\nAçıklama: '{qualifier}' alias'ı sorgunun FROM/JOIN kısmında tanımlı değil. {where} " +
                       $"{JoinHint(owners, qualifier, aliases, catalog)} SELECT, WHERE, GROUP BY ve ORDER BY'da kullandığın her alias'ın FROM/JOIN'de tanımlı olduğunu kontrol et.";

            var cte = sources.FirstOrDefault(ctes.Contains);
            if (cte != null)
            {
                var reused = sources.Where(x => !ctes.Contains(x)).ToList();
                var reuseNote = reused.Count > 0
                    ? $"Aynı '{qualifier}' alias'ını hem {string.Join(", ", reused)} tablosu hem de '{cte}' CTE'si için kullanmışsın; '{cte}' CTE'sini okuyan kısımda '{qualifier}.{column}' bulunamıyor. "
                    : "";
                return $"{error}\nAçıklama: {reuseNote}'{qualifier}' alias'ı '{cte}' adlı CTE'yi gösteriyor ve bu CTE'nin SELECT listesinde '{column}' kolonu yok. {where} " +
                       $"Çözüm: '{column}'u '{cte}' CTE'sinin SELECT listesine ekle (örn. ... {column} AS {column}) ve CTE'ye tablo alias'larından (i, k, m, sb, s, h) farklı bir ad ver (örn. FROM {cte} t).";
            }

            var source = sources[0];
            var table = catalog.Tables.FirstOrDefault(t => t.Name.Equals(source, StringComparison.OrdinalIgnoreCase));
            if (table != null)
                return $"{error}\nAçıklama: '{qualifier}' alias'ı {table.Name} tablosunu gösteriyor ama {table.Name} tablosunda '{column}' kolonu yok. " +
                       $"{table.Name} kolonları: {string.Join(", ", table.Columns.Select(c => c.Name))}. {where}";

            return $"{error}\nAçıklama: {where}";
        }

        var tbl = NoSuchTable().Match(error);
        if (tbl.Success)
            return $"{error}\nAçıklama: '{tbl.Groups[1].Value}' diye bir tablo yok. Kullanılabilir tablolar: {string.Join(", ", catalog.Tables.Select(t => t.Name))}. " +
                   "CTE kullanıyorsan CTE'yi kullanmadan önce WITH içinde tanımladığından emin ol.";

        return error;
    }

    /// <summary>Eksik tabloyu, sorguda zaten bulunan bir tabloya FK üzerinden bağlayan JOIN önerisi.</summary>
    static string JoinHint(List<string> owners, string wantedAlias, Dictionary<string, List<string>> aliases, SchemaCatalog catalog)
    {
        if (owners.Count != 1) return "";
        var target = catalog.Tables.First(t => t.Name == owners[0]);
        foreach (var (alias, sources) in aliases)
        foreach (var source in sources)
        {
            var present = catalog.Tables.FirstOrDefault(t => t.Name.Equals(source, StringComparison.OrdinalIgnoreCase));
            if (present == null) continue;
            var fk = present.Columns.FirstOrDefault(c => c.FkTable == target.Name);
            if (fk != null) return $"Eksik JOIN'i ekle: JOIN {target.Name} {wantedAlias} ON {alias}.{fk.Name} = {wantedAlias}.{fk.FkColumn}.";
            var back = target.Columns.FirstOrDefault(c => c.FkTable == present.Name);
            if (back != null) return $"Eksik JOIN'i ekle: JOIN {target.Name} {wantedAlias} ON {wantedAlias}.{back.Name} = {alias}.{back.FkColumn}.";
        }
        var std = StandardAlias.GetValueOrDefault(target.Name, wantedAlias);
        return $"{target.Name} tablosunu ilişkiler bölümündeki koşulla JOIN et (önerilen alias: {std}).";
    }

    static string NearestHint(string column, SchemaCatalog catalog)
    {
        var best = catalog.Tables.SelectMany(t => t.Columns.Select(c => (Table: t.Name, Col: c.Name)))
            .Select(x => (x.Table, x.Col, Dist: Levenshtein(x.Col.ToLowerInvariant(), column.ToLowerInvariant())))
            .OrderBy(x => x.Dist).Take(3).ToList();
        return $"Şemada '{column}' adında bir kolon yok. En yakın kolonlar: {string.Join(", ", best.Select(b => $"{b.Table}.{b.Col}"))}.";
    }

    static int Levenshtein(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }
}
