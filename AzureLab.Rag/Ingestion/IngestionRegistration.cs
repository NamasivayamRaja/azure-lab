using Microsoft.Extensions.DependencyInjection;

namespace AzureLab.Rag.Ingestion;

public static class IngestionRegistration
{
    /// <summary>Ingestion pipeline. Functions only. The host must also register IDocumentContentSource.</summary>
    public static IServiceCollection AddAzureLabRagIngestion(this IServiceCollection services)
    {
        services.AddSingleton<IDocumentTextExtractor, PdfTextExtractor>();
        services.AddSingleton<IDocumentTextExtractor, DocxTextExtractor>();
        services.AddSingleton<DocumentIngestionService>();
        return services;
    }
}