using Microsoft.Extensions.Options;

namespace Fms.BuildingBlocks.Cloud;

public enum StorageProvider
{
    /// <summary>Files under Deployment:LocalStoragePath. The on-prem default.</summary>
    Local,
    AzureBlob,

    /// <summary>Amazon S3 or any S3-compatible store (MinIO, Ceph, ...).</summary>
    S3,

    /// <summary>Process memory. Development and tests only: nothing survives a restart.</summary>
    InMemory,
}

public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    public StorageProvider Provider { get; set; } = StorageProvider.Local;

    public AzureBlobOptions AzureBlob { get; set; } = new();
    public S3Options S3 { get; set; } = new();
}

public sealed class AzureBlobOptions
{
    /// <summary>Full connection string (development/Azurite). In production prefer <see cref="AccountUrl"/> with managed identity.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>e.g. https://account.blob.core.windows.net. Authenticated with DefaultAzureCredential (managed identity).</summary>
    public string? AccountUrl { get; set; }

    public string Container { get; set; } = "fms";
}

public sealed class S3Options
{
    public string Bucket { get; set; } = "";
    public string? Region { get; set; }

    /// <summary>Set for S3-compatible stores such as MinIO. Leave empty for Amazon S3.</summary>
    public string? ServiceUrl { get; set; }

    public bool ForcePathStyle { get; set; }

    /// <summary>Optional static credentials (development). Empty means the default AWS credential chain / IAM role.</summary>
    public string? AccessKey { get; set; }

    public string? SecretKey { get; set; }
}

public sealed class StorageOptionsValidator : IValidateOptions<StorageOptions>
{
    public ValidateOptionsResult Validate(string? name, StorageOptions o)
    {
        var errors = new List<string>();
        if (o.Provider == StorageProvider.AzureBlob)
        {
            if (string.IsNullOrWhiteSpace(o.AzureBlob.ConnectionString) == string.IsNullOrWhiteSpace(o.AzureBlob.AccountUrl))
                errors.Add("Storage:AzureBlob needs exactly one of ConnectionString or AccountUrl.");
            if (string.IsNullOrWhiteSpace(o.AzureBlob.Container)) errors.Add("Storage:AzureBlob:Container is required.");
        }

        if (o.Provider == StorageProvider.S3)
        {
            if (string.IsNullOrWhiteSpace(o.S3.Bucket)) errors.Add("Storage:S3:Bucket is required.");
            if (string.IsNullOrWhiteSpace(o.S3.Region) && string.IsNullOrWhiteSpace(o.S3.ServiceUrl))
                errors.Add("Storage:S3 needs Region (Amazon S3) or ServiceUrl (S3-compatible store).");
            if (string.IsNullOrWhiteSpace(o.S3.AccessKey) != string.IsNullOrWhiteSpace(o.S3.SecretKey))
                errors.Add("Storage:S3:AccessKey and SecretKey must be set together.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
