using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace AzureLab.Rag.Providers.AzureAISearch;

internal sealed class SearchChunkDocument
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("eventId")]
    public string EventId { get; set; } = "";

    [JsonPropertyName("documentKey")]
    public string DocumentKey { get; set; } = "";

    [JsonPropertyName("blobName")]
    public string BlobName { get; set; } = "";

    [JsonPropertyName("chunkIndex")]
    public int ChunkIndex { get; set; }

    [JsonPropertyName("content")]
    public string Content { get; set; } = "";

    [JsonPropertyName("contentHash")]
    public string ContentHash { get; set; } = "";

    [JsonPropertyName("embedding")]
    public float[]? Embedding { get; set; }

    [JsonPropertyName("embeddingModel")]
    public string EmbeddingModel { get; set; } = "";

    [JsonPropertyName("sourceVersionUtc")]
    public DateTimeOffset SourceVersionUtc { get; set; }

    /// <summary>
    /// Deterministic, strictly alphanumeric Search key ([0-9a-f], 64 chars) from DocumentKey + ChunkIndex.
    /// Avoids Azure AI Search's InvalidDocumentKey rule ("Keys cannot start with a leading underscore", which Base64Url hits ~1/64 times).
    /// </summary>
    public static string ComputeKey(string documentKey, int chunkIndex)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{documentKey}|{chunkIndex}"));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
