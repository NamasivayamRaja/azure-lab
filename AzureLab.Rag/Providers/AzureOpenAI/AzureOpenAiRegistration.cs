using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AzureLab.Rag.Providers.AzureOpenAI;

internal static class AzureOpenAiRegistration
{
    public static IServiceCollection AddAzureOpenAiModels(this IServiceCollection services)
    {
        // Single AzureOpenAIClient shared by chat & embedding clients.
        // Keyless Entra auth only (disableLocalAuth=true on the Azure resource).
        services.AddSingleton(sp =>
        {
            var o = sp.GetRequiredService<IOptions<AiOptions>>().Value;
            return new AzureOpenAIClient(new Uri(o.AzureOpenAI.Endpoint), new DefaultAzureCredential());
        });

        services.AddSingleton<IChatClient>(sp =>
        {
            var o = sp.GetRequiredService<IOptions<AiOptions>>().Value;
            var client = sp.GetRequiredService<AzureOpenAIClient>();
            IChatClient inner = client.GetChatClient(o.Chat.Model).AsIChatClient();

            // GPT-5 / o-series reasoning models reject custom Temperature (HTTP 400).
            // Isolate that quirk at the provider boundary so business code stays provider-agnostic.
            return IsReasoningModel(o.Chat.Model)
                ? new ReasoningModelChatClient(inner)
                : inner;
        });

        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
        {
            var o = sp.GetRequiredService<IOptions<AiOptions>>().Value;
            var client = sp.GetRequiredService<AzureOpenAIClient>();
            return client.GetEmbeddingClient(o.Embedding.Model).AsIEmbeddingGenerator();
        });

        return services;
    }

    private static bool IsReasoningModel(string modelOrDeployment) =>
        modelOrDeployment.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase) ||
        modelOrDeployment.StartsWith("o1", StringComparison.OrdinalIgnoreCase) ||
        modelOrDeployment.StartsWith("o3", StringComparison.OrdinalIgnoreCase) ||
        modelOrDeployment.StartsWith("o4", StringComparison.OrdinalIgnoreCase);

    private sealed class ReasoningModelChatClient(IChatClient innerClient) : DelegatingChatClient(innerClient)
    {
        public override Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => base.GetResponseAsync(messages, Sanitize(options), cancellationToken);

        public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => base.GetStreamingResponseAsync(messages, Sanitize(options), cancellationToken);

        private static ChatOptions? Sanitize(ChatOptions? options)
        {
            if (options is null || options.Temperature is null)
                return options;

            var clone = options.Clone();
            clone.Temperature = null;
            return clone;
        }
    }
}
