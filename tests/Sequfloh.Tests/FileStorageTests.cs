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

    [Fact]
    public async Task Unreachable_bucket_gives_a_plain_storage_message()
    {
        var store = new S3FileStorage(new StorageOptions { Provider = "s3", ServiceUrl = "http://127.0.0.1:9", Bucket = "b", AccessKey = "k", SecretKey = "s" });
        var e = await Assert.ThrowsAsync<StorageUnavailableException>(() => store.PutAsync("x/y.png", new MemoryStream([1]), "image/png", default));
        Assert.Equal(503, e.StatusCode);
        Assert.StartsWith("File storage is not reachable", e.Message);
    }

    [Fact]
    public async Task Refused_credentials_report_the_provider_error_code()
    {
        if (Environment.GetEnvironmentVariable("SEQUFLOH_TEST_S3") is not { Length: > 0 } spec) return;
        var p = spec.Split('|');
        var store = new S3FileStorage(new StorageOptions { Provider = "s3", ServiceUrl = p[0], Bucket = p[1], AccessKey = "wrong-key", SecretKey = "wrong-secret" });
        var e = await Assert.ThrowsAsync<StorageUnavailableException>(() => store.PutAsync("x/y.png", new MemoryStream([1]), "image/png", default));
        Assert.StartsWith("File storage refused the upload (check storage keys)", e.Message);
        Assert.Contains("InvalidAccessKeyId", e.Message);
    }

    [Theory]
    [InlineData(" https://acct.r2.cloudflarestorage.com/sequfloh-files/ \n", "https://acct.r2.cloudflarestorage.com")]
    [InlineData("https://acct.r2.cloudflarestorage.com/", "https://acct.r2.cloudflarestorage.com")]
    [InlineData("https://acct.r2.cloudflarestorage.com", "https://acct.r2.cloudflarestorage.com")]
    public void Pasted_settings_are_cleaned(string url, string expected)
    {
        var o = S3FileStorage.Clean(new StorageOptions { Provider = "s3 ", ServiceUrl = url, Bucket = " sequfloh-files\n", AccessKey = " key\r\n", SecretKey = "secret ", Region = " " });
        Assert.Equal(expected, o.ServiceUrl);
        Assert.Equal("sequfloh-files", o.Bucket);
        Assert.Equal("key", o.AccessKey);
        Assert.Equal("secret", o.SecretKey);
        Assert.Equal("auto", o.Region);
        Assert.Equal("s3", o.Provider);
    }
}
