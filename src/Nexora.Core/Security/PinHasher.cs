using System.Security.Cryptography;

namespace Nexora.Core.Security;

/// <summary>PINs are stored scrambled (PBKDF2), never as plain text.</summary>
public static class PinHasher
{
    private const int Iterations = 60_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public static bool IsValidPin(string? pin) =>
        pin is { Length: 4 } && pin.All(char.IsAsciiDigit);

    public static string Hash(string pin)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(pin, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"pbkdf2${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string pin, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2" || !int.TryParse(parts[1], out var iterations)) return false;
        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(pin, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static string NewPairingCode() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
}
