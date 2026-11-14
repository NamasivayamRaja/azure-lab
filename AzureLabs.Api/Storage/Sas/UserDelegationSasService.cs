using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Options;

namespace AzureLabs.Api.Storage.Sas
{
    public class UserDelegationSasService
        (BlobServiceClient blobServiceClient, BlobContainerClient containerClient, 
            IOptions<BlobStorageOptions> options, TimeProvider clock, ILogger<UserDelegationSasService> logger) : ISasService
    {
        private static readonly TimeSpan KeyLifeTime = TimeSpan.FromHours(2);
        private readonly SemaphoreSlim _lock = new(1, 1);
        private volatile UserDelegationKey? _userDelegationKey;

        public async Task<SasResult> CreateAsync(BlobSasRequest request, CancellationToken ct)
        {
            var policyBuild= SasPolicy.Build(containerClient.Name, request, options.Value, clock.GetUtcNow());

            var key = await GetUserDelegationKeyAsync(policyBuild.ExpiresOn, ct);

            var uri = new BlobUriBuilder(containerClient.GetBlobClient(request.BlobName).Uri)
            {
                Sas = policyBuild.ToSasQueryParameters(key, blobServiceClient.AccountName)
            }.ToUri();

            return new SasResult(uri, policyBuild.ExpiresOn);
        }

        private async Task<UserDelegationKey> GetUserDelegationKeyAsync(DateTimeOffset sasExpiresOn, CancellationToken ct)
        {
            if (_userDelegationKey is not null && _userDelegationKey.SignedExpiresOn > sasExpiresOn)
            {
                return _userDelegationKey;
            }

            await _lock.WaitAsync(ct);
            try
            {
                if (_userDelegationKey is not null && _userDelegationKey.SignedExpiresOn > sasExpiresOn)
                {
                    return _userDelegationKey;
                }

                var now = clock.GetUtcNow();
                var expiresOn = now.Add(KeyLifeTime);
                _userDelegationKey = await blobServiceClient.GetUserDelegationKeyAsync(now, expiresOn, cancellationToken: ct);

                logger.LogInformation("Obtained new user delegation key expiring at {ExpiresOn}", expiresOn);

                return _userDelegationKey;
            }
            finally
            {
                _lock.Release();
            }
        }
    }
}
