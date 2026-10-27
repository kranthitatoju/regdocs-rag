# regdocs-rag

Public RAG (retrieval-augmented generation) demo aimed at **market-ready evidence by Week 4 (Nov 22)** — a repo you can point to with eval CI and before/after scores.

## Goal

Ship a small, reviewable ASP.NET Core service that:

1. Ingests regulatory / document-style text
2. Retrieves relevant chunks for a query
3. Answers with citations
4. Proves quality with automated evals (baseline vs improved scores in CI)

This is career evidence: **link + number + date**, not a toy chatbot.

## Stack

- .NET 8 / ASP.NET Core minimal APIs
- More pieces (chunking, embeddings, vector store, eval harness) land via the Week 1–4 issue cards

## Run locally

```bash
dotnet run --project src/RegDocs.Rag
```

Then open:

- Health: `GET http://localhost:5xxx/health` (see console for the bound port)
- Swagger (Development): `/swagger`

## Health check

`GET /health` returns a simple JSON payload confirming the API is up.

## Status

Scaffold only: minimal API + health endpoint. RAG pipeline and eval CI are next.
