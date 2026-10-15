using System.Text.RegularExpressions;

namespace Diyalog360.Api.Services;

/// <summary>
/// LLM'in ürettiği SQL'de iş kurallarına aykırı bilinen kalıpları deterministik olarak yakalar.
/// Bulunan sorun, SQL hatası gibi düzeltme döngüsüne verilir; model sorguyu kurala uygun yeniden yazar.
/// Elle düzenlenen SQL'e ve eval'deki beklenen sorgulara uygulanmaz.
/// </summary>
public static partial class SqlLint
{
    // date('now','localtime','-6 months') gibi bugünden geriye kayan ay penceresi ('start of month' olmadan)
    [GeneratedRegex(@"\b(?:date|datetime)\(\s*'now'\s*(?:,\s*'localtime'\s*)?,\s*'-\s*(\d+)\s*months?'\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex RollingMonths();

    [GeneratedRegex(@"aynı\s+dönem", RegexOptions.IgnoreCase)]
    private static partial Regex SamePeriodQuestion();

    [GeneratedRegex(@"date\(\s*'now'\s*,\s*'localtime'\s*,\s*'-1 year'\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex SamePeriodBound();

    // SektorId = 7, s.Id IN (1, 2) gibi tahmin edilmiş Id ile filtre (sektör, şube, ürün adla filtrelenmeli)
    [GeneratedRegex(@"\b(?:\w+\.)?(?:SektorId|SubeId|UrunId)\s*(?:=|IN\s*\()\s*\d+|\b(?:s|sb|u|se|sek|sub|ur)\.Id\s*(?:=|IN\s*\()\s*\d+", RegexOptions.IgnoreCase)]
    private static partial Regex HardcodedId();

    // SUM(i.Tutar) / AVG(Tutar) var ama hiçbir yerde IslemTipi filtresi yok
    [GeneratedRegex(@"\b(?:SUM|AVG|TOTAL)\(\s*(?:(\w+)\.)?Tutar\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex AmountAggregate();

    // Kart ölçüsü (limit ortalaması/toplamı, kart sayısı) ile Islem JOIN'i aynı sorgu seviyesinde
    [GeneratedRegex(@"\b(?:AVG|SUM|TOTAL)\(\s*(?:\w+\.)?KartLimiti\s*\)|\bCOUNT\(\s*(?:DISTINCT\s+)?k\d*\.Id\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex CardMeasure();

    [GeneratedRegex(@"\bJOIN\s+Islem\b", RegexOptions.IgnoreCase)]
    private static partial Regex JoinIslem();

    [GeneratedRegex(@"^\s*WITH\b|\(\s*SELECT\b", RegexOptions.IgnoreCase)]
    private static partial Regex HasSubquery();

    [GeneratedRegex(@"\b(?:FROM|JOIN)\s+Hesap\b(?:\s+(?:AS\s+)?(?!ON\b|WHERE\b|JOIN\b|LEFT\b|INNER\b|GROUP\b|ORDER\b|CROSS\b)(\w+))?", RegexOptions.IgnoreCase)]
    private static partial Regex HesapRef();

    [GeneratedRegex(@"kapal[ıi]|t[üu]m\s+hesap|b[üu]t[üu]n\s+hesap", RegexOptions.IgnoreCase)]
    private static partial Regex AllAccountsQuestion();

    [GeneratedRegex(@"d[öo]viz", RegexOptions.IgnoreCase)]
    private static partial Regex FxQuestion();

    [GeneratedRegex(@"<\s*date\(\s*'now'\s*,\s*'localtime'\s*,\s*'start of year'\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex ExcludesThisYear();

    // Müşteri sorgusunda EXISTS'in Kart JOIN etmeden dıştaki tek bir karta (k.Id) bağlanması
    [GeneratedRegex(@"EXISTS\s*\(\s*SELECT\s+1\s+FROM\s+Islem\s+(\w+)\b([^()]*?)\bWHERE\b[^()]*?\b\1\.KartId\s*=\s*(\w+)\.Id\b", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ExistsBoundToOuterCard();

    [GeneratedRegex(@"\bMusteri\b", RegexOptions.IgnoreCase)]
    private static partial Regex UsesMusteri();

    // Kart sahipliği: Kart doğrudan müşteriye bağlanmış (JOIN Kart k ON k.MusteriId = m.Id)
    [GeneratedRegex(@"\bJOIN\s+Kart\s+(?:AS\s+)?(\w+)\s+ON\s+(?:\1\.MusteriId\s*=\s*\w+\.Id|\w+\.Id\s*=\s*\1\.MusteriId)\b", RegexOptions.IgnoreCase)]
    private static partial Regex OwnershipJoin();

    [GeneratedRegex(@"\bEXISTS\s*\(\s*SELECT\s+1\s+FROM\s+(Kart|Islem)\s+(?:AS\s+)?(\w+)", RegexOptions.IgnoreCase)]
    private static partial Regex ExistsOver();

    [GeneratedRegex(@"harcama", RegexOptions.IgnoreCase)]
    private static partial Regex SpendQuestion();

    /// <summary>Açılış parantezinden itibaren dengeli parantez içeriğini döndürür.</summary>
    static string ParenBody(string sql, int from)
    {
        int open = sql.IndexOf('(', from);
        if (open < 0) return "";
        int depth = 0;
        for (int i = open; i < sql.Length; i++)
        {
            if (sql[i] == '(') depth++;
            else if (sql[i] == ')' && --depth == 0) return sql.Substring(open + 1, i - open - 1);
        }
        return sql[(open + 1)..];
    }

    static bool Has(string sql, string pattern) => Regex.IsMatch(sql, pattern, RegexOptions.IgnoreCase);

    /// <returns>Sorun yoksa null; varsa modele verilecek, tüm ihlalleri tek seferde listeleyen düzeltme talimatı.</returns>
    public static string? Check(string sql, string? question = null)
    {
        var issues = Issues(sql, question);
        if (issues.Count == 0) return null;
        var text = issues.Count == 1
            ? issues[0]
            : $"Sorguda {issues.Count} kural ihlali var:\n" + string.Join("\n", issues.Select((x, n) => $"{n + 1}) {x}"));
        return text + "\nYalnızca bu sorunları düzelt; sorgudaki diğer tüm koşulları (Durum, IslemTipi, tarih aralıkları, JOIN'ler, filtreler) aynen koru.";
    }

    /// <summary>Bulunan tüm ihlaller (model bir turda hepsini düzeltebilsin diye ilk ihlalde durmaz).</summary>
    public static List<string> Issues(string sql, string? question = null)
    {
        var issues = new List<string>();

        var m = RollingMonths().Match(sql);
        if (m.Success)
        {
            var n = m.Groups[1].Value;
            issues.Add($"Tarih kuralı ihlali: '{m.Value}' bugünden geriye kayan bir pencere. \"Son {n} ay\" her zaman tamamlanmış son {n} takvim ayıdır; " +
                       $"şu kalıbı kullan: >= date('now','localtime','start of month','-{n} months') AND < date('now','localtime','start of month').");
        }

        if (question != null && SamePeriodQuestion().IsMatch(question))
        {
            if (!SamePeriodBound().IsMatch(sql))
                issues.Add("Tarih kuralı ihlali: \"geçen yılın aynı dönemi\" geçen yılın tamamı değildir. Geçen yılın aynı dönemi = " +
                           ">= date('now','localtime','start of year','-1 year') AND < date('now','localtime','-1 year'); bu yıl = >= date('now','localtime','start of year').");
            else if (ExcludesThisYear().IsMatch(sql))
                issues.Add("Tarih kuralı ihlali: sorgudaki \"< date('now','localtime','start of year')\" koşulu bu yılın verisini tamamen dışarıda bırakıyor, " +
                           "bu yüzden bu yılın toplamı 0 çıkar. WHERE'de iki dönemi de kapsayan yalnızca bir alt sınır kullan: " +
                           "i.IslemTarihi >= date('now','localtime','start of year','-1 year'); dönemleri CASE WHEN ile ayır.");
        }

        var id = HardcodedId().Match(sql);
        if (id.Success)
            issues.Add($"'{id.Value}' tahmin edilmiş bir Id ile filtreliyor. Sektör, şube ve ürünlerin Id değerlerini bilemezsin; " +
                       "ilgili tabloyu JOIN edip adıyla filtrele (örn. JOIN Sektor s ON i.SektorId = s.Id ... s.Ad = 'Market', EXISTS içinde de aynı şekilde).");

        foreach (Match a in AmountAggregate().Matches(sql))
        {
            var alias = a.Groups[1].Success ? a.Groups[1].Value : null;
            // Alias doğrudan Islem tablosunu gösteriyorsa o alias'ın filtresi aranır; bir CTE'yi gösteriyorsa (filtre CTE'nin içinde olabilir) sorguda herhangi bir IslemTipi yeterlidir
            bool islemAlias = alias != null && Has(sql, $@"\b(?:FROM|JOIN)\s+Islem\s+(?:AS\s+)?{Regex.Escape(alias)}\b");
            bool filtered = islemAlias ? Has(sql, $@"\b{Regex.Escape(alias!)}\.IslemTipi\b") : Has(sql, @"\bIslemTipi\b");
            if (!filtered)
            {
                issues.Add($"'{a.Value}' işlem tutarını topluyor ama {(alias != null ? $"'{alias}' alias'ı için " : "")}IslemTipi filtresi yok; iadeler harcamaya karışıyor. " +
                           $"Bu toplamın geçtiği sorguya (alt sorgu dahil) {alias ?? "i"}.IslemTipi = 'Harcama' ekle (iade isteniyorsa 'İade', net harcama isteniyorsa CASE WHEN ile ayır).");
                break;
            }
        }

        if (CardMeasure().IsMatch(sql) && JoinIslem().IsMatch(sql) && !HasSubquery().IsMatch(sql))
            issues.Add("Satır çoğalması: kart sayısı veya kart limiti, Islem tablosu JOIN edilmiş aynı sorguda hesaplanıyor. " +
                       "Bu JOIN her kartı işlem sayısı kadar tekrarlar ve işlemi olmayan kartları düşürür. Kart ölçülerini bir CTE'de, " +
                       "harcamayı ayrı bir CTE'de grupla ve ortak anahtarla birleştir (Örnek 10'daki gibi).");

        foreach (Match h in HesapRef().Matches(sql))
        {
            var alias = h.Groups[1].Success ? h.Groups[1].Value : null;
            bool hasStatus = alias != null ? Has(sql, $@"\b{Regex.Escape(alias)}\.Durum\b") : Has(sql, @"\bDurum\b");
            if (!hasStatus && (question == null || !AllAccountsQuestion().IsMatch(question)))
            {
                issues.Add($"Hesap tablosu kullanılıyor ama hesap durumu filtrelenmiyor; kapalı hesaplar da sayılıyor. " +
                           $"Kullanıcı aksini söylemedikçe yalnızca aktif hesaplar kullanılır: {alias ?? "h"}.Durum = 'Aktif' ekle.");
                break;
            }
        }

        // Kart sahipliği (müşterinin X kartı var mı, kart sayısı) → yalnızca aktif kartlar. Harcama için Islem'e bağlanan kartlara uygulanmaz.
        foreach (Match o in OwnershipJoin().Matches(sql))
        {
            var k = Regex.Escape(o.Groups[1].Value);
            bool feedsIslem = Has(sql, $@"\bJOIN\s+Islem\s+(?:AS\s+)?\w+\s+ON\s+\w+\.KartId\s*=\s*{k}\.Id\b|\bJOIN\s+Islem\s+(?:AS\s+)?\w+\s+ON\s+{k}\.Id\s*=\s*\w+\.KartId\b");
            if (!feedsIslem && !Has(sql, $@"\b{k}\.Durum\b") && !KartStatusQuestion(question))
            {
                issues.Add($"Kart sahipliği kontrol ediliyor ama kart durumu filtrelenmiyor; iptal edilmiş kartlar da sayılıyor. " +
                           $"\"X kartı olan müşteri\" ve kart sayımlarında yalnızca aktif kartlar kullanılır: {o.Groups[1].Value}.Durum = 'Aktif' ekle.");
                break;
            }
        }

        foreach (Match e in ExistsOver().Matches(sql))
        {
            var table = e.Groups[1].Value;
            var alias = Regex.Escape(e.Groups[2].Value);
            var body = ParenBody(sql, e.Index);
            if (table.Equals("Kart", StringComparison.OrdinalIgnoreCase) && !Has(body, @"\bIslem\b") && !Has(body, $@"\b{alias}\.Durum\b") && !KartStatusQuestion(question))
            {
                issues.Add($"EXISTS ile kart sahipliği kontrol ediliyor ama '{e.Groups[2].Value}.Durum' filtresi yok; iptal kartlar da sayılıyor. Alt sorguya {e.Groups[2].Value}.Durum = 'Aktif' ekle.");
                break;
            }
            if (table.Equals("Islem", StringComparison.OrdinalIgnoreCase) && question != null && SpendQuestion().IsMatch(question) && !Has(body, $@"\b{alias}\.IslemTipi\b"))
            {
                issues.Add($"Soru harcama hakkında ama Islem üzerindeki EXISTS alt sorgusunda '{e.Groups[2].Value}.IslemTipi' filtresi yok; iadeler de harcama sayılıyor. " +
                           $"Alt sorguya {e.Groups[2].Value}.IslemTipi = 'Harcama' ekle.");
                break;
            }
        }

        if (question != null && FxQuestion().IsMatch(question) && HesapRef().IsMatch(sql) && !sql.Contains("'Döviz'", StringComparison.OrdinalIgnoreCase))
            issues.Add("\"Döviz hesabı\" HesapTuru = 'Döviz' olan hesaplardır (USD ve EUR'nun ikisi birden). " +
                       "ParaBirimi = 'USD' gibi tek bir para birimiyle filtreleme; h.HesapTuru = 'Döviz' kullan.");

        if (UsesMusteri().IsMatch(sql))
            foreach (Match e in ExistsBoundToOuterCard().Matches(sql))
                if (!Has(e.Groups[2].Value, $@"\bJOIN\s+Kart\s+(?:AS\s+)?{Regex.Escape(e.Groups[3].Value)}\b"))
                {
                    issues.Add($"EXISTS alt sorgusu '{e.Groups[1].Value}.KartId = {e.Groups[3].Value}.Id' ile dıştaki TEK bir karta bağlı; " +
                               "müşterinin diğer kartlarındaki işlemler görülmüyor. Müşteri bazlı koşulda müşterinin tüm kartlarına bak: " +
                               "EXISTS (SELECT 1 FROM Islem i2 JOIN Kart k2 ON i2.KartId = k2.Id WHERE k2.MusteriId = m.Id AND ...). " +
                               "Birden çok koşul (her kanal, her sektör) için GROUP BY k.MusteriId HAVING COUNT(DISTINCT ...) da kullanabilirsin.");
                    break;
                }

        return issues;
    }

    static bool KartStatusQuestion(string? question) =>
        question != null && Regex.IsMatch(question, @"iptal|kapal[ıi]|t[üu]m\s+kart|b[üu]t[üu]n\s+kart", RegexOptions.IgnoreCase);
}
