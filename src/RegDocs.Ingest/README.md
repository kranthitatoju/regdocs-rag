# RegDocs.Ingest

Console tool that builds the corpus: ~150 public FDA medical-device (CDRH) guidance PDFs, with text extracted per page.

## Run it (from the repo root)

```bash
dotnet run --project src/RegDocs.Ingest -- manifest   # 1. pick the documents → data/manifest.csv
#    review the CSV: set include=n to drop a row, or add your own rows (title + page_url is enough)
dotnet run --project src/RegDocs.Ingest -- download   # 2. PDFs → data/pdfs/{doc_id}.pdf
dotnet run --project src/RegDocs.Ingest -- extract    # 3. pages → data/pages.jsonl + data/corpus-stats.json
```

Or `-- all` to run all three in one go (it only builds the manifest if it doesn't exist yet).

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

## Outputs

| file | committed? | what |
|---|---|---|
| `data/manifest.csv` | yes | source of truth for metadata (title, date, status, docket, URLs) |
| `data/download-log.csv` | yes | per-doc status, bytes, SHA-256 |
| `data/corpus-stats.json` | yes | doc/page counts per bucket, low-text pages, failures |
| `data/pdfs/` | no | the PDFs (re-downloadable) |
| `data/pages.jsonl` | no | one JSON object per page: metadata + `page`, `page_count`, `chars`, `text` |

Every step can be rerun safely. `download` skips PDFs it already has, and failures are written to `data/failed.csv` (same format as the manifest), so `download --manifest data/failed.csv` retries just those.

## Options

`--data <dir>`, `--manifest <file>`, `--force` (rebuild manifest), `--json <file>` (use a saved copy of the feed if fda.gov blocks the request), `--outliers <n>`, `--delay <seconds>` (default 1.5).
