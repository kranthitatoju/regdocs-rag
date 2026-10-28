# regdocs-rag

Public RAG (retrieval-augmented generation) demo aimed at **market-ready evidence by Week 4 (Nov 22)**: a repo you can point to with eval CI and before/after scores.

## Goal

Ship a small, reviewable ASP.NET Core service that:

1. Ingests regulatory / document-style text (public FDA medical-device guidance)
2. Retrieves relevant chunks for a query
3. Answers with citations
4. Proves quality with automated evals (baseline vs improved scores in CI)

This is career evidence: **link + number + date**, not a toy chatbot.

## Repo layout

```
src/
  RegDocs.Rag/       ASP.NET Core minimal API (health now; /search and /ask next)
  RegDocs.Ingest/    Console tool: builds the corpus (manifest → PDFs → per-page text)
data/                Corpus output (manifest and stats committed; PDFs and page text git-ignored)
  samples/           4 synthetic policy .txt fixtures for quick chunk/retrieve tests
```

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) (`dotnet --list-sdks` should show 10.0.x)

## Build the corpus

About 150 public FDA medical-device (CDRH) guidance PDFs, spread across subtopics (software/AI, premarket, implants/orthopedics, quality/postmarket, drafts, cross-cutting) plus a few non-device outliers for negative tests.

```bash
dotnet run --project src/RegDocs.Ingest -- manifest   # pick documents → data/manifest.csv (review it)
dotnet run --project src/RegDocs.Ingest -- download   # PDFs → data/pdfs/
dotnet run --project src/RegDocs.Ingest -- extract    # per-page text → data/pages.jsonl + data/corpus-stats.json
```

Every step is safe to rerun. See [src/RegDocs.Ingest/README.md](src/RegDocs.Ingest/README.md) for how documents are selected, the output formats and options.

## Sample data (warm-up)

Synthetic policy fixtures for early chunk/retrieve tests live in [`data/samples/`](data/samples/) (4 short `.txt` files). Use them for fast local checks; the FDA corpus above is the real dataset.

## Run the API

```bash
dotnet run --project src/RegDocs.Rag
```

Then open (the console shows the bound port):

- Health: `GET http://localhost:5xxx/health`
- Swagger UI (Development): `/swagger`
- OpenAPI document (Development): `/openapi/v1.json`

## Stack

- .NET 10 / ASP.NET Core minimal APIs, built-in OpenAPI
- PdfPig for PDF text extraction
- Next (via the Week 1–4 issue cards): chunking, embeddings (Microsoft.Extensions.AI), Postgres + pgvector, hybrid search, cited answers, eval harness in CI

## Status

- [x] Minimal API + health endpoint
- [x] Synthetic sample fixtures
- [x] Corpus ingestion tool (manifest, download, per-page extraction)
- [ ] Chunking (fixed-size vs heading-aware)
- [ ] Embeddings + vector store
- [ ] `/search`, then `/ask` with citations
- [ ] Eval harness + CI
