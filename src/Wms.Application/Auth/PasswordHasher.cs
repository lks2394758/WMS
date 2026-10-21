using System.Globalization;
using System.Security.Cryptography;

namespace Wms.Application.Auth;

public interface IPasswordHasher
{
    /// <summary>A well-formed hash that no password matches; verifying against it costs the same as a real check.</summary>
    string UnusableHash { get; }

    string Hash(string password);

    bool Verify(string password, string encodedHash);
}

/// <summary>
/// PBKDF2-HMAC-SHA256 with a random salt. Encoded as <c>PBKDF2-SHA256$iterations$salt$hash</c> so the work factor
/// can be raised later without invalidating stored hashes.
/// </summary>
public sealed class Pbkdf2PasswordHasher : IPasswordHasher
{
    public const int DefaultIterations = 600_000;
    private const string Scheme = "PBKDF2-SHA256";
    private const int SaltSize = 16;
    private const int HashSize = 32;

    private readonly int _iterations;

    public Pbkdf2PasswordHasher(int iterations = DefaultIterations)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(iterations, 1);
        _iterations = iterations;
    }

    public string UnusableHash => Encode(_iterations, new byte[SaltSize], new byte[HashSize]);

    public string Hash(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, _iterations, HashAlgorithmName.SHA256, HashSize);
        return Encode(_iterations, salt, hash);
    }

    public bool Verify(string password, string encodedHash)
    {
        ArgumentNullException.ThrowIfNull(password);
        var parts = (encodedHash ?? string.Empty).Split('$');
        if (parts.Length != 4
            || !string.Equals(parts[0], Scheme, StringComparison.Ordinal)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var iterations)
            || iterations < 1)
        {
            return false;
        }

        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        if (expected.Length == 0)
        {
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static string Encode(int iterations, byte[] salt, byte[] hash) =>
        string.Join(
            '$',
            Scheme,
            iterations.ToString(CultureInfo.InvariantCulture),
            Convert.ToBase64String(salt),
            Convert.ToBase64String(hash));
}
