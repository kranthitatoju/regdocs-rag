using System.Net;
using System.Text;

namespace RegDocs.Ingest;

public sealed class IngestPaths
{
    public IngestPaths(string dataDir, string? manifest)
    {
        DataDir = Path.GetFullPath(dataDir);
        Manifest = Path.GetFullPath(manifest ?? Path.Combine(DataDir, "manifest.csv"));
    }

    public string DataDir { get; }
    public string Manifest { get; }
    public string PdfDir => Path.Combine(DataDir, "pdfs");
    public string DownloadLog => Path.Combine(DataDir, "download-log.csv");
    public string Failed => Path.Combine(DataDir, "failed.csv");
    public string Pages => Path.Combine(DataDir, "pages.jsonl");
    public string Stats => Path.Combine(DataDir, "corpus-stats.json");

    /// <summary>Walks up from the current folder to the folder holding RegDocs.Rag.sln, so the
    /// tool writes to &lt;repo&gt;/data wherever it's run from.</summary>
    public static string DefaultDataDir()
    {
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "RegDocs.Rag.sln")))
                return Path.Combine(dir.FullName, "data");
        }
        return Path.Combine(Directory.GetCurrentDirectory(), "data");
    }
}

public static class Http
{
    public static HttpClient Create()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(120) };
        // fda.gov sits behind a CDN that can reject unknown clients, so identify as a normal browser
        // plus a project tag.
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36 regdocs-rag/0.1");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "*/*");
        return http;
    }

    /// <summary>GET with up to 4 attempts and exponential backoff for 429/5xx and network errors.</summary>
    public static async Task<byte[]> GetBytesWithRetryAsync(HttpClient http, string url, CancellationToken ct)
    {
        const int maxAttempts = 4;
        for (var attempt = 1; ; attempt++)
        {
            int? status = null;
            try
            {
                using var response = await http.GetAsync(url, ct);
                if (response.IsSuccessStatusCode)
                    return await response.Content.ReadAsByteArrayAsync(ct);
                status = (int)response.StatusCode;
            }
            catch (HttpRequestException) when (attempt < maxAttempts) { } // network error: retry
            catch (TaskCanceledException) when (!ct.IsCancellationRequested && attempt < maxAttempts) { } // timeout: retry

            if (status is int code)
            {
                var retryable = code == 429 || code >= 500; // a 404 or 403 won't fix itself
                if (!retryable || attempt == maxAttempts)
                    throw new HttpRequestException($"HTTP {code} for {url}");
            }

            await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
        }
    }
}

/// <summary>Minimal RFC 4180 CSV reader/writer (handles quoted commas and newlines, Excel-friendly).</summary>
public static class Csv
{
    public static List<Dictionary<string, string>> Read(string path)
    {
        var rows = Parse(File.ReadAllText(path)); // ReadAllText strips a UTF-8 BOM
        if (rows.Count == 0) return [];

        var header = rows[0].Select(h => h.Trim().ToLowerInvariant()).ToArray();
        var result = new List<Dictionary<string, string>>();
        foreach (var row in rows.Skip(1))
        {
            if (row.All(string.IsNullOrWhiteSpace)) continue;
            var record = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < header.Length; i++)
                record[header[i]] = i < row.Count ? row[i].Trim() : "";
            result.Add(record);
        }
        return result;
    }

    public static void Write(string path, IEnumerable<string> header, IEnumerable<IEnumerable<string>> rows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".part";
        using (var w = new StreamWriter(tmp, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)))
        {
            w.WriteLine(Line(header));
            foreach (var r in rows) w.WriteLine(Line(r));
        }
        File.Move(tmp, path, overwrite: true);
    }

    private static string Line(IEnumerable<string> fields) => string.Join(",", fields.Select(Escape));

    private static string Escape(string? s)
    {
        s ??= "";
        return s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    private static List<List<string>> Parse(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else field.Append(c);
                continue;
            }

            switch (c)
            {
                case '"': inQuotes = true; break;
                case ',': row.Add(field.ToString()); field.Clear(); break;
                case '\r': break;
                case '\n': row.Add(field.ToString()); field.Clear(); rows.Add(row); row = []; break;
                default: field.Append(c); break;
            }
        }
        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); rows.Add(row); }
        return rows;
    }
}
