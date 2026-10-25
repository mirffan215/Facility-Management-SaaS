using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Azure.Identity;
using Azure.Storage.Blobs;
using Fms.BuildingBlocks.Deployment;
using Fms.BuildingBlocks.Storage;
using Fms.BuildingBlocks.Tenancy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Fms.BuildingBlocks.Cloud;

public static class StorageServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IFileStore"/>, always wrapped so keys stay inside the current tenant's prefix, over the configured
    /// backend. The provider is configuration, so the same build runs on Azure, on S3 or on a customer's disk.
    /// </summary>
    public static IServiceCollection AddFmsStorage(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IValidateOptions<StorageOptions>, StorageOptionsValidator>();
        services.AddOptions<StorageOptions>().Bind(configuration.GetSection(StorageOptions.SectionName)).ValidateOnStart();

        services.AddSingleton<IBlobBackend>(sp => CreateBackend(
            sp.GetRequiredService<IOptions<StorageOptions>>().Value,
            sp.GetRequiredService<IOptions<DeploymentOptions>>().Value));
        services.AddScoped<IFileStore>(sp => new TenantScopedFileStore(sp.GetRequiredService<IBlobBackend>(), sp.GetRequiredService<ITenantContext>()));
        return services;
    }

    internal static IBlobBackend CreateBackend(StorageOptions options, DeploymentOptions deployment) => options.Provider switch
    {
        StorageProvider.InMemory => new InMemoryBlobBackend(),
        StorageProvider.Local => new LocalFileBlobBackend(
            deployment.LocalStoragePath ?? throw new InvalidOperationException("Deployment:LocalStoragePath is required for local storage.")),
        StorageProvider.AzureBlob => new AzureBlobBackend(AzureContainer(options.AzureBlob)),
        StorageProvider.S3 => new S3BlobBackend(S3Client(options.S3), options.S3.Bucket),
        _ => throw new InvalidOperationException($"Unknown storage provider '{options.Provider}'."),
    };

    private static BlobContainerClient AzureContainer(AzureBlobOptions o) =>
        !string.IsNullOrWhiteSpace(o.ConnectionString)
            ? new BlobContainerClient(o.ConnectionString, o.Container)
            : new BlobContainerClient(new Uri(new Uri(o.AccountUrl!), o.Container), new DefaultAzureCredential());

    private static AmazonS3Client S3Client(S3Options o)
    {
        var config = new AmazonS3Config { ForcePathStyle = o.ForcePathStyle };
        if (!string.IsNullOrWhiteSpace(o.ServiceUrl)) config.ServiceURL = o.ServiceUrl;
        if (!string.IsNullOrWhiteSpace(o.Region)) config.RegionEndpoint = RegionEndpoint.GetBySystemName(o.Region);

        return string.IsNullOrWhiteSpace(o.AccessKey)
            ? new AmazonS3Client(config)
            : new AmazonS3Client(new BasicAWSCredentials(o.AccessKey, o.SecretKey), config);
    }
}
