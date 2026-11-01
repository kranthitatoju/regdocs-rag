using RegDocs.Ingest;

// Usage (from the repo root):
//   dotnet run --project src/RegDocs.Ingest -- manifest   build data/manifest.csv from FDA's guidance feed
//   dotnet run --project src/RegDocs.Ingest -- download   download every included PDF into data/pdfs
//   dotnet run --project src/RegDocs.Ingest -- extract    write one JSON line per page to data/pages.jsonl
//   dotnet run --project src/RegDocs.Ingest -- chunk      write data/chunks.fixed.jsonl + data/chunks.heading.jsonl
//   dotnet run --project src/RegDocs.Ingest -- all        manifest (if missing) + download + extract + chunk
//   dotnet run --project src/RegDocs.Ingest -- embed      embed chunks with Ollama into Postgres (docker compose up -d first)
//   dotnet run --project src/RegDocs.Ingest -- search "what should a threat model include?"

var command = args.Length > 0 && !args[0].StartsWith("--") ? args[0].ToLowerInvariant() : "help";

string? Opt(string name)
{
    var i = Array.IndexOf(args, "--" + name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
bool Flag(string name) => args.Contains("--" + name);

var paths = new IngestPaths(Opt("data") ?? IngestPaths.DefaultDataDir(), Opt("manifest"));

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); Console.WriteLine("Stopping after the current file..."); };

using var http = Http.Create();
var delay = double.TryParse(Opt("delay"), out var d) ? d : 1.5;
var outliers = int.TryParse(Opt("outliers"), out var o) ? o : 5;
var chunkOptions = new ChunkOptions(
    TargetTokens: int.TryParse(Opt("target-tokens"), out var tt) ? tt : 800,
    Overlap: double.TryParse(Opt("overlap"), System.Globalization.CultureInfo.InvariantCulture, out var ov) ? ov : 0.15);

try
{
    return command switch
    {
        "manifest" => await ManifestBuilder.RunAsync(http, paths.Manifest, Opt("json"), Flag("force"), outliers, cts.Token),
        "download" => await Downloader.RunAsync(http, paths, delay, cts.Token),
        "extract" => Extractor.Run(paths),
        "chunk" => Chunker.Run(paths, chunkOptions),
        "embed" => await Embedder.RunAsync(paths, EmbedOptions.From(Opt), cts.Token),
        "search" => args.Length > 1 && !args[1].StartsWith("--")
            ? await Searcher.RunAsync(args[1], EmbedOptions.From(Opt), int.TryParse(Opt("k"), out var k) ? k : 5, cts.Token)
            : Help(),
        "all" => await RunAllAsync(),
        _ => Help(),
    };
}
catch (OperationCanceledException)
{
    Console.WriteLine("Cancelled. Rerun the same command to pick up where it stopped.");
    return 130;
}

async Task<int> RunAllAsync()
{
    if (!File.Exists(paths.Manifest))
    {
        var m = await ManifestBuilder.RunAsync(http, paths.Manifest, Opt("json"), false, outliers, cts.Token);
        if (m != 0) return m;
    }
    var dl = await Downloader.RunAsync(http, paths, delay, cts.Token);
    var ex = Extractor.Run(paths);
    var ch = Chunker.Run(paths, chunkOptions);
    return dl != 0 ? dl : ex != 0 ? ex : ch;
}

static int Help()
{
    Console.WriteLine("""
        RegDocs.Ingest: build the FDA guidance corpus

        Commands
          manifest   Build data/manifest.csv (~150 CDRH guidances in subtopic buckets) from FDA's public feed
          download   Download every row with include=y into data/pdfs (skips files already there)
          extract    Extract text per page with PdfPig into data/pages.jsonl + data/corpus-stats.json
          chunk      Clean page text and write two chunk sets: data/chunks.fixed.jsonl, data/chunks.heading.jsonl
          all        manifest (only if missing), then download, extract and chunk
          embed      Embed chunks with Ollama and store them in Postgres (needs: docker compose up -d)
          search "<question>"   Print the nearest chunks for a question, per strategy

        Options
          --data <dir>        Data folder (default: <repo>/data)
          --manifest <file>   Manifest to read or write (default: <data>/manifest.csv); pass data/failed.csv to retry failures
          --force             manifest: overwrite an existing manifest.csv
          --json <file>       manifest: use a saved copy of the FDA feed instead of downloading it
          --outliers <n>      manifest: non-device guidances to include for negative tests (default 5)
          --delay <seconds>   download: pause between requests (default 1.5)
          --target-tokens <n> chunk: target chunk size in estimated tokens (default 800)
          --overlap <0-1>     chunk: overlap between fixed-size chunks (default 0.15)
          --ollama <url>      embed/search: Ollama address (default http://localhost:11434, or $OLLAMA_URL)
          --model <name>      embed/search: embedding model (default nomic-embed-text)
          --db <connstring>   embed/search: Postgres connection (default: the docker-compose database, or $REGDOCS_DB)
          --strategy <s>      embed/search: fixed | heading | all (default all)
          --batch <n>         embed: chunks per Ollama call (default 32)
          --limit <n>         embed: embed at most n new chunks per strategy (for a quick trial run)
          --k <n>             search: results per strategy (default 5)
        """);
    return 0;
}
