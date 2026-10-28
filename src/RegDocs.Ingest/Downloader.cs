using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace RegDocs.Ingest;

/// <summary>
/// Downloads each included manifest row to data/pdfs/{doc_id}.pdf. Idempotent: files already on disk
/// are skipped, so rerunning only fetches what's missing. Failures go to data/failed.csv, which has the
/// manifest format, so `download --manifest data/failed.csv` retries just those.
/// </summary>
public static class Downloader
{
    public static async Task<int> RunAsync(HttpClient http, IngestPaths paths, double delaySeconds, CancellationToken ct)
    {
        var rows = Manifest.Load(paths.Manifest).Where(r => r.Included).ToList();
        Directory.CreateDirectory(paths.PdfDir);
        Console.WriteLine($"Downloading {rows.Count} PDFs to {paths.PdfDir}");

        var log = new List<string[]>();
        var failedRows = new List<ManifestRow>();
        int downloaded = 0, skipped = 0;

        for (var i = 0; i < rows.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var row = rows[i];
            var dest = Path.Combine(paths.PdfDir, row.DocId + ".pdf");
            var prefix = $"[{i + 1}/{rows.Count}] {row.DocId}";

            if (File.Exists(dest))
            {
                var existing = await File.ReadAllBytesAsync(dest, ct);
                if (IsPdf(existing))
                {
                    log.Add([row.DocId, "skipped", existing.Length.ToString(), Sha256(existing), row.PdfUrl, ""]);
                    skipped++;
                    continue;
                }
            }

            try
            {
                var pdfUrl = string.IsNullOrWhiteSpace(row.PdfUrl)
                    ? await ResolvePdfUrlAsync(http, row.PageUrl, ct)
                    : row.PdfUrl;

                var bytes = await Http.GetBytesWithRetryAsync(http, pdfUrl, ct);
                if (!IsPdf(bytes))
                    throw new InvalidDataException("response was not a PDF (FDA may have returned an HTML page)");

                var tmp = dest + ".part";
                await File.WriteAllBytesAsync(tmp, bytes, ct);
                File.Move(tmp, dest, overwrite: true);

                log.Add([row.DocId, "downloaded", bytes.Length.ToString(), Sha256(bytes), pdfUrl, ""]);
                downloaded++;
                Console.WriteLine($"{prefix}  {bytes.Length / 1024,6} KB  {Truncate(row.Title, 70)}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.Add([row.DocId, "failed", "", "", row.PdfUrl, ex.Message]);
                failedRows.Add(row);
                Console.WriteLine($"{prefix}  FAILED: {ex.Message}");
            }

            // Be polite to fda.gov: pause only after a real network request.
            if (i < rows.Count - 1) await Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct);
        }

        Csv.Write(paths.DownloadLog, ["doc_id", "status", "bytes", "sha256", "pdf_url", "error"], log);
        if (failedRows.Count > 0)
            Csv.Write(paths.Failed, ManifestRow.Header, failedRows.Select(r => r.ToFields()));
        else if (File.Exists(paths.Failed))
            File.Delete(paths.Failed);

        Console.WriteLine();
        Console.WriteLine($"Downloaded {downloaded}, already had {skipped}, failed {failedRows.Count}. Log: {paths.DownloadLog}");
        if (failedRows.Count > 0)
            Console.WriteLine($"Retry failures with: download --manifest {paths.Failed}");
        return failedRows.Count == 0 ? 0 : 2;
    }

    /// <summary>For hand-added rows with only a page_url: find the PDF link on the guidance page.</summary>
    private static async Task<string> ResolvePdfUrlAsync(HttpClient http, string pageUrl, CancellationToken ct)
    {
        if (Regex.IsMatch(pageUrl, @"/media/\d+/download")) return pageUrl;

        var html = System.Text.Encoding.UTF8.GetString(await Http.GetBytesWithRetryAsync(http, pageUrl, ct));
        var match = Regex.Match(html, "href=\"((?:https://www\\.fda\\.gov)?/media/\\d+/download)[^\"]*\"");
        if (!match.Success) throw new InvalidDataException("no PDF link found on the guidance page");

        var href = match.Groups[1].Value;
        return href.StartsWith("http") ? href : "https://www.fda.gov" + href;
    }

    private static bool IsPdf(byte[] bytes) =>
        bytes.Length > 4 && bytes[0] == '%' && bytes[1] == 'P' && bytes[2] == 'D' && bytes[3] == 'F';

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
