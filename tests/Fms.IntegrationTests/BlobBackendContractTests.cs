using System.Security.Cryptography;
using System.Text;
using Amazon.S3;
using Azure.Storage.Blobs;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Fms.BuildingBlocks.Cloud;
using Fms.BuildingBlocks.Storage;
using Fms.BuildingBlocks.Tenancy;
using Testcontainers.Azurite;

namespace Fms.IntegrationTests;

/// <summary>The behaviour every storage backend must have, so SaaS (Azure/S3) and on-prem (disk/S3-compatible) are interchangeable.</summary>
public abstract class BlobBackendContract : IAsyncLifetime
{
    protected abstract IBlobBackend Backend { get; }
    public virtual Task InitializeAsync() => Task.CompletedTask;
    public virtual Task DisposeAsync() => Task.CompletedTask;

    private static string NewKey(string file = "file.bin") => TenantStoragePath.Build(Guid.NewGuid(), "doc", Guid.NewGuid(), file);

    private static async Task<byte[]> ReadAllAsync(Stream? s)
    {
        Assert.NotNull(s);
        await using var stream = s;
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        return ms.ToArray();
    }

    [Fact]
    public async Task Stores_and_returns_the_same_bytes()
    {
        var key = NewKey();
        var data = RandomNumberGenerator.GetBytes(3 * 1024 * 1024);

        await Backend.PutAsync(key, new MemoryStream(data));

        Assert.Equal(data, await ReadAllAsync(await Backend.GetAsync(key)));
    }

    [Fact]
    public async Task Accepts_a_forward_only_stream()
    {
        var key = NewKey();
        var data = Encoding.UTF8.GetBytes("streamed");
        await using var forwardOnly = new ForwardOnlyStream(data);

        await Backend.PutAsync(key, forwardOnly);

        Assert.Equal(data, await ReadAllAsync(await Backend.GetAsync(key)));
    }

    [Fact]
    public async Task A_missing_key_returns_null() => Assert.Null(await Backend.GetAsync(NewKey()));

    [Fact]
    public async Task Objects_are_write_once()
    {
        var key = NewKey();
        await Backend.PutAsync(key, new MemoryStream("first"u8.ToArray()));

        await Assert.ThrowsAsync<InvalidOperationException>(() => Backend.PutAsync(key, new MemoryStream("second"u8.ToArray())));

        Assert.Equal("first"u8.ToArray(), await ReadAllAsync(await Backend.GetAsync(key)));
    }

    [Fact]
    public async Task Delete_removes_the_object_and_is_safe_to_repeat()
    {
        var key = NewKey();
        await Backend.PutAsync(key, new MemoryStream([1, 2, 3]));

        await Backend.DeleteAsync(key);
        await Backend.DeleteAsync(key);

        Assert.Null(await Backend.GetAsync(key));
    }

    [Fact]
    public async Task Arabic_and_spaced_file_names_round_trip()
    {
        var key = NewKey("تقرير الصيانة 2026.pdf");
        await Backend.PutAsync(key, new MemoryStream([9, 9, 9]));

        Assert.Equal(new byte[] { 9, 9, 9 }, await ReadAllAsync(await Backend.GetAsync(key)));
    }

    [Fact]
    public async Task The_tenant_scoped_store_keeps_tenants_apart()
    {
        var a = new TenantContext(); a.SetTenant(Guid.NewGuid());
        var b = new TenantContext(); b.SetTenant(Guid.NewGuid());

        var key = await new TenantScopedFileStore(Backend, a).PutAsync("asset-photo", Guid.NewGuid(), "pump.jpg", new MemoryStream([7]));

        Assert.NotNull(await new TenantScopedFileStore(Backend, a).GetAsync(key));
        await Assert.ThrowsAsync<TenantViolationException>(() => new TenantScopedFileStore(Backend, b).GetAsync(key));
        await Assert.ThrowsAsync<TenantViolationException>(() => new TenantScopedFileStore(Backend, b).DeleteAsync(key));
    }

    private sealed class ForwardOnlyStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

public sealed class InMemoryBackendContractTests : BlobBackendContract
{
    protected override IBlobBackend Backend { get; } = new InMemoryBlobBackend();
}

public sealed class LocalBackendContractTests : BlobBackendContract
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fms-contract-" + Guid.NewGuid().ToString("N"));
    protected override IBlobBackend Backend => field ??= new LocalFileBlobBackend(_root);

    public override Task DisposeAsync()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        return Task.CompletedTask;
    }
}

public sealed class AzureBlobBackendContractTests : BlobBackendContract
{
    private readonly AzuriteContainer _azurite = new AzuriteBuilder("mcr.microsoft.com/azure-storage/azurite:latest").WithCommand("--skipApiVersionCheck").Build();
    private IBlobBackend? _backend;
    protected override IBlobBackend Backend => _backend!;

    public override async Task InitializeAsync()
    {
        await _azurite.StartAsync();
        _backend = new AzureBlobBackend(new BlobContainerClient(_azurite.GetConnectionString(), "fms-" + Guid.NewGuid().ToString("N")[..8]));
    }

    public override Task DisposeAsync() => _azurite.DisposeAsync().AsTask();
}

public sealed class S3BackendContractTests : BlobBackendContract
{
    private readonly IContainer _s3 = new ContainerBuilder("adobe/s3mock:latest")
        .WithPortBinding(9090, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(9090).ForPath("/")))
        .Build();

    private IBlobBackend? _backend;
    protected override IBlobBackend Backend => _backend!;

    public override async Task InitializeAsync()
    {
        await _s3.StartAsync();
        var client = new AmazonS3Client(
            new Amazon.Runtime.BasicAWSCredentials("test", "test"),
            new AmazonS3Config { ServiceURL = $"http://{_s3.Hostname}:{_s3.GetMappedPublicPort(9090)}", ForcePathStyle = true });
        await client.PutBucketAsync("fms");
        _backend = new S3BlobBackend(client, "fms");
    }

    public override Task DisposeAsync() => _s3.DisposeAsync().AsTask();
}
