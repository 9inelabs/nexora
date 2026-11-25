using System.Security.Cryptography;
using System.Text;

namespace Nexora.Server;

/// <summary>Keeps secrets such as the stream key encrypted on disk (Windows DPAPI, tied to this PC).</summary>
public static class Secrets
{
    private const string Prefix = "dpapi:";
    private const string PlainPrefix = "b64:";

    public static string? Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return plain;
        var bytes = Encoding.UTF8.GetBytes(plain);
        if (OperatingSystem.IsWindows())
        {
            try { return Prefix + Convert.ToBase64String(ProtectedData.Protect(bytes, null, DataProtectionScope.LocalMachine)); }
            catch (CryptographicException) { }
        }
        return PlainPrefix + Convert.ToBase64String(bytes);
    }

    public static string? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return stored;
        try
        {
            if (stored.StartsWith(Prefix) && OperatingSystem.IsWindows())
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored[Prefix.Length..]), null, DataProtectionScope.LocalMachine));
            if (stored.StartsWith(PlainPrefix)) return Encoding.UTF8.GetString(Convert.FromBase64String(stored[PlainPrefix.Length..]));
        }
        catch (Exception) { return null; }
        return stored;
    }
}
