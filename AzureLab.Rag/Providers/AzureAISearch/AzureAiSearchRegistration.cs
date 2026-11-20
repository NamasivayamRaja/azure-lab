using Azure.Identity;
using Azure.Search.Documents;
using AzureLab.Rag.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AzureLab.Rag.Providers.AzureAISearch;

internal static class AzureAiSearchRegistration
{
    public static IServiceCollection AddAzureAiSearchVectorStore(this IServiceCollection services)
    {
        // Keyless Entra ID authentication only (disableLocalAuth=true on the Azure AI Search service).
        services.AddSingleton(sp =>
        {
            var o = sp.GetRequiredService<IOptions<AiOptions>>().Value.VectorStore.AzureAISearch;
            return new SearchClient(new Uri(o.Endpoint), o.IndexName, new DefaultAzureCredential());
        });

        services.AddSingleton<IDocumentVectorStore, AzureAiSearchDocumentVectorStore>();
        return services;
    }
}
