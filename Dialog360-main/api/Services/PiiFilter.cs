using System.Globalization;
using System.Text.RegularExpressions;

namespace Diyalog360.Api.Services;

/// <summary>
/// Kişisel veri (ad-soyad, TCKN, telefon, e-posta, adres) isteyen soruları LLM'e gitmeden deterministik olarak durdurur (KVKK).
/// Model bu istekleri bazen kişisel kolonsuz bir rapora çevirip cevaplıyordu; karar artık koddadır.
/// </summary>
public static partial class PiiFilter
{
    [GeneratedRegex(@"telefon|\bgsm\b|cep\s+no|tckn|t\.?\s?c\.?\s+kimlik|\btc\s+no|kimlik\s+no|kimlik\s+numara|ad[ıi]\s*soyad|\bad\s+soyad|isim\s+soyisim|e-?posta|e-?mail|\badres|müşteri\w*\s+(?:ad|ad[ıi]|ad[ıi]n[ıi]|adlar[ıi]|adlar[ıi]n[ıi]|isim\w*)\b")]
    private static partial Regex PiiRequest();

    public static bool IsPiiRequest(string message) =>
        PiiRequest().IsMatch(message.ToLower(CultureInfo.GetCultureInfo("tr-TR")));

    public const string Reason =
        "Bu istek müşterilerin kişisel verilerini (ad-soyad, T.C. kimlik no, telefon, e-posta, adres) içeriyor. " +
        "KVKK gereği Diyalog 360 kişisel veri listelemez; müşterileri müşteri numarasıyla raporlayabilirim.";
}
