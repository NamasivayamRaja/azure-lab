using AzureLab.Rag.Abstractions;
using AzureLab.Rag.Providers.AzureAISearch;
using AzureLab.Rag.Providers.AzureOpenAI;
using AzureLab.Rag.Providers.Ollama;
using AzureLab.Rag.Providers.Postgres;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AzureLab.Rag;

public static class RagRegistration
{
    public static IServiceCollection AddAzureLabRag(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IValidateOptions<AiOptions>, AiOptionsValidator>();
        services.AddOptions<AiOptions>()
            .Bind(configuration.GetSection(AiOptions.SectionName))
            .ValidateOnStart();

        var enabled = configuration.GetValue<bool>($"{AiOptions.SectionName}:Enabled");
        if (!enabled)
        {
            // A2: Register disabled sentinel implementations so DI graphs (AskEndpoint, AssistantService, DocumentIngestionFunction)
            // can be constructed when Ai:Enabled=false without requiring Ollama or Postgres connection strings.
            services.AddSingleton<IChatClient>(new DisabledChatClient());
            services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new DisabledEmbeddingGenerator());
            services.AddSingleton<ITextEmbedder, TextEmbedder>();
            services.AddSingleton<IDocumentVectorStore>(new DisabledDocumentVectorStore());
            return services;
        }

        // Provider is decided ONCE, here. Business code never sees it.
        var provider = configuration.GetValue<AiProvider>($"{AiOptions.SectionName}:Provider");
        switch (provider)
        {
            case AiProvider.Ollama:
                services.AddOllamaModels();
                break;
            case AiProvider.AzureOpenAI:
                services.AddAzureOpenAiModels();
                break;
            default:
                break; // AiOptionsValidator reports a clear startup error via ValidateOnStart()
        }

        services.AddSingleton<ITextEmbedder, TextEmbedder>();

        var storeProvider = configuration.GetValue<VectorStoreProvider>($"{AiOptions.SectionName}:VectorStore:Provider");
        switch (storeProvider)
        {
            case VectorStoreProvider.Postgres:
                services.AddPostgresVectorStore(configuration);
                break;
            case VectorStoreProvider.AzureAISearch:
                services.AddAzureAiSearchVectorStore();
                break;
            default:
                break; // AiOptionsValidator reports a clear startup error via ValidateOnStart()
        }

        return services;
    }

    private sealed class DisabledChatClient : IChatClient
    {
        public ChatClientMetadata Metadata { get; } = new("disabled");
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("AI features are disabled (Ai:Enabled=false).");
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("AI features are disabled (Ai:Enabled=false).");
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class DisabledEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        public EmbeddingGeneratorMetadata Metadata { get; } = new("disabled");
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("AI features are disabled (Ai:Enabled=false).");
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class DisabledDocumentVectorStore : IDocumentVectorStore
    {
        public Task<ReplaceOutcome> ReplaceDocumentAsync(DocumentToStore document, CancellationToken ct) =>
            throw new InvalidOperationException("AI features are disabled (Ai:Enabled=false).");
        public Task<IReadOnlyList<RetrievedChunk>> SearchAsync(Guid eventId, ReadOnlyMemory<float> queryVector, int topK, CancellationToken ct, string? queryText = null) =>
            throw new InvalidOperationException("AI features are disabled (Ai:Enabled=false).");
    }
}
