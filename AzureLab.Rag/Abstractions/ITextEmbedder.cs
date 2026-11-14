namespace AzureLab.Rag.Abstractions;

public interface ITextEmbedder
{
    string ModelName { get; }
    int Dimensions { get; }
    Task<IReadOnlyList<ReadOnlyMemory<float>>> EmbedDocumentsAsync(IReadOnlyList<string> texts, CancellationToken ct);
    Task<ReadOnlyMemory<float>> EmbedQueryAsync(string text, CancellationToken ct);
}