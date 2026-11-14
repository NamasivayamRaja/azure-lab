namespace AzureLabs.Api.Storage
{
    public sealed class BlobStorageOptions
    {
        public const string SectionName = "BlobStorage";

        public string? ConnectionString { get; init; }
        public Uri? ServiceUri { get; init; }
        public string ContainerName { get; init; } = "bulk-player-enrollment";
        public bool CreateContainerIfMissing { get; init; }

        public bool UsesManagedIdentity => ServiceUri is not null;

        public int DownloadSasExpiryMinutes { get; init; } = 15;
        public int UploadSasExpiryMinutes { get; init; } = 10;
        public int ClockSkewMinutes { get; init; } = 5;

        public bool AllowHttpSas { get; init; } = false;
    }
}
