using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace AzureLab.Rag;

public static class RagTelemetry
{
    public const string Name = "AzureLab.Rag";

    public static readonly ActivitySource Source = new(Name);
    private static readonly Meter Meter = new(Name);

    public static readonly Histogram<double> EmbedMs = Meter.CreateHistogram<double>("rag.embed.duration", "ms");
    public static readonly Histogram<double> SearchMs = Meter.CreateHistogram<double>("rag.search.duration", "ms");
    public static readonly Histogram<double> GenerateMs = Meter.CreateHistogram<double>("rag.generate.duration", "ms");
    public static readonly Histogram<double> TopSimilarity = Meter.CreateHistogram<double>("rag.retrieval.top_similarity");
    public static readonly Histogram<int> ChunksUsed = Meter.CreateHistogram<int>("rag.retrieval.chunks_used");
    public static readonly Counter<long> Tokens = Meter.CreateCounter<long>("rag.tokens");
    public static readonly Counter<long> AskOutcomes = Meter.CreateCounter<long>("rag.ask.outcomes");
    public static readonly Counter<long> IngestOutcomes = Meter.CreateCounter<long>("rag.ingest.outcomes");

    public static KeyValuePair<string, object?> Tag(string key, object? value) => new(key, value);
}