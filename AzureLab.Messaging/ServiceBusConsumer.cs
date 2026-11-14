using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Net;
using System.Text.Json;

namespace AzureLab.Messaging;

public sealed class ServiceBusConsumer<THandler>(
    ServiceBusClient client,
    IServiceScopeFactory scopes,
    ConsumerEndpoint endPoint,
    ILogger<ServiceBusConsumer<THandler>> logger) : BackgroundService
    where THandler : IMessageHandler
{
    private ServiceBusProcessor? _processor;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = new ServiceBusProcessorOptions
        {
            ReceiveMode = ServiceBusReceiveMode.PeekLock,
            PrefetchCount = 0,
            MaxAutoLockRenewalDuration = endPoint.MaxAutoLockRenewalDuration,
            MaxConcurrentCalls = endPoint.MaxConcurrentCalls,
            AutoCompleteMessages = false,
        };

        _processor = endPoint.Subscription is null
         ? client.CreateProcessor(endPoint.EntityPath, options)
         : client.CreateProcessor(endPoint.EntityPath, endPoint.Subscription, options);

        _processor.ProcessMessageAsync += OnProcessMessageAsync;
        _processor.ProcessErrorAsync += e => 
        {
            logger.LogError(e.Exception, "Error processing message {MessageId}", e.Exception.Message);            
            return Task.CompletedTask;
        };

        await _processor.StartProcessingAsync(stoppingToken);

        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
        catch (OperationCanceledException) { }
    }

    private async Task OnProcessMessageAsync(ProcessMessageEventArgs args)
    {
        var message = args.Message;
        var cancellationToken = args.CancellationToken;
        var sw = Stopwatch.StartNew();
        try
        {
            using var _ = logger.BeginScope(new Dictionary<string, object>
            {
                ["MessageId"] = message.MessageId,
                ["CorrelationId"] = message.CorrelationId,
                ["DeliveryCount"] = message.DeliveryCount
            });

            await using var scope = scopes.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredService<THandler>();

            if (await handler.HandleAsync(message, cancellationToken))
            {
                logger.LogInformation("[{SubscriptionName}] Message {MessageId} handled, completing", 
                    endPoint.Subscription ?? endPoint.EntityPath, message.MessageId);
                await args.CompleteMessageAsync(message, cancellationToken);

            }
            else if (endPoint.UnknownMessagePolicy == UnknownMessagePolicy.DeadLetter)
            {
                await TrySettleAsync(() => args.DeadLetterMessageAsync(message, "UnknownMessageType",
                    $"Subject '{message.Subject}' not handled by {typeof(THandler).Name}", cancellationToken), "DeadLetter");
            }
            else
            {
                logger.LogInformation("Ignoring {Subject} (tolerant reader)", message.Subject);
                await args.CompleteMessageAsync(message, cancellationToken);
            }
        }
        catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.MessageLockLost)
        {
            // Work may already be COMMITTED. The broker redelivers → idempotency (Step 6) makes it a no-op.
            logger.LogWarning("Lock lost after {elapsed:c}; broker will redeliver", sw.Elapsed);
        }
        catch (PermanentMessageException ex)
        {
            logger.LogError("Dead-lettering: {Reason} - {Description}", ex.Reason, ex.Message);
            await TrySettleAsync(() => args.DeadLetterMessageAsync(message, ex.Reason, ex.Message, cancellationToken), "DeadLetter");
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Dead-lettering: malformed payload");
            await TrySettleAsync(() => args.DeadLetterMessageAsync(message, "MalformedPayload",
                Truncate(ex.Message, 1024), cancellationToken), "DeadLetter");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Transient failure, abandoning (attempt {count})", message.DeliveryCount);
            await TrySettleAsync(() => args.AbandonMessageAsync(message,
                new Dictionary<string, object> { ["LastError"] = Truncate(ex.Message, 256) }, cancellationToken), "Abandon");
        }
    }
    private async Task TrySettleAsync(Func<Task> settle, string action)
    {
        try { await settle(); }
        catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.MessageLockLost)
        {
            logger.LogWarning("{action} skipped: lock already lost; broker will redeliver", action);
        }
    }
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_processor != null) {
            await _processor.StopProcessingAsync(cancellationToken);
            await _processor.DisposeAsync();
        }
        await base.StopAsync(cancellationToken);
    }

    //protected static T Read<T>(ServiceBusReceivedMessage message, string property) where T : class =>  
    //message.Body.ToObjectFromJson<T>() ?? throw new PermanentMessageException("Malformed message body", $"Body is not a valid {typeof(T).Name}");

    private static string Truncate(string str, int maxLength) => str.Length <= maxLength ? str : str[..maxLength];
}