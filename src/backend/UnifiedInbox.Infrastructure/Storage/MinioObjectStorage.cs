using Microsoft.Extensions.Configuration;
using Minio;
using Minio.DataModel.Args;
using UnifiedInbox.Application;

namespace UnifiedInbox.Infrastructure.Storage;

public sealed class MinioObjectStorage : IObjectStorage
{
    private readonly IMinioClient internalClient;
    private readonly IMinioClient presignClient;
    private readonly string bucket;

    public MinioObjectStorage(IConfiguration configuration)
    {
        bucket = configuration["Storage:Bucket"] ?? Environment.GetEnvironmentVariable("STORAGE_BUCKET") ?? "attachments";
        var endpoint = configuration["Storage:Endpoint"] ?? Environment.GetEnvironmentVariable("STORAGE_ENDPOINT") ?? "localhost:9000";
        var presignEndpoint = configuration["Storage:PresignEndpoint"] ?? Environment.GetEnvironmentVariable("STORAGE_PRESIGN_ENDPOINT") ?? endpoint;
        var accessKey = configuration["Storage:AccessKey"] ?? Environment.GetEnvironmentVariable("STORAGE_ACCESS_KEY") ?? "minioadmin";
        var secretKey = configuration["Storage:SecretKey"] ?? Environment.GetEnvironmentVariable("STORAGE_SECRET_KEY") ?? "minioadmin";
        var useSsl = bool.TryParse(configuration["Storage:UseSsl"] ?? Environment.GetEnvironmentVariable("STORAGE_USE_SSL"), out var ssl) && ssl;
        internalClient = Build(endpoint, accessKey, secretKey, useSsl);
        presignClient = string.Equals(endpoint, presignEndpoint, StringComparison.OrdinalIgnoreCase)
            ? internalClient
            : Build(presignEndpoint, accessKey, secretKey, useSsl);
    }

    internal MinioObjectStorage(IMinioClient client, string bucketName)
    {
        internalClient = client;
        presignClient = client;
        bucket = bucketName;
    }

    public bool IsConfigured { get; init; } = true;

    private static IMinioClient Build(string endpoint, string accessKey, string secretKey, bool useSsl)
    {
        var normalized = endpoint.Replace("http://", "", StringComparison.OrdinalIgnoreCase).Replace("https://", "", StringComparison.OrdinalIgnoreCase).TrimEnd('/');
        return new MinioClient().WithEndpoint(normalized).WithCredentials(accessKey, secretKey).WithSSL(useSsl).Build();
    }

    private async Task EnsureBucketAsync(CancellationToken cancellationToken)
    {
        if (!await internalClient.BucketExistsAsync(new BucketExistsArgs().WithBucket(bucket), cancellationToken).ConfigureAwait(false))
            await internalClient.MakeBucketAsync(new MakeBucketArgs().WithBucket(bucket), cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> PresignedPutAsync(string objectKey, string contentType, TimeSpan timeToLive, CancellationToken cancellationToken)
    {
        await EnsureBucketAsync(cancellationToken).ConfigureAwait(false);
        var expirySeconds = Math.Clamp((int)timeToLive.TotalSeconds, 1, 7 * 24 * 3600);
        return await presignClient.PresignedPutObjectAsync(new PresignedPutObjectArgs().WithBucket(bucket).WithObject(objectKey).WithExpiry(expirySeconds)).ConfigureAwait(false);
    }

    public async Task<string> PresignedGetAsync(string objectKey, TimeSpan timeToLive, CancellationToken cancellationToken)
    {
        var expirySeconds = Math.Clamp((int)timeToLive.TotalSeconds, 1, 7 * 24 * 3600);
        return await presignClient.PresignedGetObjectAsync(new PresignedGetObjectArgs().WithBucket(bucket).WithObject(objectKey).WithExpiry(expirySeconds)).ConfigureAwait(false);
    }

    public async Task<Stream> OpenReadAsync(string objectKey, CancellationToken cancellationToken)
    {
        var output = new MemoryStream();
        await internalClient.GetObjectAsync(new GetObjectArgs().WithBucket(bucket).WithObject(objectKey).WithCallbackStream(stream => stream.CopyTo(output)), cancellationToken).ConfigureAwait(false);
        output.Position = 0;
        return output;
    }

    public async Task<StoredObjectInfo?> StatAsync(string objectKey, CancellationToken cancellationToken)
    {
        try
        {
            var stat = await internalClient.StatObjectAsync(new StatObjectArgs().WithBucket(bucket).WithObject(objectKey), cancellationToken).ConfigureAwait(false);
            return new(stat.Size, stat.ContentType);
        }
        catch (Minio.Exceptions.ObjectNotFoundException)
        {
            return null;
        }
    }

    public async Task DeleteAsync(string objectKey, CancellationToken cancellationToken)
    {
        try
        {
            await internalClient.RemoveObjectAsync(new RemoveObjectArgs().WithBucket(bucket).WithObject(objectKey), cancellationToken).ConfigureAwait(false);
        }
        catch (Minio.Exceptions.ObjectNotFoundException)
        {
            // Already gone: the desired end state.
        }
    }
}
