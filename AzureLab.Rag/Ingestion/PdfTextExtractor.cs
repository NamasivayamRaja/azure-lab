using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace AzureLab.Rag.Ingestion;

public sealed class PdfTextExtractor : IDocumentTextExtractor
{
    public bool CanHandle(string extension) => extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    public Task<DocumentExtractionResult> ExtractTextAsync(Stream content, string extension, CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            using var pdf = PdfDocument.Open(content);
            var sb = new StringBuilder();
            var pageCount = 0;
            foreach (var page in pdf.GetPages())
            {
                ct.ThrowIfCancellationRequested();
                pageCount++;
                sb.AppendLine(ContentOrderTextExtractor.GetText(page));
                sb.AppendLine();
            }
            return Task.FromResult(new DocumentExtractionResult(sb.ToString(), pageCount));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Bytes are in memory: failing to parse is deterministic, so retrying cannot help.
            throw new PermanentIngestionException("CorruptDocument",
                $"PDF could not be read ({ex.GetType().Name}).", ex);
        }
    }
}