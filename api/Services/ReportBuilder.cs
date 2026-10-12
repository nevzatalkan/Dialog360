using System.Globalization;
using System.Text.RegularExpressions;

namespace Diyalog360.Api.Services;

/// <param name="Type">kpi | bar | line | multiline | table</param>
public record ChartSpec(string Type, string? X, IReadOnlyList<string> Y, string? Series);

/// <summary>Sonucun şekline bakarak grafik tipini seçer ve sayıları kodda hesaplayıp Türkçe özet yazar (LLM kullanılmaz).</summary>
public static class ReportBuilder
{
    static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");
    static readonly Regex IdLike = new(@"(^|\s)(no|id|numara|numarası|kod|kodu|yıl|yil)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex Money = new(@"tutar|harcama|bakiye|mevduat|ciro|limit|sepet|gelir", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex Percent = new(@"oran|yüzde|pay", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex NonAdditive = new(@"ortalama|ort\.|oran|yüzde|pay|medyan|en yüksek|en düşük|maks|min", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>"Müşteri No", "Yıl" gibi sayısal ama etiket olan kolonları metin sayar.</summary>
    public static IReadOnlyList<QueryColumn> Classify(IReadOnlyList<QueryColumn> columns) =>
        columns.Select(c => c.Kind == "number" && IdLike.IsMatch(c.Name.Trim()) ? c with { Kind = "text" } : c).ToList();

    public static ChartSpec PickChart(IReadOnlyList<QueryColumn> cols, List<object?[]> rows)
    {
        var nums = cols.Where(c => c.Kind == "number").Select(c => c.Name).ToList();
        var dates = cols.Where(c => c.Kind == "date").Select(c => c.Name).ToList();
        var texts = cols.Where(c => c.Kind == "text").Select(c => c.Name).ToList();
        var table = new ChartSpec("table", null, [], null);
        if (rows.Count == 0 || nums.Count == 0) return table;

        if (rows.Count == 1 && nums.Count <= 4) return new ChartSpec("kpi", null, nums, null);
        if (dates.Count == 1 && texts.Count == 0) return new ChartSpec("line", dates[0], nums.Take(3).ToList(), null);
        if (dates.Count == 1 && texts.Count == 1)
        {
            int seriesCount = rows.Select(r => Str(r[Index(cols, texts[0])])).Distinct().Count();
            return seriesCount <= 12 ? new ChartSpec("multiline", dates[0], [nums[0]], texts[0]) : table;
        }
        if (dates.Count == 0 && texts.Count >= 1 && rows.Count <= 30)
        {
            int x = Index(cols, texts[0]);
            bool unique = rows.Select(r => Str(r[x])).Distinct().Count() == rows.Count;
            if (unique) return new ChartSpec("bar", texts[0], nums, null);
        }
        return table;
    }

    public static string Summarize(IReadOnlyList<QueryColumn> cols, List<object?[]> rows, ChartSpec chart, bool truncated)
    {
        if (rows.Count == 0) return "Sorgu sonuç döndürmedi; seçilen dönemde veya filtrelerde kayıt bulunamadı.";
        var s = chart.Type switch
        {
            "kpi" => string.Join("; ", chart.Y.Select(y => $"{y}: {Fmt(Num(rows[0][Index(cols, y)]), y)}")) + ".",
            "bar" => SummarizeBar(cols, rows, chart),
            "line" => SummarizeLine(cols, rows, chart),
            "multiline" => SummarizeMulti(cols, rows, chart),
            _ => $"{rows.Count.ToString("N0", Tr)} satır listelendi.",
        };
        return truncated ? s + $" (Sonuç çok uzun olduğu için ilk {rows.Count.ToString("N0", Tr)} satır gösteriliyor.)" : s;
    }

    static string SummarizeBar(IReadOnlyList<QueryColumn> cols, List<object?[]> rows, ChartSpec chart)
    {
        string y = chart.Y[0];
        int xi = Index(cols, chart.X!), yi = Index(cols, y);
        string prefix = IdLike.IsMatch(chart.X!.Trim()) ? chart.X + " " : "";
        var items = rows.Select(r => (Label: prefix + Str(r[xi]), Value: Num(r[yi]))).OrderByDescending(p => p.Value).ToList();
        var top = items[0];
        var bottom = items[^1];
        var parts = new List<string> { $"En yüksek {y.ToLower(Tr)}: {top.Label} ({Fmt(top.Value, y)})" };
        if (items.Count > 1) parts.Add($"en düşük: {bottom.Label} ({Fmt(bottom.Value, y)})");
        var sentence = string.Join(", ", parts) + ".";

        double total = items.Sum(p => p.Value);
        if (!NonAdditive.IsMatch(y) && items.Count > 1 && items.All(p => p.Value >= 0) && total > 0)
            sentence += $" Toplam {Fmt(total, y)}; {top.Label} payı %{(top.Value / total * 100).ToString("N1", Tr)}.";
        return sentence;
    }

    static string SummarizeLine(IReadOnlyList<QueryColumn> cols, List<object?[]> rows, ChartSpec chart)
    {
        string y = chart.Y[0];
        int xi = Index(cols, chart.X!), yi = Index(cols, y);
        var points = rows.Select(r => (X: Str(r[xi]), V: Num(r[yi]))).OrderBy(p => p.X, StringComparer.Ordinal).ToList();
        if (points.Count < 2) return $"{points[0].X}: {Fmt(points[0].V, y)}.";
        var first = points[0];
        var last = points[^1];
        var peak = points.MaxBy(p => p.V);
        return $"{y}: {first.X} döneminde {Fmt(first.V, y)}, {last.X} döneminde {Fmt(last.V, y)}{Change(first.V, last.V)}. " +
               $"En yüksek dönem {peak.X} ({Fmt(peak.V, y)}).";
    }

    static string SummarizeMulti(IReadOnlyList<QueryColumn> cols, List<object?[]> rows, ChartSpec chart)
    {
        string y = chart.Y[0];
        int xi = Index(cols, chart.X!), si = Index(cols, chart.Series!), yi = Index(cols, y);
        var periods = rows.Select(r => Str(r[xi])).Distinct().Order(StringComparer.Ordinal).ToList();
        var series = rows.GroupBy(r => Str(r[si]))
            .Select(g => (Name: g.Key, Total: g.Sum(r => Num(r[yi])),
                          First: g.Where(r => Str(r[xi]) == periods[0]).Sum(r => Num(r[yi])),
                          Last: g.Where(r => Str(r[xi]) == periods[^1]).Sum(r => Num(r[yi]))))
            .OrderByDescending(x => x.Total).ToList();

        var text = $"{periods[0]} – {periods[^1]} arasında {TotalOf(y)} en yüksek {series[0].Name} ({Fmt(series[0].Total, y)})";
        if (series.Count > 1) text += $", en düşük {series[^1].Name} ({Fmt(series[^1].Total, y)})";
        text += ".";

        var trends = series.Where(x => x.First > 0 && periods.Count > 1).Select(x => (x.Name, Pct: (x.Last - x.First) / x.First * 100)).ToList();
        if (trends.Count > 1)
        {
            var up = trends.MaxBy(t => t.Pct);
            var down = trends.MinBy(t => t.Pct);
            text += $" İlk ve son dönem karşılaştırıldığında en çok artan {up.Name} (%{up.Pct.ToString("N1", Tr)})";
            text += down.Pct < 0 ? $", en çok düşen {down.Name} (%{Math.Abs(down.Pct).ToString("N1", Tr)})." : ".";
        }
        return text;
    }

    static string TotalOf(string y)
    {
        var lower = y.ToLower(Tr);
        return lower.StartsWith("toplam") ? lower : "toplam " + lower;
    }

    static string Change(double from, double to)
    {
        if (from == 0) return "";
        double pct = (to - from) / Math.Abs(from) * 100;
        return Math.Abs(pct) < 0.05 ? " (değişim yok)" : $" (%{Math.Abs(pct).ToString("N1", Tr)} {(pct > 0 ? "artış" : "düşüş")})";
    }

    static string Fmt(double v, string column)
    {
        if (Percent.IsMatch(column)) return "%" + v.ToString("N1", Tr);
        bool integral = Math.Abs(v - Math.Round(v)) < 1e-9;
        var s = v.ToString(integral || Math.Abs(v) >= 1000 ? "N0" : "N2", Tr);
        return Money.IsMatch(column) ? s + " TL" : s;
    }

    static int Index(IReadOnlyList<QueryColumn> cols, string name) => cols.ToList().FindIndex(c => c.Name == name);
    static string Str(object? v) => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "(boş)";
    static double Num(object? v) => v switch { long l => l, double d => d, int i => i, _ => 0 };
}
