using Azure.Storage.Sas;

namespace AzureLabs.Api.Storage.Sas
{
    public sealed record BlobSasRequest(string BlobName, BlobSasPermissions BlobSasPermissions, TimeSpan LifeTime, string? ContentDisposition = null);
    public interface ISasService
    {
        Task<SasResult> CreateAsync(BlobSasRequest request, CancellationToken ct);
    }
}
