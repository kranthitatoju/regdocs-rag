using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace RegDocs.Ingest;

/// <summary>One line of data/pages.jsonl: a single PDF page plus the manifest metadata, so later
/// steps (chunking, citations) never need to look anything up again.</summary>
public sealed record PageRecord(
    string DocId, string Title, string IssueDate, string Status, string Center, string Bucket,
    string Topics, string Docket, string SourceUrl, string PdfUrl,
    int Page, int PageCount, int Chars, string Text);

public static class Extractor
{
    // Pages with less text than this are almost always cover/blank pages, or scanned images that need OCR.
    private const int LowTextThreshold = 50;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // keep quotes, §, ® readable
    };

    public static int Run(IngestPaths paths)
    {
        var rows = Manifest.Load(paths.Manifest).Where(r => r.Included).ToList();
        Console.WriteLine($"Extracting text from {rows.Count} PDFs → {paths.Pages}");

        int docs = 0, pages = 0, lowTextPages = 0;
        long chars = 0;
        var missing = new List<string>();
        var failed = new List<string>();
        var lowTextDocs = new List<string>();
        var perBucket = new SortedDictionary<string, (int Docs, int Pages)>();

        var tmp = paths.Pages + ".part";
        using (var writer = new StreamWriter(tmp, false, new UTF8Encoding(false)))
        {
            foreach (var row in rows)
            {
                var pdfPath = Path.Combine(paths.PdfDir, row.DocId + ".pdf");
                if (!File.Exists(pdfPath)) { missing.Add(row.DocId); continue; }

                try
                {
                    using var pdf = PdfDocument.Open(pdfPath);
                    var pageCount = pdf.NumberOfPages;
                    var docLow = 0;
                    var lines = new List<string>(pageCount);

                    foreach (var page in pdf.GetPages())
                    {
                        var text = Clean(ContentOrderTextExtractor.GetText(page));
                        if (text.Length < LowTextThreshold) docLow++;

                        lines.Add(JsonSerializer.Serialize(new PageRecord(
                            row.DocId, row.Title, row.IssueDate, row.Status, row.Center, row.Bucket,
                            row.Topics, row.Docket, row.PageUrl, row.PdfUrl,
                            page.Number, pageCount, text.Length, text), JsonOpts));
                        chars += text.Length;
                    }

                    // Write a document only once all its pages parsed, so a bad PDF never leaves half a doc.
                    foreach (var line in lines) writer.WriteLine(line);

                    docs++;
                    pages += pageCount;
                    lowTextPages += docLow;
                    if (pageCount > 0 && docLow > pageCount / 2) lowTextDocs.Add(row.DocId);
                    var b = perBucket.GetValueOrDefault(row.Bucket);
                    perBucket[row.Bucket] = (b.Docs + 1, b.Pages + pageCount);
                }
                catch (Exception ex)
                {
                    failed.Add($"{row.DocId}: {ex.Message}");
                    Console.WriteLine($"  FAILED {row.DocId}: {ex.Message}");
                }
            }
        }
        File.Move(tmp, paths.Pages, overwrite: true);

        var stats = new
        {
            GeneratedAt = DateTimeOffset.Now,
            Documents = docs,
            Pages = pages,
            TotalChars = chars,
            AvgCharsPerPage = pages == 0 ? 0 : chars / pages,
            LowTextPages = lowTextPages,
            LikelyScannedDocs = lowTextDocs,
            MissingPdfs = missing,
            Failed = failed,
            ByBucket = perBucket.ToDictionary(kv => kv.Key, kv => new { kv.Value.Docs, kv.Value.Pages }),
        };
        File.WriteAllText(paths.Stats, JsonSerializer.Serialize(stats, new JsonSerializerOptions(JsonOpts) { WriteIndented = true }));

        Console.WriteLine();
        Console.WriteLine($"Documents: {docs}   Pages: {pages}   Avg chars/page: {stats.AvgCharsPerPage}");
        Console.WriteLine($"Low-text pages: {lowTextPages}   Likely scanned docs: {lowTextDocs.Count}");
        foreach (var (bucket, v) in perBucket) Console.WriteLine($"  {bucket,-20} {v.Docs,4} docs {v.Pages,6} pages");
        if (missing.Count > 0) Console.WriteLine($"Missing PDFs (run download first): {missing.Count}");
        if (failed.Count > 0) Console.WriteLine($"Failed to parse: {failed.Count} (see {paths.Stats})");
        Console.WriteLine($"Stats: {paths.Stats}");
        return failed.Count == 0 ? 0 : 2;
    }

    /// <summary>Light cleanup only; chunking decides what to do with headers/footers.</summary>
    private static string Clean(string s)
    {
        s = s.Replace("\r", "").Replace(' ', ' ');
        s = Regex.Replace(s, @"[ \t]+", " ");
        s = Regex.Replace(s, @" *\n *", "\n");
        s = Regex.Replace(s, @"\n{3,}", "\n\n");
        return s.Trim();
    }
}
