using Azure.Storage.Sas;

namespace AzureLabs.Api.Storage.Sas;


internal static class  SasPolicy
{
    private static readonly TimeSpan MaxLifeTime = TimeSpan.FromHours(1);

    public static BlobSasBuilder Build
        (string container, BlobSasRequest request, BlobStorageOptions options, DateTimeOffset now)
    {
        if (request.LifeTime > MaxLifeTime)
            throw new ArgumentOutOfRangeException(nameof(request.LifeTime), $"SAS lifetime cannot exceed {MaxLifeTime.TotalMinutes} minutes.");

        var expiresOn = now.Add(request.LifeTime);

        var builder = new BlobSasBuilder(request.BlobSasPermissions, expiresOn)
        {
            BlobContainerName = container,
            BlobName = request.BlobName,
            ContentDisposition = request.ContentDisposition,
            StartsOn = now.AddMinutes(-options.ClockSkewMinutes),
            Protocol = options.AllowHttpSas? SasProtocol.HttpsAndHttp : SasProtocol.Https
        };

        return builder;
    }
}