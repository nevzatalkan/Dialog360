using Diyalog360.Api.Data;
using Diyalog360.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Diyalog360.Tests;

public sealed class ZbTests(DemoDbFixture db) : IClassFixture<DemoDbFixture>
{
    FilterCatalog Catalog() => new(db.DbPath, db.Catalog);

    ZbAnalyzer Analyzer() => new(
        new LlmClient(new HttpClient(), new LlmOptions { BaseUrl = "http://127.0.0.1:9/v1", TimeoutSeconds = 2 }, NullLogger<LlmClient>.Instance),
        Catalog(), NullLogger<ZbAnalyzer>.Instance);

    [Fact]
    public void Demo_veritabaninda_cinsiyet_addan_turetilir()
    {
        using var conn = new SqliteConnection($"Data Source={db.DbPath};Mode=ReadOnly;Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Cinsiyet, COUNT(*), SUM(AdSoyad LIKE 'Ayşe %'), SUM(AdSoyad LIKE 'Ahmet %') FROM Musteri GROUP BY Cinsiyet ORDER BY Cinsiyet";
        using var r = cmd.ExecuteReader();
        r.Read(); Assert.Equal("Erkek", r.GetString(0)); Assert.Equal(0, r.GetInt64(2)); Assert.True(r.GetInt64(3) > 0);
        r.Read(); Assert.Equal("Kadın", r.GetString(0)); Assert.True(r.GetInt64(2) > 0); Assert.Equal(0, r.GetInt64(3));
        Assert.Contains("Cinsiyet", db.Catalog.PromptText);
    }

    [Fact]
    public void Eski_veritabanina_cinsiyet_kolonu_veri_kaybetmeden_eklenir()
    {
        var path = Path.Combine(Path.GetTempPath(), $"d360-old-{Guid.NewGuid():N}.db");
        try
        {
            using (var conn = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "CREATE TABLE Musteri (Id INTEGER PRIMARY KEY, AdSoyad TEXT NOT NULL); INSERT INTO Musteri VALUES (1, 'Zeynep Kaya'), (2, 'Murat Demir'), (3, 'Deniz Koç'), (4, 'Deniz Kurt');";
                cmd.ExecuteNonQuery();
            }
            Assert.True(DemoSeeder.EnsureCinsiyet(path));
            Assert.False(DemoSeeder.EnsureCinsiyet(path)); // ikinci kez dokunmaz
            using var c2 = new SqliteConnection($"Data Source={path};Pooling=False");
            c2.Open();
            using var q = c2.CreateCommand();
            q.CommandText = "SELECT group_concat(Cinsiyet, ',') FROM (SELECT Cinsiyet FROM Musteri ORDER BY Id)";
            Assert.Equal("Kadın,Erkek,Erkek,Kadın", q.ExecuteScalar());
        }
        finally { SqliteConnection.ClearAllPools(); File.Delete(path); }
    }

    [Fact]
    public void Filtre_alanlari_veritabanindan_doldurulur()
    {
        var c = Catalog();
        var yas = c.Get("yas")!;
        Assert.InRange(yas.Min!.Value, 18, 30);
        Assert.InRange(yas.Max!.Value, 60, 90);
        Assert.Equal(["Erkek", "Kadın"], c.Get("cinsiyet")!.Values);
        Assert.Contains("İstanbul", c.Get("sehir")!.Values!);
        Assert.Contains("Market", c.Get("sektor")!.Values!);
        Assert.True(c.Get("kartlimiti")!.Max > c.Get("kartlimiti")!.Min);
    }

    [Theory]
    [InlineData("yaş", "yas", null, null, null)]
    [InlineData("genç kadın", "yas", "yas", 18.0, 30.0)]
    [InlineData("30 yaş üstü", "yas", "yas", 31.0, null)]
    [InlineData("Platinum", "karttipi", null, null, null)]
    [InlineData("kart limiti", "kartlimiti", null, null, null)]
    [InlineData("istanbul", "sehir", null, null, null)]
    public void Model_yokken_kurallarla_alan_secilir(string term, string primary, string? rangeField, double? min, double? max)
    {
        var a = Analyzer().ByRules(term);
        Assert.Equal("kural", a.Source);
        Assert.Equal(primary, a.Primary);
        Assert.NotEmpty(a.Extras);
        if (rangeField != null)
        {
            var d = a.Defaults.Single(x => x.Field == rangeField);
            Assert.Equal(min is null ? null : Math.Max(min.Value, Catalog().Get(rangeField)!.Min!.Value), d.Min); // veritabanındaki en küçük değere kırpılır
            Assert.Equal(max, d.Max);
        }
        if (term == "genç kadın") Assert.Equal(["Kadın"], a.Defaults.Single(x => x.Field == "cinsiyet").Values);
        if (term == "Platinum") Assert.Equal(["Platinum"], a.Defaults.Single(x => x.Field == "karttipi").Values);
    }

    [Fact]
    public void Tanınmayan_ifadede_genel_filtreler_onerilir()
    {
        var a = Analyzer().ByRules("qwxyz");
        Assert.Null(a.Primary);
        Assert.Equal(["yas", "cinsiyet", "segment", "sehir"], a.Extras);
    }

    [Fact]
    public void Model_cevabi_dogrulanir_ve_kirpilir()
    {
        const string raw = """{"ifade":"genç kadın","baslik":"Genç kadın müşteriler","aciklama":"Yaş ve cinsiyet","ana_alan":"yas","ek_alanlar":["cinsiyet","yok_alan","segment"],"varsayilanlar":[{"alan":"yas","min":"5","max":"30","degerler":[]},{"alan":"cinsiyet","min":"","max":"","degerler":["kadın","Uzaylı"]}]}""";
        var a = Analyzer().FromLlm("genç kadın müşterilerin harcaması", raw)!;
        Assert.Equal("llm", a.Source);
        Assert.Equal("genç kadın", a.Phrase);
        Assert.Equal("yas", a.Primary);
        Assert.Equal(["cinsiyet", "segment"], a.Extras);
        var yas = a.Defaults.Single(d => d.Field == "yas");
        Assert.True(yas.Min >= 18); // veritabanındaki en küçük yaşa kırpılır
        Assert.Equal(["Kadın"], a.Defaults.Single(d => d.Field == "cinsiyet").Values);
        Assert.Null(Analyzer().FromLlm("x", """{"ifade":"x","baslik":"","aciklama":"","ana_alan":"bilinmeyen","ek_alanlar":[],"varsayilanlar":[]}"""));
    }

    [Fact]
    public async Task Model_kapaliysa_pencere_yine_acilir()
    {
        var a = await Analyzer().AnalyzeAsync("yaş", "ZB yaş müşterilerin toplam harcaması", CancellationToken.None);
        Assert.Equal("kural", a.Source);
        Assert.Equal("yas", a.Primary);
        Assert.Equal(Catalog().Fields.Count, a.Fields.Count);
    }

    [Fact]
    public void ZB_ile_uretilen_kriter_cumlesinin_beklenen_sorgulari_calisir()
    {
        Assert.Equal(GuardStatus.Ok, db.Runner.Run("SELECT m.Segment, COUNT(*) FROM Musteri m WHERE CAST(strftime('%Y','now') AS INTEGER) - m.DogumYili BETWEEN 25 AND 40 AND m.Cinsiyet = 'Kadın' GROUP BY m.Segment").Status);
    }
}

public sealed class ZbContextTests(DemoDbFixture db) : IClassFixture<DemoDbFixture>
{
    FilterCatalog Catalog() => new(db.DbPath, db.Catalog);

    long Scalar(string sql)
    {
        using var conn = new SqliteConnection($"Data Source={db.DbPath};Mode=ReadOnly;Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    [Fact]
    public void Sehir_secilince_subeler_o_sehirle_daralir_kendi_alani_daralmaz()
    {
        var r = Catalog().Facets([new FilterDefault("sehir", null, null, ["Ankara"])]);
        var sube = r.Fields.Single(f => f.Key == "sube");
        Assert.Equal(new[] { "Kızılay", "Çankaya" }.Order(), sube.Values!.Order());
        Assert.All(sube.Values!, v => Assert.True(sube.Counts![v] > 0));
        Assert.True(r.Fields.Single(f => f.Key == "sehir").Values!.Count > 1); // şehir alanı kendi seçimiyle daralmaz
        Assert.Equal(Scalar("SELECT COUNT(*) FROM Musteri m JOIN Sube sb ON m.SubeId = sb.Id WHERE sb.Sehir = 'Ankara'"), r.Matching);
        Assert.Equal(r.Matching, sube.Counts!.Values.Sum());
    }

    [Fact]
    public void Aralik_ve_coklu_kriterler_birlikte_uygulanir()
    {
        var r = Catalog().Facets([new FilterDefault("yas", 26, 35, []), new FilterDefault("cinsiyet", null, null, ["Kadın"]), new FilterDefault("sube", null, null, ["O'Neil", "Çankaya"])]);
        Assert.Equal(Scalar("SELECT COUNT(*) FROM Musteri m JOIN Sube sb ON m.SubeId = sb.Id WHERE CAST(strftime('%Y','now') AS INTEGER) - m.DogumYili BETWEEN 26 AND 35 AND m.Cinsiyet = 'Kadın' AND sb.Ad = 'Çankaya'"), r.Matching);
        var yas = r.Fields.Single(f => f.Key == "yas");
        Assert.True(yas.Min >= 18 && yas.Max <= 90);
    }

    [Fact]
    public void Donem_ve_harcama_alanlari_birlikte_hesaplanir()
    {
        var r = Catalog().Facets([new FilterDefault("donem", null, null, ["son 3 ay"]), new FilterDefault("sektor", null, null, ["Market"])]);
        Assert.True(r.Matching > 0);
        Assert.Contains("Market", r.Fields.Single(f => f.Key == "sektor").Values!);
        Assert.True(r.Fields.Single(f => f.Key == "kanal").Counts!.Count > 0);
    }

    [Fact]
    public void Soruda_gecen_degerler_bulunur()
    {
        var m = Catalog().Mentions("Ankara'daki Premium kadın müşterilerin Gold kartları");
        Assert.Equal(["Ankara"], m.Single(x => x.Field == "sehir").Values);
        Assert.Equal(["Premium"], m.Single(x => x.Field == "segment").Values);
        Assert.Equal(["Kadın"], m.Single(x => x.Field == "cinsiyet").Values);
        Assert.Equal(["Gold"], m.Single(x => x.Field == "karttipi").Values);
        Assert.DoesNotContain(Catalog().Mentions("postane ve ankaralılar"), x => x.Field is "kanal" or "sehir");
    }

    [Fact]
    public async Task ZB_ilce_sorudaki_sehre_gore_daraltilir()
    {
        var analyzer = new ZbAnalyzer(
            new LlmClient(new HttpClient(), new LlmOptions { BaseUrl = "http://127.0.0.1:9/v1", TimeoutSeconds = 2 }, NullLogger<LlmClient>.Instance),
            Catalog(), NullLogger<ZbAnalyzer>.Instance);
        var a = await analyzer.AnalyzeAsync("ilçe", "Şehri Ankara olan müşteriler ZB ilçe", CancellationToken.None);
        Assert.Equal("sube", a.Primary);
        Assert.Equal(new[] { "Kızılay", "Çankaya" }.Order(), a.Fields.Single(f => f.Key == "sube").Values!.Order());
        var ctx = Assert.Single(a.Context);
        Assert.Equal(("sehir", "Ankara", "metin"), (ctx.Field, ctx.Text, ctx.Source));

        // Arayüzün yolladığı önceki ZB seçimi metindeki eşleşmeden önceliklidir
        var b = await analyzer.AnalyzeAsync("ilçe", "müşteriler ZB ilçe", CancellationToken.None, [new FilterDefault("sehir", null, null, ["İstanbul"])]);
        Assert.DoesNotContain("Çankaya", b.Fields.Single(f => f.Key == "sube").Values!);
        Assert.Equal("soru", Assert.Single(b.Context).Source);
    }
}
