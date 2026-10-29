using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Configuration;

namespace Fms.BuildingBlocks.Security;

/// <summary>Keys for column-level encryption, from the operator's configuration (a key vault reference or secret store), never from the database.</summary>
public sealed class FieldEncryptionOptions
{
    public const string SectionName = "Encryption";

    /// <summary>Key id to 32-byte key in base64. Keep old keys so older values can still be read after a rotation.</summary>
    public Dictionary<string, string> Keys { get; set; } = [];

    /// <summary>The key new values are written with.</summary>
    public string? CurrentKeyId { get; set; }

    /// <summary>Refuse to start without a usable key, instead of storing sensitive fields readable.</summary>
    public bool Required { get; set; }
}

/// <summary>
/// AES-256-GCM over a single value. A stored value reads "enc1:KEYID:base64(nonce, tag, ciphertext)". The column name is bound in as associated data, so a value copied into another
/// column does not decrypt. A value without the prefix is taken to be plain text written before encryption was switched on and is returned as it is.
/// </summary>
public sealed class KeyRing
{
    public const string Prefix = "enc1:";
    private readonly Dictionary<string, byte[]> _keys;
    private readonly string? _current;

    public KeyRing(FieldEncryptionOptions options)
    {
        _keys = [];
        foreach (var (id, text) in options.Keys)
        {
            if (string.IsNullOrWhiteSpace(id) || id.Contains(':') || id.Length > 40) throw new InvalidOperationException($"Encryption key id '{id}' is not valid: use up to 40 characters, no colon.");
            byte[] key;
            try { key = Convert.FromBase64String(text); }
            catch (FormatException) { throw new InvalidOperationException($"Encryption key '{id}' is not valid base64."); }
            if (key.Length != 32) throw new InvalidOperationException($"Encryption key '{id}' must be 32 bytes (256 bits).");
            _keys[id] = key;
        }

        if (_keys.Count > 0)
        {
            _current = options.CurrentKeyId ?? (_keys.Count == 1 ? _keys.Keys.Single() : throw new InvalidOperationException("Encryption:CurrentKeyId is required when there is more than one key."));
            if (!_keys.ContainsKey(_current)) throw new InvalidOperationException($"Encryption:CurrentKeyId '{_current}' is not among the keys.");
        }
    }

    public bool IsEnabled => _current is not null;

    public string? CurrentKeyId => _current;

    public string? Protect(string? value, string purpose)
    {
        if (value is null || _current is null) return value;
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plain = Encoding.UTF8.GetBytes(value);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(_keys[_current], 16)) aes.Encrypt(nonce, plain, cipher, tag, Encoding.UTF8.GetBytes(purpose));
        return Prefix + _current + ":" + Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }

    public string? Unprotect(string? stored, string purpose)
    {
        if (stored is null || !stored.StartsWith(Prefix, StringComparison.Ordinal)) return stored;
        var parts = stored.Split(':', 3);
        if (parts.Length != 3 || !_keys.TryGetValue(parts[1], out var key)) throw new CryptographicException($"A protected value was written with key '{(parts.Length > 1 ? parts[1] : "?")}', which is not configured.");
        var raw = Convert.FromBase64String(parts[2]);
        if (raw.Length < 28) throw new CryptographicException("A protected value is damaged.");
        var plain = new byte[raw.Length - 28];
        using (var aes = new AesGcm(key, 16)) aes.Decrypt(raw.AsSpan(0, 12), raw.AsSpan(28), raw.AsSpan(12, 16), plain, Encoding.UTF8.GetBytes(purpose));
        return Encoding.UTF8.GetString(plain);
    }
}

/// <summary>The key ring for this process, set once at start-up from configuration. Value converters in the data models use it.</summary>
public static class FieldEncryption
{
    private static KeyRing _current = new(new FieldEncryptionOptions());

    public static KeyRing Current => Volatile.Read(ref _current);

    public static bool IsEnabled => Current.IsEnabled;

    public static void Configure(FieldEncryptionOptions options)
    {
        var ring = new KeyRing(options);
        if (options.Required && !ring.IsEnabled) throw new InvalidOperationException("Encryption:Required is set but no key is configured.");
        Volatile.Write(ref _current, ring);
    }

    public static void Configure(IConfiguration configuration) =>
        Configure(configuration.GetSection(FieldEncryptionOptions.SectionName).Get<FieldEncryptionOptions>() ?? new FieldEncryptionOptions());

    /// <summary>A converter for one column. The column's name binds the value to it.</summary>
    public static ValueConverter<string?, string?> Converter(string purpose) =>
        new(v => Current.Protect(v, purpose), v => Current.Unprotect(v, purpose));
}
