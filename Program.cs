using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<S3Options>(builder.Configuration.GetSection("S3"));
builder.Services.AddSingleton<BucketLister>();
builder.Services.AddHostedService<StartupListing>();

var app = builder.Build();

app.MapGet("/", (IOptions<S3Options> o) => Results.Ok(new
{
    bucketArn = o.Value.BucketArn,
    region = o.Value.Region,
    hint = "GET /list to list the bucket again; results are also written to the log."
}));

// Re-run the listing on demand. The same values are logged and returned as JSON.
app.MapGet("/list", async (BucketLister lister, CancellationToken ct) =>
{
    var result = await lister.ListAllAsync(ct);
    return result.Error is null ? Results.Ok(result) : Results.Json(result, statusCode: 502);
});

app.Run();

public class S3Options
{
    public string BucketArn { get; set; } = "arn:aws:s3:::alphatec-app";
    public string Region { get; set; } = "ap-southeast-1";
}

public record ObjectInfo(string Key, long Size, DateTime? LastModified, string? StorageClass, string? ETag);

public record ListResult(string Bucket, string? Prefix, int Count, long TotalBytes, List<ObjectInfo> Objects, string? Error);

// Opens the bucket named by an S3 ARN and logs every object it holds.
public class BucketLister
{
    private readonly S3Options _options;
    private readonly ILogger<BucketLister> _log;

    public BucketLister(IOptions<S3Options> options, ILogger<BucketLister> log)
    {
        _options = options.Value;
        _log = log;
    }

    public async Task<ListResult> ListAllAsync(CancellationToken ct = default)
    {
        string bucket;
        string? prefix;
        try
        {
            (bucket, prefix) = ParseBucketArn(_options.BucketArn);
        }
        catch (FormatException ex)
        {
            _log.LogError("{Message}", ex.Message);
            return new ListResult("", null, 0, 0, new(), ex.Message);
        }

        _log.LogInformation("Opening bucket '{Bucket}' (prefix '{Prefix}') in {Region} from ARN {Arn}",
            bucket, prefix ?? "", _options.Region, _options.BucketArn);

        var objects = new List<ObjectInfo>();
        long total = 0;

        try
        {
            // Default credential chain: env vars, ~/.aws/credentials, SSO, or an IAM role.
            using var client = new AmazonS3Client(RegionEndpoint.GetBySystemName(_options.Region));

            var request = new ListObjectsV2Request { BucketName = bucket, Prefix = prefix };
            ListObjectsV2Response response;
            do
            {
                response = await client.ListObjectsV2Async(request, ct);

                foreach (var o in response.S3Objects ?? new List<S3Object>())
                {
                    var info = new ObjectInfo(o.Key, o.Size ?? 0, o.LastModified, o.StorageClass?.Value, o.ETag);
                    objects.Add(info);
                    total += info.Size;
                    _log.LogInformation("OBJECT key={Key} size={Size} lastModified={LastModified:u} storageClass={StorageClass} etag={ETag}",
                        info.Key, info.Size, info.LastModified, info.StorageClass, info.ETag);
                }

                request.ContinuationToken = response.NextContinuationToken;
            } while (response.IsTruncated == true);

            _log.LogInformation("Done. {Count} object(s), {Bytes:N0} bytes in '{Bucket}'.", objects.Count, total, bucket);
            return new ListResult(bucket, prefix, objects.Count, total, objects, null);
        }
        catch (AmazonS3Exception ex)
        {
            _log.LogError(ex, "S3 error {Status} {Code} on bucket '{Bucket}': {Message}",
                (int)ex.StatusCode, ex.ErrorCode, bucket, ex.Message);
            return new ListResult(bucket, prefix, objects.Count, total, objects, $"{ex.ErrorCode}: {ex.Message}");
        }
        catch (AmazonClientException ex)
        {
            // Typically "Unable to find credentials" when this machine has no AWS identity.
            _log.LogError(ex, "AWS client error (check credentials/region): {Message}", ex.Message);
            return new ListResult(bucket, prefix, objects.Count, total, objects, ex.Message);
        }
    }

    // arn:aws:s3:::bucket  or  arn:aws:s3:::bucket/optional/prefix
    private static (string Bucket, string? Prefix) ParseBucketArn(string arn)
    {
        var parts = arn.Split(':', 6);
        if (parts.Length != 6 || parts[0] != "arn" || parts[2] != "s3" || parts[5].Length == 0)
            throw new FormatException($"'{arn}' is not an S3 ARN like arn:aws:s3:::my-bucket");

        var resource = parts[5];
        var slash = resource.IndexOf('/');
        return slash < 0
            ? (resource, null)
            : (resource[..slash], resource[(slash + 1)..].Length == 0 ? null : resource[(slash + 1)..]);
    }
}

// Lists the bucket once, right after the app starts, so the values show up in the console log.
public class StartupListing : BackgroundService
{
    private readonly BucketLister _lister;

    public StartupListing(BucketLister lister) => _lister = lister;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        await _lister.ListAllAsync(stoppingToken);
    }
}
