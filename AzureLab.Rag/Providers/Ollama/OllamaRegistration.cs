using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using OllamaSharp;
using Polly;

namespace AzureLab.Rag.Providers.Ollama;

internal static class OllamaRegistration
{
    public const string ChatClientName = "ollama-chat";
    public const string EmbeddingClientName = "ollama-embedding";

    public static IServiceCollection AddOllamaModels(this IServiceCollection services)
    {
        // 1. Ollama Embedding client: stateless & idempotent POST /api/embeddings
        //    AttemptTimeout (5s) -> Retry (2x exponential + jitter) -> CircuitBreaker (opens for 15s after 3 failures) -> TotalTimeout (30s)
        services.AddHttpClient(EmbeddingClientName, (sp, http) =>
        {
            var o = sp.GetRequiredService<IOptions<AiOptions>>().Value;
            http.BaseAddress = new Uri(o.Ollama.Endpoint);
            http.Timeout = Timeout.InfiniteTimeSpan; // managed by Polly TotalRequestTimeout
        })
        .AddStandardResilienceHandler(options =>
        {
            options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(5);
            options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(30);

            options.Retry.MaxRetryAttempts = 2;
            options.Retry.BackoffType = DelayBackoffType.Exponential;
            options.Retry.UseJitter = true;
            options.Retry.Delay = TimeSpan.FromMilliseconds(250);

            options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
            options.CircuitBreaker.FailureRatio = 0.5;
            options.CircuitBreaker.MinimumThroughput = 3;
            options.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(15);
        });

        // 2. Ollama Chat client: stateless inference POST /api/chat
        //    AttemptTimeout (60s) -> Retry (1x exponential + jitter for transport errors) -> CircuitBreaker -> TotalTimeout (180s)
        services.AddHttpClient(ChatClientName, (sp, http) =>
        {
            var o = sp.GetRequiredService<IOptions<AiOptions>>().Value;
            http.BaseAddress = new Uri(o.Ollama.Endpoint);
            http.Timeout = Timeout.InfiniteTimeSpan; // managed by Polly TotalRequestTimeout
        })
        .AddStandardResilienceHandler(options =>
        {
            options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(60);
            options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(180);

            options.Retry.MaxRetryAttempts = 1;
            options.Retry.BackoffType = DelayBackoffType.Exponential;
            options.Retry.UseJitter = true;
            options.Retry.Delay = TimeSpan.FromMilliseconds(400);

            options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(120);
            options.CircuitBreaker.FailureRatio = 0.5;
            options.CircuitBreaker.MinimumThroughput = 3;
            options.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(15);
        });

        services.AddSingleton<IChatClient>(sp =>
        {
            var o = sp.GetRequiredService<IOptions<AiOptions>>().Value;
            var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient(ChatClientName);
            return new OllamaApiClient(http, o.Chat.Model);
        });

        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
        {
            var o = sp.GetRequiredService<IOptions<AiOptions>>().Value;
            var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient(EmbeddingClientName);
            return new OllamaApiClient(http, o.Embedding.Model);
        });

        return services;
    }
}
