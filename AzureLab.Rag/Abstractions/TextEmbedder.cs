using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace AzureLab.Rag.Abstractions;

public sealed class TextEmbedder(
    IEmbeddingGenerator<string, Embedding<float>> generator,
    IOptions<AiOptions> options) : ITextEmbedder
{
    private readonly EmbeddingModelOptions _o = options.Value.Embedding;

    public string ModelName => _o.Model;
    public int Dimensions => _o.Dimensions;

    public Task<IReadOnlyList<ReadOnlyMemory<float>>> EmbedDocumentsAsync(
        IReadOnlyList<string> texts, CancellationToken ct)
        => texts.Count == 0
            ? Task.FromResult<IReadOnlyList<ReadOnlyMemory<float>>>([])
            : GenerateAsync(texts.Select(t => _o.DocumentPrefix + t).ToList(), ct);

    public async Task<ReadOnlyMemory<float>> EmbedQueryAsync(string text, CancellationToken ct)
        => (await GenerateAsync([_o.QueryPrefix + text], ct))[0];

    private async Task<IReadOnlyList<ReadOnlyMemory<float>>> GenerateAsync(IList<string> inputs, CancellationToken ct)
    {
        var result = await generator.GenerateAsync(inputs, cancellationToken: ct);

        if (result.Count != inputs.Count)
            throw new InvalidOperationException(
                $"Embedding provider returned {result.Count} vectors for {inputs.Count} inputs.");

        var vectors = new List<ReadOnlyMemory<float>>(result.Count);
        foreach (var embedding in result)
        {
            if (embedding.Vector.Length != _o.Dimensions)
                throw new InvalidOperationException(
                    $"Model '{_o.Model}' returned {embedding.Vector.Length} dimensions, " +
                    $"but Ai:Embedding:Dimensions is {_o.Dimensions}.");
            vectors.Add(embedding.Vector);
        }
        return vectors;
    }
}