using Diyalog360.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Diyalog360.Tests;

public sealed class DrillTests(DemoDbFixture db) : IClassFixture<DemoDbFixture>, IDisposable
{
    readonly string _appDb = Path.Combine(Path.GetTempPath(), $"d360-drill-{Guid.NewGuid():N}.db");
    public void Dispose() { SqliteConnection.ClearAllPools(); try { File.Delete(_appDb); } catch (IOException) { } }

    const string Mevduat = "SELECT sb.Bolge AS [Bölge], ROUND(SUM(h.Bakiye), 2) AS [TL Mevduat] FROM Hesap h JOIN Musteri m ON h.MusteriId = m.Id JOIN Sube sb ON m.SubeId = sb.Id WHERE h.Durum = 'Aktif' AND h.ParaBirimi = 'TRY' GROUP BY sb.Bolge ORDER BY [TL Mevduat] DESC";
    const string SektorHarcama = "SELECT s.Ad AS [Sektör], ROUND(SUM(i.Tutar), 2) AS [Toplam Harcama] FROM Islem i JOIN Sektor s ON i.SektorId = s.Id WHERE i.IslemTipi = 'Harcama' AND i.IslemTarihi >= date('now','localtime','start of month','-6 months') GROUP BY s.Ad ORDER BY [Toplam Harcama] DESC";
    const string AylikHarcama = "SELECT strftime('%Y-%m', i.IslemTarihi) AS [Ay], ROUND(SUM(i.Tutar), 2) AS [Toplam Harcama] FROM Islem i WHERE i.IslemTipi = 'Harcama' AND i.IslemTarihi >= date('now','localtime','start of month','-6 months') AND i.IslemTarihi < date('now','localtime','start of month') GROUP BY 1";

    List<object?[]> Rows(string sql)
    {
        var o = db.Runner.Run(sql);
        Assert.True(o.Status == GuardStatus.Ok, o.Error + "\n" + sql);
        return o.Result!.Rows;
    }

    static double Sum(List<object?[]> rows, int col) => rows.Sum(r => Convert.ToDouble(r[col]));

    double ValueOf(string sql, string key) => Convert.ToDouble(Rows(sql).Single(r => Convert.ToString(r[0]) == key)[1]);

    [Fact]
    public void Secenekler_olgu_tablosuna_gore_belirlenir()
    {
        var cols = db.Runner.Run(Mevduat).Result!.Columns;
        var bolge = Assert.Single(DrillPlanner.Options(Mevduat, cols, db.Catalog));
        Assert.Equal("bolge", bolge.Level);
        Assert.Equal(["sehir", "sube", "musteri"], bolge.Targets.Where(t => t.Kind == "down").Select(t => t.Key));
        Assert.Contains(bolge.Targets, t => t.Key == "segment");
        Assert.Contains(bolge.Targets, t => t.Key == "hesapturu");
        Assert.DoesNotContain(bolge.Targets, t => t.Key is "sektor" or "kanal" or "ay"); // mevduat sorgusunda işlem boyutları yok
        Assert.Null(bolge.Up);

        var ay = Assert.Single(DrillPlanner.Options(AylikHarcama, db.Runner.Run(AylikHarcama).Result!.Columns, db.Catalog));
        Assert.Equal("gun", ay.Targets[0].Key);
        Assert.Equal("yil", ay.Up!.Key);
    }

    [Fact]
    public void Bolgeden_sehre_inince_toplam_korunur()
    {
        var (sql, reason) = DrillPlanner.Rewrite(Mevduat, "Bölge", "Marmara", "sehir", db.Catalog);
        Assert.True(sql != null, reason);
        Assert.Contains("sb.Sehir AS [Şehir]", sql);
        Assert.Contains("sb.Bolge = 'Marmara'", sql);
        Assert.Contains("GROUP BY sb.Sehir", sql);
        Assert.Equal(ValueOf(Mevduat, "Marmara"), Sum(Rows(sql!), 1), 1);
    }

    [Fact]
    public void Eksik_tablolar_coka_bir_JOIN_ile_eklenir()
    {
        var (sql, reason) = DrillPlanner.Rewrite(SektorHarcama, "Sektör", "Market", "bolge", db.Catalog);
        Assert.True(sql != null, reason);
        Assert.Contains("JOIN Kart k ON i.KartId = k.Id", sql);
        Assert.Contains("JOIN Musteri m ON k.MusteriId = m.Id", sql);
        Assert.Contains("JOIN Sube sb ON m.SubeId = sb.Id", sql);
        Assert.Equal(ValueOf(SektorHarcama, "Market"), Sum(Rows(sql!), 1), 1);
    }

    [Fact]
    public void Aydan_gune_inilir_ve_tarih_sirasina_dizilir()
    {
        var month = Convert.ToString(Rows(AylikHarcama)[0][0])!;
        var (sql, _) = DrillPlanner.Rewrite(AylikHarcama, "Ay", month, "gun", db.Catalog);
        Assert.Contains($"strftime('%Y-%m', i.IslemTarihi) = '{month}'", sql);
        Assert.EndsWith("ORDER BY [Gün]", sql);
        var days = Rows(sql!);
        Assert.Equal(days.Select(r => (string)r[0]!).Order(StringComparer.Ordinal), days.Select(r => (string)r[0]!));
        Assert.Equal(ValueOf(AylikHarcama, month), Sum(days, 1), 1);
    }

    [Fact]
    public void Ust_seviyeye_cikinca_alt_filtreler_kalkar_ust_filtreler_kalir()
    {
        var sehir = DrillPlanner.Rewrite(Mevduat, "Bölge", "Marmara", "sehir", db.Catalog).Sql!;
        var city = Convert.ToString(Rows(sehir)[0][0])!;
        var sube = DrillPlanner.Rewrite(sehir, "Şehir", city, "sube", db.Catalog).Sql!;
        var (up, reason) = DrillPlanner.Rewrite(sube, "Şube", null, "sehir", db.Catalog);
        Assert.True(up != null, reason);
        Assert.Contains("sb.Bolge = 'Marmara'", up);
        Assert.DoesNotContain("sb.Sehir = ", up);
        Assert.Equal(Rows(sehir).Count, Rows(up!).Count);
    }

    [Fact]
    public void Musteri_seviyesinde_en_buyukten_siralanir_ve_sinirlanir()
    {
        var (sql, _) = DrillPlanner.Rewrite(Mevduat, "Bölge", "Ege", "musteri", db.Catalog);
        Assert.Contains("m.Id AS [Müşteri No]", sql);
        Assert.Contains("ORDER BY [TL Mevduat] DESC", sql);
        Assert.EndsWith("LIMIT 50", sql);
        Assert.InRange(Rows(sql!).Count, 1, 50);
    }

    [Fact]
    public void Tirnakli_deger_kacislanir_ve_OR_iceren_WHERE_parantezlenir()
    {
        const string sql = "SELECT sb.Ad AS [Şube], COUNT(*) AS [Adet] FROM Musteri m JOIN Sube sb ON m.SubeId = sb.Id WHERE m.Segment = 'KOBİ' OR m.Segment = 'Premium' GROUP BY sb.Ad";
        var (rewritten, _) = DrillPlanner.Rewrite(sql, "Şube", "O'Neil", "musteri", db.Catalog);
        Assert.Contains("(m.Segment = 'KOBİ' OR m.Segment = 'Premium') AND sb.Ad = 'O''Neil'", rewritten);
    }

    [Fact]
    public void Karmasik_sorgu_LLMe_birakilir_ama_kolon_adiyla_secenek_sunulur()
    {
        const string sql = "WITH t AS (SELECT sb.Sehir AS Sehir, SUM(i.Tutar) AS Tutar FROM Islem i JOIN Kart k ON i.KartId = k.Id JOIN Musteri m ON k.MusteriId = m.Id JOIN Sube sb ON m.SubeId = sb.Id WHERE i.IslemTipi = 'Harcama' GROUP BY sb.Sehir) SELECT Sehir AS [Şehir], ROUND(Tutar, 2) AS [Toplam Harcama] FROM t";
        Assert.Null(DrillPlanner.Rewrite(sql, "Şehir", "Ankara", "sube", db.Catalog).Sql);
        var col = Assert.Single(DrillPlanner.Options(sql, [new("Şehir", "text"), new("Toplam Harcama", "number")], db.Catalog));
        Assert.Contains(col.Targets, t => t.Key == "sube");
        Assert.Contains("Şehir = 'Ankara'", DrillPlanner.FollowUpMessage(col.Level, "Ankara", "sube"));
    }

    [Fact]
    public void Detaya_inme_ve_geri_donme_gecmise_yazilir()
    {
        var store = new AppStore(_appDb);
        var prompt = new PromptBuilder(Path.Combine(db.ApiDir, "Prompt"), db.Catalog);
        var llm = new LlmClient(new HttpClient(), new LlmOptions { BaseUrl = "http://127.0.0.1:9/v1" }, NullLogger<LlmClient>.Instance);
        var orchestrator = new ChatOrchestrator(prompt, llm, db.Runner, store, db.Catalog, NullLogger<ChatOrchestrator>.Instance);

        var root = orchestrator.RunManual(null, Mevduat);
        Assert.Contains(root.Report!.Drill!, c => c.Column == "Bölge");

        var city = orchestrator.DrillAsync(root.ChatId, root.MessageId, "Bölge", "Marmara", "sehir", CancellationToken.None).Result;
        Assert.Equal("report", city.Kind);
        Assert.Equal("rewrite", city.DrillSource);
        Assert.Equal("Marmara › Şehir kırılımı", city.Title);
        Assert.Equal([root.MessageId, null], city.Crumbs!.Select(c => c.MessageId));

        var branch = orchestrator.DrillAsync(root.ChatId, city.MessageId, "Şehir", city.Report!.Rows[0][0], "sube", CancellationToken.None).Result;
        Assert.Equal(3, branch.Crumbs!.Count);
        Assert.Equal(city.MessageId, branch.Crumbs[1].MessageId);

        var back = orchestrator.DrillBack(root.ChatId, city.MessageId);
        Assert.Equal("back", back.DrillSource);
        Assert.Equal(city.Report.Sql, back.Report!.Sql);
        Assert.Equal(city.Crumbs.Count, back.Crumbs!.Count);

        var history = store.GetMessages(root.ChatId);
        Assert.Equal(8, history.Count);
        Assert.Equal("[Detay] Marmara → Şehirlere in", history[2].Content);
        Assert.Throws<ArgumentException>(() => orchestrator.DrillAsync(root.ChatId, root.MessageId, "TL Mevduat", 5, "sehir", CancellationToken.None).GetAwaiter().GetResult());
    }
}
