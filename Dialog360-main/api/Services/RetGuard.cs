using System.Globalization;
using System.Text.RegularExpressions;

namespace Diyalog360.Api.Services;

/// <summary>
/// Modelin okuma amaçlı uzun/karmaşık rapor isteklerini "veri değiştirme" sanıp reddetmesini engeller.
/// Soruda veri değiştiren bir fiil yok ve soru bu veritabanının konularından (müşteri, işlem, kart, kampanya...) bahsediyorsa
/// ret cevabı modele bir kez geri gönderilir. Model ikinci kez de reddederse ret kabul edilir; asıl güvenlik zaten
/// read-only bağlantı ve SQL doğrulayıcıdadır, bu katman yalnızca gereksiz retleri azaltır.
/// </summary>
public static partial class RetGuard
{
    [GeneratedRegex(@"\bsil\b|\bsilin|\bsilmek|\bsilip|güncelle|\bekle\b|\bekleyin|\beklesene|değiştir|\bartır|\bazalt|kaydet|oluştur|\bkaldır|\bdrop\b|\bdelete\b|\bupdate\b|\binsert\b|\btruncate\b|\balter\b")]
    private static partial Regex ModifyVerb();

    [GeneratedRegex(@"müşteri|işlem|kart|hesap|harcama|kampanya|şube|segment|sektör|ürün|mevduat|bakiye|limit|kanal|iade|ciro|sepet")]
    private static partial Regex DomainTerm();

    public static bool ShouldChallenge(string message)
    {
        var m = message.ToLower(CultureInfo.GetCultureInfo("tr-TR"));
        return DomainTerm().IsMatch(m) && !ModifyVerb().IsMatch(m);
    }

    public const string Challenge =
        "Bu istek veriyi değiştirmiyor; yalnızca okuyan bir analiz/rapor isteği. \"İşlem\" kart işlemi (Islem tablosu) demektir; " +
        "\"analiz et\", \"segmentasyon yap\", \"yakala\" ifadeleri SELECT ile cevaplanır. Tur \"rapor\" olan JSON'u döndür: gerekirse CTE'lerle tek bir SELECT yaz, " +
        "yorumladığın noktaları varsayimlar alanına yaz. İstek gerçekten birden çok şekilde anlaşılıyorsa netleştirme sorabilirsin.";
}
