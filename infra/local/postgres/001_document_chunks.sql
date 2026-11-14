BEGIN;

CREATE EXTENSION IF NOT EXISTS vector;

CREATE TABLE IF NOT EXISTS document_chunks (
    id                 bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    document_key       text         NOT NULL,   -- "{blobName}|{etag}"
    event_id           uuid         NOT NULL,
    blob_name          text         NOT NULL,
    chunk_index        integer      NOT NULL,
    content            text         NOT NULL,
    content_hash       char(64)     NOT NULL,   -- SHA-256 hex of content
    embedding          vector(768)  NOT NULL,
    embedding_model    text         NOT NULL,
    source_version_utc timestamptz  NOT NULL,   -- DocumentProcessed.ProccessedUtc
    created_utc        timestamptz  NOT NULL DEFAULT now(),
    CONSTRAINT uq_document_chunks_key_index UNIQUE (document_key, chunk_index),
    CONSTRAINT ck_document_chunks_index CHECK (chunk_index >= 0)
);

CREATE INDEX IF NOT EXISTS ix_document_chunks_event_blob
    ON document_chunks (event_id, blob_name);

CREATE INDEX IF NOT EXISTS ix_document_chunks_embedding_hnsw
    ON document_chunks USING hnsw (embedding vector_cosine_ops)
    WITH (m = 16, ef_construction = 64);

COMMIT;