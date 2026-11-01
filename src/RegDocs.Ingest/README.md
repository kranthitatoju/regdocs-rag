# RegDocs.Ingest

Console tool that builds the corpus: ~150 public FDA medical-device (CDRH) guidance PDFs, with text extracted per page.

## Run it (from the repo root)

```bash
dotnet run --project src/RegDocs.Ingest -- manifest   # 1. pick the documents → data/manifest.csv
#    review the CSV: set include=n to drop a row, or add your own rows (title + page_url is enough)
dotnet run --project src/RegDocs.Ingest -- download   # 2. PDFs → data/pdfs/{doc_id}.pdf
dotnet run --project src/RegDocs.Ingest -- extract    # 3. pages → data/pages.jsonl + data/corpus-stats.json
dotnet run --project src/RegDocs.Ingest -- chunk      # 4. chunks → data/chunks.fixed.jsonl + data/chunks.heading.jsonl
docker compose up -d                                   #    start Postgres + pgvector (repo root)
dotnet run --project src/RegDocs.Ingest -- embed      # 5. vectors → Postgres `chunks` table (needs Ollama + nomic-embed-text)
dotnet run --project src/RegDocs.Ingest -- search "what should a threat model include?"
```

Or `-- all` to run every step in one go (it only builds the manifest if it doesn't exist yet).

## How documents are picked

`manifest` reads the JSON feed behind FDA's [guidance search table](https://www.fda.gov/regulatory-information/search-fda-guidance-documents), keeps CDRH guidances that have a PDF, and assigns each to the first bucket it matches, newest first:

| bucket | cap | matches |
|---|---|---|
| draft | 15 | status = Draft (for "is this final?" and draft-vs-final tests) |
| software-ai | 35 | Digital Health topic; software, cybersecurity, AI/ML, PCCP, CDS… |
| implants-ortho | 30 | Orthopedic topic; implants, biocompatibility, MR safety, sterilization… |
| quality-postmarket | 20 | Postmarket/UDI topics; recalls, MDR, quality system… |
| premarket | 40 | 510(k), PMA, De Novo, Q-Sub, eSTAR… |
| cross-cutting | 20 | everything else (human factors, labeling, clinical…) |
| outlier | 5 | recent drug (CDER) and food guidances, for negative tests |

## Chunking

`chunk` first cleans the page text, then writes two chunk sets from the same cleaned lines, so evals can compare them.

**Cleaning:** drops FDA running headers ("Contains Nonbinding Recommendations", "Draft – Not for Implementation") and any other line repeated on more than half a document's pages; drops page-number lines and table-of-contents pages; strips the margin line numbers that draft guidances print on every line.

| strategy | how | `section` field |
|---|---|---|
| `fixed` | ~800 tokens (≈3,200 chars, 4 chars/token estimate), closed at the next sentence end (hard cap 1.25×), 15% overlap | empty |
| `heading` | one chunk per section, detected from the FDA outline `I.` → `A.` → `1.` → `(1)` (and `5.1.2` style); sections under 300 chars merge into the next; long sections split like `fixed` | heading path, e.g. `IV. … > B. …` |

Every chunk keeps `doc_id`, `page_start`/`page_end` and the manifest metadata for citations. Stats (sizes, tiny chunks, heading counts, cleaning counts) go to `data/chunk-stats.json`.

## Embedding and search

`embed` reads both chunk files, embeds each chunk with Ollama (`nomic-embed-text`, 768 numbers per chunk) through `IEmbeddingGenerator` (Microsoft.Extensions.AI, via OllamaSharp), and upserts it into the `chunks` table (`db/init/001_schema.sql`).

- **Prefixes:** chunks are embedded as `search_document: <text>`, questions as `search_query: <question>`. nomic-embed-text was trained this way.
- **Re-runs are cheap:** each row stores a SHA-256 of exactly what was embedded plus the model name. Only new or changed chunks are embedded again; unchanged ones just get their metadata refreshed. Rows no longer in the chunk files are deleted.
- **One transaction per batch:** an interrupted run loses at most one batch and resumes on the next run.
- **Try it small first:** `embed --limit 20` embeds 20 chunks per strategy.

`search` embeds the question and runs an exact nearest-neighbour query (`ORDER BY embedding <=> query`, cosine distance) for each strategy, printing score (cosine similarity, higher is closer), title, section, pages and a preview.

Ollama on another machine: `--ollama http://<ip>:11434` (or set `OLLAMA_URL`). Different database: `--db "<connection string>"` (or `REGDOCS_DB`).

## Outputs

| file | committed? | what |
|---|---|---|
| `data/manifest.csv` | yes | source of truth for metadata (title, date, status, docket, URLs) |
| `data/download-log.csv` | yes | per-doc status, bytes, SHA-256 |
| `data/corpus-stats.json` | yes | doc/page counts per bucket, low-text pages, failures |
| `data/chunk-stats.json` | yes | chunk size distribution per strategy, cleaning and heading counts |
| `data/pdfs/` | no | the PDFs (re-downloadable) |
| `data/pages.jsonl` | no | one JSON object per page: metadata + `page`, `page_count`, `chars`, `text` |
| `data/chunks.*.jsonl` | no | one JSON object per chunk: `chunk_id`, `strategy`, `section`, `page_start`, `page_end`, `tokens_est`, `text` + metadata |

Every step can be rerun safely. `download` skips PDFs it already has, and failures are written to `data/failed.csv` (same format as the manifest), so `download --manifest data/failed.csv` retries just those.

## Options

`--data <dir>`, `--manifest <file>`, `--force` (rebuild manifest), `--json <file>` (use a saved copy of the feed if fda.gov blocks the request), `--outliers <n>`, `--delay <seconds>` (default 1.5), `--target-tokens <n>` (default 800), `--overlap <0-1>` (default 0.15), `--ollama <url>`, `--model <name>`, `--db <connstring>`, `--strategy fixed|heading|all`, `--batch <n>` (default 32), `--limit <n>`, `--k <n>` (search, default 5).
