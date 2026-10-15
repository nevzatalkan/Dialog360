using Diyalog360.Api.Services;

namespace Diyalog360.Tests;

public sealed class SqlLintTests
{
    [Theory]
    [InlineData("SELECT 1 FROM Islem i WHERE i.IslemTarihi >= date('now','localtime','-6 months')")]
    [InlineData("SELECT 1 FROM Islem i WHERE i.IslemTarihi >= date('now', 'localtime', '-12 month')")]
    [InlineData("SELECT 1 FROM Islem i WHERE i.IslemTarihi >= datetime('now','-3 months')")]
    public void Kayan_ay_penceresi_yakalanir(string sql) => Assert.NotNull(SqlLint.Check(sql));

    [Theory]
    [InlineData("SELECT 1 FROM Islem i WHERE i.IslemTarihi >= date('now','localtime','start of month','-6 months') AND i.IslemTarihi < date('now','localtime','start of month')")]
    [InlineData("SELECT 1 FROM Islem i WHERE i.IslemTarihi >= date('now','localtime','-90 days')")]
    [InlineData("SELECT CAST(strftime('%Y','now') AS INTEGER) - DogumYili FROM Musteri")]
    public void Kurala_uygun_tarihler_gecer(string sql) => Assert.Null(SqlLint.Check(sql));

    [Fact]
    public void Ayni_donem_sorusunda_tum_gecen_yil_reddedilir()
    {
        const string q = "Bu yıl ile geçen yılın aynı dönemini karşılaştır";
        Assert.NotNull(SqlLint.Check("SELECT 1 FROM Islem i WHERE i.IslemTarihi >= date('now','localtime','start of year','-1 year') AND i.IslemTarihi < date('now','localtime','start of year')", q));
        Assert.Null(SqlLint.Check("SELECT 1 FROM Islem i WHERE i.IslemTarihi >= date('now','localtime','start of year','-1 year') AND i.IslemTarihi < date('now','localtime','-1 year')", q));
    }

    [Theory]
    [InlineData("SELECT COUNT(*) FROM Islem i1 WHERE i1.SektorId = 7 AND i1.IslemTipi = 'Harcama'")]
    [InlineData("SELECT COUNT(*) FROM Musteri m WHERE m.SubeId IN (1, 2)")]
    [InlineData("SELECT COUNT(*) FROM Islem i JOIN Sektor s ON i.SektorId = s.Id WHERE s.Id = 3 AND i.IslemTipi = 'Harcama'")]
    public void Tahmin_edilen_id_filtresi_yakalanir(string sql) => Assert.NotNull(SqlLint.Check(sql));

    [Fact]
    public void Musteri_numarasiyla_filtre_serbesttir() =>
        Assert.Null(SqlLint.Check("SELECT SUM(h.Bakiye) FROM Hesap h WHERE h.MusteriId = 2567 AND h.Durum = 'Aktif'"));

    [Fact]
    public void IslemTipi_olmadan_tutar_toplami_yakalanir()
    {
        Assert.NotNull(SqlLint.Check("SELECT SUM(i.Tutar) FROM Islem i JOIN Kart k ON i.KartId = k.Id"));
        Assert.Null(SqlLint.Check("SELECT SUM(i.Tutar) FROM Islem i WHERE i.IslemTipi = 'Harcama'"));
        Assert.Null(SqlLint.Check("SELECT SUM(CASE WHEN i.IslemTipi = 'Harcama' THEN i.Tutar ELSE -i.Tutar END) FROM Islem i"));
    }

    [Fact]
    public void Kart_olcusu_ve_islem_ayni_seviyede_yakalanir()
    {
        Assert.NotNull(SqlLint.Check("SELECT sb.Ad, COUNT(DISTINCT k.Id), ROUND(AVG(k.KartLimiti), 2), SUM(i.Tutar) FROM Sube sb JOIN Musteri m ON sb.Id = m.SubeId JOIN Kart k ON k.MusteriId = m.Id JOIN Islem i ON i.KartId = k.Id WHERE i.IslemTipi = 'Harcama' GROUP BY sb.Ad"));
        Assert.Null(SqlLint.Check("SELECT sb.Ad, ROUND(AVG(k.KartLimiti), 2) FROM Kart k JOIN Musteri m ON k.MusteriId = m.Id JOIN Sube sb ON m.SubeId = sb.Id WHERE k.Durum = 'Aktif' GROUP BY sb.Ad"));
        Assert.Null(SqlLint.Check("SELECT k.KartTipi, ROUND(SUM(i.Tutar), 2) FROM Islem i JOIN Kart k ON i.KartId = k.Id WHERE i.IslemTipi = 'Harcama' GROUP BY k.KartTipi"));
    }

    [Fact]
    public void Alt_sorgudaki_tutar_toplaminda_IslemTipi_aranir() =>
        Assert.NotNull(SqlLint.Check("SELECT k.MusteriId, SUM(i.Tutar) FROM Islem i JOIN Kart k ON i.KartId = k.Id WHERE i.IslemTipi = 'Harcama' GROUP BY k.MusteriId HAVING SUM(i.Tutar) > 0.5 * (SELECT SUM(i2.Tutar) FROM Islem i2 JOIN Kart k2 ON i2.KartId = k2.Id WHERE k2.MusteriId = k.MusteriId)"));

    [Fact]
    public void Hesap_durumu_olmadan_yakalanir()
    {
        Assert.NotNull(SqlLint.Check("SELECT h.ParaBirimi, COUNT(DISTINCT h.MusteriId) FROM Hesap h WHERE h.HesapTuru = 'Döviz' GROUP BY h.ParaBirimi"));
        Assert.Null(SqlLint.Check("SELECT h.ParaBirimi, COUNT(DISTINCT h.MusteriId) FROM Hesap h WHERE h.HesapTuru = 'Döviz' AND h.Durum = 'Aktif' GROUP BY h.ParaBirimi"));
        Assert.Null(SqlLint.Check("SELECT COUNT(*) FROM Hesap h", "Kapalı hesaplar dahil tüm hesap sayısı"));
    }

    [Fact]
    public void Doviz_tek_para_birimine_indirgenemez() =>
        Assert.NotNull(SqlLint.Check("SELECT COUNT(*) FROM Hesap h WHERE h.ParaBirimi = 'USD' AND h.Durum = 'Aktif'", "Döviz hesabı olan müşteri sayısı"));

    [Fact]
    public void Ayni_donem_sorusunda_bu_yili_dislayan_where_yakalanir() =>
        Assert.NotNull(SqlLint.Check("SELECT SUM(CASE WHEN i.IslemTarihi >= date('now','localtime','start of year') THEN i.Tutar ELSE 0 END) FROM Islem i WHERE i.IslemTipi = 'Harcama' AND i.IslemTarihi >= date('now','localtime','start of year','-1 year') AND i.IslemTarihi < date('now','localtime','-1 year') AND i.IslemTarihi < date('now','localtime','start of year')", "Bu yıl ile geçen yılın aynı dönemi"));

    [Fact]
    public void Musteri_kosulunu_tek_karta_baglayan_exists_yakalanir()
    {
        Assert.NotNull(SqlLint.Check("SELECT m.Segment, COUNT(DISTINCT m.Id) FROM Musteri m JOIN Kart k ON k.MusteriId = m.Id WHERE EXISTS (SELECT 1 FROM Islem i2 WHERE i2.KartId = k.Id AND i2.IslemTipi = 'Harcama' AND i2.Kanal = 'Mobil') GROUP BY m.Segment"));
        Assert.Null(SqlLint.Check("SELECT k.KartTipi, COUNT(*) FROM Kart k WHERE EXISTS (SELECT 1 FROM Islem i WHERE i.KartId = k.Id AND i.IslemTipi = 'Harcama') GROUP BY k.KartTipi"));
        Assert.Null(SqlLint.Check("SELECT COUNT(DISTINCT m.Id) FROM Musteri m WHERE NOT EXISTS (SELECT 1 FROM Kart k2 JOIN Islem i ON i.KartId = k2.Id WHERE k2.MusteriId = m.Id)"));
    }
}

public sealed class SqlLintRegressionTests(DemoDbFixture db) : IClassFixture<DemoDbFixture>
{
    sealed record EvalItem(string id, string soru, string? beklenen_sql);

    /// <summary>Doğru olduğu bilinen hiçbir sorgu (eval'deki beklenen sorgular ve prompt örnekleri) kontrollere takılmamalı.</summary>
    [Fact]
    public void Beklenen_sorgular_ve_ornekler_kontrollerden_gecer()
    {
        var evalPath = Path.Combine(db.ApiDir, "..", "eval", "questions.json");
        var items = System.Text.Json.JsonSerializer.Deserialize<List<EvalItem>>(File.ReadAllText(evalPath))!;
        var failures = new List<string>();
        foreach (var item in items.Where(i => i.beklenen_sql != null))
        {
            var issues = SqlLint.Issues(item.beklenen_sql!, item.soru);
            if (issues.Count > 0) failures.Add($"{item.id}: {issues[0]}");
            if (SqlAutoFix.Apply(item.beklenen_sql!).Notes.Count > 0) failures.Add($"{item.id}: otomatik düzeltme beklenen sorguyu değiştirdi");
        }
        foreach (var line in File.ReadAllLines(Path.Combine(db.ApiDir, "Prompt", "examples.md")).Where(l => l.StartsWith('{')))
        {
            var sql = System.Text.Json.JsonDocument.Parse(line).RootElement.GetProperty("sql").GetString()!;
            if (sql.Length > 0 && SqlLint.Issues(sql).Count > 0) failures.Add($"örnek: {SqlLint.Issues(sql)[0]}");
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }
}

public sealed class SqlLintOwnershipTests
{
    [Fact]
    public void Kart_sahipliginde_durum_aranir()
    {
        Assert.NotNull(SqlLint.Check("SELECT COUNT(DISTINCT m.Id) FROM Musteri m JOIN Kart k ON k.MusteriId = m.Id WHERE k.KartTipi = 'Platinum'"));
        Assert.Null(SqlLint.Check("SELECT COUNT(DISTINCT m.Id) FROM Musteri m JOIN Kart k ON k.MusteriId = m.Id WHERE k.KartTipi = 'Platinum' AND k.Durum = 'Aktif'"));
        // Harcama için Islem'e bağlanan kart: iptal kartların geçmiş işlemleri de sayılır
        Assert.Null(SqlLint.Check("SELECT m.Segment, SUM(i.Tutar) FROM Musteri m JOIN Kart k ON k.MusteriId = m.Id JOIN Islem i ON i.KartId = k.Id WHERE i.IslemTipi = 'Harcama' GROUP BY m.Segment"));
        Assert.NotNull(SqlLint.Check("SELECT COUNT(*) FROM Musteri m WHERE EXISTS (SELECT 1 FROM Kart k WHERE k.MusteriId = m.Id AND k.KartTipi = 'Gold')"));
        Assert.Null(SqlLint.Check("SELECT COUNT(*) FROM Musteri m JOIN Kart k ON k.MusteriId = m.Id", "İptal edilenler dahil tüm kart sayısı"));
    }

    [Fact]
    public void Harcama_sorusunda_exists_islemtipi_aranir()
    {
        const string q = "Son 90 günde E-ticaret harcaması yapmış müşteri sayısı";
        Assert.NotNull(SqlLint.Check("SELECT COUNT(*) FROM Musteri m WHERE EXISTS (SELECT 1 FROM Islem i JOIN Kart k2 ON i.KartId = k2.Id JOIN Sektor s ON i.SektorId = s.Id WHERE k2.MusteriId = m.Id AND s.Ad = 'E-ticaret' AND i.IslemTarihi >= date('now','localtime','-90 days'))", q));
        Assert.Null(SqlLint.Check("SELECT COUNT(*) FROM Musteri m WHERE EXISTS (SELECT 1 FROM Islem i JOIN Kart k2 ON i.KartId = k2.Id JOIN Sektor s ON i.SektorId = s.Id WHERE k2.MusteriId = m.Id AND s.Ad = 'E-ticaret' AND i.IslemTipi = 'Harcama' AND i.IslemTarihi >= date('now','localtime','-90 days'))", q));
    }

    [Fact]
    public void Birden_fazla_ihlal_tek_mesajda_listelenir()
    {
        var msg = SqlLint.Check("SELECT k.KartTipi, SUM(i.Tutar) FROM Islem i JOIN Kart k ON i.KartId = k.Id WHERE i.IslemTarihi >= date('now','localtime','-6 months') GROUP BY k.KartTipi");
        Assert.NotNull(msg);
        Assert.Contains("2 kural ihlali", msg);
        Assert.Contains("aynen koru", msg);
    }
}

public sealed class SqlAutoFixTests
{
    [Fact]
    public void Kayan_ay_alt_siniri_takvim_ayina_cevrilir()
    {
        var (sql, notes) = SqlAutoFix.Apply("SELECT SUM(i.Tutar) FROM Islem i WHERE i.IslemTipi = 'Harcama' AND i.IslemTarihi >= date('now','localtime','-6 months')");
        Assert.Contains("i.IslemTarihi >= date('now','localtime','start of month','-6 months') AND i.IslemTarihi < date('now','localtime','start of month')", sql);
        Assert.Single(notes);
        Assert.Empty(SqlLint.Issues(sql));
    }

    [Fact]
    public void Dogru_sorgu_degismez()
    {
        const string ok = "SELECT 1 FROM Islem i WHERE i.IslemTarihi >= date('now','localtime','start of month','-6 months') AND i.IslemTarihi < date('now','localtime','start of month')";
        Assert.Equal(ok, SqlAutoFix.Apply(ok).Sql);
    }
}

public sealed class SqlAutoFixDateTests
{
    [Fact]
    public void Saatli_tarih_ile_gun_kiyasi_duzeltilir()
    {
        var (sql, notes) = SqlAutoFix.Apply("SELECT COUNT(*) FROM Islem i JOIN ilk ON 1 = 1 WHERE i.IslemTipi = 'Harcama' AND i.IslemTarihi > ilk.t0 AND i.IslemTarihi <= date(ilk.t0, '+30 days')");
        Assert.Contains("date(i.IslemTarihi) <= date(ilk.t0, '+30 days')", sql);
        Assert.Contains("i.IslemTarihi > ilk.t0", sql);
        Assert.Single(notes);
    }

    [Fact]
    public void Zaten_date_ile_sarili_karsilastirma_degismez()
    {
        const string ok = "SELECT 1 FROM Islem i WHERE date(i.IslemTarihi) <= date('now','localtime')";
        Assert.Equal(ok, SqlAutoFix.Apply(ok).Sql);
    }
}
