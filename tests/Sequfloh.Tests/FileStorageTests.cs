using System.Text;
using Amazon.Runtime;
using Sequfloh.Platform;

namespace Sequfloh.Tests;

/// <summary>Bucket storage settings needed for Cloudflare R2, plus a round trip against a local MinIO when one is configured.</summary>
public sealed class FileStorageTests
{
    [Fact]
    public void Bucket_client_only_sends_checksums_when_required()
    {
        var cfg = S3FileStorage.ConfigFor(new StorageOptions { ServiceUrl = "https://example.r2.cloudflarestorage.com", Bucket = "b", AccessKey = "k", SecretKey = "s" });
        Assert.Equal(RequestChecksumCalculation.WHEN_REQUIRED, cfg.RequestChecksumCalculation);
        Assert.Equal(ResponseChecksumValidation.WHEN_REQUIRED, cfg.ResponseChecksumValidation);
        Assert.True(cfg.ForcePathStyle);
        Assert.Equal("auto", cfg.AuthenticationRegion);
    }

    [Fact]
    public async Task Bucket_round_trip_when_local_bucket_is_configured()
    {
        // e.g. SEQUFLOH_TEST_S3=http://localhost:9000|sequfloh-test|minioadmin|minioadmin
        if (Environment.GetEnvironmentVariable("SEQUFLOH_TEST_S3") is not { Length: > 0 } spec) return;
        var p = spec.Split('|');
        var store = new S3FileStorage(new StorageOptions { Provider = "s3", ServiceUrl = p[0], Bucket = p[1], AccessKey = p[2], SecretKey = p[3] });
        var key = $"test/{Guid.NewGuid():N}.png";
        await store.PutAsync(key, new MemoryStream(Encoding.UTF8.GetBytes("picture")), "image/png", default);
        await using var back = await store.GetAsync(key, default);
        Assert.NotNull(back);
        Assert.Equal("picture", await new StreamReader(back!).ReadToEndAsync());
        Assert.Null(await store.GetAsync($"test/{Guid.NewGuid():N}.png", default));
    }
}
