using System.Net;
using System.Security.Cryptography;
using Fms.Api;
using Fms.BuildingBlocks.Results;
using Fms.BuildingBlocks.Security;
using Fms.Facilities.Services;
using Fms.Platform.Security;
using Fms.Platform.Services;
using Fms.Vendors.Services;
using Microsoft.EntityFrameworkCore;
using static Fms.IntegrationTests.ApiHarness;

namespace Fms.IntegrationTests;

/// <summary>Vendor and vendor-contact e-mail and phone are unreadable in the database when the operator has configured a key, and read back as ever through the API.</summary>
[Collection(SqlCollection.Name)]
public sealed class VendorEncryptionApiTests(SqlServerFixture sql) : IDisposable
{
    private static readonly string Key1 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private static readonly string Key2 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private ApiHarness? _api;

    public void Dispose()
    {
        _api?.Dispose();
        FieldEncryption.Configure(new FieldEncryptionOptions());
    }

    private ApiHarness Start(params (string Id, string Key)[] keys)
    {
        _api?.Dispose();
        if (keys.Length == 0) return _api = new ApiHarness(sql);
        var settings = new Dictionary<string, string?> { ["Encryption:CurrentKeyId"] = keys[^1].Id };
        foreach (var (id, key) in keys) settings[$"Encryption:Keys:{id}"] = key;
        return _api = new ApiHarness(sql, settings: settings);
    }

    private async Task<Guid> NewTenantAsync()
    {
        var tenant = Guid.NewGuid();
        await using var platform = sql.App(null, platform: true);
        platform.Tenants.Add(new Fms.Platform.Domain.Tenant { Id = tenant, Name = "VEnc " + tenant.ToString("N")[..6], CreatedAt = DateTimeOffset.UtcNow });
        await platform.SaveChangesAsync();
        await SystemRoleSync.EnsureAsync(platform, tenant);
        return tenant;
    }

    private static async Task<VendorDto> VendorAsync(HttpClient manager, string email = "ops@coolair.test", string phone = "+966 11 555 0100")
    {
        var vendor = await ReadAsync<VendorDto>(await PostAsync(manager, "/api/v1/vendors", new { code = FacilitiesScope.Code("V"), name = "CoolAir", email, phone, country = "sa" }));
        var saved = await PutAsync(manager, $"/api/v1/vendors/{vendor.Id}/contacts", new { contacts = new object[] { new { name = "Omar", email = "omar@coolair.test", phone = "0501234567", isPrimary = true } } });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        return vendor;
    }

    private async Task<(string? VendorEmail, string? VendorPhone, string? ContactEmail, string? ContactPhone)> StoredAsync(Guid tenant, Guid vendorId)
    {
        await using var db = sql.Vendors(tenant);
        async Task<string?> One(string query) => (await db.Database.SqlQueryRaw<string>(query, vendorId).ToListAsync()).SingleOrDefault();
        return (await One("SELECT Email AS Value FROM vendors.Vendors WHERE Id = {0}"), await One("SELECT Phone AS Value FROM vendors.Vendors WHERE Id = {0}"),
            await One("SELECT Email AS Value FROM vendors.Contacts WHERE VendorId = {0}"), await One("SELECT Phone AS Value FROM vendors.Contacts WHERE VendorId = {0}"));
    }

    [Fact]
    public async Task Vendor_and_contact_details_are_stored_unreadable_and_come_back_unchanged()
    {
        var api = Start(("k1", Key1));
        var tenant = await NewTenantAsync();
        var manager = await api.ClientAsync(tenant, SystemRoles.FacilityManager);
        var vendor = await VendorAsync(manager);

        var stored = await StoredAsync(tenant, vendor.Id);
        foreach (var value in new[] { stored.VendorEmail, stored.VendorPhone, stored.ContactEmail, stored.ContactPhone }) Assert.StartsWith("enc1:k1:", value);
        Assert.DoesNotContain("coolair", stored.VendorEmail);
        Assert.DoesNotContain("0501234567", stored.ContactPhone);

        var read = await ReadAsync<VendorDto>(await manager.GetAsync($"/api/v1/vendors/{vendor.Id}"));
        Assert.Equal("ops@coolair.test", read.Email);
        Assert.Equal("+966 11 555 0100", read.Phone);
        var contact = Assert.Single(await ReadAsync<VendorContactDto[]>(await manager.GetAsync($"/api/v1/vendors/{vendor.Id}/contacts")));
        Assert.Equal(("omar@coolair.test", "0501234567"), (contact.Email, contact.Phone));

        // The name and code stay searchable; only the contact details are protected.
        var found = await ReadAsync<Page<VendorDto>>(await manager.GetAsync("/api/v1/vendors?q=CoolAir"));
        Assert.Contains(found.Items, v => v.Id == vendor.Id);
    }

    [Fact]
    public async Task After_a_rotation_older_values_still_read_and_an_edit_writes_with_the_new_key()
    {
        var first = Start(("k1", Key1));
        var tenant = await NewTenantAsync();
        var vendor = await VendorAsync(await first.ClientAsync(tenant, SystemRoles.FacilityManager));
        Assert.StartsWith("enc1:k1:", (await StoredAsync(tenant, vendor.Id)).VendorEmail);

        var second = Start(("k1", Key1), ("k2", Key2));
        var manager = await second.ClientAsync(tenant, SystemRoles.FacilityManager);
        Assert.Equal("ops@coolair.test", (await ReadAsync<VendorDto>(await manager.GetAsync($"/api/v1/vendors/{vendor.Id}"))).Email);

        var edited = await PutAsync(manager, $"/api/v1/vendors/{vendor.Id}", new { code = vendor.Code, name = "CoolAir", email = "new@coolair.test", phone = vendor.Phone, country = "SA", rowVersion = vendor.RowVersion });
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        var stored = await StoredAsync(tenant, vendor.Id);
        Assert.StartsWith("enc1:k2:", stored.VendorEmail);
        Assert.StartsWith("enc1:k1:", stored.ContactEmail); // untouched rows keep their key until the migrator re-encrypts
        Assert.Equal("omar@coolair.test", Assert.Single(await ReadAsync<VendorContactDto[]>(await manager.GetAsync($"/api/v1/vendors/{vendor.Id}/contacts"))).Email);
    }

    [Fact]
    public async Task Without_a_key_nothing_changes_and_plain_values_are_read_once_a_key_is_added_and_other_organisations_see_nothing()
    {
        var plain = Start();
        var tenant = await NewTenantAsync();
        var vendor = await VendorAsync(await plain.ClientAsync(tenant, SystemRoles.FacilityManager));
        Assert.Equal("ops@coolair.test", (await StoredAsync(tenant, vendor.Id)).VendorEmail);

        var keyed = Start(("k1", Key1));
        var manager = await keyed.ClientAsync(tenant, SystemRoles.FacilityManager);
        Assert.Equal("ops@coolair.test", (await ReadAsync<VendorDto>(await manager.GetAsync($"/api/v1/vendors/{vendor.Id}"))).Email);

        var other = await keyed.ClientAsync(await NewTenantAsync(), SystemRoles.FacilityManager);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/v1/vendors/{vendor.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/v1/vendors/{vendor.Id}/contacts")).StatusCode);
    }

    [Fact]
    public async Task Lease_notes_and_termination_reason_and_invitation_addresses_are_stored_unreadable_and_read_back()
    {
        var api = Start(("k1", Key1));
        var tenant = await NewTenantAsync();
        var manager = await api.ClientAsync(tenant, SystemRoles.FacilityManager);
        var site = await ReadAsync<LocationDto>(await PostAsync(manager, "/api/v1/locations", new { type = "Site", code = FacilitiesScope.Code("S"), name = "HQ" }));
        string D(int days) => DateOnly.FromDateTime(DateTime.UtcNow.AddDays(days)).ToString("yyyy-MM-dd");
        var lease = await ReadAsync<Fms.Leases.Services.LeaseDto>(await PostAsync(manager, "/api/v1/leases", new
        {
            reference = "L" + Guid.NewGuid().ToString("N")[..8], title = "Head office", kind = "Occupied", counterparty = "Al Noor Properties", locationId = site.Id,
            startOn = D(-100), endOn = D(265), noticeDays = 60, currencyCode = "SAR", baseRent = 10000, frequency = "Monthly", notes = "Landlord agreed a rent review in 2027",
        }));
        var activated = await ReadAsync<Fms.Leases.Services.LeaseDto>(await PostAsync(manager, $"/api/v1/leases/{lease.Id}/activate", new { rowVersion = lease.RowVersion }));
        var ended = await ReadAsync<Fms.Leases.Services.LeaseDto>(await PostAsync(manager, $"/api/v1/leases/{lease.Id}/terminate", new { on = D(-1), reason = "Moved to the new campus", rowVersion = activated.RowVersion }));

        await using (var db = sql.Leases(tenant))
        {
            async Task<string?> One(string column) => (await (column switch
            {
                "Notes" => db.Database.SqlQueryRaw<string>("SELECT Notes AS Value FROM leases.Leases WHERE Id = {0}", lease.Id),
                "TerminationReason" => db.Database.SqlQueryRaw<string>("SELECT TerminationReason AS Value FROM leases.Leases WHERE Id = {0}", lease.Id),
                _ => db.Database.SqlQueryRaw<string>("SELECT Counterparty AS Value FROM leases.Leases WHERE Id = {0}", lease.Id),
            }).ToListAsync()).SingleOrDefault();
            Assert.StartsWith("enc1:k1:", await One("Notes"));
            Assert.StartsWith("enc1:k1:", await One("TerminationReason"));
            Assert.Equal("Al Noor Properties", await One("Counterparty")); // searched, so left readable
        }

        var read = await ReadAsync<Fms.Leases.Services.LeaseDto>(await manager.GetAsync($"/api/v1/leases/{lease.Id}"));
        Assert.Equal(("Landlord agreed a rent review in 2027", "Moved to the new campus"), (read.Notes, read.TerminationReason));
        Assert.Equal(ended.TerminationReason, read.TerminationReason);

        var vendor = await ReadAsync<VendorDto>(await PostAsync(manager, "/api/v1/vendors", new { code = FacilitiesScope.Code("V"), name = "CoolAir" }));
        var invited = await ReadAsync<InvitationCreatedDto>(await PostAsync(manager, $"/api/v1/vendors/{vendor.Id}/invitations", new { email = "ops@coolair.test" }));
        await using (var platform = sql.App(tenant))
            Assert.StartsWith("enc1:k1:", await platform.Database.SqlQueryRaw<string>("SELECT Email AS Value FROM dbo.GuestInvitations WHERE Id = {0}", invited.Invitation.Id).SingleAsync());
        Assert.Equal("ops@coolair.test", invited.Invitation.Email);

        // The invitation still works: the address is read back to fill the new user's profile.
        var guest = api.GuestClient();
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(guest, "/api/v1/portal/invitations/accept", new { token = invited.Token })).StatusCode);
    }
}
