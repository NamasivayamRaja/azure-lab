using AzureLab.Rag.Abstractions;
using Microsoft.Extensions.Logging;
using Npgsql;
using Pgvector;

namespace AzureLab.Rag.Providers.Postgres;

internal sealed class PostgresDocumentVectorStore(
    NpgsqlDataSource dataSource,
    ILogger<PostgresDocumentVectorStore> logger) : IDocumentVectorStore
{
    private const string UpsertSql = """
        INSERT INTO document_chunks
            (document_key, event_id, blob_name, chunk_index, content, content_hash,
             embedding, embedding_model, source_version_utc)
        VALUES (@key, @event, @blob, @idx, @content, @hash, @emb, @model, @ver)
        ON CONFLICT (document_key, chunk_index) DO UPDATE SET
            event_id = EXCLUDED.event_id,
            blob_name = EXCLUDED.blob_name,
            content = EXCLUDED.content,
            content_hash = EXCLUDED.content_hash,
            embedding = EXCLUDED.embedding,
            embedding_model = EXCLUDED.embedding_model,
            source_version_utc = EXCLUDED.source_version_utc,
            created_utc = now()
        """;

    public async Task<ReplaceOutcome> ReplaceDocumentAsync(DocumentToStore doc, CancellationToken ct)
    {
        foreach (var c in doc.Chunks)
        {
            if (c.Embedding.Length != doc.EmbeddingDimensions)
                throw new InvalidOperationException(
                    $"Chunk {c.ChunkIndex} has {c.Embedding.Length} dimensions, expected {doc.EmbeddingDimensions}.");
        }

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // 1) Serialize writers of the same (event, blob). Released automatically at commit/rollback.
        await using (var lockCmd = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended(@k, 0))", conn, tx))
        {
            lockCmd.Parameters.AddWithValue("k", $"{doc.EventId}|{doc.BlobName}");
            await lockCmd.ExecuteNonQueryAsync(ct);
        }

        // 2) A newer version of this blob already stored? Then this message is stale.
        await using (var verCmd = new NpgsqlCommand("""
            SELECT max(source_version_utc) FROM document_chunks
            WHERE event_id = @event AND blob_name = @blob AND document_key <> @key
            """, conn, tx))
        {
            verCmd.Parameters.AddWithValue("event", doc.EventId);
            verCmd.Parameters.AddWithValue("blob", doc.BlobName);
            verCmd.Parameters.AddWithValue("key", doc.DocumentKey);

            if (await verCmd.ExecuteScalarAsync(ct) is DateTime newest &&
                new DateTimeOffset(newest, TimeSpan.Zero) > doc.SourceVersionUtc.ToUniversalTime())
            {
                await tx.RollbackAsync(ct);
                logger.LogInformation("Superseded: newer version of {Blob} already stored, skipping {Key}",
                    doc.BlobName, doc.DocumentKey);
                return ReplaceOutcome.Superseded;
            }
        }

        // 3) Upserts + cleanup in one round trip, inside the same transaction.
        await using var batch = new NpgsqlBatch(conn, tx);

        foreach (var c in doc.Chunks)
        {
            var cmd = new NpgsqlBatchCommand(UpsertSql);
            cmd.Parameters.AddWithValue("key", doc.DocumentKey);
            cmd.Parameters.AddWithValue("event", doc.EventId);
            cmd.Parameters.AddWithValue("blob", doc.BlobName);
            cmd.Parameters.AddWithValue("idx", c.ChunkIndex);
            cmd.Parameters.AddWithValue("content", c.Content);
            cmd.Parameters.AddWithValue("hash", c.ContentHash);
            cmd.Parameters.AddWithValue("emb", new Vector(c.Embedding.ToArray()));
            cmd.Parameters.AddWithValue("model", doc.EmbeddingModel);
            cmd.Parameters.AddWithValue("ver", doc.SourceVersionUtc.UtcDateTime);
            batch.BatchCommands.Add(cmd);
        }

        // Same version, fewer chunks than before: drop the leftover indexes.
        var dropStale = new NpgsqlBatchCommand(
            "DELETE FROM document_chunks WHERE document_key = @key AND chunk_index <> ALL(@idxs)");
        dropStale.Parameters.AddWithValue("key", doc.DocumentKey);
        dropStale.Parameters.AddWithValue("idxs", doc.Chunks.Select(c => c.ChunkIndex).ToArray());
        batch.BatchCommands.Add(dropStale);

        // Replace-by-blob: older versions go away in the same transaction.
        var dropOld = new NpgsqlBatchCommand(
            "DELETE FROM document_chunks WHERE event_id = @event AND blob_name = @blob AND document_key <> @key");
        dropOld.Parameters.AddWithValue("event", doc.EventId);
        dropOld.Parameters.AddWithValue("blob", doc.BlobName);
        dropOld.Parameters.AddWithValue("key", doc.DocumentKey);
        batch.BatchCommands.Add(dropOld);

        await batch.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);

        logger.LogInformation("Stored {Count} chunks for {Key}", doc.Chunks.Count, doc.DocumentKey);
        return ReplaceOutcome.Stored;
    }

    public async Task<IReadOnlyList<RetrievedChunk>> SearchAsync(
        Guid eventId, ReadOnlyMemory<float> queryEmbedding, int topK, CancellationToken ct)
    {
        if (topK <= 0) throw new ArgumentOutOfRangeException(nameof(topK));

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // pgvector 0.8+: keep scanning the index until the EventId filter yields enough rows.
        await using (var set = new NpgsqlCommand("SET LOCAL hnsw.iterative_scan = strict_order", conn, tx))
            await set.ExecuteNonQueryAsync(ct);

        await using var cmd = new NpgsqlCommand("""
            SELECT document_key, blob_name, chunk_index, content,
                   1 - (embedding <=> @q) AS similarity
            FROM document_chunks
            WHERE event_id = @event
            ORDER BY embedding <=> @q
            LIMIT @k
            """, conn, tx);
        cmd.Parameters.AddWithValue("q", new Vector(queryEmbedding.ToArray()));
        cmd.Parameters.AddWithValue("event", eventId);
        cmd.Parameters.AddWithValue("k", topK);

        var hits = new List<RetrievedChunk>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            hits.Add(new RetrievedChunk(
                reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3),
                Math.Max(0d, reader.GetDouble(4))));
        }
        return hits;
    }
}