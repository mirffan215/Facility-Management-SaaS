using Fms.BuildingBlocks.Cloud;

namespace Fms.UnitTests;

public sealed class StorageOptionsTests
{
    private static bool Valid(StorageOptions o) => new StorageOptionsValidator().Validate(null, o).Succeeded;

    [Fact]
    public void Local_and_in_memory_need_nothing_here() =>
        Assert.True(Valid(new StorageOptions { Provider = StorageProvider.Local }) && Valid(new StorageOptions { Provider = StorageProvider.InMemory }));

    [Fact]
    public void Azure_needs_exactly_one_of_connection_string_or_account_url()
    {
        var o = new StorageOptions { Provider = StorageProvider.AzureBlob };
        Assert.False(Valid(o));
        o.AzureBlob.ConnectionString = "UseDevelopmentStorage=true";
        Assert.True(Valid(o));
        o.AzureBlob.AccountUrl = "https://acct.blob.core.windows.net";
        Assert.False(Valid(o));
    }

    [Fact]
    public void S3_needs_a_bucket_and_a_region_or_service_url()
    {
        var o = new StorageOptions { Provider = StorageProvider.S3 };
        Assert.False(Valid(o));
        o.S3.Bucket = "b";
        Assert.False(Valid(o));
        o.S3.Region = "eu-west-1";
        Assert.True(Valid(o));
    }

    [Fact]
    public void S3_keys_must_come_as_a_pair()
    {
        var o = new StorageOptions { Provider = StorageProvider.S3, S3 = { Bucket = "b", ServiceUrl = "http://minio:9000", AccessKey = "a" } };
        Assert.False(Valid(o));
        o.S3.SecretKey = "s";
        Assert.True(Valid(o));
    }
}
