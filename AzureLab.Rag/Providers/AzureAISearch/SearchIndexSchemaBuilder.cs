using Azure.Search.Documents.Indexes.Models;

namespace AzureLab.Rag.Providers.AzureAISearch;

/// <summary>
/// Single source of truth for the Azure AI Search index schema.
/// Used by the deploy-time schema / re-index tool (control-plane), never by runtime app identities.
/// </summary>
public static class SearchIndexSchemaBuilder
{
    public const string VectorProfileName = "vprofile-hnsw";
    public const string HnswAlgorithmName = "algo-hnsw";
    public const string DefaultSemanticConfigName = "sem-default";

    public static SearchIndex BuildIndex(
        string indexName,
        int embeddingDimensions,
        string semanticConfigurationName = DefaultSemanticConfigName)
    {
        if (string.IsNullOrWhiteSpace(indexName))
            throw new ArgumentException("Index name is required.", nameof(indexName));
        if (embeddingDimensions <= 0)
            throw new ArgumentOutOfRangeException(nameof(embeddingDimensions), "Embedding dimensions must be > 0.");

        var fields = new List<SearchField>
        {
            new SimpleField("id", SearchFieldDataType.String) { IsKey = true },
            new SimpleField("eventId", SearchFieldDataType.String) { IsFilterable = true },
            new SimpleField("documentKey", SearchFieldDataType.String) { IsFilterable = true },
            new SimpleField("blobName", SearchFieldDataType.String) { IsFilterable = true },
            new SimpleField("chunkIndex", SearchFieldDataType.Int32) { IsFilterable = true, IsSortable = true },
            new SearchableField("content") { AnalyzerName = LexicalAnalyzerName.EnMicrosoft },
            new SimpleField("contentHash", SearchFieldDataType.String),
            new VectorSearchField("embedding", embeddingDimensions, VectorProfileName) { IsHidden = true },
            new SimpleField("embeddingModel", SearchFieldDataType.String) { IsFilterable = true },
            new SimpleField("sourceVersionUtc", SearchFieldDataType.DateTimeOffset) { IsFilterable = true, IsSortable = true }
        };

        var vectorSearch = new VectorSearch
        {
            Algorithms =
            {
                new HnswAlgorithmConfiguration(HnswAlgorithmName)
                {
                    Parameters = new HnswParameters
                    {
                        Metric = VectorSearchAlgorithmMetric.Cosine,
                        M = 4,
                        EfConstruction = 400,
                        EfSearch = 500
                    }
                }
            },
            Profiles =
            {
                new VectorSearchProfile(VectorProfileName, HnswAlgorithmName)
            }
        };

        var semanticSearch = new SemanticSearch
        {
            Configurations =
            {
                new SemanticConfiguration(
                    semanticConfigurationName,
                    new SemanticPrioritizedFields
                    {
                        TitleField = new SemanticField("blobName"),
                        ContentFields = { new SemanticField("content") }
                    })
            }
        };

        return new SearchIndex(indexName, fields)
        {
            VectorSearch = vectorSearch,
            SemanticSearch = semanticSearch
        };
    }
}
