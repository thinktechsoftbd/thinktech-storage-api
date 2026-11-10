using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;

namespace ThinkTech.Storage.Api.Authentication;

/// <summary>
/// Generates client secrets and stores them only as salted PBKDF2-SHA256 hashes.
/// Format: <c>pbkdf2-sha256.{iterations}.{salt}.{hash}</c> (base64url, shell- and JSON-safe).
/// </summary>
public static class ClientSecretHasher
{
    private const string Algorithm = "pbkdf2-sha256";
    private const int Iterations = 100_000;
    private const int MinIterations = 10_000;
    private const int SaltSize = 16;
    private const int HashSize = 32;

    /// <summary>A new random 256-bit secret, base64url encoded (43 characters).</summary>
    public static string GenerateSecret() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string secret)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(secret, salt, Iterations, HashAlgorithmName.SHA256, HashSize);

        return string.Join('.', Algorithm, Iterations.ToString(CultureInfo.InvariantCulture),
            Base64Url.EncodeToString(salt), Base64Url.EncodeToString(hash));
    }

    public static bool Verify(string secret, string encodedHash)
    {
        if (string.IsNullOrEmpty(secret) || !TryParse(encodedHash, out var iterations, out var salt, out var expected))
        {
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(secret, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    public static bool IsValidHash(string? encodedHash) => TryParse(encodedHash, out _, out _, out _);

    private static bool TryParse(string? encodedHash, out int iterations, out byte[] salt, out byte[] hash)
    {
        iterations = 0;
        salt = [];
        hash = [];

        if (string.IsNullOrWhiteSpace(encodedHash))
        {
            return false;
        }

        var parts = encodedHash.Split('.');
        if (parts.Length != 4 || !string.Equals(parts[0], Algorithm, StringComparison.Ordinal))
        {
            return false;
        }

        if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out iterations) || iterations < MinIterations)
        {
            return false;
        }

        try
        {
            salt = Base64Url.DecodeFromChars(parts[2]);
            hash = Base64Url.DecodeFromChars(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        return salt.Length >= SaltSize && hash.Length >= HashSize;
    }
}
