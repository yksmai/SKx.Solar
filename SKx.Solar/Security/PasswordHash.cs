using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SKx.Solar.Security;

public static class PasswordHash
{
    private const int Iterations = 200_000;
    private static readonly string Decoy = Create(Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)));

    /// <summary>Rechnet auch ohne Konto gleich lange, damit die Antwortzeit nicht verrät, welche Namen es gibt.</summary>
    public static bool VerifyAccount(string password, string? stored)
    {
        var matches = Verify(password, stored ?? Decoy);
        return stored is not null && matches;
    }

    public static string Create(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('.');
        if (parts.Length != 3 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var iterations))
            return false;
        try
        {
            var expected = Convert.FromBase64String(parts[2]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), Convert.FromBase64String(parts[1]), iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

/// <summary>Was ein Konto ohne Adminrechte darf. Neue Schnittstellen zum Ändern sind damit von selbst gesperrt.</summary>
public static class AccessRules
{
    private static readonly string[] AdminReads = ["/api/settings", "/api/users", "/api/export/snapshot.db"];
    private static readonly string[] OpenWrites = ["/api/login", "/api/logout", "/api/setup-password", "/api/password", "/api/mirror/push"];

    public static bool Allows(string method, string path, bool isAdmin)
    {
        var target = path.TrimEnd('/');
        if (isAdmin || !target.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
            return true;
        return HttpMethods.IsGet(method) || HttpMethods.IsHead(method)
            ? !AdminReads.Contains(target, StringComparer.OrdinalIgnoreCase)
            : OpenWrites.Contains(target, StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>Bremst Passwort-Raten: nach fünf Fehlversuchen je Adresse eine Minute Sperre.</summary>
public sealed class LoginThrottle(Func<long>? clock = null)
{
    private const int MaxFailures = 5;
    private const int LockSeconds = 60;
    private readonly Func<long> _clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    private readonly ConcurrentDictionary<string, (int Failures, long Until)> _entries = new();

    public bool IsLocked(string address) =>
        _entries.TryGetValue(address, out var entry) && entry.Until > _clock();

    public void Fail(string address) => _entries.AddOrUpdate(address,
        _ => (1, 0),
        (_, old) => old.Failures + 1 >= MaxFailures ? (0, _clock() + LockSeconds) : (old.Failures + 1, old.Until));

    public void Success(string address) => _entries.TryRemove(address, out _);
}
