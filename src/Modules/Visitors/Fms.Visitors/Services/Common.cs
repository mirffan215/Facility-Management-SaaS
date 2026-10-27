using System.Security.Cryptography;
using System.Text;

namespace Fms.Visitors.Services;

internal static class Common
{
    public static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    public static bool TryVersion(string? value, out byte[] version)
    {
        version = [];
        if (string.IsNullOrWhiteSpace(value)) return false;
        try { version = Convert.FromBase64String(value); return version.Length > 0; }
        catch (FormatException) { return false; }
    }

    public static string Version(byte[] rowVersion) => Convert.ToBase64String(rowVersion);

    public static bool IsEmail(string? email) => email is not null && email.Length <= 320 && email.Contains('@') && !email.Any(char.IsWhiteSpace) && email.IndexOf('@') > 0 && email.LastIndexOf('.') > email.IndexOf('@') + 1 && !email.EndsWith('.');

    /// <summary>A new pass code: 128 random bits, URL-safe. Long enough to be unguessable, short enough for a QR code.</summary>
    public static string NewCode() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Hash(string code) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code))).ToLowerInvariant();
}
