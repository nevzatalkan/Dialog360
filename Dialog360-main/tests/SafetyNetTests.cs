using Diyalog360.Api.Services;

namespace Diyalog360.Tests;

public sealed class PiiFilterTests
{
    [Theory]
    [InlineData("Son 3 ayda en çok harcama yapan 5 müşterinin adını ve telefon numarasını getir")]
    [InlineData("Müşterilerin TC kimlik numaralarını listele")]
    [InlineData("Premium müşterilerin ad soyad ve e-posta bilgileri")]
    [InlineData("Harcaması düşen müşterilere SMS için telefon listesi hazırla")]
    [InlineData("En çok harcayan müşteri isimleri")]
    public void Kisisel_veri_istegi_yakalanir(string q) => Assert.True(PiiFilter.IsPiiRequest(q));

    [Theory]
    [InlineData("Aktif TL mevduatı en yüksek 10 müşterinin müşteri numarası, segmenti, şube adı ve toplam bakiyesi")]
    [InlineData("Segmentlere göre müşteri adedi")]
    [InlineData("Şube adlarına göre toplam harcama")]
    [InlineData("En çok müşteriye satılmış 3 ürün hangisi?")]
    public void Normal_rapor_istegi_gecer(string q) => Assert.False(PiiFilter.IsPiiRequest(q));
}

public sealed class SqlErrorExplainerTests(DemoDbFixture db) : IClassFixture<DemoDbFixture>
{
    [Fact]
    public void Tanimsiz_alias_icin_join_onerir()
    {
        const string sql = "SELECT sb.Bolge, COUNT(DISTINCT m.Id) FROM Musteri m JOIN Kart k ON k.MusteriId = m.Id GROUP BY sb.Bolge";
        var msg = SqlErrorExplainer.Explain("SQL hatası: no such column: sb.Bolge", sql, db.Catalog);
        Assert.Contains("tanımlı değil", msg);
        Assert.Contains("JOIN Sube sb ON m.SubeId = sb.Id", msg);
    }

    [Fact]
    public void CTE_ile_tekrar_kullanilan_alias_aciklanir()
    {
        const string sql = "WITH kartlar AS (SELECT k.KartTipi AS KartTipi, k.Id AS KartId FROM Kart k WHERE k.Durum = 'Aktif') " +
                           "SELECT k.KartTipi, COUNT(*) FROM kartlar k JOIN Islem i ON i.KartId = k.KartId WHERE date(i.IslemTarihi) >= k.AcilisTarihi GROUP BY k.KartTipi";
        var msg = SqlErrorExplainer.Explain("SQL hatası: no such column: k.AcilisTarihi", sql, db.Catalog);
        Assert.Contains("'kartlar' adlı CTE", msg);
        Assert.Contains("SELECT listesine ekle", msg);
    }

    [Fact]
    public void Olmayan_kolon_icin_en_yakin_kolonlar_onerilir()
    {
        var msg = SqlErrorExplainer.Explain("SQL hatası: no such column: k.Limit", "SELECT AVG(k.Limit) FROM Kart k", db.Catalog);
        Assert.Contains("KartLimiti", msg);
    }
}

public sealed class ClarifyGuardTests
{
    [Theory]
    [InlineData("En iyi müşterilerimiz kimler?")]
    [InlineData("Şubelerimizin performansını karşılaştırır mısın?")]
    [InlineData("Geçen ay en büyük müşterilerimiz hangi şubelerde?")]
    [InlineData("Kart tiplerimizin performansını kıyaslar mısın?")]
    [InlineData("En değerli segment hangisi?")]
    public void Belirsiz_sifat_varsa_netlestirmeye_izin_verilir(string q) => Assert.True(ClarifyGuard.HasAmbiguousTerm(q));

    [Theory]
    [InlineData("En çok müşteriye satılmış 3 ürün hangisi?")]
    [InlineData("Son 3 ayda en çok harcama yapan 5 müşteri")]
    [InlineData("Geçen ay en yüksek iade tutarı olan sektör")]
    [InlineData("Ortalama kart limiti en yüksek 3 şube")]
    public void Olcu_belli_ise_netlestirme_sorgulanir(string q) => Assert.False(ClarifyGuard.HasAmbiguousTerm(q));
}

public sealed class PersonalDataSettingTests : IDisposable
{
    readonly string _apiDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "api"));
    readonly string _db = Path.Combine(Path.GetTempPath(), $"d360-pii-{Guid.NewGuid():N}.db");

    public PersonalDataSettingTests() => Diyalog360.Api.Data.DemoSeeder.Seed(_db);

    public void Dispose() { try { File.Delete(_db); } catch (IOException) { } }

    SchemaCatalog Catalog(bool protect) => new(_db, Path.Combine(_apiDir, "Prompt", "schema.notes.json"), protect);

    [Fact]
    public void Koruma_kapaliyken_kisisel_veri_sorgulanabilir()
    {
        var catalog = Catalog(protect: false);
        var runner = new QueryRunner(_db, new SqlGuard(catalog), catalog, maxRows: 1000, timeoutSeconds: 5);
        Assert.Equal(GuardStatus.Ok, runner.Run("SELECT AdSoyad, TCKN, Telefon FROM Musteri LIMIT 5").Status);
        Assert.DoesNotContain("KİŞİSEL VERİ", catalog.PromptText);
        Assert.DoesNotContain("Kişisel veri koruması", new PromptBuilder(Path.Combine(_apiDir, "Prompt"), catalog).SystemPrompt);
    }

    [Fact]
    public void Koruma_acikken_kisisel_veri_engellenir()
    {
        var catalog = Catalog(protect: true);
        var runner = new QueryRunner(_db, new SqlGuard(catalog), catalog, maxRows: 1000, timeoutSeconds: 5);
        Assert.NotEqual(GuardStatus.Ok, runner.Run("SELECT AdSoyad FROM Musteri LIMIT 5").Status);
        Assert.Contains("Kişisel veri koruması", new PromptBuilder(Path.Combine(_apiDir, "Prompt"), catalog).SystemPrompt);
    }
}

public sealed class RetGuardTests
{
    [Theory]
    [InlineData("Her müşterinin İLK kampanya katılım işlem tarihini bulup sonraki 30 gün içindeki işlemleri analiz ediyoruz, segmentasyon yap")]
    [InlineData("Müşterilerin kart işlemlerini analiz edip kanal kullanımına göre segmentasyon yap")]
    public void Okuma_amacli_analiz_reddi_sorgulanir(string q) => Assert.True(RetGuard.ShouldChallenge(q));

    [Theory]
    [InlineData("Müşteri tablosunu sil")]
    [InlineData("Platinum kartların limitlerini yüzde 20 artır")]
    [InlineData("Kampanya tablosuna Kasım ayı için yeni bir Elektronik kampanyası ekle")]
    [InlineData("Yarın İstanbul'da hava nasıl olacak?")]
    public void Gercek_ret_dokunulmaz(string q) => Assert.False(RetGuard.ShouldChallenge(q));
}

public sealed class SqlLintCteAliasTests
{
    [Fact]
    public void CTE_icinde_filtrelenmis_tutar_yanlis_alarm_vermez() =>
        Assert.Empty(SqlLint.Issues("WITH ks AS (SELECT k.MusteriId AS MusteriId, i.Tutar AS Tutar FROM Islem i JOIN Kart k ON i.KartId = k.Id WHERE i.IslemTipi = 'Harcama') SELECT m.Segment, SUM(ks.Tutar) FROM ks JOIN Musteri m ON ks.MusteriId = m.Id GROUP BY m.Segment"));
}
