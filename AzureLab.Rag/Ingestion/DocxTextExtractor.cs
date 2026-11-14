using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace AzureLab.Rag.Ingestion;

internal sealed class DocxTextExtractor : IDocumentTextExtractor
{
    public bool CanHandle(string extension) => extension.Equals(".docx", StringComparison.OrdinalIgnoreCase);

    public string ExtractText(Stream content)
    {
        try
        {
            using var doc = WordprocessingDocument.Open(content, false);
            var body = doc.MainDocumentPart?.Document?.Body;
            if (body is null) return "";

            var sb = new StringBuilder();
            foreach (var paragraph in body.Descendants<Paragraph>())   // includes table cells
                sb.Append(paragraph.InnerText).Append("\n\n");
            return sb.ToString();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new PermanentIngestionException("CorruptDocument",
                $"DOCX could not be read ({ex.GetType().Name}).", ex);
        }
    }
}