using System;
using System.Collections.Generic;
using System.Text;

namespace AzureLab.Messaging
{
    public sealed record DocumentProcessed(
        string DocumentKey, // stable id for this blob version (blob name + ETag)
        string BlobName,
        Guid? EventId,
        string ContentType,
        long SizeBytes,
        DateTimeOffset ProccessedUtc
        );
}
