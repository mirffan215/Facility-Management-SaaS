using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Fms.BuildingBlocks.Licensing;

/// <summary>What a license grants. Signed by the vendor; the installation only ever reads it.</summary>
public sealed record LicensePayload(
    string LicenseId,
    string Customer,
    string Edition,
    IReadOnlyList<string> Modules,
    DateTimeOffset IssuedAt,
    DateTimeOffset NotBefore,
    DateTimeOffset ExpiresAt,
    Guid? TenantId = null,
    int? MaxUsers = null,
    int GraceDays = 30);

/// <summary>The file as shipped to a customer: the payload and an ECDSA P-256 signature over its exact bytes, plus which vendor key signed it.</summary>
public sealed record LicenseEnvelope(string KeyId, string Payload, string Signature);

public static class LicenseJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] FromBase64Url(string text)
    {
        var s = text.Replace('-', '+').Replace('_', '/');
        s = s.PadRight(s.Length + ((4 - s.Length % 4) % 4), '=');
        return Convert.FromBase64String(s);
    }
}

/// <summary>Vendor-side: makes a license file. Used by the license tool and by tests; the private key never ships with the product.</summary>
public static class LicenseSigner
{
    public static string Sign(LicensePayload payload, string keyId, ECDsa privateKey)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, LicenseJson.Options);
        var signature = privateKey.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return JsonSerializer.Serialize(new LicenseEnvelope(keyId, LicenseJson.Base64Url(bytes), LicenseJson.Base64Url(signature)), LicenseJson.Options);
    }

    /// <summary>A new key pair: the private key (PKCS#8 PEM, kept by the vendor) and the public key (SubjectPublicKeyInfo, base64, embedded in the product).</summary>
    public static (string PrivatePem, string PublicBase64) NewKeyPair()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (key.ExportPkcs8PrivateKeyPem(), Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
    }
}

public enum LicenseFailure { None, Unreadable, UnknownKey, BadSignature }

/// <summary>Checks a license file against the vendor keys this build trusts. It does not look at dates or installation; the provider does.</summary>
public sealed class LicenseVerifier(IReadOnlyDictionary<string, string> trustedPublicKeys)
{
    public (LicensePayload? Payload, LicenseFailure Failure) Verify(string text)
    {
        LicenseEnvelope? envelope;
        byte[] payloadBytes;
        byte[] signature;
        try
        {
            envelope = JsonSerializer.Deserialize<LicenseEnvelope>(text, LicenseJson.Options);
            if (envelope is null || string.IsNullOrEmpty(envelope.Payload) || string.IsNullOrEmpty(envelope.Signature)) return (null, LicenseFailure.Unreadable);
            payloadBytes = LicenseJson.FromBase64Url(envelope.Payload);
            signature = LicenseJson.FromBase64Url(envelope.Signature);
        }
        catch (Exception e) when (e is JsonException or FormatException)
        {
            return (null, LicenseFailure.Unreadable);
        }

        if (envelope.KeyId is null || !trustedPublicKeys.TryGetValue(envelope.KeyId, out var publicKey)) return (null, LicenseFailure.UnknownKey);

        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
            if (!key.VerifyData(payloadBytes, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) return (null, LicenseFailure.BadSignature);
            var payload = JsonSerializer.Deserialize<LicensePayload>(Encoding.UTF8.GetString(payloadBytes), LicenseJson.Options);
            return payload is null || payload.Modules is null ? (null, LicenseFailure.Unreadable) : (payload, LicenseFailure.None);
        }
        catch (Exception e) when (e is JsonException or CryptographicException or FormatException or ArgumentException)
        {
            return (null, LicenseFailure.BadSignature);
        }
    }
}
