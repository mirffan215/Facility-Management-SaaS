using System.Security.Cryptography;
using Fms.BuildingBlocks.Licensing;

// Vendor-side license tool. The private key stays with the vendor; customers only ever receive the signed file.
//   fms-license keygen --out <dir> [--key-id <id>]
//   fms-license sign --key <private.pem> --key-id <id> --customer <name> --edition <name> --modules <a,b|*> --expires <yyyy-MM-dd>
//                    [--tenant <guid>] [--max-users <n>] [--grace-days <n>] [--not-before <yyyy-MM-dd>] --out <file>
//   fms-license inspect <file> [--trusted <trusted-keys.json>]
if (args.Length == 0) return Usage();

var opts = Parse(args.Skip(1).ToArray());
switch (args[0])
{
    case "keygen":
    {
        var dir = Required(opts, "out");
        var id = opts.GetValueOrDefault("key-id") ?? $"fms-{DateTime.UtcNow:yyyy}-{Guid.NewGuid().ToString("N")[..6]}";
        Directory.CreateDirectory(dir);
        var (priv, pub) = LicenseSigner.NewKeyPair();
        var privPath = Path.Combine(dir, $"{id}.private.pem");
        File.WriteAllText(privPath, priv);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(privPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.WriteAllText(Path.Combine(dir, $"{id}.public.txt"), pub);
        Console.WriteLine($"Key id: {id}");
        Console.WriteLine($"Private key (keep secret, never commit): {privPath}");
        Console.WriteLine($"Public key entry for trusted-keys.json: {{ \"keyId\": \"{id}\", \"publicKey\": \"{pub}\" }}");
        return 0;
    }
    case "sign":
    {
        using var key = ECDsa.Create();
        var keyText = File.ReadAllText(Required(opts, "key")).Trim();
        if (keyText.StartsWith("-----", StringComparison.Ordinal)) key.ImportFromPem(keyText);
        else key.ImportPkcs8PrivateKey(Convert.FromBase64String(keyText), out _);
        var now = DateTimeOffset.UtcNow;
        var notBefore = opts.TryGetValue("not-before", out var nb) ? Date(nb) : now;
        var payload = new LicensePayload(
            LicenseId: opts.GetValueOrDefault("license-id") ?? Guid.NewGuid().ToString("N")[..12],
            Customer: Required(opts, "customer"),
            Edition: Required(opts, "edition"),
            Modules: Required(opts, "modules").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            IssuedAt: now,
            NotBefore: notBefore,
            ExpiresAt: Date(Required(opts, "expires")).AddDays(1).AddTicks(-1),
            TenantId: opts.TryGetValue("tenant", out var t) ? Guid.Parse(t) : null,
            MaxUsers: opts.TryGetValue("max-users", out var mu) ? int.Parse(mu) : null,
            GraceDays: opts.TryGetValue("grace-days", out var gd) ? int.Parse(gd) : 30);
        var unknown = payload.Modules.Where(m => m != "*" && !LicenseModules.All.Contains(m)).ToList();
        if (unknown.Count > 0) { Console.Error.WriteLine($"Unknown module(s): {string.Join(", ", unknown)}. Known: {string.Join(", ", LicenseModules.All)} or *."); return 2; }
        File.WriteAllText(Required(opts, "out"), LicenseSigner.Sign(payload, Required(opts, "key-id"), key));
        Console.WriteLine($"Wrote {opts["out"]} for {payload.Customer}, expires {payload.ExpiresAt:yyyy-MM-dd}.");
        return 0;
    }
    case "inspect":
    {
        var file = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : Required(opts, "file");
        var text = File.ReadAllText(file);
        var keys = opts.TryGetValue("trusted", out var trusted)
            ? System.Text.Json.JsonSerializer.Deserialize<List<Dictionary<string, string>>>(File.ReadAllText(trusted), LicenseJson.Options)!.ToDictionary(e => e["keyId"], e => e["publicKey"])
            : TrustedLicenseKeys.Embedded();
        var (payload, failure) = new LicenseVerifier(keys).Verify(text);
        if (payload is null) { Console.Error.WriteLine($"NOT VALID: {failure}"); return 1; }
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(payload, LicenseJson.Options));
        return 0;
    }
    default:
        return Usage();
}

static int Usage()
{
    Console.Error.WriteLine("Usage: fms-license keygen|sign|inspect (see the comment at the top of Program.cs and docs/licensing.md)");
    return 2;
}

static Dictionary<string, string> Parse(string[] a)
{
    var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < a.Length; i++)
    {
        if (!a[i].StartsWith("--")) continue;
        d[a[i][2..]] = i + 1 < a.Length && !a[i + 1].StartsWith("--") ? a[++i] : "true";
    }

    return d;
}

static string Required(Dictionary<string, string> d, string name) => d.TryGetValue(name, out var v) ? v : throw new ArgumentException($"--{name} is required.");
static DateTimeOffset Date(string s) => new(DateOnly.ParseExact(s, "yyyy-MM-dd").ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
