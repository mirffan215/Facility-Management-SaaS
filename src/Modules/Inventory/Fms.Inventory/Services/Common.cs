using System.Text.RegularExpressions;

namespace Fms.Inventory.Services;

internal static partial class Common
{
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._\-/]{0,49}$")]
    private static partial Regex CodePattern();

    public static bool IsCode(string? code) => code is not null && CodePattern().IsMatch(code.Trim());

    public static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    public static bool TryVersion(string? value, out byte[] version)
    {
        version = [];
        if (string.IsNullOrWhiteSpace(value)) return false;
        try { version = Convert.FromBase64String(value); return version.Length > 0; }
        catch (FormatException) { return false; }
    }

    public static string Version(byte[] rowVersion) => Convert.ToBase64String(rowVersion);
}
