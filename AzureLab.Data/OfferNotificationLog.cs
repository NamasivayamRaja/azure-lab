using System;
using System.Collections.Generic;
using System.Text;

namespace AzureLab.Data
{
    public sealed class OfferNotificationLog
    {
        public long Id { get; set; }
        public Guid NotificationId { get; set; }
        public Guid OfferId { get; set; }
        public Guid EventId { get; set; }
        public string NotificationType { get; set; } = default!;
        public DateTime CreatedUtc { get; set; }
    }
}
