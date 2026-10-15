using Diyalog360.Api.Services;
using Microsoft.Data.Sqlite;

namespace Diyalog360.Tests;

public sealed class AuthTests(DemoDbFixture db) : IClassFixture<DemoDbFixture>, IDisposable
{
    readonly string _appDb = Path.Combine(Path.GetTempPath(), $"d360-app-{Guid.NewGuid():N}.db");

    public void Dispose() { SqliteConnection.ClearAllPools(); try { File.Delete(_appDb); } catch (IOException) { } }

    [Fact]
    public void Varsayilan_admin_kullanicisi_olusur_ve_dogrulanir()
    {
        var store = new AppStore(_appDb);
        Assert.True(store.ValidateUser("admin", "admin"));
        Assert.True(store.ValidateUser("ADMIN", "admin"));   // kullanıcı adı büyük/küçük harf duyarsız
        Assert.False(store.ValidateUser("admin", "Admin"));  // şifre duyarlı
        Assert.False(store.ValidateUser("admin", ""));
        Assert.False(store.ValidateUser("baskasi", "admin"));
    }

    [Fact]
    public void Tek_kullanici_vardir_ve_sifre_duz_metin_saklanmaz()
    {
        _ = new AppStore(_appDb);
        _ = new AppStore(_appDb); // ikinci açılış yeni kullanıcı eklememeli
        using var conn = new SqliteConnection($"Data Source={_appDb}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*), MAX(PasswordHash) FROM AppUser";
        using var r = cmd.ExecuteReader();
        r.Read();
        Assert.Equal(1L, r.GetInt64(0));
        Assert.StartsWith("pbkdf2-sha256$", r.GetString(1));
        Assert.DoesNotContain("admin", r.GetString(1));
    }

    [Fact]
    public void Kullanici_tablosu_sorgu_motoruna_gorunmez()
    {
        Assert.DoesNotContain(db.Catalog.Tables, t => t.Name.Equals("AppUser", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("AppUser", db.Catalog.PromptText);
        Assert.NotEqual(GuardStatus.Ok, db.Runner.Run("SELECT Username, PasswordHash FROM AppUser").Status);
    }
}
