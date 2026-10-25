using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Fms.BuildingBlocks.Storage;

namespace Fms.BuildingBlocks.Cloud;

/// <summary>Azure Blob Storage. Objects are write-once: a second write to the same key fails instead of overwriting.</summary>
public sealed class AzureBlobBackend(BlobContainerClient container) : IBlobBackend
{
    private bool _containerReady;

    public async Task PutAsync(string key, Stream content, CancellationToken ct = default)
    {
        await EnsureContainerAsync(ct);
        try
        {
            await container.GetBlobClient(key).UploadAsync(content, new BlobUploadOptions
            {
                Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
            }, ct);
        }
        catch (RequestFailedException ex) when (ex.Status is 409 or 412)
        {
            throw new InvalidOperationException("Storage key already exists; objects are write-once.", ex);
        }
    }

    public async Task<Stream?> GetAsync(string key, CancellationToken ct = default)
    {
        try
        {
            var response = await container.GetBlobClient(key).DownloadStreamingAsync(cancellationToken: ct);
            return response.Value.Content;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task DeleteAsync(string key, CancellationToken ct = default) =>
        await container.GetBlobClient(key).DeleteIfExistsAsync(cancellationToken: ct);

    private async Task EnsureContainerAsync(CancellationToken ct)
    {
        if (_containerReady) return;
        await container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: ct);
        _containerReady = true;
    }
}
