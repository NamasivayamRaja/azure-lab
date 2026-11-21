using Azure.Core;
using Azure.Identity;
using AzureLab.Rag.Providers.DocumentIntelligence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace AzureLab.Rag.Ingestion;

public static class IngestionRegistration
{
    /// <summary>Ingestion pipeline. Functions and SearchTool. The host must also register IDocumentContentSource.</summary>
    public static IServiceCollection AddAzureLabRagIngestion(this IServiceCollection services)
    {
        services.TryAddSingleton<TokenCredential>(_ => new DefaultAzureCredential());
        services.AddSingleton<PdfTextExtractor>();
        services.AddSingleton<DocxTextExtractor>();
        services.AddHttpClient<AzureDocumentIntelligenceExtractor>();
        services.AddSingleton<IDocumentTextExtractor, ConfiguredDocumentTextExtractor>();
        services.AddSingleton<DocumentIngestionService>();
        return services;
    }

    private sealed class ConfiguredDocumentTextExtractor(
        PdfTextExtractor pdf,
        DocxTextExtractor docx,
        AzureDocumentIntelligenceExtractor azure,
        IOptions<AiOptions> options) : IDocumentTextExtractor
    {
        private bool UseAzure => options.Value.DocumentIntelligence.Provider == DocumentExtractorProvider.Azure;

        public bool CanHandle(string extension) =>
            UseAzure
                ? azure.CanHandle(extension)
                : pdf.CanHandle(extension) || docx.CanHandle(extension);

        public Task<DocumentExtractionResult> ExtractTextAsync(Stream content, string extension, CancellationToken ct = default)
        {
            if (UseAzure)
                return azure.ExtractTextAsync(content, extension, ct);
            if (pdf.CanHandle(extension))
                return pdf.ExtractTextAsync(content, extension, ct);
            if (docx.CanHandle(extension))
                return docx.ExtractTextAsync(content, extension, ct);
            throw new PermanentIngestionException("UnsupportedExtension", $"Unsupported file extension '{extension}'.");
        }
    }
}