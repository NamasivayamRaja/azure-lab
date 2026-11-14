using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using AzureLabs.Api.Storage.Sas;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace AzureLabs.Api.Storage
{
    public sealed record UploadResult(bool Conflict, string BlobName, string? ETag, string ContentType);

    public sealed record SasResult(Uri Uri, DateTimeOffset ExpiresOn);

    public sealed record UploadSasResult(Uri Uri, DateTimeOffset ExpiresOn, IReadOnlyDictionary<string, string> RequiredHeaders);

    public enum CompleteStatus { Completed, NotFound, Invalid }

    public sealed record CompleteResult(CompleteStatus Status, string? Error = null, string? ETag = null);

    public sealed class EventDocumentStore(BlobContainerClient blobContainerClient, IOptions<BlobStorageOptions> options, ISasService sasService, ILogger<EventDocumentStore> logger)
    {
        private const string StatusKey = "status";
        private const string Active = "Active";
        private readonly BlobStorageOptions _options = options.Value;
        public static string GenerateBlobName(Guid eventId, string fileName) => $"{eventId:D}/{fileName}";

        public async Task<UploadResult> UploadAsync(Guid eventId, string fileName, string contentType, Stream content, bool overwrite, CancellationToken ct)
        {
            var blobName = GenerateBlobName(eventId, fileName);
            var blobClient = blobContainerClient.GetBlobClient(blobName);

            var options = new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = contentType },
                Metadata = new Dictionary<string, string>
                {
                    { "EventId", eventId.ToString() },
                    { "uploadedUtc", DateTimeOffset.UtcNow.ToString("o") },
                    { StatusKey, Active }
                },
                Conditions = overwrite ? null : new BlobRequestConditions { IfNoneMatch = ETag.All }
            };

            try
            {
                var response = await blobClient.UploadAsync(content, options, cancellationToken: ct);
                return new UploadResult(false, blobName, response.Value.ETag.ToString(), contentType);
            }
            catch (RequestFailedException ex) when (ex.Status == 409)
            {
                logger.LogWarning(ex, "Conflict uploading blob {BlobName} for event {EventId}", blobName, eventId);
                return new UploadResult(true, blobName, null, contentType);
            }
        }

        public async Task<SasResult?> GetDownloadSasAsync(Guid eventId, string fileName, CancellationToken ct)
        {
            var blobName = GenerateBlobName(eventId, fileName);
            var blobClient = blobContainerClient.GetBlobClient(blobName);

            if (!await blobClient.ExistsAsync(cancellationToken: ct))
            {
                return null;
            }
            
            BlobProperties properties;
            
            try 
            {
                properties = await blobClient.GetPropertiesAsync(cancellationToken: ct);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return null;
            }

            if(!properties.Metadata.TryGetValue(StatusKey, out var status) || status != Active)
            {
                return null; // pending/invalidated -> treat as not found
            }

            return await sasService.CreateAsync(
                new BlobSasRequest(
                    blobName, 
                    BlobSasPermissions.Read, 
                    TimeSpan.FromMinutes(_options.DownloadSasExpiryMinutes), 
                    $"attachment; filename=\"{fileName}\"")
                ,ct);
        }

        public async Task<UploadSasResult> GetUploadSasAsync(Guid eventId, string fileName, string contentType, CancellationToken ct) 
        {            
            var sas = await sasService.CreateAsync(
                new BlobSasRequest(
                    GenerateBlobName(eventId, fileName),
                    BlobSasPermissions.Write | BlobSasPermissions.Create,
                    TimeSpan.FromMinutes(_options.UploadSasExpiryMinutes),
                    $"attachment; filename=\"{fileName}\"")
                , ct);

            return new UploadSasResult(sas.Uri, sas.ExpiresOn,
                new Dictionary<string, string>
                {
                    ["x-ms-blob-type"] = "BlockBlob",
                    ["Content-Type"]= contentType
                });
        }

        public async Task<CompleteResult> CompleteAsync(Guid eventId, string fileName, FileRule rule, CancellationToken ct)
        {
            var blob = blobContainerClient.GetBlobClient(GenerateBlobName(eventId,fileName));

            BlobProperties props;

            try
            {
                props = await blob.GetPropertiesAsync(cancellationToken: ct);
            }
            catch(RequestFailedException ex) when (ex.Status == 404)
            {
                return new CompleteResult(CompleteStatus.NotFound);
            }

            if(props.Metadata.TryGetValue(StatusKey,out var s) && s == Active)
            {
                return new CompleteResult(CompleteStatus.Completed, ETag: props.ETag.ToString());
            }

            var eTag = props.ETag;
            string? error = null;

            if(props.ContentLength == 0 || props.ContentLength > DocumentFileRules.MaxBytes)
            {
                error = "File is empty or exceeds 10 mb";
            }
            else
            {
                var length = Math.Min(props.ContentLength, rule.SniffBytes);
                var fileContent = await blob.DownloadContentAsync(new BlobDownloadOptions
                {
                    Range = new HttpRange(0, length),
                    Conditions = new BlobRequestConditions { IfMatch = eTag }
                }, ct);

                error = rule.Check(fileContent.Value.Content.ToMemory(), props.ContentLength <= rule.SniffBytes);
            }

            if(error is not null)
            {
                await blob.DeleteIfExistsAsync(conditions: new BlobRequestConditions { IfMatch = eTag }, cancellationToken: ct);
                return new CompleteResult(CompleteStatus.Invalid, error);
            }

            var header = await blob.SetHttpHeadersAsync(
                new BlobHttpHeaders { ContentType = rule.ContentType },
                new BlobRequestConditions { IfMatch = eTag }, ct);

            var metaData = await blob.SetMetadataAsync(new Dictionary<string, string>
            {
                ["eventId"] = eventId.ToString("D"),
                ["uploadUtc"] = props.CreatedOn.ToString("o"),
                ["validatedUtc"] = DateTimeOffset.UtcNow.ToString("o"),
                [StatusKey] = Active
            }, new BlobRequestConditions { IfMatch = header.Value.ETag }, ct);

            return new CompleteResult(CompleteStatus.Completed, ETag: metaData.Value.ETag.ToString());
        }

    }   
}
