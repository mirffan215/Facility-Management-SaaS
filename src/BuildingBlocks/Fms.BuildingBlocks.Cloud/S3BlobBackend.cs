using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using Fms.BuildingBlocks.Storage;

namespace Fms.BuildingBlocks.Cloud;

/// <summary>
/// Amazon S3 and S3-compatible stores. Writes send If-None-Match: * so an existing key is never overwritten (enforced by S3 and
/// MinIO; a store that ignores the header would allow overwrite, see docs).
/// </summary>
public sealed class S3BlobBackend(IAmazonS3 s3, string bucket) : IBlobBackend
{
    public async Task PutAsync(string key, Stream content, CancellationToken ct = default)
    {
        try
        {
            // PutObject needs a seekable stream or a known length to sign the payload.
            await using var buffered = content.CanSeek ? null : new MemoryStream();
            if (buffered is not null)
            {
                await content.CopyToAsync(buffered, ct);
                buffered.Position = 0;
            }

            await s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = bucket,
                Key = key,
                InputStream = buffered ?? content,
                IfNoneMatch = "*",
                ServerSideEncryptionMethod = ServerSideEncryptionMethod.AES256,
            }, ct);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict)
        {
            throw new InvalidOperationException("Storage key already exists; objects are write-once.", ex);
        }
    }

    public async Task<Stream?> GetAsync(string key, CancellationToken ct = default)
    {
        try
        {
            var response = await s3.GetObjectAsync(bucket, key, ct);
            return response.ResponseStream;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task DeleteAsync(string key, CancellationToken ct = default) =>
        await s3.DeleteObjectAsync(bucket, key, ct);
}
