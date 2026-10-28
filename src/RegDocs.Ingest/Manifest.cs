using System.Text.RegularExpressions;

namespace RegDocs.Ingest;

/// <summary>One row of data/manifest.csv. The manifest is the source of truth for metadata;
/// nothing is parsed out of the PDF text.</summary>
public sealed record ManifestRow
{
    public string DocId { get; init; } = "";
    public string Bucket { get; init; } = "";
    public string Title { get; init; } = "";
    public string IssueDate { get; init; } = ""; // yyyy-MM-dd
    public string Status { get; init; } = "";    // Final | Draft
    public string Center { get; init; } = "";
    public string Topics { get; init; } = "";
    public string DocType { get; init; } = "";
    public string Docket { get; init; } = "";
    public string PageUrl { get; init; } = "";
    public string PdfUrl { get; init; } = "";
    public string Include { get; init; } = "y";

    public bool Included => Include.Trim().ToLowerInvariant() is not ("n" or "no" or "0" or "false");

    public static readonly string[] Header =
        ["doc_id", "bucket", "title", "issue_date", "status", "center", "topics", "doc_type", "docket", "page_url", "pdf_url", "include"];

    public string[] ToFields() =>
        [DocId, Bucket, Title, IssueDate, Status, Center, Topics, DocType, Docket, PageUrl, PdfUrl, Include];
}

public static class Manifest
{
    public static List<ManifestRow> Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"No manifest at {path}. Run the 'manifest' command first.");

        var rows = new List<ManifestRow>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var r in Csv.Read(path))
        {
            string G(string key) => r.TryGetValue(key, out var v) ? v : "";

            var row = new ManifestRow
            {
                DocId = G("doc_id"), Bucket = G("bucket"), Title = G("title"), IssueDate = G("issue_date"),
                Status = G("status"), Center = G("center"), Topics = G("topics"), DocType = G("doc_type"),
                Docket = G("docket"), PageUrl = G("page_url"), PdfUrl = G("pdf_url"),
                Include = string.IsNullOrWhiteSpace(G("include")) ? "y" : G("include"),
            };

            if (string.IsNullOrWhiteSpace(row.DocId))
                row = row with { DocId = DeriveId(row) };
            if (string.IsNullOrWhiteSpace(row.PageUrl) && string.IsNullOrWhiteSpace(row.PdfUrl))
            {
                Console.WriteLine($"  skipping '{row.Title}': no page_url or pdf_url");
                continue;
            }
            if (!seen.Add(row.DocId))
            {
                Console.WriteLine($"  skipping duplicate doc_id {row.DocId}");
                continue;
            }
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>Hand-added rows may have no doc_id: use the FDA media id if there is one, else a slug of the title.</summary>
    private static string DeriveId(ManifestRow row)
    {
        var media = Regex.Match(row.PdfUrl + " " + row.PageUrl, @"/media/(\d+)/download");
        if (media.Success) return "fda-" + media.Groups[1].Value;
        var slug = Regex.Replace(row.Title.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return "doc-" + (slug.Length > 60 ? slug[..60].TrimEnd('-') : slug);
    }
}
