using System.Globalization;
using System.Text.RegularExpressions;

namespace Diyalog360.Api.Services;

/// <summary>
/// Modelin gereksiz netleştirme sorusu sormasını engeller. Sözlükteki belirsiz sıfatlardan (iyi, değerli, önemli, büyük,
/// performans, başarı...) hiçbiri soruda yoksa netleştirme cevabı modele bir kez geri gönderilir ve rapor üretmesi istenir.
/// Model ikinci kez de netleştirmede ısrar ederse soru gerçekten belirsiz kabul edilir ve kullanıcıya sorulur.
/// </summary>
public static partial class ClarifyGuard
{
    [GeneratedRegex(@"\biyi|değerli|önemli|büyük|performans|başar|verimli|\bk[âa]rl[ıi]|potansiyel|sadık")]
    private static partial Regex AmbiguousTerm();

    public static bool HasAmbiguousTerm(string message) =>
        AmbiguousTerm().IsMatch(message.ToLower(CultureInfo.GetCultureInfo("tr-TR")));

    public const string Challenge =
        "Bu soruda sözlükteki belirsiz sıfatlardan (iyi, değerli, önemli, büyük, performans, başarı) hiçbiri yok; ölçü cümlenin içinde. " +
        "\"En çok / en fazla / en yüksek\" ifadeleri belirsiz değildir (örn. \"en çok müşteriye satılmış\" = müşteri sayısı, \"en çok harcama yapan\" = harcama tutarı). " +
        "Netleştirme sorma: tur \"rapor\" olan JSON'u döndür; küçük belirsizlikleri varsayimlar alanına yaz. " +
        "Soru gerçekten birbirinden çok farklı sonuçlar veren birden fazla ölçüye açıksa yeniden netleştirme döndürebilirsin.";
}
