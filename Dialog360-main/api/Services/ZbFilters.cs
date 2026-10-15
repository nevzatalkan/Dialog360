using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Diyalog360.Api.Services;

/// <summary>Hazır aralık (örn. yaş 26–35).</summary>
public record FilterPreset(string Label, double? Min, double? Max);

/// <summary>
/// ZB penceresinde gösterilen bir filtre alanı. Metin şablonları seçimi soruya eklenecek Türkçe ifadeye çevirir:
/// {min}, {max}, {values}. Style: attr (sonuna "olan" gelir: "yaşı 25-40 arası olan"), verb (kendi fiiliyle biter:
/// "Market sektöründe harcama yapan"), time (cümlenin başına gelir: "son 3 ay içinde").
/// </summary>
/// <param name="Kind">range (en az / en çok) | choice (çoklu seçim) | single (tek seçim)</param>
public record FilterField(
    string Key, string Label, string Kind, string Style, string? Unit, double? Min, double? Max, double Step,
    IReadOnlyList<string>? Values, IReadOnlyList<FilterPreset>? Presets,
    string? TextRange, string? TextMin, string? TextMax, string? TextChoice, string Hint)
{
    /// <summary>Kural tabanlı eşleştirme için eş anlamlılar (arayüze gönderilmez).</summary>
    [JsonIgnore] public string[] Synonyms { get; init; } = [];

    /// <summary>Bağlama göre her değerdeki müşteri sayısı (örn. Şehir = Ankara iken "Çankaya": 412). Yalnızca seçim alanlarında.</summary>
    public IReadOnlyDictionary<string, long>? Counts { get; init; }
}

/// <summary>Seçenekleri daraltan mevcut bir kriter (sorudan ya da pencerede yapılan seçimden).</summary>
/// <param name="Source">soru (önceki ZB seçimi), metin (soruda geçen değer), pencere</param>
public record ZbContextItem(string Field, string Label, string Text, string Source);

/// <summary>Bağlama göre daraltılmış alanlar ve bu bağlamdaki müşteri sayısı.</summary>
public record FacetResult(IReadOnlyList<FilterField> Fields, long Matching, IReadOnlyList<ZbContextItem> Context);

/// <summary>Bir alan için önerilen başlangıç değeri (örn. "genç" → yaş 18-30).</summary>
public record FilterDefault(string Field, double? Min, double? Max, IReadOnlyList<string> Values);

/// <summary>ZB ifadesinin analizi: hangi alan(lar)la netleştirileceği ve başlangıç değerleri.</summary>
/// <param name="Phrase">Terimin filtreye karşılık gelen kısmı; metinde "ZB " + Phrase seçilen kriterlerle değiştirilir.</param>
/// <param name="Source">llm | kural</param>
public record ZbAnalysis(string Term, string Phrase, string Title, string Description, string? Primary,
    IReadOnlyList<string> Extras, IReadOnlyList<FilterDefault> Defaults, string Source, IReadOnlyList<FilterField> Fields)
{
    /// <summary>Seçenekleri daraltan mevcut kriterler (örn. Şehir: Ankara).</summary>
    public IReadOnlyList<ZbContextItem> Context { get; init; } = [];

    /// <summary>Bağlamdaki müşteri sayısı (bağlam yoksa tüm müşteriler).</summary>
    public long Matching { get; init; }

    /// <summary>Bağlamın ham hali; pencere seçim değiştikçe /api/zb/facets'e bununla birlikte gönderir.</summary>
    public IReadOnlyList<FilterDefault> ContextFilters { get; init; } = [];
}

/// <summary>
/// ZB penceresinin filtre alanları. Değer listeleri ve aralıklar açılışta rapor veritabanından okunur
/// (yaş aralığı, kart limiti, şehirler, sektörler...). Yeni bir alan eklemek için Build içine bir satır eklemek yeterlidir.
/// </summary>
public sealed class FilterCatalog
{
    public IReadOnlyList<FilterField> Fields { get; }

    public FilterField? Get(string? key) => Fields.FirstOrDefault(f => f.Key == key);

    public FilterCatalog(string bankDbPath, SchemaCatalog schema)
    {
        using var conn = new SqliteConnection($"Data Source={bankDbPath};Mode=ReadOnly;Pooling=False");
        conn.Open();
        double[] Range(string sql)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            using var r = cmd.ExecuteReader();
            r.Read();
            return [r.IsDBNull(0) ? 0 : r.GetDouble(0), r.IsDBNull(1) ? 0 : r.GetDouble(1)];
        }
        IReadOnlyList<string> Values(string table, string column) =>
            schema.Tables.FirstOrDefault(t => t.Name == table)?.Columns.FirstOrDefault(c => c.Name == column)?.Values ?? [];
        static double Floor(double v, double step) => Math.Floor(v / step) * step;
        static double Ceil(double v, double step) => Math.Ceiling(v / step) * step;

        var fields = new List<FilterField>();
        var yas = Range("SELECT CAST(strftime('%Y','now') AS INTEGER) - MAX(DogumYili), CAST(strftime('%Y','now') AS INTEGER) - MIN(DogumYili) FROM Musteri");
        double yasMin = yas[0], yasMax = yas[1];
        fields.Add(new FilterField("yas", "Yaş", "range", "attr", "yaş", yasMin, yasMax, 1, null,
            new FilterPreset[] { new("18–25", 18, 25), new("26–35", 26, 35), new("36–45", 36, 45), new("46–60", 46, 60), new("60 üstü", 61, null) }
                .Where(p => (p.Max ?? yasMax) >= yasMin && (p.Min ?? yasMin) <= yasMax).ToList(),
            "yaşı {min}-{max} arası", "yaşı {min} ve üzeri", "yaşı {max} ve altı", null, "Musteri.DogumYili'den hesaplanır")
        { Synonyms = ["yas", "yasi", "yasli", "genc", "emekli", "dogum", "orta yas"] });

        fields.Add(Choice("cinsiyet", "Cinsiyet", "attr", Values("Musteri", "Cinsiyet"), "cinsiyeti {values}", "Musteri.Cinsiyet",
            ["cinsiyet", "kadin", "erkek", "bayan", "bay"]));
        fields.Add(Choice("segment", "Segment", "attr", Values("Musteri", "Segment"), "segmenti {values}", "Musteri.Segment",
            ["segment", "bireysel", "premium", "kobi", "ticari", "kurumsal"]));
        fields.Add(Choice("bolge", "Bölge", "attr", Values("Sube", "Bolge"), "bölgesi {values}", "Sube.Bolge", ["bolge", "ege", "marmara", "akdeniz", "karadeniz", "anadolu"]));
        fields.Add(Choice("sehir", "Şehir", "attr", Values("Sube", "Sehir"), "şehri {values}", "Sube.Sehir",
            ["sehir", "il", .. Values("Sube", "Sehir").Select(AppStore.Fold)]));
        fields.Add(Choice("sube", "Şube / ilçe", "attr", Values("Sube", "Ad"), "şubesi {values}", "Sube.Ad — şube adları ilçe/semt adıdır",
            ["sube", "ilce", "semt", "mahalle", .. Values("Sube", "Ad").Select(AppStore.Fold)]));
        fields.Add(Choice("karttipi", "Kart tipi", "verb", Values("Kart", "KartTipi"), "{values} kartı bulunan", "Kart.KartTipi (aktif kartlar)",
            ["kart", "kart tipi", "classic", "gold", "platinum", "kredi karti"]));

        var limit = Range("SELECT MIN(KartLimiti), MAX(KartLimiti) FROM Kart WHERE Durum = 'Aktif'");
        fields.Add(new FilterField("kartlimiti", "Kart limiti", "range", "attr", "TL", Floor(limit[0], 1000), Ceil(limit[1], 1000), 1000, null,
            [new("50.000 altı", null, 49999), new("50.000–150.000", 50000, 150000), new("150.000 üstü", 150001, null)],
            "kart limiti {min}-{max} TL arası", "kart limiti {min} TL ve üzeri", "kart limiti {max} TL ve altı", null, "Kart.KartLimiti (aktif kartlar)")
        { Synonyms = ["limit", "kart limiti"] });

        var bakiye = Range("SELECT MIN(t), MAX(t) FROM (SELECT SUM(Bakiye) AS t FROM Hesap WHERE Durum = 'Aktif' AND ParaBirimi = 'TRY' GROUP BY MusteriId)");
        fields.Add(new FilterField("bakiye", "TL mevduat bakiyesi", "range", "attr", "TL", 0, Ceil(bakiye[1], 10000), 10000, null,
            [new("100.000 altı", null, 99999), new("100.000–500.000", 100000, 500000), new("500.000 üstü", 500001, null)],
            "toplam TL mevduat bakiyesi {min}-{max} TL arası", "toplam TL mevduat bakiyesi {min} TL ve üzeri", "toplam TL mevduat bakiyesi {max} TL ve altı", null,
            "Aktif TL hesaplarının toplamı")
        { Synonyms = ["bakiye", "mevduat", "varlik", "zengin", "birikim", "para"] });

        fields.Add(Choice("hesapturu", "Hesap türü", "verb", Values("Hesap", "HesapTuru"), "{values} hesabı bulunan", "Hesap.HesapTuru (aktif hesaplar)",
            ["hesap", "vadeli", "vadesiz", "doviz"]));
        fields.Add(Choice("urun", "Ürün", "verb", Values("Urun", "Ad"), "{values} ürününe sahip", "MusteriUrun → Urun.Ad",
            ["urun", "kredi", "sigorta", "kasko", "fon", "emeklilik", "pos", .. Values("Urun", "Ad").Select(AppStore.Fold)]));
        fields.Add(Choice("sektor", "Harcama sektörü", "verb", Values("Sektor", "Ad"), "{values} sektöründe kart harcaması yapan", "Islem → Sektor.Ad",
            ["sektor", "harcama", "alisveris", .. Values("Sektor", "Ad").Select(AppStore.Fold)]));
        fields.Add(Choice("kanal", "İşlem kanalı", "verb", Values("Islem", "Kanal"), "{values} kanalını kullanan", "Islem.Kanal",
            ["kanal", "mobil", "internet", "pos", "dijital", "online"]));

        var sure = Range("SELECT MIN((julianday('now') - julianday(KayitTarihi)) / 365.25), MAX((julianday('now') - julianday(KayitTarihi)) / 365.25) FROM Musteri");
        fields.Add(new FilterField("musterisuresi", "Müşteri olma süresi", "range", "attr", "yıl", 0, Math.Ceiling(sure[1]), 1, null,
            [new("1 yıldan az", null, 1), new("1–3 yıl", 1, 3), new("3 yıldan fazla", 3, null)],
            "müşteri olma süresi {min}-{max} yıl arası", "müşteri olma süresi {min} yıl ve üzeri", "müşteri olma süresi {max} yıl ve altı", null,
            "Musteri.KayitTarihi'nden hesaplanır")
        { Synonyms = ["kidem", "sure", "yeni musteri", "eski musteri", "kayit", "sadik"] });

        fields.Add(new FilterField("donem", "İşlem dönemi", "single", "time", null, null, null, 1,
            ["son 30 gün", "son 3 ay", "son 6 ay", "son 12 ay", "bu yıl", "geçen yıl"], null,
            null, null, null, "{values} içinde", "Islem.IslemTarihi")
        { Synonyms = ["donem", "tarih", "son", "ay", "gun", "yil", "zaman"] });

        Fields = fields;
        _bankDbPath = bankDbPath;

        static FilterField Choice(string key, string label, string style, IReadOnlyList<string> values, string text, string hint, string[] synonyms) =>
            new(key, label, "choice", style, null, null, null, 1, values, null, null, null, null, text, hint) { Synonyms = synonyms };
    }

    readonly string _bankDbPath;

    // ---------------------------------------------------------------- Bağlama göre daraltma (faceted search)

    /// <summary>
    /// Her alanın değerlerini ve aralığını bağlamdaki diğer kriterlere göre yeniden hesaplar: Şehir = Ankara seçiliyse
    /// Şube listesinde yalnızca Ankara şubeleri, yaş aralığında Ankara müşterilerinin en küçük/en büyük yaşı gelir.
    /// Bir alan kendi seçimiyle daraltılmaz (o alanda başka değer seçilebilsin). Değerler SQL'e parametreyle girer.
    /// </summary>
    public FacetResult Facets(IReadOnlyList<FilterDefault> context, IReadOnlyList<ZbContextItem>? describe = null)
    {
        context = context.Where(c => Get(c.Field) != null && (c.Values.Count > 0 || c.Min != null || c.Max != null)).ToList();
        using var conn = new SqliteConnection($"Data Source={_bankDbPath};Mode=ReadOnly;Pooling=False");
        conn.Open();
        string? period = context.FirstOrDefault(c => c.Field == "donem")?.Values.FirstOrDefault();

        SqliteCommand Command(string select, string? join, string? groupBy, string except)
        {
            var cmd = conn.CreateCommand();
            var where = new List<string>();
            foreach (var c in context.Where(c => c.Field != except && c.Field != "donem"))
                where.Add(Predicate(c, cmd, period));
            if (period != null && except != "donem" && !context.Any(c => c.Field is "sektor" or "kanal") && except is not ("sektor" or "kanal"))
                where.Add($"EXISTS (SELECT 1 FROM Islem i JOIN Kart k ON i.KartId = k.Id WHERE k.MusteriId = m.Id AND {PeriodSql("i", period)})");
            cmd.CommandText = $"SELECT {select} FROM Musteri m JOIN Sube sb ON m.SubeId = sb.Id {join} " +
                              (where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "") + (groupBy != null ? $" GROUP BY {groupBy}" : "");
            return cmd;
        }

        var result = new List<FilterField>();
        foreach (var f in Fields)
        {
            var (join, expr) = FacetSource(f.Key, period);
            if (f.Kind == "range" && expr != null)
            {
                using var cmd = Command($"MIN({expr}), MAX({expr})", join, null, f.Key);
                using var r = cmd.ExecuteReader();
                r.Read();
                if (r.IsDBNull(0)) { result.Add(f with { Values = f.Values }); continue; }
                double lo = Math.Floor(r.GetDouble(0) / f.Step) * f.Step, hi = Math.Ceiling(r.GetDouble(1) / f.Step) * f.Step;
                result.Add(f with
                {
                    Min = lo, Max = hi,
                    Presets = f.Presets?.Where(p => (p.Max ?? hi) >= lo && (p.Min ?? lo) <= hi).ToList(),
                });
            }
            else if (f.Kind == "choice" && expr != null)
            {
                using var cmd = Command($"{expr}, COUNT(DISTINCT m.Id)", join, expr, f.Key);
                using var r = cmd.ExecuteReader();
                var counts = new Dictionary<string, long>();
                while (r.Read()) if (!r.IsDBNull(0)) counts[r.GetString(0)] = r.GetInt64(1);
                result.Add(f with { Values = f.Values!.Where(counts.ContainsKey).ToList(), Counts = counts });
            }
            else result.Add(f);
        }

        long matching;
        using (var cmd = Command("COUNT(*)", null, null, "\u0000")) matching = Convert.ToInt64(cmd.ExecuteScalar());
        return new FacetResult(result, matching, describe ?? Describe(context, "pencere"));
    }

    /// <summary>Kriterlerin kullanıcıya gösterilecek kısa metni: "Şehir: Ankara", "Yaş: 26–35".</summary>
    public List<ZbContextItem> Describe(IEnumerable<FilterDefault> context, string source) => context
        .Select(c => (Field: Get(c.Field), c)).Where(x => x.Field != null)
        .Select(x => new ZbContextItem(x.c.Field, x.Field!.Label, x.c.Values.Count > 0 ? string.Join(", ", x.c.Values)
            : x.c.Min != null && x.c.Max != null ? $"{x.c.Min:0}–{x.c.Max:0}" : x.c.Min != null ? $"{x.c.Min:0}+" : $"≤ {x.c.Max:0}", source))
        .ToList();

    /// <summary>Soruda geçen değerler (örn. "Ankara'daki", "Premium", "kadın"): kelime başında ve sonunda eşleşmeli, kesme işaretli ekler serbest.</summary>
    public List<FilterDefault> Mentions(string text)
    {
        var folded = AppStore.Fold(text);
        var found = new List<FilterDefault>();
        foreach (var f in Fields.Where(f => f.Values is { Count: > 0 }))
        {
            var hits = f.Values!.Where(v => Regex.IsMatch(folded, $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(AppStore.Fold(v))}(?![\p{{L}}\p{{N}}])")).ToList();
            if (f.Kind == "single") hits = hits.Take(1).ToList();
            if (hits.Count > 0) found.Add(new FilterDefault(f.Key, null, null, hits));
        }
        return found;
    }

    /// <summary>Bir kriterin müşteri bazlı SQL koşulu (Musteri m, Sube sb üzerinden).</summary>
    static string Predicate(FilterDefault c, SqliteCommand cmd, string? period)
    {
        string P(object v) { var name = $"$p{cmd.Parameters.Count}"; cmd.Parameters.AddWithValue(name, v); return name; }
        string In(string expr) => $"{expr} IN ({string.Join(", ", c.Values.Select(v => P(v)))})";
        string Between(string expr) => c.Min != null && c.Max != null ? $"{expr} BETWEEN {P(c.Min)} AND {P(c.Max)}"
            : c.Min != null ? $"{expr} >= {P(c.Min)}" : $"{expr} <= {P(c.Max!)}";
        var periodSql = period != null ? " AND " + PeriodSql("i", period) : "";
        return c.Field switch
        {
            "yas" => Between("(CAST(strftime('%Y','now') AS INTEGER) - m.DogumYili)"),
            "cinsiyet" => In("m.Cinsiyet"),
            "segment" => In("m.Segment"),
            "bolge" => In("sb.Bolge"),
            "sehir" => In("sb.Sehir"),
            "sube" => In("sb.Ad"),
            "karttipi" => $"EXISTS (SELECT 1 FROM Kart k WHERE k.MusteriId = m.Id AND k.Durum = 'Aktif' AND {In("k.KartTipi")})",
            "kartlimiti" => $"EXISTS (SELECT 1 FROM Kart k WHERE k.MusteriId = m.Id AND k.Durum = 'Aktif' AND {Between("k.KartLimiti")})",
            "bakiye" => Between("(SELECT SUM(h.Bakiye) FROM Hesap h WHERE h.MusteriId = m.Id AND h.Durum = 'Aktif' AND h.ParaBirimi = 'TRY')"),
            "hesapturu" => $"EXISTS (SELECT 1 FROM Hesap h WHERE h.MusteriId = m.Id AND h.Durum = 'Aktif' AND {In("h.HesapTuru")})",
            "urun" => $"EXISTS (SELECT 1 FROM MusteriUrun mu JOIN Urun u ON u.Id = mu.UrunId WHERE mu.MusteriId = m.Id AND {In("u.Ad")})",
            "sektor" => $"EXISTS (SELECT 1 FROM Islem i JOIN Kart k ON i.KartId = k.Id JOIN Sektor s ON s.Id = i.SektorId WHERE k.MusteriId = m.Id AND i.IslemTipi = 'Harcama' AND {In("s.Ad")}{periodSql})",
            "kanal" => $"EXISTS (SELECT 1 FROM Islem i JOIN Kart k ON i.KartId = k.Id WHERE k.MusteriId = m.Id AND {In("i.Kanal")}{periodSql})",
            "musterisuresi" => Between("((julianday('now') - julianday(m.KayitTarihi)) / 365.25)"),
            _ => "1 = 1",
        };
    }

    /// <summary>Alan değerlerinin sayılacağı ek JOIN ve ifade.</summary>
    static (string? Join, string? Expr) FacetSource(string key, string? period)
    {
        var periodSql = period != null ? " AND " + PeriodSql("fi", period) : "";
        return key switch
        {
            "yas" => (null, "(CAST(strftime('%Y','now') AS INTEGER) - m.DogumYili)"),
            "cinsiyet" => (null, "m.Cinsiyet"),
            "segment" => (null, "m.Segment"),
            "bolge" => (null, "sb.Bolge"),
            "sehir" => (null, "sb.Sehir"),
            "sube" => (null, "sb.Ad"),
            "karttipi" => ("JOIN Kart fk ON fk.MusteriId = m.Id AND fk.Durum = 'Aktif'", "fk.KartTipi"),
            "kartlimiti" => ("JOIN Kart fk ON fk.MusteriId = m.Id AND fk.Durum = 'Aktif'", "fk.KartLimiti"),
            "bakiye" => (null, "(SELECT SUM(h.Bakiye) FROM Hesap h WHERE h.MusteriId = m.Id AND h.Durum = 'Aktif' AND h.ParaBirimi = 'TRY')"),
            "hesapturu" => ("JOIN Hesap fh ON fh.MusteriId = m.Id AND fh.Durum = 'Aktif'", "fh.HesapTuru"),
            "urun" => ("JOIN MusteriUrun fmu ON fmu.MusteriId = m.Id JOIN Urun fu ON fu.Id = fmu.UrunId", "fu.Ad"),
            "sektor" => ($"JOIN Kart fk ON fk.MusteriId = m.Id JOIN Islem fi ON fi.KartId = fk.Id AND fi.IslemTipi = 'Harcama'{periodSql} JOIN Sektor fs ON fs.Id = fi.SektorId", "fs.Ad"),
            "kanal" => ($"JOIN Kart fk ON fk.MusteriId = m.Id JOIN Islem fi ON fi.KartId = fk.Id{periodSql}", "fi.Kanal"),
            "musterisuresi" => (null, "((julianday('now') - julianday(m.KayitTarihi)) / 365.25)"),
            _ => (null, null),
        };
    }

    /// <summary>İşlem dönemi seçeneklerinin SQL karşılığı (glossary'deki takvim ayı kuralıyla aynı).</summary>
    static string PeriodSql(string a, string period) => period switch
    {
        "son 30 gün" => $"{a}.IslemTarihi >= date('now','localtime','-30 days')",
        "son 3 ay" => $"{a}.IslemTarihi >= date('now','localtime','start of month','-3 months') AND {a}.IslemTarihi < date('now','localtime','start of month')",
        "son 6 ay" => $"{a}.IslemTarihi >= date('now','localtime','start of month','-6 months') AND {a}.IslemTarihi < date('now','localtime','start of month')",
        "son 12 ay" => $"{a}.IslemTarihi >= date('now','localtime','start of month','-12 months') AND {a}.IslemTarihi < date('now','localtime','start of month')",
        "bu yıl" => $"{a}.IslemTarihi >= date('now','localtime','start of year')",
        "geçen yıl" => $"{a}.IslemTarihi >= date('now','localtime','start of year','-1 year') AND {a}.IslemTarihi < date('now','localtime','start of year')",
        _ => "1 = 1",
    };

    /// <summary>Alanlar için önerilen ek filtreler (kural tabanlı yedek ve LLM'in boş bıraktığı durumlar için).</summary>
    public static readonly Dictionary<string, string[]> DefaultExtras = new()
    {
        ["yas"] = ["cinsiyet", "segment", "sehir"], ["cinsiyet"] = ["yas", "segment", "sehir"], ["segment"] = ["yas", "cinsiyet", "bolge"],
        ["bolge"] = ["sehir", "yas", "segment"], ["sehir"] = ["yas", "cinsiyet", "segment"], ["sube"] = ["yas", "cinsiyet", "segment"],
        ["karttipi"] = ["kartlimiti", "yas", "segment"], ["kartlimiti"] = ["karttipi", "yas", "segment"], ["bakiye"] = ["hesapturu", "yas", "segment"],
        ["hesapturu"] = ["bakiye", "yas", "segment"], ["urun"] = ["yas", "segment", "cinsiyet"], ["sektor"] = ["donem", "kanal", "yas"],
        ["kanal"] = ["donem", "sektor", "yas"], ["musterisuresi"] = ["yas", "segment", "sube"], ["donem"] = ["sektor", "kanal", "segment"],
    };
}

/// <summary>
/// Soru kutusunda "ZB" ile işaretlenen ifadeyi (örn. "ZB yaş", "ZB genç kadın", "ZB Platinum") analiz eder:
/// LLM hangi filtre alanlarıyla netleştirileceğini ve varsa başlangıç değerlerini seçer. Model kapalıysa ya da
/// geçersiz cevap verirse eş anlamlı kelime eşleştirmesine (kural) düşer; pencere her durumda açılır.
/// </summary>
public sealed partial class ZbAnalyzer(LlmClient llm, FilterCatalog catalog, ILogger<ZbAnalyzer> log)
{
    static readonly ConcurrentDictionary<string, ZbAnalysis> Cache = new();

    /// <param name="context">Sorudaki önceki ZB seçimleri (arayüz yollar); soruda geçen değerler (örn. "Ankara") ayrıca metinden bulunur.</param>
    public async Task<ZbAnalysis> AnalyzeAsync(string term, string? text, CancellationToken ct, IReadOnlyList<FilterDefault>? context = null)
    {
        term = Regex.Replace(term.Trim(), @"\s+", " ");
        var (ctx, described) = Context(term, text, context);
        var facets = catalog.Facets(ctx, described);
        var analysis = await AnalyzeTermAsync(term, text, described, ct);
        // Varsayılanlardan bu bağlamda bulunmayan değerler atılır (Ankara seçiliyken "Kadıköy" önerilmez)
        var fields = facets.Fields.ToDictionary(f => f.Key);
        var defaults = analysis.Defaults
            .Select(d => d.Values.Count > 0 ? d with { Values = d.Values.Where(v => fields[d.Field].Values?.Contains(v) ?? true).ToList() } : d)
            .Where(d => d.Values.Count > 0 || d.Min != null || d.Max != null).ToList();
        return analysis with { Fields = facets.Fields, Defaults = defaults, Context = described, Matching = facets.Matching, ContextFilters = ctx };
    }

    /// <summary>Bağlam: arayüzün yolladığı önceki seçimler + soruda geçen değerler (ZB ifadesinin kendisi hariç).</summary>
    (List<FilterDefault> Context, List<ZbContextItem> Described) Context(string term, string? text, IReadOnlyList<FilterDefault>? given)
    {
        var structured = (given ?? []).Where(c => catalog.Get(c.Field) != null).ToList();
        var rest = Regex.Replace(text ?? "", $@"(^|\s)zb\s+{Regex.Escape(term)}", " ", RegexOptions.IgnoreCase);
        rest = Regex.Replace(rest, @"(^|\s)zb\s+\S+", " ", RegexOptions.IgnoreCase);
        var mentioned = catalog.Mentions(rest).Where(m => structured.All(s => s.Field != m.Field)).ToList();
        var described = catalog.Describe(structured, "soru").Concat(catalog.Describe(mentioned, "metin")).ToList();
        return ([.. structured, .. mentioned], described);
    }

    async Task<ZbAnalysis> AnalyzeTermAsync(string term, string? text, IReadOnlyList<ZbContextItem> context, CancellationToken ct)
    {
        var contextText = context.Count == 0 ? "" : "\nSorudaki mevcut kriterler: " + string.Join("; ", context.Select(c => $"{c.Label} = {c.Text}"));
        var key = $"{AppStore.Fold(term)}\n{contextText}\n{llm.Model}";
        if (Cache.TryGetValue(key, out var cached)) return cached;

        ZbAnalysis? result = null;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            var raw = await llm.AskJsonAsync(
                [new ChatMessage("system", SystemPrompt()), new ChatMessage("user", $"Kullanıcının yazdığı soru: {text?.Trim()}{contextText}\nZB ile işaretlenen ifade: {term}")],
                "diyalog360_zb", Schema(), cts.Token, maxTokens: 400);
            result = FromLlm(term, raw);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or JsonException or InvalidOperationException)
        {
            log.LogInformation("ZB analizi modelsiz yapıldı: {Message}", ex.Message);
        }
        result ??= ByRules(term);
        if (result.Source == "llm") Cache[key] = result;
        return result;
    }

    string SystemPrompt()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Sen Diyalog 360'ın filtre asistanısın. Kullanıcı bir bankanın verileri hakkında soru yazarken bir ifadeyi \"ZB\" ile işaretledi; bu ifadeyi bir filtre penceresiyle netleştireceğiz.");
        sb.AppendLine("Görevin: aşağıdaki filtre alanlarından ifadeye en uygun ANA alanı ve kullanıcının bu soruda büyük olasılıkla daraltmak isteyeceği 2-4 EK alanı seçmek.");
        sb.AppendLine("Kurallar:");
        sb.AppendLine("- ifade: ZB'den sonraki metnin filtreye karşılık gelen kısmı (genelde ilk 1-3 kelime). Sorunun geri kalanını (örn. \"müşterilerin toplam harcaması\") ifadeye katma.");
        sb.AppendLine("- İfade bir değer içeriyorsa varsayılanları doldur: \"genç\" → yas 18-30, \"yaşlı\"/\"emekli\" → yas min 60, \"30 yaş üstü\" → yas min 31, \"kadın\" → cinsiyet [Kadın], \"Platinum\" → karttipi [Platinum]. Değerleri yalnızca listede yazıldığı gibi kullan. min/max yoksa boş metin bırak.");
        sb.AppendLine("- baslik: pencerenin kısa başlığı (en fazla 5 kelime). aciklama: neyi netleştirdiğimizi anlatan tek cümle.");
        sb.AppendLine("- \"İlçe\", \"semt\" = sube alanı (şube adları ilçe/semt adıdır). \"İl\" = sehir.");
        sb.AppendLine("- \"Sorudaki mevcut kriterler\" verilmişse o alanları ek alan olarak yeniden önerme; seçenekler zaten bu kriterlere göre daraltılır.");
        sb.AppendLine("Filtre alanları:");
        foreach (var f in catalog.Fields)
        {
            sb.Append($"- {f.Key}: {f.Label} ({(f.Kind == "range" ? $"aralık {f.Min:0}-{f.Max:0} {f.Unit}" : "seçim")})");
            if (f.Values is { Count: > 0 }) sb.Append($" değerler: {string.Join(", ", f.Values)}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    JsonNode Schema()
    {
        var keys = new JsonArray(catalog.Fields.Select(f => (JsonNode)JsonValue.Create(f.Key)!).ToArray());
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["ifade"] = new JsonObject { ["type"] = "string" },
                ["baslik"] = new JsonObject { ["type"] = "string" },
                ["aciklama"] = new JsonObject { ["type"] = "string" },
                ["ana_alan"] = new JsonObject { ["type"] = "string", ["enum"] = keys.DeepClone() },
                ["ek_alanlar"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string", ["enum"] = keys.DeepClone() } },
                ["varsayilanlar"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["alan"] = new JsonObject { ["type"] = "string", ["enum"] = keys.DeepClone() },
                            ["min"] = new JsonObject { ["type"] = "string" },
                            ["max"] = new JsonObject { ["type"] = "string" },
                            ["degerler"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
                        },
                        ["required"] = new JsonArray("alan", "min", "max", "degerler"),
                        ["additionalProperties"] = false,
                    },
                },
            },
            ["required"] = new JsonArray("ifade", "baslik", "aciklama", "ana_alan", "ek_alanlar", "varsayilanlar"),
            ["additionalProperties"] = false,
        };
    }

    sealed record LlmZb(
        [property: JsonPropertyName("ifade")] string? Ifade,
        [property: JsonPropertyName("baslik")] string? Baslik,
        [property: JsonPropertyName("aciklama")] string? Aciklama,
        [property: JsonPropertyName("ana_alan")] string? AnaAlan,
        [property: JsonPropertyName("ek_alanlar")] List<string>? EkAlanlar,
        [property: JsonPropertyName("varsayilanlar")] List<LlmDefault>? Varsayilanlar);

    sealed record LlmDefault(
        [property: JsonPropertyName("alan")] string? Alan,
        [property: JsonPropertyName("min")] string? Min,
        [property: JsonPropertyName("max")] string? Max,
        [property: JsonPropertyName("degerler")] List<string>? Degerler);

    /// <summary>LLM cevabını doğrular: bilinmeyen alanlar atılır, değerler listedekilere eşlenir, aralıklar sınırlara kırpılır.</summary>
    public ZbAnalysis? FromLlm(string term, string raw)
    {
        var a = JsonSerializer.Deserialize<LlmZb>(raw);
        var primary = catalog.Get(a?.AnaAlan);
        if (a == null || primary == null) return null;
        var extras = (a.EkAlanlar ?? []).Where(k => k != primary.Key && catalog.Get(k) != null).Distinct().Take(4).ToList();
        if (extras.Count == 0) extras = [.. FilterCatalog.DefaultExtras.GetValueOrDefault(primary.Key, [])];
        var defaults = new List<FilterDefault>();
        foreach (var d in a.Varsayilanlar ?? [])
            if (Normalize(catalog.Get(d.Alan), Num(d.Min), Num(d.Max), d.Degerler ?? []) is { } n) defaults.Add(n);
        foreach (var d in defaults.Where(d => d.Field != primary.Key && !extras.Contains(d.Field)).ToList()) extras.Add(d.Field);

        return new ZbAnalysis(term, PhraseOf(term, a.Ifade), Fallback(a.Baslik, $"{primary.Label} kriteri"),
            Fallback(a.Aciklama, $"\"{term}\" ifadesini {primary.Label.ToLower(CultureInfo.GetCultureInfo("tr-TR"))} ile netleştirin."),
            primary.Key, extras, defaults, "llm", catalog.Fields);
    }

    /// <summary>Model yokken: eş anlamlı kelime eşleştirmesi ve basit değer çıkarımı ("30 yaş üstü", "kadın", "Platinum").</summary>
    public ZbAnalysis ByRules(string term)
    {
        var folded = AppStore.Fold(term);
        var words = Regex.Split(folded, @"[^\p{L}\p{N}]+").Where(w => w.Length > 0).ToList();
        int Score(FilterField f) => f.Synonyms.Max(s => s.Contains(' ') ? (folded.Contains(s) ? 4 : 0)
            : words.Any(w => w == s) ? 3 : words.Any(w => w.Length >= 3 && (w.StartsWith(s) || s.StartsWith(w))) ? 2 : 0);
        var ranked = catalog.Fields.Select(f => (Field: f, Score: Score(f))).Where(x => x.Score > 0).OrderByDescending(x => x.Score).ToList();
        var primary = ranked.FirstOrDefault().Field;

        var defaults = new List<FilterDefault>();
        foreach (var f in catalog.Fields.Where(f => f.Values != null))
        {
            var hits = f.Values!.Where(v => Regex.IsMatch(folded, $@"(^|[^\p{{L}}]){Regex.Escape(AppStore.Fold(v))}($|[^\p{{L}}])")).ToList();
            if (hits.Count > 0 && Normalize(f, null, null, hits) is { } n) defaults.Add(n);
        }
        var age = catalog.Get("yas")!;
        if (Regex.Match(folded, @"(\d{2})\s*[-–]\s*(\d{2})") is { Success: true } r2 && (primary?.Key ?? "yas") == "yas")
            defaults.Add(Normalize(age, double.Parse(r2.Groups[1].Value), double.Parse(r2.Groups[2].Value), [])!);
        else if (Regex.Match(folded, @"(\d{2})\s*(?:yas\s*)?(ustu|uzeri|ve uzeri|buyuk)") is { Success: true } up)
            defaults.Add(Normalize(age, double.Parse(up.Groups[1].Value) + (up.Groups[2].Value == "ustu" ? 1 : 0), null, [])!);
        else if (Regex.Match(folded, @"(\d{2})\s*(?:yas\s*)?(alti|altinda|kucuk)") is { Success: true } down)
            defaults.Add(Normalize(age, null, double.Parse(down.Groups[1].Value) - 1, [])!);
        else if (words.Contains("genc")) defaults.Add(Normalize(age, 18, 30, [])!);
        else if (words.Contains("yasli") || words.Contains("emekli")) defaults.Add(Normalize(age, 60, null, [])!);

        primary ??= defaults.Count > 0 ? catalog.Get(defaults[0].Field) : null;
        var extras = primary == null ? ["yas", "cinsiyet", "segment", "sehir"] : FilterCatalog.DefaultExtras.GetValueOrDefault(primary.Key, []).ToList();
        foreach (var d in defaults.Where(d => d.Field != primary?.Key && !extras.Contains(d.Field))) extras.Add(d.Field);
        var tr = CultureInfo.GetCultureInfo("tr-TR");
        return new ZbAnalysis(term, term, primary != null ? $"{primary.Label} kriteri" : "Kriterleri seçin",
            primary != null ? $"\"{term}\" ifadesini {primary.Label.ToLower(tr)} ile netleştirin." : $"\"{term}\" için uygun bir alan bulunamadı; aşağıdan filtre seçebilirsiniz.",
            primary?.Key, extras, defaults, "kural", catalog.Fields);
    }

    static FilterDefault? Normalize(FilterField? f, double? min, double? max, IEnumerable<string> values)
    {
        if (f == null) return null;
        if (f.Kind == "range")
        {
            if (min is null && max is null) return null;
            double? Clamp(double? v) => v is null ? null : Math.Clamp(v.Value, f.Min ?? v.Value, f.Max ?? v.Value);
            return new FilterDefault(f.Key, Clamp(min), Clamp(max), []);
        }
        var matched = values.Select(v => f.Values!.FirstOrDefault(x => AppStore.Fold(x) == AppStore.Fold(v.Trim()))).Where(v => v != null).Cast<string>().Distinct().ToList();
        if (f.Kind == "single") matched = matched.Take(1).ToList();
        return matched.Count == 0 ? null : new FilterDefault(f.Key, null, null, matched);
    }

    static double? Num(string? s) =>
        double.TryParse(Regex.Replace(s ?? "", @"[^\d,.\-]", "").Replace(".", "").Replace(",", "."), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>LLM'in döndürdüğü ifade terimin başındaysa onu, değilse terimin tamamını kullanır.</summary>
    static string PhraseOf(string term, string? phrase)
    {
        phrase = phrase?.Trim();
        return !string.IsNullOrEmpty(phrase) && AppStore.Fold(term).StartsWith(AppStore.Fold(phrase)) ? term[..phrase.Length] : term;
    }

    static string Fallback(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
