using System.Text.RegularExpressions;

namespace Diyalog360.Api.Services;

/// <summary>
/// Anlamı kesin olan küçük kural ihlallerini LLM'e geri göndermeden deterministik olarak düzeltir; her düzeltme bir LLM turu (10-20 sn) kazandırır.
/// Yapılan düzeltme rapordaki varsayımlar listesine yazılır, kullanıcı ne değiştiğini görür.
/// </summary>
public static partial class SqlAutoFix
{
    // X >= date('now','localtime','-6 months')  →  X >= date('now','localtime','start of month','-6 months') AND X < date('now','localtime','start of month')
    [GeneratedRegex(@"(?<col>\b\w+\.\w+|\b\w+)\s*>=\s*(?:date|datetime)\(\s*'now'\s*(?:,\s*'localtime'\s*)?,\s*'-\s*(?<n>\d+)\s*months?'\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex RollingMonthLowerBound();

    // i.IslemTarihi <= date(...): saatli metin ('2026-05-10 14:22') tarihle ('2026-05-10') kıyaslanınca son gün dışarıda kalır
    [GeneratedRegex(@"(?<!date\()(?<col>\b\w+\.IslemTarihi|\bIslemTarihi)\s*<=\s*(?=date\()", RegexOptions.IgnoreCase)]
    private static partial Regex DateTimeLessOrEqualDate();

    public static (string Sql, IReadOnlyList<string> Notes) Apply(string sql)
    {
        var notes = new List<string>();
        sql = DateTimeLessOrEqualDate().Replace(sql, m =>
        {
            const string note = "Bitiş günü dahil edildi: saatli işlem tarihi gün olarak karşılaştırıldı (otomatik düzeltme)";
            if (!notes.Contains(note)) notes.Add(note);
            return $"date({m.Groups["col"].Value}) <= ";
        });
        var fixedSql = RollingMonthLowerBound().Replace(sql, m =>
        {
            var col = m.Groups["col"].Value;
            var n = m.Groups["n"].Value;
            var note = $"\"Son {n} ay\" tamamlanmış son {n} takvim ayı olarak uygulandı (otomatik düzeltme)";
            if (!notes.Contains(note)) notes.Add(note);
            return $"{col} >= date('now','localtime','start of month','-{n} months') AND {col} < date('now','localtime','start of month')";
        });
        return (fixedSql, notes);
    }
}
