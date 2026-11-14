using Azure.Messaging.ServiceBus;
using AzureLab.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

const int MaxResubmits = 3;  // stop infinite DLQ -> queue -> DLQ loops

var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddUserSecrets<Program>();
builder.Services.AddAzureLabServiceBus(builder.Configuration);
using var host = builder.Build();
var client = host.Services.GetRequiredService<ServiceBusClient>();

return args switch
{
    ["peek", var entity] => await PeekAsync(entity),
    ["resubmit", var queue, var seq] => await ResubmitAsync(queue, long.Parse(seq)),
    ["resubmit-all", var queue] => await ResubmitAllAsync(queue),
    ["discard", var queue, var seq, var reason] => await DiscardAsync(queue, long.Parse(seq), reason),
    ["purge", var entity, var reason] => await PurgeAsync(entity, reason),
    _ => Usage()
};

int Usage()
{
    Console.WriteLine("""
    peek <queue|topic/subscription>
    resubmit <queue> <sequenceNumber>
    resubmit-all <queue>
    discard <queue> <sequenceNumber> "<reason>"
    purge <queue|topic/subscription> "<reason>"
    """);
    return 1;
}

ServiceBusReceiver CreateDlqReceiver(string entity)
{
    var opts = new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter, PrefetchCount = 0 };
    var parts = entity.Split('/');
    return parts.Length == 2
        ? client.CreateReceiver(parts[0], parts[1], opts)
        : client.CreateReceiver(entity, opts);
}

async Task<int> PeekAsync(string entity)
{
    await using var dlq = CreateDlqReceiver(entity);
    long? from = null;
    var total = 0;

    while (true)
    {
        var batch = await dlq.PeekMessagesAsync(50, from);
        if (batch.Count == 0) break;

        foreach (var m in batch)
        {
            m.ApplicationProperties.TryGetValue("LastError", out var lastError);
            m.ApplicationProperties.TryGetValue("ResubmitCount", out var resubmits);
            var body = m.Body.ToString();

            Console.WriteLine($"""
            #{m.SequenceNumber} MessageId={m.MessageId} Subject={m.Subject} Corr={m.CorrelationId}
            DeliveryCount={m.DeliveryCount} Reason={m.DeadLetterReason}
            Description={m.DeadLetterErrorDescription}
            LastError={lastError} ResubmitCount={resubmits ?? 0} Enqueued={m.EnqueuedTime:u}
            Body={(body.Length > 200 ? body[..200] + "…" : body)}
            """);
        }

        total += batch.Count;
        from = batch[^1].SequenceNumber + 1;
    }

    Console.WriteLine($"{total} message(s) in DLQ of {entity}");
    return 0;
}

async Task<int> ResubmitAsync(string queue, long seq)
{
    if (queue.Contains('/'))
    {
        Console.WriteLine("Subscription resubmit would fan out to ALL subscriptions via the topic. Not supported.");
        return 1;
    }

    await using var sender = client.CreateSender(queue);
    return await SettleOneAsync(queue, seq, async (dlq, m) =>
    {
        var count = m.ApplicationProperties.TryGetValue("ResubmitCount", out var v) ? Convert.ToInt32(v) : 0;
        if (count >= MaxResubmits)
        {
            Console.WriteLine($"#{seq} already resubmitted {count} times. Investigate or discard.");
            await dlq.AbandonMessageAsync(m);
            return false;
        }

        var copy = new ServiceBusMessage(m);
        foreach (var key in new[] { "DeadLetterReason", "DeadLetterErrorDescription", "LastError" })
            copy.ApplicationProperties.Remove(key);
        copy.ApplicationProperties["ResubmitCount"] = count + 1;
        copy.ApplicationProperties["ResubmittedUtc"] = DateTime.UtcNow.ToString("O");

        await sender.SendMessageAsync(copy);
        await dlq.CompleteMessageAsync(m);
        Console.WriteLine($"Resubmitted #{seq} (MessageId={m.MessageId}) to {queue}, attempt {count + 1}");
        return true;
    });
}

async Task<int> DiscardAsync(string queue, long seq, string reason) =>
    await SettleOneAsync(queue, seq, async (dlq, m) =>
    {
        // Production: archive body + properties to Blob BEFORE completing → audit trail
        Console.WriteLine($"Discarding #{m.SequenceNumber} MessageId={m.MessageId} Reason='{reason}' Body={m.Body}");
        await dlq.CompleteMessageAsync(m);
        return true;
    });

async Task<int> PurgeAsync(string entity, string reason)
{
    await using var dlq = CreateDlqReceiver(entity);
    var total = 0;
    while (true)
    {
        var batch = await dlq.ReceiveMessagesAsync(50, TimeSpan.FromSeconds(2));
        if (batch.Count == 0) break;
        foreach (var m in batch)
        {
            await dlq.CompleteMessageAsync(m);
            total++;
        }
    }
    Console.WriteLine($"Purged {total} message(s) from DLQ of {entity} (Reason='{reason}')");
    return 0;
}

async Task<int> ResubmitAllAsync(string queue)
{
    if (queue.Contains('/'))
    {
        Console.WriteLine("Subscription resubmit would fan out to ALL subscriptions via the topic. Not supported.");
        return 1;
    }

    await using var dlq = CreateDlqReceiver(queue);
    await using var sender = client.CreateSender(queue);
    var resubmitted = 0;
    var skipped = 0;

    while (true)
    {
        var batch = await dlq.ReceiveMessagesAsync(50, TimeSpan.FromSeconds(2));
        if (batch.Count == 0) break;

        foreach (var m in batch)
        {
            var count = m.ApplicationProperties.TryGetValue("ResubmitCount", out var v) ? Convert.ToInt32(v) : 0;
            if (count >= MaxResubmits)
            {
                Console.WriteLine($"Skipping #{m.SequenceNumber} (MessageId={m.MessageId}): already resubmitted {count} times.");
                await dlq.AbandonMessageAsync(m);
                skipped++;
                continue;
            }

            var copy = new ServiceBusMessage(m);
            foreach (var key in new[] { "DeadLetterReason", "DeadLetterErrorDescription", "LastError" })
                copy.ApplicationProperties.Remove(key);
            copy.ApplicationProperties["ResubmitCount"] = count + 1;
            copy.ApplicationProperties["ResubmittedUtc"] = DateTime.UtcNow.ToString("O");

            await sender.SendMessageAsync(copy);
            await dlq.CompleteMessageAsync(m);
            resubmitted++;
        }
    }

    Console.WriteLine($"Resubmitted {resubmitted} message(s) from DLQ to {queue} (skipped={skipped}).");
    return 0;
}

async Task<int> SettleOneAsync(string queue, long seq,
    Func<ServiceBusReceiver, ServiceBusReceivedMessage, Task<bool>> action)
{
    await using var dlq = CreateDlqReceiver(queue);
    var held = new List<ServiceBusReceivedMessage>();
    try
    {
        while (true)
        {
            var batch = await dlq.ReceiveMessagesAsync(20, TimeSpan.FromSeconds(3));
            if (batch.Count == 0)
            {
                Console.WriteLine($"#{seq} not found in DLQ of {queue}");
                return 1;
            }

            ServiceBusReceivedMessage? target = null;
            foreach (var m in batch)
                if (m.SequenceNumber == seq) target = m; else held.Add(m);

            if (target is not null) return await action(dlq, target) ? 0 : 1;
        }
    }
    finally
    {
        foreach (var m in held) await dlq.AbandonMessageAsync(m);
    }
}
