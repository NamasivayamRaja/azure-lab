using Microsoft.Extensions.DependencyInjection;

namespace AzureLab.Rag.Query;

public static class QueryRegistration
{
    /// <summary>Query path. API only.</summary>
    public static IServiceCollection AddAzureLabRagQuery(this IServiceCollection services)
        => services.AddSingleton<EventQuestionService>();
}