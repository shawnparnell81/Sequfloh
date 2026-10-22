using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sequfloh.Application.Abstractions;

namespace Sequfloh.Platform;

/// <summary>Where uploaded file contents are kept. Records only hold the storage key.</summary>
public interface IFileStorage
{
    Task PutAsync(string key, Stream content, string contentType, CancellationToken ct);
    Task<Stream?> GetAsync(string key, CancellationToken ct);
    Task EnsureReadyAsync(CancellationToken ct);
    /// <summary>Removes a stored file. A file that is already gone is not an error.</summary>
    Task DeleteAsync(string key, CancellationToken ct);
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

    public Task DeleteAsync(string key, CancellationToken ct)
    {
        var path = PathOf(key);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    public Task EnsureReadyAsync(CancellationToken ct) { Directory.CreateDirectory(Path.GetFullPath(o.LocalPath)); return Task.CompletedTask; }
}

public sealed class S3FileStorage : IFileStorage
{
    readonly AmazonS3Client _s3;
    readonly string _bucket;
    readonly bool _https;
    readonly ILogger _log;

    public S3FileStorage(StorageOptions o, ILogger? log = null)
    {
        o = Clean(o);
        _log = log ?? NullLogger.Instance;
        _bucket = o.Bucket ?? throw new InvalidOperationException("STORAGE_BUCKET is required when STORAGE_PROVIDER is s3.");
        _https = o.ServiceUrl?.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ?? true;
        _s3 = new AmazonS3Client(new BasicAWSCredentials(o.AccessKey, o.SecretKey), ConfigFor(o));
    }

    /// <summary>
    /// Removes stray spaces and line breaks pasted into the settings, and a bucket name or trailing slash left on the end of the address
    /// (the address must be the account endpoint only, for example https://&lt;account id&gt;.r2.cloudflarestorage.com).
    /// </summary>
    public static StorageOptions Clean(StorageOptions o)
    {
        static string? T(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        var bucket = T(o.Bucket);
        var url = T(o.ServiceUrl)?.TrimEnd('/');
        if (url is not null && bucket is not null && url.EndsWith("/" + bucket, StringComparison.OrdinalIgnoreCase)) url = url[..^(bucket.Length + 1)].TrimEnd('/');
        return new StorageOptions { Provider = o.Provider.Trim(), LocalPath = o.LocalPath.Trim(), ServiceUrl = url, Bucket = bucket, AccessKey = T(o.AccessKey), SecretKey = T(o.SecretKey), Region = T(o.Region) ?? "auto" };
    }

    /// <summary>
    /// Client settings that work with Cloudflare R2 as well as MinIO and AWS. R2 does not accept the streaming
    /// trailer checksums the SDK adds by default, so checksums are only sent or checked when an operation demands them.
    /// </summary>
    public static AmazonS3Config ConfigFor(StorageOptions o) => new()
    {
        ServiceURL = o.ServiceUrl,
        ForcePathStyle = true,
        AuthenticationRegion = string.IsNullOrWhiteSpace(o.Region) ? "auto" : o.Region,
        RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
        ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
    };

    public async Task PutAsync(string key, Stream content, string contentType, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct);
        ms.Position = 0;
        try { await _s3.PutObjectAsync(new PutObjectRequest { BucketName = _bucket, Key = key, InputStream = ms, ContentType = contentType, DisablePayloadSigning = _https, UseChunkEncoding = false }, ct); }
        catch (Exception e) when (Unreachable(e)) { throw Fail("save", key, e); }
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
        catch (Exception e) when (Unreachable(e)) { throw Fail("read", key, e); }
    }

    public async Task DeleteAsync(string key, CancellationToken ct)
    {
        try { await _s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = _bucket, Key = key }, ct); }
        catch (AmazonS3Exception e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { }
        catch (Exception e) when (Unreachable(e)) { throw Fail("delete", key, e); }
    }

    static bool Unreachable(Exception e) => e is AmazonServiceException or AmazonClientException or HttpRequestException or IOException;

    /// <summary>Logs the full storage failure and gives the user a plain message with the provider's error code (never keys or addresses).</summary>
    StorageUnavailableException Fail(string action, string key, Exception e)
    {
        var code = e is AmazonServiceException s ? $"{s.ErrorCode ?? "no code"}, HTTP {(int)s.StatusCode}" : e.GetType().Name;
        _log.LogError("File storage could not {Action} {Key} in bucket {Bucket}: {Code}{NewLine}{Detail}", action, key, _bucket, code, Environment.NewLine, e.ToString());
        if (e is AmazonServiceException { ErrorCode: "AccessDenied" or "InvalidAccessKeyId" or "SignatureDoesNotMatch" } || e is AmazonServiceException { StatusCode: System.Net.HttpStatusCode.Forbidden })
            return new StorageUnavailableException($"File storage refused the upload (check storage keys). Provider answer: {code}.");
        return new StorageUnavailableException($"File storage is not reachable right now ({code}). The file was not saved. Ask your administrator to check the file storage settings.");
    }

    // the bucket is created ahead of time in the provider's dashboard; nothing to do at start-up
    public Task EnsureReadyAsync(CancellationToken ct) => Task.CompletedTask;
}

/// <summary>The file bucket refused or could not be reached; shown to the user as is.</summary>
public sealed class StorageUnavailableException(string message) : AppException(message, 503);
