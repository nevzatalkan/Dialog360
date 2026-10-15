using Diyalog360.Api.Services;

namespace Diyalog360.Tests;

public sealed class AuthServiceTests : IDisposable
{
    readonly string _file = Path.Combine(Path.GetTempPath(), $"d360-auth-{Guid.NewGuid():N}.json");
    readonly ManualClock _clock = new();

    sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    public void Dispose() => File.Delete(_file);

    AuthService Create(string user = "analist", string password = "dogru-sifre-123")
    {
        AuthService.SetPassword(_file, user, password);
        return new AuthService(_file, _clock);
    }

    [Fact]
    public void Dosya_yoksa_giris_yapilamaz() =>
        Assert.Equal(LoginStatus.NotConfigured, new AuthService(_file, _clock).Verify("analist", "herhangi-bir-sey").Status);

    [Fact]
    public void Dogru_bilgilerle_giris_yapilir_kullanici_adi_buyuk_kucuk_harf_duyarsiz()
    {
        var auth = Create();
        Assert.Equal(LoginStatus.Success, auth.Verify("analist", "dogru-sifre-123").Status);
        Assert.Equal(LoginStatus.Success, auth.Verify(" ANALIST ", "dogru-sifre-123").Status);
    }

    [Theory]
    [InlineData("analist", "yanlis-sifre-123")]
    [InlineData("baskasi", "dogru-sifre-123")]
    [InlineData("analist", "DOGRU-SIFRE-123")]
    public void Yanlis_bilgilerle_giris_yapilamaz(string user, string password) =>
        Assert.Equal(LoginStatus.Failed, Create().Verify(user, password).Status);

    [Fact]
    public void Dosyada_sifre_acik_metin_olarak_durmaz()
    {
        Create(password: "acik-metin-olmamali");
        Assert.DoesNotContain("acik-metin-olmamali", File.ReadAllText(_file));
    }

    [Fact]
    public void Bulutta_ayardan_gelen_kimlik_bilgisiyle_giris_yapilir()
    {
        var credentials = AuthService.CreateCredentials("bulut", "bulut-sifresi-123");
        Assert.DoesNotContain("bulut-sifresi-123", credentials);
        var auth = new AuthService(_file, _clock, configuredCredentials: credentials); // dosya yok, ayar var
        Assert.True(auth.IsConfigured);
        Assert.Equal(LoginStatus.Success, auth.Verify("bulut", "bulut-sifresi-123").Status);
        Assert.Equal(LoginStatus.Failed, auth.Verify("bulut", "yanlis-sifre").Status);
    }

    [Fact]
    public void Kisa_sifre_reddedilir() =>
        Assert.Throws<ArgumentException>(() => AuthService.SetPassword(_file, "analist", "kisa"));

    [Fact]
    public void Arka_arkaya_hatali_denemede_kilitlenir_ve_sure_dolunca_acilir()
    {
        var auth = Create();
        for (int i = 1; i < AuthService.MaxFailures; i++) Assert.Equal(LoginStatus.Failed, auth.Verify("analist", "yanlis").Status);
        Assert.Equal(LoginStatus.Locked, auth.Verify("analist", "yanlis").Status);

        // Kilitliyken doğru şifre de kabul edilmez
        Assert.Equal(LoginStatus.Locked, auth.Verify("analist", "dogru-sifre-123").Status);

        _clock.Now += AuthService.LockDuration + TimeSpan.FromSeconds(1);
        Assert.Equal(LoginStatus.Success, auth.Verify("analist", "dogru-sifre-123").Status);
    }
}
