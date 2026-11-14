#if DEBUG
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Azure.Messaging.ServiceBus;
using System.Net;
using Microsoft.AspNetCore.Mvc;

public class PeekMessagesFunction
{
    private readonly ServiceBusClient _client;
    private readonly ILogger _logger;

    public PeekMessagesFunction(ServiceBusClient client, ILoggerFactory loggerFactory)
    {
        _client = client;
        _logger = loggerFactory.CreateLogger<PeekMessagesFunction>();
    }

    [Function("PeekOfferNotifications")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "peek/{queueName}")] HttpRequestData req, string queueName)
    {
        var response = req.CreateResponse(HttpStatusCode.OK);

        // Peek from main queue
        var receiver = _client.CreateReceiver(queueName);
        var peekedMessages = await receiver.PeekMessagesAsync(maxMessages: 5);

        foreach (var msg in peekedMessages)
        {
            _logger.LogInformation($"Peeked from main queue: {msg.Body}");
            // await response.WriteStringAsync($"Main Queue: {msg.Body}\n");
        }

        // Peek from DLQ
        var dlqReceiver = _client.CreateReceiver(queueName, new ServiceBusReceiverOptions
        {
            SubQueue = SubQueue.DeadLetter
        });
        var dlqMessages = await dlqReceiver.PeekMessagesAsync(maxMessages: 5);

        return new OkObjectResult(new {
            peekedMessages = peekedMessages.Select(m => new { m.MessageId, Body = m.Body.ToString(), m.DeliveryCount }),
            dlqMessages = dlqMessages.Select(m => new { m.MessageId, Body = m.Body.ToString(), m.DeliveryCount })
        });
    }
}
#endif