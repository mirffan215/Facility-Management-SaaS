using System.Text;
using Fms.BuildingBlocks.Caching;
using Fms.BuildingBlocks.Storage;
using Fms.BuildingBlocks.Tenancy;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Fms.UnitTests;

public sealed class StorageAndCacheTests
{
    private static readonly Guid TenantA = Guid.NewGuid();
    private static readonly Guid TenantB = Guid.NewGuid();

    private static TenantContext Tenant(Guid id)
    {
        var t = new TenantContext();
        t.SetTenant(id);
        return t;
    }

    private static MemoryStream Bytes(string s) => new(Encoding.UTF8.GetBytes(s));

    [Fact]
    public async Task File_store_keys_are_tenant_prefixed_and_other_tenants_are_refused()
    {
        var backend = new InMemoryBlobBackend();
        var key = await new TenantScopedFileStore(backend, Tenant(TenantA)).PutAsync("asset-doc", Guid.NewGuid(), "manual.pdf", Bytes("x"));

        Assert.StartsWith(TenantA.ToString("N") + "/", key);
        var other = new TenantScopedFileStore(backend, Tenant(TenantB));
        await Assert.ThrowsAsync<TenantViolationException>(() => other.GetAsync(key));
        await Assert.ThrowsAsync<TenantViolationException>(() => other.DeleteAsync(key));
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("..")]
    [InlineData("")]
    public void Unsafe_path_segments_are_rejected(string segment) =>
        Assert.Throws<ArgumentException>(() => TenantStoragePath.Build(TenantA, "k", Guid.NewGuid(), segment));

    [Fact]
    public void Traversal_keys_do_not_belong_to_a_tenant() =>
        Assert.False(TenantStoragePath.BelongsTo($"{TenantA:N}/../{TenantB:N}/x", TenantA));

    [Fact]
    public async Task Local_backend_round_trips_and_refuses_keys_outside_root()
    {
        var root = Path.Combine(Path.GetTempPath(), "fms-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var backend = new LocalFileBlobBackend(root);
            var key = $"{TenantA:N}/doc/{Guid.NewGuid():N}/a.txt";
            await backend.PutAsync(key, Bytes("hello"));
            using var read = (await backend.GetAsync(key))!;
            Assert.Equal("hello", await new StreamReader(read).ReadToEndAsync());

            await Assert.ThrowsAsync<InvalidOperationException>(() => backend.PutAsync(key, Bytes("again")));
            await Assert.ThrowsAsync<ArgumentException>(() => backend.GetAsync("../outside.txt"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Cache_entries_are_isolated_per_tenant()
    {
        IDistributedCache shared = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var a = new TenantCache(shared, Tenant(TenantA));
        var b = new TenantCache(shared, Tenant(TenantB));

        await a.SetAsync("k", [1], TimeSpan.FromMinutes(1));
        Assert.Null(await b.GetAsync("k"));
        Assert.NotNull(await a.GetAsync("k"));
    }
}
