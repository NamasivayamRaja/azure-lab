using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Options;

namespace AzureLabs.Api.Storage
{
    public static class BlobStorageServiceCollectionExtensions
    {
        public static IServiceCollection AddBlobStorage(this IServiceCollection services, IConfiguration config)
        {
            services.AddOptions<BlobStorageOptions>()
                .Bind(config.GetSection(BlobStorageOptions.SectionName))
                .Validate(options => (string.IsNullOrEmpty(options.ConnectionString) || options.ServiceUri is null) == false ? false : true,
                "Configure exactly one of ConnectionString or ServiceUri")
                .Validate(o => !string.IsNullOrWhiteSpace(o.ContainerName), "ContainerName is required")
                .ValidateOnStart();

            services.AddSingleton(sp => {
               var options = sp.GetRequiredService<IOptions<BlobStorageOptions>>().Value;

                var clientOptions = new Azure.Storage.Blobs.BlobClientOptions()
                {
                    Retry = 
                    { 
                        Mode = Azure.Core.RetryMode.Exponential, 
                        Delay = TimeSpan.FromSeconds(1), 
                        MaxDelay = TimeSpan.FromSeconds(10), 
                        MaxRetries = 5 
                    }
                };

                return options.UsesManagedIdentity
                    ? new BlobServiceClient(options.ServiceUri!, new DefaultAzureCredential(), clientOptions)
                    : new BlobServiceClient(options.ConnectionString!, clientOptions);
            });

            //container client: also a singleton, but created from the service client
            services.AddSingleton(sp =>
            {
                var options = sp.GetRequiredService<IOptions<BlobStorageOptions>>().Value;
                var serviceClient = sp.GetRequiredService<BlobServiceClient>();
                var containerClient = serviceClient.GetBlobContainerClient(options.ContainerName);
                if (options.CreateContainerIfMissing)
                {
                    containerClient.CreateIfNotExists();
                }
                return containerClient;
            });
            return services;
        }
    }
}
