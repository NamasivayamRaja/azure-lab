using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AzureLab.Messaging
{
    public static class ServiceBusRegistration
    {
        public static IServiceCollection AddAzureLabServiceBus(this IServiceCollection services, IConfiguration configuration)
        {

            services.AddOptions<ServiceBusOptions>()
                .Bind(configuration.GetSection(ServiceBusOptions.SectionName))
                .Validate(o=> !string.IsNullOrEmpty(o.ConnectionString) 
                    || !string.IsNullOrEmpty(o.FullyQualifiedNamespace), 
                    "Either ConnectionString or FullyQualifiedNamespace must be provided.")
                .ValidateOnStart();

            services.AddSingleton(sp => 
            {
                var options = sp.GetRequiredService<IOptions<ServiceBusOptions>>().Value;

                var clientOptions = new ServiceBusClientOptions
                {
                    TransportType = ServiceBusTransportType.AmqpTcp, //emulator supports only TCP, not WebSockets
                    RetryOptions = new ServiceBusRetryOptions
                    {
                        Mode = ServiceBusRetryMode.Exponential,
                        Delay = TimeSpan.FromSeconds(0.8),
                        MaxDelay = TimeSpan.FromSeconds(5),
                        MaxRetries = 3,
                        TryTimeout = TimeSpan.FromSeconds(30)
                    }
                };

                if (!string.IsNullOrEmpty(options.ConnectionString))
                {
                    return new ServiceBusClient(options.ConnectionString, clientOptions);
                }
                else if (!string.IsNullOrEmpty(options.FullyQualifiedNamespace))
                {
                    return new ServiceBusClient(options.FullyQualifiedNamespace, new DefaultAzureCredential(), clientOptions);
                }
                else
                {
                    throw new InvalidOperationException("Either ConnectionString or FullyQualifiedNamespace must be provided.");
                }
            });

            return services;
        }
    }
}
