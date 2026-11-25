using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<S3Options>(builder.Configuration.GetSection("S3"));
builder.Services.AddSingleton<BucketService>();
builder.Services.AddHostedService<StartupListing>();

var app = builder.Build();

// The website: wwwroot/index.html lists the buckets; clicking one shows up to 10 objects.
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// Only the buckets named in configuration are ever offered or opened.
app.MapGet("/api/buckets", (BucketService svc) =>
    Results.Ok(svc.Buckets.Select(b => new { name = b.Name, prefix = b.Prefix })));

// At most 10 objects per bucket, newest key order as S3 returns it (alphabetical by key).
app.MapGet("/api/buckets/{name}/objects", async (string name, BucketService svc, CancellationToken ct) =>
{
    var bucket = svc.Find(name);
    if (bucket is null) return Results.NotFound(new { error = "unknown bucket" });

    var result = await svc.ListAsync(bucket, maxKeys: 10, ct);
    return result.Error is null ? Results.Ok(result) : Results.Json(result, statusCode: 502);
});

// Full listing of one bucket (every object), also written to the log.
app.MapGet("/list", async (string? bucket, BucketService svc, CancellationToken ct) =>
{
    var target = bucket is null ? svc.Buckets.FirstOrDefault() : svc.Find(bucket);
    if (target is null) return Results.NotFound(new { error = "unknown bucket" });

    var result = await svc.ListAsync(target, maxKeys: null, ct);
    return result.Error is null ? Results.Ok(result) : Results.Json(result, statusCode: 502);
});

app.Run();

public class S3Options
{
    // Buckets this site may show, as S3 ARNs (arn:aws:s3:::bucket or arn:aws:s3:::bucket/prefix).
    // No code default: config arrays append to defaults, which would duplicate entries.
    public string[] BucketArns { get; set; } = Array.Empty<string>();
    public string Region { get; set; } = "ap-southeast-1";
}

public record BucketRef(string Name, string? Prefix, string Arn);

public record ObjectInfo(string Key, long Size, DateTime? LastModified, string? StorageClass, string? ETag);

public record ListResult(
    string Bucket, string? Prefix, int Count, long TotalBytes, bool Truncated, List<ObjectInfo> Objects, string? Error);

// Opens buckets named by S3 ARNs and lists their objects with the default credential chain
// (on ECS that is the task role, so no access keys are involved).
public class BucketService : IDisposable
{
    private readonly S3Options _options;
    private readonly ILogger<BucketService> _log;
    private readonly Lazy<AmazonS3Client> _client;

    public IReadOnlyList<BucketRef> Buckets { get; }

    public BucketService(IOptions<S3Options> options, ILogger<BucketService> log)
    {
        _options = options.Value;
        _log = log;
        _client = new Lazy<AmazonS3Client>(() => new AmazonS3Client(RegionEndpoint.GetBySystemName(_options.Region)));

        var parsed = new List<BucketRef>();
        foreach (var arn in _options.BucketArns)
        {
            try { parsed.Add(ParseBucketArn(arn)); }
            catch (FormatException ex) { _log.LogError("{Message}", ex.Message); }
        }
        Buckets = parsed;
    }

    public BucketRef? Find(string name) =>
        Buckets.FirstOrDefault(b => string.Equals(b.Name, name, StringComparison.Ordinal));

    // maxKeys = null lists everything; otherwise only the first maxKeys objects.
    public async Task<ListResult> ListAsync(BucketRef bucket, int? maxKeys, CancellationToken ct = default)
    {
        _log.LogInformation("Opening bucket '{Bucket}' (prefix '{Prefix}') in {Region} from ARN {Arn}",
            bucket.Name, bucket.Prefix ?? "", _options.Region, bucket.Arn);

        var objects = new List<ObjectInfo>();
        long total = 0;
        var truncated = false;

        try
        {
            var request = new ListObjectsV2Request { BucketName = bucket.Name, Prefix = bucket.Prefix };
            if (maxKeys is int max) request.MaxKeys = max;

            ListObjectsV2Response response;
            do
            {
                response = await _client.Value.ListObjectsV2Async(request, ct);

                foreach (var o in response.S3Objects ?? new List<S3Object>())
                {
                    var info = new ObjectInfo(o.Key, o.Size ?? 0, o.LastModified, o.StorageClass?.Value, o.ETag);
                    objects.Add(info);
                    total += info.Size;
                    _log.LogInformation("OBJECT key={Key} size={Size} lastModified={LastModified:u} storageClass={StorageClass} etag={ETag}",
                        info.Key, info.Size, info.LastModified, info.StorageClass, info.ETag);
                }

                if (maxKeys is not null)
                {
                    truncated = response.IsTruncated == true;
                    break;
                }

                request.ContinuationToken = response.NextContinuationToken;
            } while (response.IsTruncated == true);

            _log.LogInformation("Done. {Count} object(s), {Bytes:N0} bytes in '{Bucket}'{More}.",
                objects.Count, total, bucket.Name, truncated ? " (more exist)" : "");
            return new ListResult(bucket.Name, bucket.Prefix, objects.Count, total, truncated, objects, null);
        }
        catch (AmazonS3Exception ex)
        {
            _log.LogError(ex, "S3 error {Status} {Code} on bucket '{Bucket}': {Message}",
                (int)ex.StatusCode, ex.ErrorCode, bucket.Name, ex.Message);
            return new ListResult(bucket.Name, bucket.Prefix, 0, 0, false, new(), $"{ex.ErrorCode}: {ex.Message}");
        }
        catch (AmazonClientException ex)
        {
            // Typically "Failed to resolve AWS credentials" when there is no role or profile.
            _log.LogError(ex, "AWS client error (check credentials/region): {Message}", ex.Message);
            return new ListResult(bucket.Name, bucket.Prefix, 0, 0, false, new(), ex.Message);
        }
    }

    // arn:aws:s3:::bucket  or  arn:aws:s3:::bucket/optional/prefix
    private static BucketRef ParseBucketArn(string arn)
    {
        var parts = arn.Split(':', 6);
        if (parts.Length != 6 || parts[0] != "arn" || parts[2] != "s3" || parts[5].Length == 0)
            throw new FormatException($"'{arn}' is not an S3 ARN like arn:aws:s3:::my-bucket");

        var resource = parts[5];
        var slash = resource.IndexOf('/');
        if (slash < 0) return new BucketRef(resource, null, arn);

        var prefix = resource[(slash + 1)..];
        return new BucketRef(resource[..slash], prefix.Length == 0 ? null : prefix, arn);
    }

    public void Dispose()
    {
        if (_client.IsValueCreated) _client.Value.Dispose();
    }
}

// Lists every configured bucket once, right after the app starts, so the values show up in the log.
public class StartupListing : BackgroundService
{
    private readonly BucketService _service;

    public StartupListing(BucketService service) => _service = service;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        foreach (var bucket in _service.Buckets)
            await _service.ListAsync(bucket, maxKeys: null, stoppingToken);
    }
}
