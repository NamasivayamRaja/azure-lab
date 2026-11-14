using Azure.Messaging.ServiceBus;
using AzureLab.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Text.Json;

public sealed class OutboxOptions
{
    public const string SectionName = "Outbox";
    public int BatchSize { get; set; } = 50;
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(30);
    public int MaxAttempts { get; set; } = 5;
    public List<string> Destinations { get; set; } = [];
}

public class OutboxDispatcher : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ServiceBusClient _serviceBusClient;
    private readonly OutboxOptions _options;
    private readonly ILogger<OutboxDispatcher> _logger;
    private readonly ConcurrentDictionary<string, ServiceBusSender> _senders = new();

    public OutboxDispatcher(
        IServiceScopeFactory scopeFactory,
        IOptions<OutboxOptions> options,
        ServiceBusClient serviceBusClient,
        ILogger<OutboxDispatcher> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _options = options.Value;
        _serviceBusClient = serviceBusClient;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "OutboxDispatcher started. PollInterval={PollInterval}, BatchSize={BatchSize}, LeaseDuration={LeaseDuration}, Destinations=[{Destinations}]",
            _options.PollInterval, _options.BatchSize, _options.LeaseDuration, string.Join(", ", _options.Destinations));

        while (!stoppingToken.IsCancellationRequested)
        {
            var sentAny = false;
            try
            {
                sentAny = await ProcessOutboxAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "OutboxDispatcher loop error");
            }

            // If we claimed a full batch, poll again immediately to drain bursts; otherwise wait PollInterval.
            if (!sentAny)
            {
                await Task.Delay(_options.PollInterval, stoppingToken);
            }
        }
    }

    private async Task<bool> ProcessOutboxAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var messages = await ClaimBatchAsync(db, ct);
        if (messages.Count == 0) return false;

        foreach (var outbox in messages)
        {
            try
            {
                var sender = _senders.GetOrAdd(outbox.Destination, dest => _serviceBusClient.CreateSender(dest));

                await sender.SendMessageAsync(ToServiceBusMessage(outbox), ct);

                outbox.ProcessedUtc = DateTimeOffset.UtcNow;
                outbox.LockedUntilUtc = null;
                outbox.Error = null;

                // B2: Persist each sent row immediately so a crash mid-batch never re-sends earlier messages in the batch.
                await db.SaveChangesAsync(ct);

                _logger.LogInformation(
                    "Outbox message sent. OutboxId={OutboxId}, Destination={Destination}, Subject={Subject}, CorrelationId={CorrelationId}",
                    outbox.Id, outbox.Destination, outbox.Subject, outbox.CorrelationId);
            }
            catch (Exception ex)
            {
                outbox.RetryCount++;
                outbox.LockedUntilUtc = null; // release lease so next poll can retry (or park if at MaxAttempts)
                outbox.Error = $"{ex.GetType().Name}: {ex.Message}";
                await db.SaveChangesAsync(ct);

                if (outbox.RetryCount >= _options.MaxAttempts)
                {
                    _logger.LogCritical(ex,
                        "ALERT OutboxPoisonMessage: OutboxId={OutboxId} Destination={Destination} Subject={Subject} CorrelationId={CorrelationId} reached MaxAttempts={MaxAttempts} and is permanently parked.",
                        outbox.Id, outbox.Destination, outbox.Subject, outbox.CorrelationId, _options.MaxAttempts);
                }
                else
                {
                    _logger.LogError(ex,
                        "Failed to send outbox message {OutboxId}. RetryCount={RetryCount}/{MaxAttempts}",
                        outbox.Id, outbox.RetryCount, _options.MaxAttempts);
                }
            }
        }

        return messages.Count == _options.BatchSize;
    }

    private async Task<List<OutboxMessage>> ClaimBatchAsync(AppDbContext db, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var lockedUntil = now.Add(_options.LeaseDuration);

        // Atomic single-statement claim with ROWLOCK + READPAST:
        // Concurrent Worker instances skip locked rows instead of blocking or double-claiming.
        var parameters = new List<object>
        {
            new SqlParameter("@batchSize", _options.BatchSize),
            new SqlParameter("@maxAttempts", _options.MaxAttempts),
            new SqlParameter("@now", now),
            new SqlParameter("@lockedUntil", lockedUntil)
        };

        var destFilter = string.Empty;
        if (_options.Destinations.Count > 0)
        {
            var names = new List<string>(_options.Destinations.Count);
            for (var i = 0; i < _options.Destinations.Count; i++)
            {
                var paramName = $"@dest{i}";
                names.Add(paramName);
                parameters.Add(new SqlParameter(paramName, _options.Destinations[i]));
            }
            destFilter = $"AND Destination IN ({string.Join(", ", names)})";
        }

        var sql = $"""
            ;WITH cte AS (
                SELECT TOP (@batchSize) *
                FROM dbo.OutboxMessages WITH (ROWLOCK, READPAST, UPDLOCK)
                WHERE ProcessedUtc IS NULL
                  AND RetryCount < @maxAttempts
                  AND (LockedUntilUtc IS NULL OR LockedUntilUtc < @now)
                  {destFilter}
                ORDER BY CreatedUtc
            )
            UPDATE cte
            SET LockedUntilUtc = @lockedUntil
            OUTPUT INSERTED.*;
            """;

        return await db.OutboxMessages
            .FromSqlRaw(sql, parameters.ToArray())
            .AsTracking()
            .ToListAsync(ct);
    }

    private static ServiceBusMessage ToServiceBusMessage(OutboxMessage row)
    {
        var message = new ServiceBusMessage(row.Payload)
        {
            MessageId = row.Id.ToString(),
            CorrelationId = string.IsNullOrWhiteSpace(row.CorrelationId) ? row.Id.ToString() : row.CorrelationId,
            Subject = row.Subject,
            ContentType = "application/json"
        };

        if (!string.IsNullOrWhiteSpace(row.Properties))
        {
            var props = JsonSerializer.Deserialize<Dictionary<string, string>>(row.Properties);
            if (props is not null)
            {
                foreach (var (key, value) in props)
                {
                    message.ApplicationProperties[key] = value;
                }
            }
        }

        return message;
    }
}
