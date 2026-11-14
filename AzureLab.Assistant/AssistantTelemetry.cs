using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace AzureLab.Assistant;

/// <summary>
/// Same source/meter NAME as Day 5's RagTelemetry, so the API's existing AddSource/AddMeter picks these up.
/// Metric tags are LOW-CARDINALITY only. Never user, event or redeem ids, never user text.
/// </summary>
public static class AssistantTelemetry
{
    public const string Name = "AzureLab.Rag";

    public static readonly ActivitySource Source = new(Name);
    private static readonly Meter Meter = new(Name);

    public static readonly Counter<long> Requests = Meter.CreateCounter<long>("assistant.requests");              // outcome
    public static readonly Counter<long> ToolCalls = Meter.CreateCounter<long>("assistant.tool.calls");           // tool, outcome (failures = outcome != ok)
    public static readonly Counter<long> Unavailable = Meter.CreateCounter<long>("assistant.unavailable");        // reason
    public static readonly Counter<long> Tokens = Meter.CreateCounter<long>("assistant.tokens");                  // direction
    public static readonly Histogram<double> RequestMs = Meter.CreateHistogram<double>("assistant.request.duration", "ms");
    public static readonly Histogram<double> ToolMs = Meter.CreateHistogram<double>("assistant.tool.duration", "ms");   // tool
    public static readonly Histogram<double> ModelMs = Meter.CreateHistogram<double>("assistant.model.duration", "ms");
    public static readonly Histogram<int> ToolCallsPerRequest = Meter.CreateHistogram<int>("assistant.request.tool_calls");
    public static readonly Histogram<int> ModelTurnsPerRequest = Meter.CreateHistogram<int>("assistant.request.model_turns");

    public static KeyValuePair<string, object?> Tag(string key, object? value) => new(key, value);
}
