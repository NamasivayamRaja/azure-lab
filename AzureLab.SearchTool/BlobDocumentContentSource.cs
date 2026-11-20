using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using AzureLab.Rag.Ingestion;

namespace AzureLab.SearchTool;

internal sealed class BlobDocumentContentSource(BlobServiceClient blobService) : IDocumentContentSource
{
    private const string Container = "event-docs";

    public async Task<Stream?> OpenAsync(string blobName, string etag, CancellationToken ct)
    {
        var blob = blobService.GetBlobContainerClient(Container).GetBlobClient(blobName);
        try
        {
            var response = await blob.DownloadStreamingAsync(new BlobDownloadOptions
            {
                Conditions = new BlobRequestConditions { IfMatch = new ETag(etag) }
            }, ct);

            using var result = response.Value;
            var ms = new MemoryStream();
            await result.Content.CopyToAsync(ms, ct);
            ms.Position = 0;
            return ms;
        }
        catch (RequestFailedException ex) when (ex.Status is 404 or 412)
        {
            return null;
        }
    }
}
