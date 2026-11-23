using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SolidBrew.Api.Auth;

/// <summary>
/// Хеш пароля PBKDF2-SHA256. Формат с версией: <c>pbkdf2-sha256$v1$&lt;итерации&gt;$&lt;соль base64&gt;$&lt;хеш base64&gt;</c>.
/// </summary>
public static class PasswordHasher
{
    public const int MinPasswordLength = 8;
    private const string Scheme = "pbkdf2-sha256";
    private const string Version = "v1";
    private const int Iterations = 210_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    // Хеш-заглушка для несуществующего пользователя: проверка идёт столько же времени.
    private static readonly Lazy<string> DummyHash = new(() => Hash(Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))));

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return string.Join('$', Scheme, Version, Iterations.ToString(CultureInfo.InvariantCulture),
            Convert.ToBase64String(salt), Convert.ToBase64String(hash));
    }

    /// <summary>Проверка пароля; при <paramref name="stored"/> = null тратит то же время и возвращает false.</summary>
    public static bool Verify(string password, string? stored)
    {
        var target = stored ?? DummyHash.Value;
        var parts = target.Split('$');
        if (parts.Length != 5 || parts[0] != Scheme || parts[1] != Version ||
            !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var iterations) || iterations < 1)
            return false;

        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[3]);
            expected = Convert.FromBase64String(parts[4]);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected) && stored is not null;
    }
}
