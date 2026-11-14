using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace AzureLab.Rag.Ingestion;

internal sealed class PdfTextExtractor : IDocumentTextExtractor
{
    public bool CanHandle(string extension) => extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    public string ExtractText(Stream content)
    {
        try
        {
            using var pdf = PdfDocument.Open(content);
            var sb = new StringBuilder();
            foreach (var page in pdf.GetPages())
            {
                sb.AppendLine(ContentOrderTextExtractor.GetText(page));
                sb.AppendLine();
            }
            return sb.ToString();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Bytes are in memory: failing to parse is deterministic, so retrying cannot help.
            throw new PermanentIngestionException("CorruptDocument",
                $"PDF could not be read ({ex.GetType().Name}).", ex);
        }
    }
}