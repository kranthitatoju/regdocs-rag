using RegDocs.Ingest;

// Usage (from the repo root):
//   dotnet run --project src/RegDocs.Ingest -- manifest   build data/manifest.csv from FDA's guidance feed
//   dotnet run --project src/RegDocs.Ingest -- download   download every included PDF into data/pdfs
//   dotnet run --project src/RegDocs.Ingest -- extract    write one JSON line per page to data/pages.jsonl
//   dotnet run --project src/RegDocs.Ingest -- all        manifest (if missing) + download + extract

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

try
{
    return command switch
    {
        "manifest" => await ManifestBuilder.RunAsync(http, paths.Manifest, Opt("json"), Flag("force"), outliers, cts.Token),
        "download" => await Downloader.RunAsync(http, paths, delay, cts.Token),
        "extract" => Extractor.Run(paths),
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
    return dl != 0 ? dl : ex;
}

static int Help()
{
    Console.WriteLine("""
        RegDocs.Ingest: build the FDA guidance corpus

        Commands
          manifest   Build data/manifest.csv (~150 CDRH guidances in subtopic buckets) from FDA's public feed
          download   Download every row with include=y into data/pdfs (skips files already there)
          extract    Extract text per page with PdfPig into data/pages.jsonl + data/corpus-stats.json
          all        manifest (only if missing), then download, then extract

        Options
          --data <dir>        Data folder (default: <repo>/data)
          --manifest <file>   Manifest to read or write (default: <data>/manifest.csv); pass data/failed.csv to retry failures
          --force             manifest: overwrite an existing manifest.csv
          --json <file>       manifest: use a saved copy of the FDA feed instead of downloading it
          --outliers <n>      manifest: non-device guidances to include for negative tests (default 5)
          --delay <seconds>   download: pause between requests (default 1.5)
        """);
    return 0;
}
