using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Diyalog360.Api.Data;

/// <summary>
/// Banka benzeri demo veritabanını üretir. Tarihler üretim anına göre görelidir (son 24 ay),
/// sonuçların ilginç çıkması için veriye bilinçli desenler konur:
///  - Seyahat yazın, Giyim/Elektronik Kasım-Aralık'ta, Eğitim Eylül'de zirve yapar
///  - E-ticaret her ay büyür
///  - İzmir Alsancak şubesinin harcaması son 6 ayda düşer
///  - Platinum kartların ~%25'i son 4-8 aydır hiç işlem yapmıyor
///  - Kampanya dönemlerinde ilgili sektörde harcama artar
/// </summary>
public static class DemoSeeder
{
    const string Schema = """
        CREATE TABLE Sube (Id INTEGER PRIMARY KEY, Ad TEXT NOT NULL, Sehir TEXT NOT NULL, Bolge TEXT NOT NULL);
        CREATE TABLE Musteri (Id INTEGER PRIMARY KEY, AdSoyad TEXT NOT NULL, TCKN TEXT NOT NULL, Telefon TEXT,
            Segment TEXT NOT NULL CHECK (Segment IN ('Bireysel','Premium','KOBİ')),
            SubeId INTEGER NOT NULL REFERENCES Sube(Id), DogumYili INTEGER, KayitTarihi TEXT NOT NULL);
        CREATE TABLE Hesap (Id INTEGER PRIMARY KEY, MusteriId INTEGER NOT NULL REFERENCES Musteri(Id),
            HesapTuru TEXT NOT NULL, ParaBirimi TEXT NOT NULL, Bakiye REAL NOT NULL, AcilisTarihi TEXT NOT NULL, Durum TEXT NOT NULL);
        CREATE TABLE Kart (Id INTEGER PRIMARY KEY, MusteriId INTEGER NOT NULL REFERENCES Musteri(Id),
            KartTipi TEXT NOT NULL CHECK (KartTipi IN ('Classic','Gold','Platinum')), KartLimiti REAL NOT NULL,
            AcilisTarihi TEXT NOT NULL, Durum TEXT NOT NULL);
        CREATE TABLE Sektor (Id INTEGER PRIMARY KEY, Ad TEXT NOT NULL);
        CREATE TABLE Islem (Id INTEGER PRIMARY KEY, KartId INTEGER NOT NULL REFERENCES Kart(Id),
            SektorId INTEGER NOT NULL REFERENCES Sektor(Id), Tutar REAL NOT NULL,
            IslemTipi TEXT NOT NULL CHECK (IslemTipi IN ('Harcama','İade')), Kanal TEXT NOT NULL, IslemTarihi TEXT NOT NULL);
        CREATE TABLE Urun (Id INTEGER PRIMARY KEY, Ad TEXT NOT NULL, Kategori TEXT NOT NULL);
        CREATE TABLE MusteriUrun (MusteriId INTEGER NOT NULL REFERENCES Musteri(Id), UrunId INTEGER NOT NULL REFERENCES Urun(Id),
            BaslangicTarihi TEXT NOT NULL, PRIMARY KEY (MusteriId, UrunId));
        CREATE TABLE Kampanya (Id INTEGER PRIMARY KEY, Ad TEXT NOT NULL, SektorId INTEGER NOT NULL REFERENCES Sektor(Id),
            BaslangicTarihi TEXT NOT NULL, BitisTarihi TEXT NOT NULL, IndirimOrani REAL NOT NULL);
        """;

    const string Indexes = """
        CREATE INDEX IX_Musteri_SubeId ON Musteri(SubeId);
        CREATE INDEX IX_Hesap_MusteriId ON Hesap(MusteriId);
        CREATE INDEX IX_Kart_MusteriId ON Kart(MusteriId);
        CREATE INDEX IX_Islem_KartId ON Islem(KartId);
        CREATE INDEX IX_Islem_SektorId ON Islem(SektorId);
        CREATE INDEX IX_Islem_IslemTarihi ON Islem(IslemTarihi);
        CREATE INDEX IX_MusteriUrun_UrunId ON MusteriUrun(UrunId);
        ANALYZE;
        """;

    static readonly (string Ad, string Sehir, string Bolge, double Agirlik)[] Subeler =
    [
        ("Kadıköy", "İstanbul", "Marmara", 1.4), ("Beşiktaş", "İstanbul", "Marmara", 1.3),
        ("Levent", "İstanbul", "Marmara", 1.2), ("Bakırköy", "İstanbul", "Marmara", 1.1),
        ("Kızılay", "Ankara", "İç Anadolu", 1.0), ("Çankaya", "Ankara", "İç Anadolu", 0.9),
        ("Alsancak", "İzmir", "Ege", 0.9), ("Karşıyaka", "İzmir", "Ege", 0.7),
        ("Nilüfer", "Bursa", "Marmara", 0.7), ("Muratpaşa", "Antalya", "Akdeniz", 0.6),
        ("Seyhan", "Adana", "Akdeniz", 0.5), ("Ortahisar", "Trabzon", "Karadeniz", 0.4),
    ];

    // Ad, ortalama tutar, temel ağırlık
    static readonly (string Ad, double Ortalama, double Agirlik)[] Sektorler =
    [
        ("Market", 450, 30), ("Akaryakıt", 1100, 14), ("Restoran", 600, 14), ("Giyim", 1300, 8),
        ("Elektronik", 4200, 4), ("Seyahat", 5500, 3), ("Sağlık", 900, 5), ("Eğitim", 3000, 2),
        ("E-ticaret", 850, 12), ("Eğlence", 400, 8),
    ];

    static readonly (string Ad, string Kategori)[] Urunler =
    [
        ("Kredi Kartı", "Ödeme"), ("İhtiyaç Kredisi", "Kredi"), ("Konut Kredisi", "Kredi"), ("Taşıt Kredisi", "Kredi"),
        ("Bireysel Emeklilik", "Yatırım"), ("Yatırım Fonu", "Yatırım"), ("Vadeli Mevduat", "Mevduat"),
        ("Hayat Sigortası", "Sigorta"), ("Kasko", "Sigorta"), ("POS Cihazı", "Ödeme"),
    ];

    static readonly string[] Adlar = ["Ahmet", "Mehmet", "Mustafa", "Ali", "Hüseyin", "Hasan", "İbrahim", "Murat", "Emre", "Burak",
        "Ayşe", "Fatma", "Zeynep", "Elif", "Emine", "Hatice", "Merve", "Esra", "Selin", "Deniz", "Can", "Ece", "Kerem", "İrem"];
    static readonly string[] Soyadlar = ["Yılmaz", "Kaya", "Demir", "Şahin", "Çelik", "Yıldız", "Yıldırım", "Öztürk", "Aydın",
        "Özdemir", "Arslan", "Doğan", "Kılıç", "Aslan", "Çetin", "Kara", "Koç", "Kurt", "Özkan", "Şimşek"];

    public static void Seed(string path)
    {
        if (File.Exists(path)) File.Delete(path);
        var rnd = new Random(360);
        var now = DateTime.Now;
        var inv = CultureInfo.InvariantCulture;
        string D(DateTime d) => d.ToString("yyyy-MM-dd", inv);
        string DT(DateTime d) => d.ToString("yyyy-MM-dd HH:mm:ss", inv);

        using var conn = new SqliteConnection($"Data Source={path};Pooling=False");
        conn.Open();
        Exec(conn, Schema);
        using var tx = conn.BeginTransaction();

        // Şubeler
        for (int i = 0; i < Subeler.Length; i++)
            Insert(conn, "INSERT INTO Sube VALUES ($a,$b,$c,$d)", i + 1, Subeler[i].Ad, Subeler[i].Sehir, Subeler[i].Bolge);

        for (int i = 0; i < Sektorler.Length; i++)
            Insert(conn, "INSERT INTO Sektor VALUES ($a,$b)", i + 1, Sektorler[i].Ad);

        for (int i = 0; i < Urunler.Length; i++)
            Insert(conn, "INSERT INTO Urun VALUES ($a,$b,$c)", i + 1, Urunler[i].Ad, Urunler[i].Kategori);

        // Kampanyalar: son 12 ay içinde, sektöre bağlı, harcamayı artırır
        var kampanyalar = new List<(int SektorId, DateTime Bas, DateTime Bit)>();
        var kampanyaTanim = new (string Ad, string Sektor, int AyOnce, int Gun, double Oran)[]
        {
            ("Yaz Tatili Fırsatı", "Seyahat", 4, 45, 0.15), ("Okula Dönüş", "Eğitim", 2, 30, 0.10),
            ("Kasım İndirimleri", "Elektronik", 11, 20, 0.20), ("Yılbaşı Alışverişi", "Giyim", 10, 25, 0.15),
            ("Akaryakıt Puan", "Akaryakıt", 6, 60, 0.05), ("Restoran Haftası", "Restoran", 3, 14, 0.25),
            ("Online Bahar", "E-ticaret", 7, 30, 0.10), ("Sinema Keyfi", "Eğlence", 1, 21, 0.30),
        };
        for (int i = 0; i < kampanyaTanim.Length; i++)
        {
            var k = kampanyaTanim[i];
            var bas = new DateTime(now.Year, now.Month, 1).AddMonths(-k.AyOnce).AddDays(rnd.Next(0, 10));
            var bit = bas.AddDays(k.Gun);
            int sektorId = Array.FindIndex(Sektorler, s => s.Ad == k.Sektor) + 1;
            kampanyalar.Add((sektorId, bas, bit));
            Insert(conn, "INSERT INTO Kampanya VALUES ($a,$b,$c,$d,$e,$f)", i + 1, k.Ad, sektorId, D(bas), D(bit), k.Oran);
        }

        // Müşteriler
        const int musteriSayisi = 5000;
        var musteriSube = new int[musteriSayisi + 1];
        var musteriSegment = new string[musteriSayisi + 1];
        double toplamAgirlik = Subeler.Sum(s => s.Agirlik);
        for (int id = 1; id <= musteriSayisi; id++)
        {
            int sube = Pick(rnd, Subeler.Select(s => s.Agirlik).ToArray(), toplamAgirlik) + 1;
            double r = rnd.NextDouble();
            string segment = r < 0.70 ? "Bireysel" : r < 0.90 ? "Premium" : "KOBİ";
            musteriSube[id] = sube;
            musteriSegment[id] = segment;
            var kayit = now.AddDays(-rnd.Next(30, 365 * 8));
            Insert(conn, "INSERT INTO Musteri VALUES ($a,$b,$c,$d,$e,$f,$g,$h)", id,
                $"{Adlar[rnd.Next(Adlar.Length)]} {Soyadlar[rnd.Next(Soyadlar.Length)]}",
                (10000000000L + (long)(rnd.NextDouble() * 89999999999L)).ToString(inv),
                $"05{rnd.Next(30, 60)}{rnd.Next(1000000, 9999999)}",
                segment, sube, rnd.Next(1950, 2005), D(kayit));

            // Hesaplar
            double segCarpan = segment == "Premium" ? 4 : segment == "KOBİ" ? 6 : 1;
            InsertHesap(conn, rnd, id, "Vadesiz", "TRY", LogNormal(rnd, 15000 * segCarpan, 1.0), now);
            if (rnd.NextDouble() < 0.4) InsertHesap(conn, rnd, id, "Vadeli", "TRY", LogNormal(rnd, 80000 * segCarpan, 0.9), now);
            if (rnd.NextDouble() < (segment == "Bireysel" ? 0.10 : 0.35))
                InsertHesap(conn, rnd, id, "Döviz", rnd.NextDouble() < 0.6 ? "USD" : "EUR", LogNormal(rnd, 5000 * segCarpan, 1.0), now);

            // Ürünler
            var urunSayisi = 1 + rnd.Next(segment == "Bireysel" ? 3 : 5);
            foreach (var urun in Enumerable.Range(1, Urunler.Length).OrderBy(_ => rnd.Next()).Take(urunSayisi))
            {
                if (urun == 10 && segment != "KOBİ") continue; // POS cihazı yalnızca KOBİ
                Insert(conn, "INSERT INTO MusteriUrun VALUES ($a,$b,$c)", id, urun, D(kayit.AddDays(rnd.Next(0, 300))));
            }
        }

        // Kartlar
        var kartlar = new List<(int Id, int MusteriId, string Tip, DateTime Acilis, DateTime? SonAktif)>();
        int kartId = 0;
        for (int m = 1; m <= musteriSayisi; m++)
        {
            int adet = rnd.NextDouble() < 0.35 ? 2 : 1;
            for (int k = 0; k < adet; k++)
            {
                string seg = musteriSegment[m];
                double r = rnd.NextDouble();
                string tip = seg switch
                {
                    "Premium" => r < 0.45 ? "Platinum" : "Gold",
                    "KOBİ" => r < 0.30 ? "Platinum" : r < 0.80 ? "Gold" : "Classic",
                    _ => r < 0.65 ? "Classic" : r < 0.95 ? "Gold" : "Platinum",
                };
                var acilis = now.AddDays(-rnd.Next(60, 365 * 6));
                // Platinum kartların ~%25'i 4-8 ay önce kullanılmaz hale gelir
                DateTime? sonAktif = tip == "Platinum" && rnd.NextDouble() < 0.25 ? now.AddDays(-rnd.Next(120, 240)) : null;
                double limit = tip switch { "Platinum" => 150000, "Gold" => 60000, _ => 20000 } * (0.6 + rnd.NextDouble() * 0.8);
                kartId++;
                kartlar.Add((kartId, m, tip, acilis, sonAktif));
                Insert(conn, "INSERT INTO Kart VALUES ($a,$b,$c,$d,$e,$f)", kartId, m, tip, Math.Round(limit / 1000) * 1000,
                    D(acilis), rnd.NextDouble() < 0.04 ? "İptal" : "Aktif");
            }
        }

        // İşlemler
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO Islem (KartId, SektorId, Tutar, IslemTipi, Kanal, IslemTarihi) VALUES ($k,$s,$t,$i,$c,$d)";
        var pK = cmd.Parameters.Add("$k", SqliteType.Integer);
        var pS = cmd.Parameters.Add("$s", SqliteType.Integer);
        var pT = cmd.Parameters.Add("$t", SqliteType.Real);
        var pI = cmd.Parameters.Add("$i", SqliteType.Text);
        var pC = cmd.Parameters.Add("$c", SqliteType.Text);
        var pD = cmd.Parameters.Add("$d", SqliteType.Text);
        int alsancak = Array.FindIndex(Subeler, s => s.Ad == "Alsancak") + 1;
        var ilkAy = new DateTime(now.Year, now.Month, 1).AddMonths(-23);

        for (int ay = 0; ay < 24; ay++)
        {
            var ayBas = ilkAy.AddMonths(ay);
            var ayBit = ayBas.AddMonths(1) > now ? now : ayBas.AddMonths(1);
            double ayOrani = (ayBit - ayBas).TotalDays / DateTime.DaysInMonth(ayBas.Year, ayBas.Month);
            int ayOnce = 23 - ay;

            // Bu ay için sektör ağırlıkları (mevsimsellik + trend)
            var agirlik = Sektorler.Select(s => s.Agirlik).ToArray();
            agirlik[Idx("Seyahat")] *= ayBas.Month is 6 or 7 or 8 ? 2.6 : ayBas.Month is 12 or 1 ? 1.3 : 1.0;
            agirlik[Idx("Giyim")] *= ayBas.Month is 11 or 12 ? 1.9 : 1.0;
            agirlik[Idx("Elektronik")] *= ayBas.Month is 11 or 12 ? 2.2 : 1.0;
            agirlik[Idx("Eğitim")] *= ayBas.Month == 9 ? 3.5 : 1.0;
            agirlik[Idx("E-ticaret")] *= 1.0 + ay * 0.05;
            double agirlikToplam = agirlik.Sum();

            foreach (var kart in kartlar)
            {
                if (kart.Acilis > ayBit) continue;
                if (kart.SonAktif is { } sa && sa < ayBas) continue;
                int musteri = kart.MusteriId;
                double lambda = kart.Tip switch { "Platinum" => 1.9, "Gold" => 1.3, _ => 0.85 } * ayOrani;
                if (musteriSube[musteri] == alsancak && ayOnce < 6) lambda *= 1.0 - (6 - ayOnce) * 0.09;
                int n = Poisson(rnd, lambda);
                double segCarpan = musteriSegment[musteri] switch { "Premium" => 1.6, "KOBİ" => 2.0, _ => 1.0 };

                for (int j = 0; j < n; j++)
                {
                    var tarih = ayBas.AddSeconds(rnd.NextDouble() * (ayBit - ayBas).TotalSeconds);
                    if (kart.SonAktif is { } son && tarih > son) continue;
                    int s = Pick(rnd, agirlik, agirlikToplam);
                    int sektorId = s + 1;
                    double tutar = LogNormal(rnd, Sektorler[s].Ortalama * segCarpan, 0.7);
                    if (kampanyalar.Any(k => k.SektorId == sektorId && tarih >= k.Bas && tarih <= k.Bit)) tutar *= 1.5;
                    string kanal = Sektorler[s].Ad == "E-ticaret"
                        ? (rnd.NextDouble() < 0.6 ? "İnternet" : "Mobil")
                        : (rnd.NextDouble() < 0.75 ? "POS" : rnd.NextDouble() < 0.5 ? "İnternet" : "Mobil");
                    pK.Value = kart.Id; pS.Value = sektorId; pT.Value = Math.Round(tutar, 2);
                    pI.Value = rnd.NextDouble() < 0.02 ? "İade" : "Harcama";
                    pC.Value = kanal; pD.Value = DT(tarih);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        tx.Commit();
        Exec(conn, Indexes);

        static int Idx(string ad) => Array.FindIndex(Sektorler, s => s.Ad == ad);

        static void InsertHesap(SqliteConnection c, Random rnd, int musteri, string tur, string pb, double bakiye, DateTime now) =>
            Insert(c, "INSERT INTO Hesap (MusteriId, HesapTuru, ParaBirimi, Bakiye, AcilisTarihi, Durum) VALUES ($a,$b,$c,$d,$e,$f)",
                musteri, tur, pb, Math.Round(bakiye, 2), now.AddDays(-rnd.Next(30, 365 * 7)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                rnd.NextDouble() < 0.05 ? "Kapalı" : "Aktif");
    }

    static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    static void Insert(SqliteConnection c, string sql, params object[] values)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        var names = new[] { "$a", "$b", "$c", "$d", "$e", "$f", "$g", "$h" };
        for (int i = 0; i < values.Length; i++) cmd.Parameters.AddWithValue(names[i], values[i]);
        cmd.ExecuteNonQuery();
    }

    static int Pick(Random rnd, double[] weights, double total)
    {
        double r = rnd.NextDouble() * total;
        for (int i = 0; i < weights.Length; i++)
        {
            r -= weights[i];
            if (r <= 0) return i;
        }
        return weights.Length - 1;
    }

    static int Poisson(Random rnd, double lambda)
    {
        double l = Math.Exp(-lambda), p = 1;
        int k = 0;
        do { k++; p *= rnd.NextDouble(); } while (p > l);
        return k - 1;
    }

    static double LogNormal(Random rnd, double mean, double sigma)
    {
        double u1 = 1.0 - rnd.NextDouble(), u2 = rnd.NextDouble();
        double z = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        double mu = Math.Log(mean) - sigma * sigma / 2;
        return Math.Exp(mu + sigma * z);
    }
}
