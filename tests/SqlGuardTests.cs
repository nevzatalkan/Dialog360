using System.Text.Json;
using System.Text.RegularExpressions;
using Diyalog360.Api.Data;
using Diyalog360.Api.Services;

namespace Diyalog360.Tests;

public sealed class DemoDbFixture : IDisposable
{
    public string ApiDir { get; } = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "api"));
    public string DbPath { get; } = Path.Combine(Path.GetTempPath(), $"d360-test-{Guid.NewGuid():N}.db");
    public SchemaCatalog Catalog { get; }
    public QueryRunner Runner { get; }

    public DemoDbFixture()
    {
        DemoSeeder.Seed(DbPath);
        Catalog = new SchemaCatalog(DbPath, Path.Combine(ApiDir, "Prompt", "schema.notes.json"));
        Runner = new QueryRunner(DbPath, new SqlGuard(Catalog), Catalog, maxRows: 1000, timeoutSeconds: 5);
    }

    public void Dispose()
    {
        // Windows'ta dosya kilidi kalabilir; geçici dosyanın silinememesi testi bozmamalı
        try { File.Delete(DbPath); } catch (IOException) { }
    }
}

public sealed class SqlGuardTests(DemoDbFixture db) : IClassFixture<DemoDbFixture>
{
    [Theory]
    [InlineData("SELECT COUNT(*) FROM Musteri")]
    [InlineData("WITH t AS (SELECT KartId, SUM(Tutar) AS Toplam FROM Islem GROUP BY KartId) SELECT MAX(Toplam) FROM t")]
    [InlineData("SELECT Segment, COUNT(*) FROM Musteri GROUP BY Segment;")]
    [InlineData("SELECT 1 -- ; DROP TABLE Musteri")]
    [InlineData("SELECT strftime('%Y-%m', IslemTarihi) AS Ay, ROW_NUMBER() OVER (ORDER BY IslemTarihi) FROM Islem LIMIT 5")]
    public void Okuma_sorgulari_calisir(string sql) =>
        Assert.Equal(GuardStatus.Ok, db.Runner.Run(sql).Status);

    [Theory]
    [InlineData("DELETE FROM Musteri")]
    [InlineData("DROP TABLE Musteri")]
    [InlineData("UPDATE Hesap SET Bakiye = 0")]
    [InlineData("INSERT INTO Sektor (Ad) VALUES ('x')")]
    [InlineData("SELECT 1; DROP TABLE Musteri")]
    [InlineData("PRAGMA table_info(Musteri)")]
    [InlineData("ATTACH DATABASE '/tmp/x.db' AS x")]
    [InlineData("CREATE TABLE Yedek AS SELECT * FROM Musteri")]
    [InlineData("BEGIN TRANSACTION")]
    public void Yazma_ve_yonetim_islemleri_engellenir(string sql) =>
        Assert.Equal(GuardStatus.Forbidden, db.Runner.Run(sql).Status);

    [Theory]
    [InlineData("SELECT AdSoyad FROM Musteri", "AdSoyad")]
    [InlineData("SELECT COUNT(*) FROM Musteri WHERE TCKN LIKE '1%'", "TCKN")]
    [InlineData("SELECT * FROM Musteri", "AdSoyad")]
    public void Kisisel_veri_kolonlari_hicbir_yerde_kullanilamaz(string sql, string column)
    {
        var r = db.Runner.Run(sql);
        Assert.Equal(GuardStatus.Invalid, r.Status);
        Assert.Contains(column, r.Error);
    }

    [Theory]
    [InlineData("SELECT * FROM sqlite_schema")]
    [InlineData("SELECT load_extension('x')")]
    [InlineData("SELECT Olmayan FROM Musteri")]
    [InlineData("SELECT 1; SELECT 2")]
    [InlineData("SELEC * FROM Musteri")]
    public void Hatali_sorgular_duzeltilmek_uzere_geri_doner(string sql) =>
        Assert.Equal(GuardStatus.Invalid, db.Runner.Run(sql).Status);

    [Fact]
    public void Devasa_metin_ureten_sorgu_bellegi_tuketemez()
    {
        var r = db.Runner.Run("SELECT length(printf('%900000000c', 'x'))");
        Assert.Equal(GuardStatus.Invalid, r.Status);
        Assert.Contains("too big", r.Error);
    }

    [Fact]
    public void Kullanilan_tablo_ve_kolonlar_raporlanir()
    {
        var r = db.Runner.Run("SELECT s.Ad, SUM(i.Tutar) FROM Islem i JOIN Sektor s ON i.SektorId = s.Id GROUP BY s.Ad");
        Assert.Equal(["Islem", "Sektor"], r.Result!.Tables);
        Assert.Contains("Islem.Tutar", r.Result.UsedColumns);
    }

    [Fact]
    public void Satir_limiti_uygulanir()
    {
        var r = db.Runner.Run("SELECT Id FROM Islem");
        Assert.Equal(1000, r.Result!.Rows.Count);
        Assert.True(r.Result.Truncated);
    }

    [Fact]
    public void Index_kullanmayan_buyuk_tablo_taramasi_uyarilir()
    {
        var scan = db.Runner.Run("SELECT COUNT(*) FROM Islem i WHERE i.Tutar > 1000");
        Assert.NotEmpty(scan.Result!.PlanWarnings);
        var seek = db.Runner.Run("SELECT COUNT(*) FROM Islem WHERE IslemTarihi >= date('now','-7 days')");
        Assert.Empty(seek.Result!.PlanWarnings);
    }

    [Fact]
    public void Ornek_dosyasindaki_tum_sqller_calisir_ve_sonuc_dondurur()
    {
        var examples = File.ReadAllLines(Path.Combine(db.ApiDir, "Prompt", "examples.md")).Where(l => l.StartsWith('{'));
        foreach (var json in examples)
        {
            var sql = JsonDocument.Parse(json).RootElement.GetProperty("sql").GetString()!;
            if (sql == "") continue;
            var r = db.Runner.Run(sql);
            Assert.True(r.Status == GuardStatus.Ok, $"{r.Error}\n{sql}");
            Assert.NotEmpty(r.Result!.Rows);
        }
    }

    [Fact]
    public void Ses_on_metni_alan_terimlerini_icerir_kisisel_veri_icermez()
    {
        var vocabulary = SpeechVocabulary.Build(db.Catalog);
        Assert.Contains("Alsancak", vocabulary);
        Assert.Contains("KOBİ", vocabulary);
        Assert.Contains("Platinum", vocabulary);
        Assert.True(vocabulary.Length < 800, "Whisper ön metni kısa kalmalı");
        // Gerçek müşteri adları (PII) ön metne sızmamalı
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db.DbPath};Mode=ReadOnly;Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT AdSoyad FROM Musteri LIMIT 200";
        using var r = cmd.ExecuteReader();
        int checkedNames = 0;
        while (r.Read()) { Assert.DoesNotContain(r.GetString(0), vocabulary); checkedNames++; }
        Assert.True(checkedNames > 50);
    }

    [Fact]
    public void Wav_dogrulamasi_yalnizca_riff_wave_kabul_eder()
    {
        var header = "RIFF\0\0\0\0WAVE"u8.ToArray();
        Assert.True(Wav.LooksValid([.. header, .. new byte[40]]));
        Assert.False(Wav.LooksValid("ID3 fake mp3 content that is long enough to pass length"u8.ToArray()));
        Assert.True(Wav.LooksValid([.. header, .. new byte[32]]));
        Assert.False(Wav.HasAudio([.. header, .. new byte[32]]), "yalnızca başlık = boş kayıt");
        Assert.True(Wav.HasAudio([.. header, .. new byte[32], .. new byte[16000]]));
    }

    [Fact]
    public void Sema_prompt_metni_pii_isaretli_ve_degerleri_icerir()
    {
        Assert.Matches(new Regex(@"AdSoyad TEXT \[KİŞİSEL VERİ"), db.Catalog.PromptText);
        Assert.Contains("'Classic', 'Gold', 'Platinum'", db.Catalog.PromptText);
        Assert.Contains("Islem.KartId = Kart.Id", db.Catalog.PromptText);
    }
}
