using Diyalog360.Api.Services;
using Microsoft.Data.Sqlite;

namespace Diyalog360.Tests;

public sealed class HistoryTests : IDisposable
{
    readonly string _appDb = Path.Combine(Path.GetTempPath(), $"d360-hist-{Guid.NewGuid():N}.db");
    public void Dispose() { SqliteConnection.ClearAllPools(); try { File.Delete(_appDb); } catch (IOException) { } }

    static string Report(string chatId) => $$"""{"chatId":"{{chatId}}","kind":"report","title":"t","text":"x"}""";

    [Fact]
    public void Sohbetler_listelenir_baslik_ilk_soru_olur_eval_ve_bos_sohbetler_gizlenir()
    {
        var store = new AppStore(_appDb);
        var a = store.EnsureChat(null);
        store.AddMessage(a, "user", "Geçen ay sektörlere göre harcama");
        store.AddMessage(a, "assistant", Report(a));
        store.AddMessage(a, "user", "Bunu şubelere göre kır");
        var empty = store.EnsureChat(null);
        var evalChat = store.EnsureChat("eval-q01-120000");
        store.AddMessage(evalChat, "user", "eval sorusu");

        var list = store.ListChats("admin", null);
        var only = Assert.Single(list);
        Assert.Equal(a, only.Id);
        Assert.Equal("Geçen ay sektörlere göre harcama", only.Title);
        Assert.Equal(2, only.Questions);
        Assert.Equal(1, only.Reports);
        Assert.DoesNotContain(list, c => c.Id == empty);
    }

    [Fact]
    public void En_son_etkinlik_ustte_arama_ve_yeniden_adlandirma_calisir()
    {
        var store = new AppStore(_appDb);
        var first = store.EnsureChat(null);
        store.AddMessage(first, "user", "Kart tiplerine göre limit");
        var second = store.EnsureChat(null);
        store.AddMessage(second, "user", "Bölgelere göre mevduat");
        Thread.Sleep(1100); // UpdatedAt saniye çözünürlüklü
        store.AddMessage(first, "user", "Sadece Platinum");

        Assert.Equal([first, second], store.ListChats("admin", null).Select(c => c.Id));
        Assert.Equal(second, Assert.Single(store.ListChats("admin", "mevduat")).Id);

        Assert.True(store.RenameChat(second, "admin", "Mevduat raporu"));
        Assert.Equal("Mevduat raporu", store.ListChats("admin", null).Single(c => c.Id == second).Title);
    }

    [Fact]
    public void Baska_kullanicinin_sohbetine_erisilemez_ve_silme_mesajlari_kaldirir()
    {
        var store = new AppStore(_appDb);
        var chat = store.EnsureChat(null);
        store.AddMessage(chat, "user", "soru");
        store.ClaimChat(chat, "admin");

        Assert.True(store.CanAccess(chat, "ADMIN"));
        Assert.False(store.CanAccess(chat, "baskasi"));
        Assert.Empty(store.ListChats("baskasi", null));
        Assert.True(store.CanAccess("henuz-yok", "admin")); // yeni sohbet kimliği

        Assert.False(store.DeleteChat(chat, "baskasi"));
        Assert.True(store.DeleteChat(chat, "admin"));
        Assert.Empty(store.GetMessages(chat));
        Assert.Empty(store.ListChats("admin", null));
    }

    [Fact]
    public void Eski_app_db_kolonlari_veri_kaybetmeden_eklenir()
    {
        using (var conn = new SqliteConnection($"Data Source={_appDb}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE Chat (Id TEXT PRIMARY KEY, CreatedAt TEXT NOT NULL DEFAULT (datetime('now','localtime')));
                CREATE TABLE Message (Id INTEGER PRIMARY KEY, ChatId TEXT NOT NULL, Role TEXT NOT NULL, Content TEXT NOT NULL,
                    CreatedAt TEXT NOT NULL DEFAULT (datetime('now','localtime')));
                INSERT INTO Chat (Id) VALUES ('eski1');
                INSERT INTO Message (ChatId, Role, Content) VALUES ('eski1', 'user', 'Eski soru');
                """;
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        var store = new AppStore(_appDb);
        var c = Assert.Single(store.ListChats("admin", null));
        Assert.Equal("Eski soru", c.Title);
    }

    static string RichReport(string chatId) => System.Text.Json.JsonSerializer.Serialize(new
    {
        chatId, kind = "report", title = "Şubelere Göre Harcama", text = "En yüksek toplam harcama: Kadıköy (4.039.929 TL).",
        report = new
        {
            sql = "SELECT sb.Ad, SUM(i.Tutar) FROM Islem i JOIN Sube sb ON 1 = 1 GROUP BY sb.Ad",
            explanation = "Şubelerin geçen ayki kart harcaması toplandı.",
            assumptions = new[] { "Geçen ay = bir önceki takvim ayı" },
            columns = new[] { new { name = "Şube", kind = "text" }, new { name = "Toplam Harcama", kind = "number" } },
            rows = new object[][] { ["Kadıköy", 4039929.17], ["Beşiktaş", 4270922.41] },
        },
    }, ChatOrchestrator.Json);

    [Fact]
    public void Arama_konusmanin_tamaminda_turkce_karakter_ve_harf_duyarsiz_calisir()
    {
        var store = new AppStore(_appDb);
        var a = store.EnsureChat(null);
        store.AddMessage(a, "user", "Şubelere göre geçen ay harcama");
        store.AddMessage(a, "assistant", RichReport(a));
        var b = store.EnsureChat(null);
        store.AddMessage(b, "user", "Segmentlere göre müşteri sayısı");
        store.AddMessage(b, "assistant", Report(b));

        // tablo hücresindeki değer, farklı yazımlarla
        foreach (var q in new[] { "Kadıköy", "kadikoy", "KADIKÖY", "beşiktaş", "BESIKTAS" })
            Assert.Equal(a, Assert.Single(store.ListChats("admin", q)).Id);
        // SQL, açıklama, varsayım, kolon adı ve özet metni
        foreach (var q in new[] { "JOIN Sube", "kart harcaması toplandı", "takvim ayı", "Toplam Harcama", "4.039.929" })
            Assert.Equal(a, Assert.Single(store.ListChats("admin", q)).Id);
        // JSON alan adları ve rapor türü gibi teknik kelimeler eşleşme yaratmaz
        Assert.Empty(store.ListChats("admin", "chatId"));
        Assert.Empty(store.ListChats("admin", "kind"));

        var hit = Assert.Single(store.ListChats("admin", "kadikoy"));
        Assert.Contains("Kadıköy", hit.Snippet);
    }

    [Fact]
    public void Mevcut_mesajlarin_arama_metni_acilista_doldurulur()
    {
        var store = new AppStore(_appDb);
        var a = store.EnsureChat(null);
        store.AddMessage(a, "assistant", RichReport(a));
        using (var conn = new SqliteConnection($"Data Source={_appDb}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE Message SET SearchText = NULL";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        var reopened = new AppStore(_appDb);
        Assert.Single(reopened.ListChats("admin", "beşiktaş"));
    }

    [Fact]
    public void Katlama_uzunlugu_korur() =>
        Assert.Equal("istanbul kadikoy sube ISIK".ToLowerInvariant(), AppStore.Fold("İSTANBUL Kadıköy Şube IŞIK"));
}
