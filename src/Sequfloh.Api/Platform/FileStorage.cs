using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace Sequfloh.Platform;

/// <summary>Where uploaded file contents are kept. Records only hold the storage key.</summary>
public interface IFileStorage
{
    Task PutAsync(string key, Stream content, string contentType, CancellationToken ct);
    Task<Stream?> GetAsync(string key, CancellationToken ct);
    Task EnsureReadyAsync(CancellationToken ct);
}

public sealed class StorageOptions
{
    /// <summary>"s3" for an S3-compatible bucket (Cloudflare R2, MinIO, AWS); anything else keeps files on local disk.</summary>
    public string Provider { get; set; } = "local";
    public string LocalPath { get; set; } = "data/files";
    public string? ServiceUrl { get; set; }
    public string? Bucket { get; set; }
    public string? AccessKey { get; set; }
    public string? SecretKey { get; set; }
    public string Region { get; set; } = "auto";
}

public sealed class LocalFileStorage(StorageOptions o) : IFileStorage
{
    string PathOf(string key)
    {
        if (key.Contains("..") || Path.IsPathRooted(key)) throw new ArgumentException("Invalid storage key.");
        return Path.Combine(Path.GetFullPath(o.LocalPath), key.Replace('/', Path.DirectorySeparatorChar));
    }

    public async Task PutAsync(string key, Stream content, string contentType, CancellationToken ct)
    {
        var path = PathOf(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var f = File.Create(path);
        await content.CopyToAsync(f, ct);
    }

    public Task<Stream?> GetAsync(string key, CancellationToken ct)
    {
        var path = PathOf(key);
        return Task.FromResult<Stream?>(File.Exists(path) ? File.OpenRead(path) : null);
    }

    public Task EnsureReadyAsync(CancellationToken ct) { Directory.CreateDirectory(Path.GetFullPath(o.LocalPath)); return Task.CompletedTask; }
}

public sealed class S3FileStorage : IFileStorage
{
    readonly AmazonS3Client _s3;
    readonly string _bucket;

    public S3FileStorage(StorageOptions o)
    {
        _bucket = o.Bucket ?? throw new InvalidOperationException("STORAGE_BUCKET is required when STORAGE_PROVIDER is s3.");
        var cfg = new AmazonS3Config { ServiceURL = o.ServiceUrl, ForcePathStyle = true, AuthenticationRegion = o.Region };
        _s3 = new AmazonS3Client(new BasicAWSCredentials(o.AccessKey, o.SecretKey), cfg);
    }

    public async Task PutAsync(string key, Stream content, string contentType, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct);
        ms.Position = 0;
        await _s3.PutObjectAsync(new PutObjectRequest { BucketName = _bucket, Key = key, InputStream = ms, ContentType = contentType, DisablePayloadSigning = true }, ct);
    }

    public async Task<Stream?> GetAsync(string key, CancellationToken ct)
    {
        try
        {
            using var r = await _s3.GetObjectAsync(_bucket, key, ct);
            var ms = new MemoryStream();
            await r.ResponseStream.CopyToAsync(ms, ct);
            ms.Position = 0;
            return ms;
        }
        catch (AmazonS3Exception e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { return null; }
    }

    // the bucket is created ahead of time in the provider's dashboard; nothing to do at start-up
    public Task EnsureReadyAsync(CancellationToken ct) => Task.CompletedTask;
}
