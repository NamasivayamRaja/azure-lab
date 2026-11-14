using Azure.Messaging.ServiceBus;

namespace AzureLab.Worker
{
    public class TopicSender
    {
        public ServiceBusSender Sender { get; }
        public TopicSender(ServiceBusSender sender) => Sender = sender;
    }
}
