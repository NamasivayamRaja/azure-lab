using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AzureLab.Messaging;

public enum UnknownMessagePolicy { DeadLetter, Complete } // command -> DeadLetter, event -> Complete

public sealed class ConsumerEndpoint
{
    public string EntityPath {get; set;} = string.Empty;
    public string? Subscription { get; set; }
    public int MaxConcurrentCalls {get; set;} = 1;
    public TimeSpan MaxAutoLockRenewalDuration {get; set;} = TimeSpan.FromMinutes(2);
    public UnknownMessagePolicy UnknownMessagePolicy {get; set;} = UnknownMessagePolicy.DeadLetter;
}

public interface IMessageHandler
{
    Task<bool> HandleAsync(ServiceBusReceivedMessage message, CancellationToken ct);
}

public static class ConsumerRegistrationExtnesion
{
    public static IServiceCollection AddServiceBusConsumer<THandler>(this IServiceCollection services, IConfiguration configuration, string name)
        where THandler : class, IMessageHandler
    {
        var endPoint = configuration.GetSection($"Consumers:{name}").Get<ConsumerEndpoint>() ?? throw new InvalidOperationException($"Missing configuration for {name}");

        if(string.IsNullOrWhiteSpace(endPoint.EntityPath))
            throw new InvalidOperationException($"Invalid configuration for {name}: EntityPath is required");

        services.AddScoped<THandler>();
        services.AddHostedService(sp =>
        {
            var client = sp.GetRequiredService<ServiceBusClient>();
            var scopes = sp.GetRequiredService<IServiceScopeFactory>();
            var logger = sp.GetRequiredService<ILogger<ServiceBusConsumer<THandler>>>();
            return new ServiceBusConsumer<THandler>(client, scopes, endPoint, logger);
        });
        return services;
    }
}