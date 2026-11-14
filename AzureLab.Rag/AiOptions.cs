namespace AzureLab.Rag;

public enum AiProvider { Unspecified = 0, Ollama, AzureOpenAI }
public enum VectorStoreProvider { Unspecified = 0, Postgres, AzureAISearch }

public sealed class AiOptions
{
    public const string SectionName = "Ai";

    /// <summary>
    /// Feature flag for Cloud Week 1 (false when deployed without Ollama/pgvector; true locally and in Cloud Week 2).
    /// When true, AiOptionsValidator enforces fail-fast startup validation on Provider, models, and VectorStore.
    /// </summary>
    public bool Enabled { get; set; } = false;

    public AiProvider Provider { get; set; }                 // no default on purpose
    public ChatModelOptions Chat { get; set; } = new();
    public EmbeddingModelOptions Embedding { get; set; } = new();
    public OllamaOptions Ollama { get; set; } = new();
    public VectorStoreOptions VectorStore { get; set; } = new();
    public IngestionOptions Ingestion { get; set; } = new();   // inside AiOptions
    public QueryOptions Query { get; set; } = new();           // inside AiOptions
}

public sealed class ChatModelOptions
{
    public string Model { get; set; } = "";                  // Azure: the deployment name
    public int TimeoutSeconds { get; set; }
}

public sealed class EmbeddingModelOptions
{
    public string Model { get; set; } = "";
    public int Dimensions { get; set; }
    public int TimeoutSeconds { get; set; }
    public string DocumentPrefix { get; set; } = "";         // nomic: "search_document: "
    public string QueryPrefix { get; set; } = "";            // nomic: "search_query: "
}

public sealed class OllamaOptions
{
    public string Endpoint { get; set; } = "";
}

public sealed class VectorStoreOptions
{
    public VectorStoreProvider Provider { get; set; }
}

public sealed class IngestionOptions
{
    public int ChunkSizeChars { get; set; } = 1000;
    public int ChunkOverlapChars { get; set; } = 150;
    public int EmbedBatchSize { get; set; } = 16;
    public int MaxChunksPerDocument { get; set; } = 500;
}

public sealed class QueryOptions
{
    public int TopK { get; set; } = 4;
    public double MinSimilarity { get; set; } = 0.6;     // provisional: calibrated in Step 6
    public int MaxContextChars { get; set; } = 4000;
    public int MaxQuestionChars { get; set; } = 500;
    public int MaxAnswerTokens { get; set; } = 300;
}
