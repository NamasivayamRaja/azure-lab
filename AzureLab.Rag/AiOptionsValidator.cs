using Microsoft.Extensions.Options;

namespace AzureLab.Rag;

internal sealed class AiOptionsValidator : IValidateOptions<AiOptions>
{
    public ValidateOptionsResult Validate(string? name, AiOptions o)
    {
        // A2: When Ai:Enabled is false (Cloud Week 1 default), skip provider/model validation so API and Functions start cleanly.
        if (!o.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        var errors = new List<string>();

        switch (o.Provider)
        {
            case AiProvider.Ollama:
                if (!Uri.TryCreate(o.Ollama.Endpoint, UriKind.Absolute, out _))
                    errors.Add("Ai:Ollama:Endpoint must be an absolute URI.");
                break;
            case AiProvider.AzureOpenAI:
                if (!Uri.TryCreate(o.AzureOpenAI.Endpoint, UriKind.Absolute, out var aoaiUri) ||
                    !string.Equals(aoaiUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                    errors.Add("Ai:AzureOpenAI:Endpoint must be an absolute HTTPS URI.");
                if (!string.IsNullOrEmpty(o.Embedding.DocumentPrefix) || !string.IsNullOrEmpty(o.Embedding.QueryPrefix))
                    errors.Add("Ai:Embedding:DocumentPrefix and QueryPrefix must be empty for AzureOpenAI (only Ollama nomic-embed-text uses prefixes).");
                break;
            default:
                errors.Add("Ai:Provider must be 'Ollama' or 'AzureOpenAI'.");
                break;
        }

        if (string.IsNullOrWhiteSpace(o.Chat.Model)) errors.Add("Ai:Chat:Model is required.");
        if (o.Chat.TimeoutSeconds <= 0) errors.Add("Ai:Chat:TimeoutSeconds must be > 0.");
        if (string.IsNullOrWhiteSpace(o.Embedding.Model)) errors.Add("Ai:Embedding:Model is required.");
        if (o.Embedding.Dimensions <= 0) errors.Add("Ai:Embedding:Dimensions must be > 0.");
        if (o.Embedding.TimeoutSeconds <= 0) errors.Add("Ai:Embedding.TimeoutSeconds must be > 0.");

        switch (o.VectorStore.Provider)
        {
            case VectorStoreProvider.Postgres:
                break;
            case VectorStoreProvider.AzureAISearch:
                var s = o.VectorStore.AzureAISearch;
                if (!Uri.TryCreate(s.Endpoint, UriKind.Absolute, out var searchUri) ||
                    !string.Equals(searchUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                    errors.Add("Ai:VectorStore:AzureAISearch:Endpoint must be an absolute HTTPS URI.");
                if (string.IsNullOrWhiteSpace(s.IndexName))
                    errors.Add("Ai:VectorStore:AzureAISearch:IndexName is required.");
                if (s.SearchMode == AzureAiSearchMode.HybridSemantic &&
                    string.IsNullOrWhiteSpace(s.SemanticConfigurationName))
                    errors.Add("Ai:VectorStore:AzureAISearch:SemanticConfigurationName is required when SearchMode is HybridSemantic.");
                break;
            default:
                errors.Add("Ai:VectorStore:Provider must be 'Postgres' or 'AzureAISearch'.");
                break;
        }

        var ing = o.Ingestion;
        if (ing.ChunkSizeChars is < 200 or > 4000)
            errors.Add("Ai:Ingestion:ChunkSizeChars must be between 200 and 4000.");
        if (ing.ChunkOverlapChars < 0 || ing.ChunkOverlapChars >= ing.ChunkSizeChars / 2)
            errors.Add("Ai:Ingestion:ChunkOverlapChars must be >= 0 and less than half of ChunkSizeChars.");
        if (ing.EmbedBatchSize is < 1 or > 128)
            errors.Add("Ai:Ingestion:EmbedBatchSize must be between 1 and 128.");
        if (ing.MaxChunksPerDocument <= 0)
            errors.Add("Ai:Ingestion:MaxChunksPerDocument must be > 0.");

        var q = o.Query;
        if (q.TopK is < 1 or > 20) errors.Add("Ai:Query:TopK must be between 1 and 20.");
        if (q.MinSimilarity is < 0 or > 1) errors.Add("Ai:Query:MinSimilarity must be between 0 and 1.");
        if (q.MaxContextChars is < 500 or > 20000) errors.Add("Ai:Query:MaxContextChars must be between 500 and 20000.");
        if (q.MaxQuestionChars is < 1 or > 2000) errors.Add("Ai:Query:MaxQuestionChars must be between 1 and 2000.");
        if (q.MaxAnswerTokens is < 16 or > 2000) errors.Add("Ai:Query:MaxAnswerTokens must be between 16 and 2000.");

        var cs = o.ContentSafety;
        if (cs.Enabled)
        {
            if (!Uri.TryCreate(cs.Endpoint, UriKind.Absolute, out var csUri) ||
                !string.Equals(csUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                errors.Add("Ai:ContentSafety:Endpoint must be an absolute HTTPS URI when Ai:ContentSafety:Enabled is true.");
            if (cs.TimeoutSeconds is < 1 or > 30)
                errors.Add("Ai:ContentSafety:TimeoutSeconds must be between 1 and 30.");
            if (cs.SeverityThreshold is not (2 or 4 or 6))
                errors.Add("Ai:ContentSafety:SeverityThreshold must be 2, 4, or 6.");
        }

        var di = o.DocumentIntelligence;
        if (di.Provider == DocumentExtractorProvider.Azure)
        {
            if (!Uri.TryCreate(di.Endpoint, UriKind.Absolute, out var diUri) ||
                !string.Equals(diUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                errors.Add("Ai:DocumentIntelligence:Endpoint must be an absolute HTTPS URI when Provider is Azure.");
            if (string.IsNullOrWhiteSpace(di.ModelId))
                errors.Add("Ai:DocumentIntelligence:ModelId is required when Provider is Azure.");
            if (di.TimeoutSeconds is < 10 or > 240)
                errors.Add("Ai:DocumentIntelligence:TimeoutSeconds must be between 10 and 240 (below the 300s Service Bus maxAutoLockRenewalDuration).");
            if (di.PollIntervalMs is < 250 or > 10000)
                errors.Add("Ai:DocumentIntelligence:PollIntervalMs must be between 250 and 10000.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
