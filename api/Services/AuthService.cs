using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Diyalog360.Api.Services;

public enum LoginStatus { Success, Failed, Locked, NotConfigured }

public record LoginResult(LoginStatus Status, TimeSpan? RetryAfter = null);

/// <summary>
/// Tek kullanıcılı kimlik doğrulama. Kullanıcı adı ve PBKDF2-SHA256 şifre özeti Data/auth.json dosyasında tutulur
/// (repoya girmez; `dotnet run -- set-password` ile oluşturulur). Dosya her girişte okunur, şifre değişince yeniden başlatma gerekmez.
/// Arka arkaya hatalı denemelerde hesap geçici olarak kilitlenir.
/// </summary>
public sealed class AuthService(string authFilePath, TimeProvider? clock = null, string? configuredCredentials = null)
{
    const int Iterations = 600_000, SaltBytes = 16, HashBytes = 32;
    public const int MaxFailures = 5;
    public static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(5);
    public const int MinPasswordLength = 8;

    readonly TimeProvider _clock = clock ?? TimeProvider.System;
    readonly Lock _gate = new();
    int _failures;
    DateTimeOffset _lockedUntil = DateTimeOffset.MinValue;

    record Stored(string Username, string Salt, string Hash, int Iterations);

    public bool IsConfigured => !string.IsNullOrWhiteSpace(configuredCredentials) || File.Exists(authFilePath);

    /// <summary>Ayar (bulut) varsa o, yoksa Data/auth.json içeriği.</summary>
    string ReadCredentials() => !string.IsNullOrWhiteSpace(configuredCredentials) ? configuredCredentials! : File.ReadAllText(authFilePath);

    /// <summary>Kullanıcı dosyasının parmak izi; oturum çerezine yazılır. Şifre değişince eski oturumların hepsi geçersizleşir.</summary>
    public string Stamp => IsConfigured
        ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ReadCredentials())))[..16]
        : "";

    /// <summary>Kullanıcı adı + PBKDF2 özetini JSON olarak üretir (şifrenin kendisi içinde yoktur).</summary>
    public static string CreateCredentials(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username)) throw new ArgumentException("Kullanıcı adı boş olamaz.");
        if (password.Length < MinPasswordLength) throw new ArgumentException($"Şifre en az {MinPasswordLength} karakter olmalı.");
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return JsonSerializer.Serialize(new Stored(username.Trim(), Convert.ToBase64String(salt), Convert.ToBase64String(hash), Iterations));
    }

    public static void SetPassword(string authFilePath, string username, string password)
    {
        File.WriteAllText(authFilePath, CreateCredentials(username, password));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(authFilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public LoginResult Verify(string username, string password)
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            if (now < _lockedUntil) return new(LoginStatus.Locked, _lockedUntil - now);
            if (!IsConfigured) return new(LoginStatus.NotConfigured);

            var stored = JsonSerializer.Deserialize<Stored>(ReadCredentials())!;
            // Kullanıcı adı yanlış olsa da şifre özeti hesaplanır: cevap süresi hangi alanın hatalı olduğunu ele vermez.
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(stored.Salt), stored.Iterations, HashAlgorithmName.SHA256, HashBytes);
            bool passwordOk = CryptographicOperations.FixedTimeEquals(actual, Convert.FromBase64String(stored.Hash));
            bool userOk = CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(Encoding.UTF8.GetBytes(username.Trim().ToLowerInvariant())),
                SHA256.HashData(Encoding.UTF8.GetBytes(stored.Username.ToLowerInvariant())));

            if (passwordOk && userOk)
            {
                _failures = 0;
                return new(LoginStatus.Success);
            }
            if (++_failures >= MaxFailures)
            {
                _failures = 0;
                _lockedUntil = now + LockDuration;
                return new(LoginStatus.Locked, LockDuration);
            }
            return new(LoginStatus.Failed);
        }
    }
}
