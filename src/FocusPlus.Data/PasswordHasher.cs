using System.Security.Cryptography;
using System.Text;

namespace FocusPlus.Data;

/// <summary>PBKDF2-SHA256 password hashing with a unique salt per operator.</summary>
public static class PasswordHasher
{
    public const int DefaultIterations = 120_000;
    private const int SaltBytes = 16;
    private const int KeyBytes = 32;

    public static (string Hash, string Salt, int Iterations) Create(string password, int iterations = DefaultIterations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var key = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            KeyBytes);

        return (Convert.ToBase64String(key), Convert.ToBase64String(salt), iterations);
    }

    public static bool Verify(string password, string hash, string salt, int iterations)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(hash) || string.IsNullOrEmpty(salt))
        {
            return false;
        }

        try
        {
            var saltBytes = Convert.FromBase64String(salt);
            var expected = Convert.FromBase64String(hash);
            var actual = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(password),
                saltBytes,
                iterations <= 0 ? DefaultIterations : iterations,
                HashAlgorithmName.SHA256,
                expected.Length);

            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
