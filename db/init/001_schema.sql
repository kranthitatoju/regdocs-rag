-- Schema for regdocs-rag. Safe to run repeatedly: Postgres runs it on the container's first start,
-- and the ingest tool's `embed` command runs it again before every run.

CREATE EXTENSION IF NOT EXISTS vector;

-- One row per chunk, for both chunking strategies. The text, the citation metadata and the vector
-- live in the same row, so one query returns everything an answer needs.
CREATE TABLE IF NOT EXISTS chunks (
    chunk_id         text PRIMARY KEY,            -- e.g. fda-119933:heading:0017
    strategy         text NOT NULL,               -- fixed | heading
    doc_id           text NOT NULL,
    ordinal          int  NOT NULL,               -- position of the chunk within its document
    section          text NOT NULL DEFAULT '',    -- heading path (heading strategy only)
    page_start       int  NOT NULL,
    page_end         int  NOT NULL,
    tokens_est       int  NOT NULL,
    title            text NOT NULL,
    issue_date       date,
    status           text NOT NULL,               -- Final | Draft
    bucket           text NOT NULL,
    docket           text NOT NULL DEFAULT '',
    source_url       text NOT NULL DEFAULT '',
    pdf_url          text NOT NULL DEFAULT '',
    text             text NOT NULL,

    content_hash     text NOT NULL,               -- SHA-256 of exactly what was embedded (prefix + text)
    embedding        vector(768),                 -- nomic-embed-text produces 768 numbers per chunk
    embedding_model  text,                        -- vectors from different models can't be compared
    embedded_at      timestamptz
);

CREATE INDEX IF NOT EXISTS chunks_strategy_idx ON chunks (strategy);
CREATE INDEX IF NOT EXISTS chunks_doc_idx      ON chunks (doc_id);

-- No vector index yet, on purpose: with ~7,000 rows an exact scan is already fast, and it is the
-- baseline to compare against. Step 9 adds an approximate (HNSW) index and measures the difference:
--   CREATE INDEX chunks_embedding_hnsw ON chunks USING hnsw (embedding vector_cosine_ops);
