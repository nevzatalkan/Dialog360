using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Diyalog360.Api.Services;

/// <summary>Detaya inme / farklı kırılım / üst seviyeye çıkma hedefi.</summary>
/// <param name="Kind">down (aynı hiyerarşide alt seviye) | across (başka bir boyut) | up (üst seviye)</param>
public record DrillTarget(string Key, string Label, string Kind);

/// <summary>Rapordaki tıklanabilir bir kolon ve gidilebilecek seviyeler.</summary>
public record DrillColumn(string Column, string Level, string LevelLabel, IReadOnlyList<DrillTarget> Targets, DrillTarget? Up);

/// <summary>Detay yolu (breadcrumb) adımı. MessageId dolu ise o rapora geri dönülebilir; son adım (mevcut rapor) boştur.</summary>
public record Crumb(string Label, long? MessageId);

/// <summary>Bir seviyenin tanımı. Template içindeki {a}, sorgudaki tablo alias'ıyla değiştirilir.</summary>
public sealed record DrillLevel(string Key, string? Hierarchy, int Depth, string Table, string Template, string Alias,
    string Label, string Dative, bool Numeric, string[] Names, string? AltPattern = null)
{
    public string Expr(string alias) => Template.Replace("{a}", alias);
    public string ColumnName => Alias.Trim('[', ']');

    /// <summary>Bu seviyenin ifadesini (herhangi bir alias'la) yakalayan desen; grup 'a' alias'tır.</summary>
    public Regex Pattern { get; } = new(
        "^" + (AltPattern != null ? $"(?:{AltPattern}|{Escape(Template)})" : Escape(Template)) + "$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static string Escape(string template)
    {
        var parts = template.Split("{a}");
        var sb = new StringBuilder();
        for (int i = 0; i < parts.Length; i++)
        {
            if (i > 0) sb.Append(i == 1 ? @"(?<a>\w+)" : @"\k<a>");
            sb.Append(Regex.Replace(Regex.Escape(parts[i]), @"(\\ )+|\s+", @"\s*").Replace(@",", @"\s*,\s*"));
        }
        return sb.ToString();
    }
}

/// <summary>
/// Drill down / drill up. Rapordaki bir değere tıklanınca önceki SQL'e o değerin filtresi eklenir ve kırılım kolonu
/// bir alt seviyeyle (Bölge → Şehir → Şube → Müşteri, Yıl → Ay → Gün) ya da başka bir boyutla değiştirilir.
/// Basit sorgularda (tek SELECT + GROUP BY) bu yeniden yazım LLM'siz ve deterministik yapılır; karmaşık sorgular
/// (CTE, pencere fonksiyonu, türetilmiş tablo) için çağıran taraf LLM'e takip mesajı gönderir.
/// </summary>
public static partial class DrillPlanner
{
    public static readonly IReadOnlyList<DrillLevel> Levels =
    [
        new("bolge", "cografya", 0, "Sube", "{a}.Bolge", "[Bölge]", "Bölge", "Bölgelere", false, ["bolge"]),
        new("sehir", "cografya", 1, "Sube", "{a}.Sehir", "[Şehir]", "Şehir", "Şehirlere", false, ["sehir", "il"]),
        new("sube", "cografya", 2, "Sube", "{a}.Ad", "[Şube]", "Şube", "Şubelere", false, ["sube", "sube adi"]),
        new("musteri", "cografya", 3, "Musteri", "{a}.Id", "[Müşteri No]", "Müşteri", "Müşterilere", true, ["musteri no", "musteri"]),
        new("yil", "zaman", 0, "Islem", "strftime('%Y', {a}.IslemTarihi)", "[Yıl]", "Yıl", "Yıllara", false, ["yil"],
            @"CAST\(\s*strftime\(\s*'%Y'\s*,\s*(?<a>\w+)\.IslemTarihi\s*\)\s+AS\s+INTEGER\s*\)"),
        new("ay", "zaman", 1, "Islem", "strftime('%Y-%m', {a}.IslemTarihi)", "[Ay]", "Ay", "Aylara", false, ["ay", "donem"]),
        new("gun", "zaman", 2, "Islem", "date({a}.IslemTarihi)", "[Gün]", "Gün", "Günlere", false, ["gun", "tarih"]),
        new("segment", null, 0, "Musteri", "{a}.Segment", "[Segment]", "Segment", "Segmentlere", false, ["segment"]),
        new("sektor", null, 0, "Sektor", "{a}.Ad", "[Sektör]", "Sektör", "Sektörlere", false, ["sektor"]),
        new("kanal", null, 0, "Islem", "{a}.Kanal", "[Kanal]", "Kanal", "Kanallara", false, ["kanal"]),
        new("karttipi", null, 0, "Kart", "{a}.KartTipi", "[Kart Tipi]", "Kart tipi", "Kart tiplerine", false, ["kart tipi"]),
        new("hesapturu", null, 0, "Hesap", "{a}.HesapTuru", "[Hesap Türü]", "Hesap türü", "Hesap türlerine", false, ["hesap turu"]),
    ];

    /// <summary>"Farklı kırılım" menüsünde önerilen boyutlar (sırayla).</summary>
    static readonly string[] AcrossKeys = ["bolge", "sehir", "ay", "sektor", "kanal", "segment", "karttipi", "hesapturu"];

    static readonly Dictionary<string, string> StandardAlias = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Islem"] = "i", ["Kart"] = "k", ["Musteri"] = "m", ["Sube"] = "sb", ["Sektor"] = "s",
        ["Hesap"] = "h", ["Urun"] = "u", ["MusteriUrun"] = "mu", ["Kampanya"] = "ka",
    };

    public static DrillLevel Level(string key) => Levels.First(l => l.Key == key);

    // ---------------------------------------------------------------- Seçenekler

    /// <summary>Rapordaki her kolon için tıklanınca sunulacak hedefler. Kolon bir seviyeye eşlenemezse listelenmez.</summary>
    public static List<DrillColumn> Options(string sql, IReadOnlyList<QueryColumn> columns, SchemaCatalog catalog)
    {
        var result = new List<DrillColumn>();
        var parsed = Parse(sql, catalog);
        var fact = FactTable(parsed?.Tables.Values.Distinct() ?? TablesAnywhere(sql, catalog), catalog);
        if (fact == null) return result;

        var present = columns.Select(c => LevelOf(c.Name, parsed, catalog)?.Key).Where(k => k != null).ToHashSet();
        foreach (var col in columns)
        {
            var level = LevelOf(col.Name, parsed, catalog);
            if (level == null || !Reachable(fact, level.Table, catalog)) continue;

            var targets = new List<DrillTarget>();
            if (level.Hierarchy != null)
                targets.AddRange(Levels.Where(l => l.Hierarchy == level.Hierarchy && l.Depth > level.Depth && !present.Contains(l.Key) && Allowed(fact, l, catalog))
                    .Select(l => new DrillTarget(l.Key, $"{l.Dative} in", "down")));
            targets.AddRange(AcrossKeys.Select(Level)
                .Where(l => l.Key != level.Key && (l.Hierarchy == null || l.Hierarchy != level.Hierarchy) && !present.Contains(l.Key) && Allowed(fact, l, catalog))
                .Take(5).Select(l => new DrillTarget(l.Key, $"{l.Dative} göre kır", "across")));

            var parent = level.Hierarchy == null ? null
                : Levels.FirstOrDefault(l => l.Hierarchy == level.Hierarchy && l.Depth == level.Depth - 1 && !present.Contains(l.Key) && Allowed(fact, l, catalog));
            var up = parent == null ? null : new DrillTarget(parent.Key, $"{parent.Dative} çık", "up");
            if (targets.Count > 0 || up != null) result.Add(new DrillColumn(col.Name, level.Key, level.Label, targets, up));
        }
        return result;
    }

    /// <summary>Zaman seviyeleri yalnızca işlem tarihi olan sorgularda; diğerleri olgu tablosundan çoka-bir ilişkiyle ulaşılabiliyorsa.</summary>
    static bool Allowed(string fact, DrillLevel l, SchemaCatalog catalog) => Reachable(fact, l.Table, catalog);

    /// <summary>Kolonun seviyesi: önce SELECT ifadesinden (alias çözülerek), olmazsa kolon adından.</summary>
    static DrillLevel? LevelOf(string column, Parsed? parsed, SchemaCatalog catalog)
    {
        if (parsed?.Items.FirstOrDefault(i => i.Name == column) is { } item && MatchLevel(item.Expr, parsed) is { } byExpr)
            return byExpr;
        var name = AppStore.Fold(column.Trim());
        return Levels.FirstOrDefault(l => l.Names.Contains(name));
    }

    static DrillLevel? MatchLevel(string expr, Parsed parsed)
    {
        foreach (var level in Levels)
        {
            var m = level.Pattern.Match(expr.Trim());
            if (m.Success && parsed.Tables.TryGetValue(m.Groups["a"].Value, out var table) && table.Equals(level.Table, StringComparison.OrdinalIgnoreCase))
                return level;
        }
        return null;
    }

    // ---------------------------------------------------------------- Yeniden yazım

    /// <summary>
    /// Deterministik drill. value null ise üst seviyeye çıkma (roll-up): kırılım üst seviyeye alınır, aynı hiyerarşideki
    /// alt seviye filtreleri kaldırılır. Basit olmayan sorgularda null döner; çağıran LLM'e düşer.
    /// </summary>
    public static (string? Sql, string? Reason) Rewrite(string sql, string column, object? value, string targetKey, SchemaCatalog catalog)
    {
        var p = Parse(sql, catalog);
        if (p == null) return (null, "Sorgu basit bir SELECT ... GROUP BY değil (CTE, pencere fonksiyonu veya türetilmiş tablo var).");
        var item = p.Items.FirstOrDefault(i => i.Name == column);
        if (item == null) return (null, $"'{column}' kolonu SELECT listesinde bulunamadı.");
        var level = MatchLevel(item.Expr, p);
        if (level == null) return (null, $"'{column}' kolonu bilinen bir seviyeye eşlenemedi.");
        var target = Levels.FirstOrDefault(l => l.Key == targetKey);
        if (target == null) return (null, "Bilinmeyen hedef seviye.");

        var fact = FactTable(p.Tables.Values.Distinct(), catalog);
        if (fact == null || p.Tables.Values.Any(t => !Reachable(fact, t, catalog)) || !Reachable(fact, target.Table, catalog))
            return (null, "Hedef seviyeye satırları çoğaltmadan ulaşılamıyor.");

        int groupIndex = p.GroupBy.FindIndex(g => Same(g, item.Expr) || Same(g, item.AliasText ?? "\u0000") || g.Trim() == (p.Items.IndexOf(item) + 1).ToString(CultureInfo.InvariantCulture));
        if (groupIndex < 0) return (null, "Kolon GROUP BY'da bulunamadı.");

        // Hedef tablonun alias'ı; sorguda yoksa olgu tablosundan çoka-bir JOIN yolu eklenir
        var joins = new StringBuilder();
        var tableAlias = p.Tables.GroupBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Key, StringComparer.OrdinalIgnoreCase);
        if (!tableAlias.ContainsKey(target.Table))
        {
            var path = JoinPath(fact, target.Table, catalog);
            if (path == null) return (null, "JOIN yolu bulunamadı.");
            foreach (var (from, fkCol, to, pkCol) in path)
            {
                if (tableAlias.ContainsKey(to)) continue;
                var alias = StandardAlias.GetValueOrDefault(to, to.ToLowerInvariant()[..1]);
                while (p.Tables.ContainsKey(alias) || tableAlias.ContainsValue(alias)) alias += "2";
                joins.Append($" JOIN {to} {alias} ON {tableAlias[from]}.{fkCol} = {alias}.{pkCol}");
                tableAlias[to] = alias;
            }
        }
        var targetExpr = target.Expr(tableAlias[target.Table]);

        // WHERE
        var where = p.Where;
        if (value != null)
        {
            var cond = $"{item.Expr} = {Literal(value, level)}";
            where = string.IsNullOrWhiteSpace(where) ? cond
                : HasTopLevelOr(where) ? $"({where}) AND {cond}" : $"{where} AND {cond}";
        }
        else if (where != null && level.Hierarchy != null)
        {
            foreach (var l in Levels.Where(l => l.Hierarchy == level.Hierarchy && l.Depth >= target.Depth && tableAlias.ContainsKey(l.Table)))
                where = where == null ? null : RemoveEquality(where, l, tableAlias[l.Table]);
        }

        // SELECT, GROUP BY, ORDER BY
        var items = p.Items.Select(i => i == item ? $"{targetExpr} AS {target.Alias}" : i.Text).ToList();
        var groups = p.GroupBy.Select((g, n) => n == groupIndex ? targetExpr : g).ToList();
        var order = p.OrderBy?.Select(o => ReplaceRef(o, item, targetExpr, target.Alias)).ToList();
        var limit = p.Limit;
        if (target.Hierarchy == "zaman")
            order = [target.Alias, .. (order ?? []).Where(o => !o.Contains(target.Alias))];
        else if (target.Key == "musteri")
        {
            if ((order == null || order.Count == 0) && p.Items.FirstOrDefault(i => i != item && Aggregate().IsMatch(i.Expr)) is { } measure)
                order = [$"{measure.AliasText ?? measure.Expr} DESC"];
            limit ??= "50";
        }

        var sb = new StringBuilder("SELECT ");
        if (p.Distinct) sb.Append("DISTINCT ");
        sb.Append(string.Join(", ", items)).Append(" FROM ").Append(p.From.Trim()).Append(joins);
        if (!string.IsNullOrWhiteSpace(where)) sb.Append(" WHERE ").Append(where.Trim());
        sb.Append(" GROUP BY ").Append(string.Join(", ", groups));
        if (!string.IsNullOrWhiteSpace(p.Having)) sb.Append(" HAVING ").Append(p.Having.Trim());
        if (order is { Count: > 0 }) sb.Append(" ORDER BY ").Append(string.Join(", ", order));
        if (limit != null) sb.Append(" LIMIT ").Append(limit);
        return (sb.ToString(), null);
    }

    /// <summary>LLM'e düşüldüğünde gönderilecek takip mesajı.</summary>
    public static string FollowUpMessage(string levelKey, object? value, string targetKey)
    {
        var level = Level(levelKey);
        var target = Level(targetKey);
        return value != null
            ? $"Bunu yalnızca {level.Label} = '{value}' olan kayıtlar için {target.Label} kırılımında göster. {level.Label} kolonunun yerine {target.Label} kolonu gelsin; diğer tüm koşulları (tarih aralıkları, IslemTipi, Durum, diğer filtreler) aynen koru."
            : $"Bunu {target.Label} kırılımında göster ({level.Label} yerine {target.Label}); {level.Label} ve daha alt seviyelere ait eşitlik filtrelerini kaldır, diğer tüm koşulları aynen koru.";
    }

    /// <summary>Kullanıcıya ve geçmişe yazılan kısa adım metni.</summary>
    public static string StepLabel(object? value, string targetKey)
    {
        var target = Level(targetKey);
        return value != null ? $"{DisplayValue(value)} → {(target.Hierarchy != null ? target.Dative + " in" : target.Dative + " göre kır")}" : $"↑ {target.Dative} çık";
    }

    static readonly string[] Months = ["Ocak", "Şubat", "Mart", "Nisan", "Mayıs", "Haziran", "Temmuz", "Ağustos", "Eylül", "Ekim", "Kasım", "Aralık"];

    /// <summary>"2026-09" → "Eylül 2026"; diğer değerler olduğu gibi.</summary>
    public static string DisplayValue(object? value)
    {
        var s = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        var m = PeriodValue().Match(s);
        return m.Success ? $"{Months[int.Parse(m.Groups[2].Value) - 1]} {m.Groups[1].Value}" : s;
    }

    // ---------------------------------------------------------------- Yardımcılar

    sealed record SelectItem(string Text, string Expr, string? AliasText, string Name);

    sealed record Parsed(bool Distinct, List<SelectItem> Items, string From, Dictionary<string, string> Tables,
        string? Where, List<string> GroupBy, string? Having, List<string>? OrderBy, string? Limit);

    [GeneratedRegex(@"\b(SUM|COUNT|AVG|MIN|MAX|TOTAL)\s*\(", RegexOptions.IgnoreCase)]
    private static partial Regex Aggregate();

    [GeneratedRegex(@"^(\d{4})-(\d{2})$")]
    private static partial Regex PeriodValue();

    [GeneratedRegex(@"\b(?:FROM|JOIN)\s+(\w+)(?:\s+(?:AS\s+)?(?!ON\b|WHERE\b|JOIN\b|LEFT\b|INNER\b|CROSS\b|OUTER\b|NATURAL\b|GROUP\b|ORDER\b|LIMIT\b|USING\b|HAVING\b)(\w+))?", RegexOptions.IgnoreCase)]
    private static partial Regex TableRef();

    /// <summary>Tek SELECT ... GROUP BY yapısındaki sorguyu parçalar; uygun değilse null.</summary>
    static Parsed? Parse(string sql, SchemaCatalog catalog)
    {
        sql = sql.Trim().TrimEnd(';').Trim();
        var masked = Mask(sql);
        if (!Regex.IsMatch(masked, @"^SELECT\b", RegexOptions.IgnoreCase)) return null;
        if (Regex.Matches(masked, @"\bSELECT\b", RegexOptions.IgnoreCase).Count != 1) return null;
        if (Regex.IsMatch(masked, @"\b(UNION|INTERSECT|EXCEPT|OVER|WINDOW)\b", RegexOptions.IgnoreCase)) return null;

        int Kw(string pattern) { var m = Regex.Match(masked, pattern, RegexOptions.IgnoreCase); return m.Success ? m.Index : -1; }
        int from = Kw(@"\bFROM\b"), where = Kw(@"\bWHERE\b"), group = Kw(@"\bGROUP\s+BY\b"), having = Kw(@"\bHAVING\b"),
            order = Kw(@"\bORDER\s+BY\b"), limit = Kw(@"\bLIMIT\b");
        if (from < 0 || group < 0) return null;
        int End(int start) => new[] { where, group, having, order, limit, sql.Length }.Where(x => x > start).Min();
        string? Seg(int start, int kwLen) => start < 0 ? null : sql[(start + kwLen)..End(start)].Trim();

        var fromText = sql[(from + 4)..End(from)];
        if (Regex.IsMatch(fromText, @"\(\s*SELECT\b", RegexOptions.IgnoreCase)) return null;
        var tables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in TableRef().Matches(Mask("FROM " + fromText)))
        {
            var table = catalog.Tables.FirstOrDefault(t => t.Name.Equals(m.Groups[1].Value, StringComparison.OrdinalIgnoreCase))?.Name;
            if (table == null) return null;
            tables[m.Groups[2].Success ? m.Groups[2].Value : table] = table;
        }

        var selectText = sql[6..from].Trim();
        bool distinct = Regex.IsMatch(selectText, @"^DISTINCT\b", RegexOptions.IgnoreCase);
        if (distinct) selectText = selectText[8..].Trim();
        var items = new List<SelectItem>();
        foreach (var part in SplitTop(selectText))
        {
            var m = Regex.Match(part, @"^(?<e>.*?)\s+AS\s+(?<a>\[[^\]]+\]|""[^""]+""|\w+)\s*$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            var expr = m.Success ? m.Groups["e"].Value.Trim() : part.Trim();
            var aliasText = m.Success ? m.Groups["a"].Value : null;
            var name = aliasText?.Trim('[', ']', '"') ?? (Regex.Match(expr, @"^\w+\.(\w+)$") is { Success: true } c ? c.Groups[1].Value : expr);
            items.Add(new SelectItem(part.Trim(), expr, aliasText, name));
        }

        return new Parsed(distinct, items, fromText, tables, Seg(where, 5), SplitTop(Seg(group, 0)![Regex.Match(masked[group..], @"^GROUP\s+BY", RegexOptions.IgnoreCase).Length..]),
            Seg(having, 6), order < 0 ? null : SplitTop(Seg(order, 0)![Regex.Match(masked[order..], @"^ORDER\s+BY", RegexOptions.IgnoreCase).Length..]), Seg(limit, 5));
    }

    /// <summary>Metin sabitlerini ve parantez içlerini boşlukla maskeler (konumlar korunur); üst seviye anahtar kelimeler aranabilir.</summary>
    static string Mask(string sql)
    {
        var chars = sql.ToCharArray();
        int depth = 0; bool quote = false;
        for (int i = 0; i < chars.Length; i++)
        {
            char c = sql[i];
            if (c == '\'') { quote = !quote; continue; }
            if (quote) { chars[i] = ' '; continue; }
            if (c == '(') { depth++; continue; }
            if (c == ')') { depth--; continue; }
            if (depth > 0) chars[i] = ' ';
        }
        return new string(chars);
    }

    static List<string> SplitTop(string text)
    {
        var parts = new List<string>();
        int depth = 0, start = 0; bool quote = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\'') quote = !quote;
            if (quote) continue;
            if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (c == ',' && depth == 0) { parts.Add(text[start..i].Trim()); start = i + 1; }
        }
        parts.Add(text[start..].Trim());
        return parts.Where(p => p.Length > 0).ToList();
    }

    static bool Same(string a, string b) => Regex.Replace(a, @"\s+", "").Equals(Regex.Replace(b, @"\s+", ""), StringComparison.OrdinalIgnoreCase);

    static bool HasTopLevelOr(string where) => Regex.IsMatch(Mask(where), @"\bOR\b", RegexOptions.IgnoreCase);

    static string ReplaceRef(string orderItem, SelectItem item, string targetExpr, string targetAlias)
    {
        var m = Regex.Match(orderItem, @"^(?<x>.*?)(?<dir>\s+(?:ASC|DESC))?$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var x = m.Groups["x"].Value.Trim();
        if (Same(x, item.Expr)) return targetExpr + m.Groups["dir"].Value;
        if (item.AliasText != null && Same(x, item.AliasText)) return targetAlias + m.Groups["dir"].Value;
        return orderItem;
    }

    static string Literal(object value, DrillLevel level) => value switch
    {
        long or int or double or decimal => Convert.ToString(value, CultureInfo.InvariantCulture)!,
        string s when level.Numeric && long.TryParse(s, out var n) => n.ToString(CultureInfo.InvariantCulture),
        _ => "'" + Convert.ToString(value, CultureInfo.InvariantCulture)!.Replace("'", "''") + "'",
    };

    /// <summary>WHERE'den "ifade = sabit" biçimindeki eşitlik koşulunu kaldırır (drill ile eklenmiş filtreler).</summary>
    static string? RemoveEquality(string where, DrillLevel level, string alias)
    {
        var expr = level.Pattern.ToString().TrimStart('^').TrimEnd('$').Replace(@"(?<a>\w+)", Regex.Escape(alias)).Replace(@"\k<a>", Regex.Escape(alias));
        const string lit = @"(?:'(?:[^']|'')*'|-?\d+(?:\.\d+)?)";
        var w = Regex.Replace(where, $@"\s+AND\s+{expr}\s*=\s*{lit}", "", RegexOptions.IgnoreCase);
        w = Regex.Replace(w, $@"^\s*{expr}\s*=\s*{lit}\s+AND\s+", "", RegexOptions.IgnoreCase);
        w = Regex.Replace(w, $@"^\s*{expr}\s*=\s*{lit}\s*$", "", RegexOptions.IgnoreCase);
        w = Regex.Replace(w, @"^\((.*)\)$", m => HasTopLevelOr(m.Groups[1].Value) ? m.Value : m.Groups[1].Value, RegexOptions.Singleline);
        return string.IsNullOrWhiteSpace(w) ? null : w.Trim();
    }

    /// <summary>Olgu tablosu: sorgudaki diğer tüm tablolara çoka-bir ilişkiyle ulaşılabilen en büyük tablo.</summary>
    static string? FactTable(IEnumerable<string> tables, SchemaCatalog catalog)
    {
        var list = tables.ToList();
        return list.Where(f => list.All(t => Reachable(f, t, catalog)))
            .OrderByDescending(f => catalog.Tables.FirstOrDefault(t => t.Name == f)?.RowCount ?? 0).FirstOrDefault()
            ?? list.OrderByDescending(f => catalog.Tables.FirstOrDefault(t => t.Name == f)?.RowCount ?? 0).FirstOrDefault();
    }

    static IEnumerable<string> TablesAnywhere(string sql, SchemaCatalog catalog) =>
        catalog.Tables.Where(t => Regex.IsMatch(sql, $@"\b(?:FROM|JOIN)\s+{t.Name}\b", RegexOptions.IgnoreCase)).Select(t => t.Name);

    static bool Reachable(string from, string to, SchemaCatalog catalog) =>
        from.Equals(to, StringComparison.OrdinalIgnoreCase) || JoinPath(from, to, catalog) != null;

    /// <summary>Yalnızca FK → PK (çoka-bir) yönünde ilerleyen en kısa JOIN yolu; satırlar çoğalmaz.</summary>
    static List<(string From, string FkCol, string To, string PkCol)>? JoinPath(string from, string to, SchemaCatalog catalog)
    {
        var queue = new Queue<(string Table, List<(string, string, string, string)> Path)>();
        queue.Enqueue((from, []));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { from };
        while (queue.Count > 0)
        {
            var (table, path) = queue.Dequeue();
            if (table.Equals(to, StringComparison.OrdinalIgnoreCase)) return path;
            var info = catalog.Tables.FirstOrDefault(t => t.Name.Equals(table, StringComparison.OrdinalIgnoreCase));
            if (info == null) continue;
            foreach (var c in info.Columns.Where(c => c.FkTable != null))
                if (seen.Add(c.FkTable!))
                    queue.Enqueue((c.FkTable!, [.. path, (info.Name, c.Name, c.FkTable!, c.FkColumn ?? "Id")]));
        }
        return null;
    }
}
