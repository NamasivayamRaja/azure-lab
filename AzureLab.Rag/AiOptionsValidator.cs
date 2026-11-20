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

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
