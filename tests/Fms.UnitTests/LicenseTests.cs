using System.Security.Cryptography;
using Fms.BuildingBlocks.Deployment;
using Fms.BuildingBlocks.Licensing;
using Microsoft.Extensions.Options;

namespace Fms.UnitTests;

public sealed class LicenseTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2027, 3, 1, 12, 0, 0, TimeSpan.Zero);
    private readonly ECDsa _vendor = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly string _dir = Directory.CreateTempSubdirectory("fms-lic").FullName;

    public void Dispose()
    {
        _vendor.Dispose();
        _stranger.Dispose();
        Directory.Delete(_dir, true);
    }

    private LicenseVerifier Verifier() => new(new Dictionary<string, string> { ["v1"] = Convert.ToBase64String(_vendor.ExportSubjectPublicKeyInfo()) });

    private static LicensePayload Payload() =>
        new("L-1", "Acme Estates", "Professional", ["work", "inventory"], Now.AddDays(-30), Now.AddDays(-30), Now.AddDays(100), null, 25, 30);

    private string Sign(LicensePayload p, ECDsa? key = null, string keyId = "v1") => LicenseSigner.Sign(p, keyId, key ?? _vendor);

    [Fact]
    public void A_signed_license_verifies_and_round_trips()
    {
        var (payload, failure) = Verifier().Verify(Sign(Payload()));
        Assert.Equal(LicenseFailure.None, failure);
        Assert.Equal("Acme Estates", payload!.Customer);
        Assert.Equal(25, payload.MaxUsers);
        Assert.Equal(["work", "inventory"], payload.Modules);
    }

    [Fact]
    public void A_changed_payload_fails_the_signature()
    {
        var file = Sign(Payload());
        var envelope = System.Text.Json.JsonSerializer.Deserialize<LicenseEnvelope>(file, LicenseJson.Options)!;
        var forged = Payload() with { MaxUsers = 9999, Modules = ["*"] };
        var forgedBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(forged, LicenseJson.Options);
        var tampered = System.Text.Json.JsonSerializer.Serialize(envelope with { Payload = LicenseJson.Base64Url(forgedBytes) }, LicenseJson.Options);

        Assert.Equal(LicenseFailure.BadSignature, Verifier().Verify(tampered).Failure);
    }

    [Fact]
    public void A_license_signed_by_another_key_is_refused_even_with_a_trusted_key_id()
    {
        Assert.Equal(LicenseFailure.BadSignature, Verifier().Verify(Sign(Payload(), _stranger)).Failure);
    }

    [Fact]
    public void An_unknown_key_id_is_refused() =>
        Assert.Equal(LicenseFailure.UnknownKey, Verifier().Verify(Sign(Payload(), keyId: "other")).Failure);

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"keyId\":\"v1\",\"payload\":\"%%%\",\"signature\":\"%%%\"}")]
    public void Garbage_is_unreadable_not_a_crash(string text) =>
        Assert.NotEqual(LicenseFailure.None, Verifier().Verify(text).Failure);

    [Fact]
    public void The_embedded_development_key_is_the_only_one_before_release() =>
        Assert.Contains("dev-2026", TrustedLicenseKeys.Embedded().Keys);

    [Theory]
    [InlineData(-1, LicenseState.NotYetValid, false)]
    [InlineData(10, LicenseState.Valid, true)]
    [InlineData(105, LicenseState.Grace, true)]
    [InlineData(131, LicenseState.Expired, false)]
    public void Dates_decide_the_state(int daysFromStart, LicenseState expected, bool allowsChanges)
    {
        var payload = Payload() with { NotBefore = Now, IssuedAt = Now, ExpiresAt = Now.AddDays(100), GraceDays = 30 };
        var info = OnPremLicenseProvider.Evaluate(payload, LicenseState.Valid, null, Now.AddDays(daysFromStart), null);
        Assert.Equal(expected, info.State);
        Assert.Equal(allowsChanges, info.AllowsChanges);
        Assert.Equal(allowsChanges, info.IsLicensed);
    }

    [Fact]
    public void Grace_is_capped_at_ninety_days()
    {
        var payload = Payload() with { ExpiresAt = Now, GraceDays = 5000 };
        Assert.Equal(Now.AddDays(90), OnPremLicenseProvider.Evaluate(payload, LicenseState.Valid, null, Now, null).GraceEndsAt);
    }

    [Fact]
    public void A_license_for_another_installation_is_not_valid_here()
    {
        var payload = Payload() with { TenantId = Guid.NewGuid() };
        var info = OnPremLicenseProvider.Evaluate(payload, LicenseState.Valid, null, Now, Guid.NewGuid());
        Assert.Equal(LicenseState.WrongInstallation, info.State);
        Assert.False(info.Allows("work"));
    }

    [Fact]
    public void A_license_bound_to_this_installation_is_valid()
    {
        var tenant = Guid.NewGuid();
        Assert.Equal(LicenseState.Valid, OnPremLicenseProvider.Evaluate(Payload() with { TenantId = tenant }, LicenseState.Valid, null, Now, tenant).State);
    }

    [Fact]
    public void Modules_are_allowed_by_name_or_wildcard_and_nothing_without_a_valid_license()
    {
        var some = OnPremLicenseProvider.Evaluate(Payload(), LicenseState.Valid, null, Now, null);
        Assert.True(some.Allows("work"));
        Assert.False(some.Allows("visitors"));
        Assert.True(OnPremLicenseProvider.Evaluate(Payload() with { Modules = ["*"] }, LicenseState.Valid, null, Now, null).Allows("visitors"));
        Assert.False(OnPremLicenseProvider.Evaluate(Payload() with { Modules = ["*"] }, LicenseState.Valid, null, Now.AddDays(1000), null).Allows("work"));
        Assert.False(OnPremLicenseProvider.Evaluate(null, LicenseState.Missing, null, Now, null).Allows("work"));
    }

    [Theory]
    [InlineData("/api/v1/work-orders", "work")]
    [InlineData("/api/v1/work-orders/123/parts", "inventory")]
    [InlineData("/api/v1/work-orders/123/parts/9", "inventory")]
    [InlineData("/api/v1/work-orders/categories", "work")]
    [InlineData("/api/v1/pm/schedules", "pm")]
    [InlineData("/api/v1/inventory/items", "inventory")]
    [InlineData("/api/v1/vendors", "vendors")]
    [InlineData("/api/v1/portal/orders", "vendors")]
    [InlineData("/api/v1/purchasing/requests", "purchasing")]
    [InlineData("/api/v1/integration/erp/settings", "purchasing")]
    [InlineData("/api/v1/spaces/occupancy/1", "spaces")]
    [InlineData("/api/v1/bookings", "bookings")]
    [InlineData("/api/v1/calendar/abc", "bookings")]
    [InlineData("/api/v1/visits/pass/abc", "visitors")]
    [InlineData("/api/v1/reports/dashboard", "reporting")]
    [InlineData("/api/v1/compliance/obligations", "compliance")]
    [InlineData("/api/v1/permits/mine", "compliance")]
    [InlineData("/api/v1/projects/abc/costs", "projects")]
    [InlineData("/api/v1/leases/abc/schedule", "leases")]
    [InlineData("/api/v1/odata/WorkOrders", "reporting")]
    [InlineData("/API/V1/Reports/dashboard", "reporting")]
    public void Paths_map_to_their_module(string path, string module) => Assert.Equal(module, LicenseModules.For(path));

    [Theory]
    [InlineData("/api/v1/locations")]
    [InlineData("/api/v1/assets/1")]
    [InlineData("/api/v1/users")]
    [InlineData("/api/v1/audit")]
    [InlineData("/api/v1/license")]
    [InlineData("/api/v1/work-ordersX")]
    [InlineData("/health/ready")]
    public void Core_paths_belong_to_no_module(string path) => Assert.Null(LicenseModules.For(path));

    [Fact]
    public void Every_licensable_module_has_a_route()
    {
        var found = new[]
        {
            "/api/v1/work-orders", "/api/v1/pm", "/api/v1/inventory", "/api/v1/vendors", "/api/v1/purchasing",
            "/api/v1/spaces", "/api/v1/bookings", "/api/v1/visits", "/api/v1/reports", "/api/v1/compliance", "/api/v1/energy/meters", "/api/v1/projects", "/api/v1/leases",
        }.Select(LicenseModules.For).ToHashSet();
        Assert.Equivalent(LicenseModules.All, found);
    }

    private OnPremLicenseProvider Provider(string path, FakeClock clock, Guid? tenant = null) =>
        new(Options.Create(new LicenseOptions { Path = path }),
            Options.Create(new DeploymentOptions { Mode = DeploymentMode.OnPrem, SingleTenantId = tenant ?? Guid.NewGuid(), LocalStoragePath = _dir }), clock, Verifier());

    private sealed class FakeClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Value { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Value;
    }

    [Fact]
    public void The_provider_has_no_license_until_a_file_appears_and_picks_up_a_renewal_without_restart()
    {
        var path = Path.Combine(_dir, "fms.license");
        var clock = new FakeClock(Now);
        var provider = Provider(path, clock);
        Assert.Equal(LicenseState.Missing, provider.Current.State);

        File.WriteAllText(path, Sign(Payload() with { ExpiresAt = Now.AddDays(5) }));
        clock.Value = Now.AddSeconds(20);
        Assert.Equal(LicenseState.Valid, provider.Current.State);

        clock.Value = Now.AddDays(40); // judged on the clock every time, with no re-read needed
        Assert.Equal(LicenseState.Expired, provider.Current.State);

        File.WriteAllText(path, Sign(Payload() with { ExpiresAt = Now.AddDays(400), MaxUsers = 500 }));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        clock.Value = Now.AddDays(40).AddSeconds(30);
        Assert.Equal(LicenseState.Valid, provider.Current.State);
        Assert.Equal(500, provider.Current.MaxUsers);
    }

    [Fact]
    public void A_damaged_file_is_invalid_and_says_so_without_revealing_contents()
    {
        var path = Path.Combine(_dir, "fms.license");
        File.WriteAllText(path, "{\"keyId\":\"v1\",\"payload\":\"abc\",\"signature\":\"def\"}");
        var info = Provider(path, new FakeClock(Now)).Current;
        Assert.Equal(LicenseState.Invalid, info.State);
        Assert.False(info.IsLicensed);
        Assert.DoesNotContain("abc", info.Detail);
    }

    [Fact]
    public void License_text_in_configuration_works_without_a_file()
    {
        var provider = new OnPremLicenseProvider(
            Options.Create(new LicenseOptions { Path = Path.Combine(_dir, "none"), Text = Sign(Payload()) }),
            Options.Create(new DeploymentOptions { Mode = DeploymentMode.OnPrem, SingleTenantId = Guid.NewGuid(), LocalStoragePath = _dir }), new FakeClock(Now), Verifier());
        Assert.Equal(LicenseState.Valid, provider.Current.State);
    }
}
