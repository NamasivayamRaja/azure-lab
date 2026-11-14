using Azure.Storage.Blobs;
using Microsoft.Extensions.Options;

namespace AzureLabs.Api.Storage.Sas
{
    public class AccountKeySasService
        (BlobContainerClient containerClient, IOptions<BlobStorageOptions> options, TimeProvider clock) : ISasService
    {
        public Task<SasResult> CreateAsync(BlobSasRequest request, CancellationToken ct)
        {
            var blobClient = containerClient.GetBlobClient(request.BlobName);


            if (!blobClient.CanGenerateSasUri)
            {
                throw new InvalidOperationException("Cannot generate SAS URI for the specified blob.");
            }

            var sasBuild = SasPolicy.Build(containerClient.Name, request, options.Value, clock.GetUtcNow());

            return Task.FromResult(new SasResult(blobClient.GenerateSasUri(sasBuild), sasBuild.ExpiresOn));
        }
    }
}
