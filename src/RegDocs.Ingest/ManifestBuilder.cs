using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RegDocs.Ingest;

/// <summary>
/// Builds data/manifest.csv from the JSON feed behind FDA's "Search for FDA Guidance Documents" table.
/// Picks ~150 CDRH (medical device) guidances spread across subtopic buckets, newest first, plus a few
/// non-device guidances as outliers for negative tests. Review the CSV afterwards: set include=n to drop
/// a row, or add rows by hand (title + page_url is enough).
/// </summary>
public static class ManifestBuilder
{
    private const string FeedUrl = "https://www.fda.gov/files/api/datatables/static/search-for-guidance.json";
    private const string FdaBase = "https://www.fda.gov";
    private const string DeviceCenter = "Devices and Radiological";

    private sealed record Candidate(
        string MediaId, string Title, string IssueDate, string Status, string Center,
        string Topics, string DocType, string Docket, string PageUrl, string PdfUrl);

    private sealed record Bucket(string Name, int Cap, Func<Candidate, bool> Matches);

    private static bool Has(string text, string pattern) => Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase);

    // Order matters: each device guidance goes to the FIRST bucket it matches.
    private static readonly Bucket[] Buckets =
    [
        new("draft", 15, c => c.Status.Equals("Draft", StringComparison.OrdinalIgnoreCase)),
        new("software-ai", 35, c =>
            Has(c.Topics, "digital health") ||
            Has(c.Title, @"software|cybersecur|artificial intelligence|machine learning|predetermined change control|clinical decision support|interoperab|mobile medical|wireless|radio frequency|cloud")),
        new("implants-ortho", 30, c =>
            Has(c.Topics, "orthopedic") ||
            Has(c.Title, @"implant|biocompatib|10993|magnetic resonance|\bMR\b|steril|spinal|\bspine\b|\bhip\b|\bknee\b|\bbone\b|\bjoint\b|orthop|metal")),
        new("quality-postmarket", 20, c =>
            Has(c.Topics, @"postmarket|\budi\b|adverse event") ||
            Has(c.Title, @"recall|medical device reporting|quality system|unique device identif|postmarket|\b522\b|corrections and removals|complaint")),
        new("premarket", 40, c =>
            Has(c.Topics, @"510\(k\)|premarket approval|de novo") ||
            Has(c.Title, @"510\(k\)|premarket|de novo|pre-submission|q-submission|estar|refuse to accept|substantial equivalence|humanitarian")),
        new("cross-cutting", 20, _ => true),
    ];

    public static async Task<int> RunAsync(HttpClient http, string outPath, string? jsonPath, bool force, int outliers, CancellationToken ct)
    {
        if (File.Exists(outPath) && !force)
        {
            Console.WriteLine($"{outPath} already exists. Edit it by hand, or rerun with --force to rebuild it (your edits will be lost).");
            return 1;
        }

        string json;
        if (jsonPath is not null)
        {
            Console.WriteLine($"Reading FDA feed from {jsonPath}");
            json = await File.ReadAllTextAsync(jsonPath, ct);
        }
        else
        {
            Console.WriteLine($"Downloading FDA guidance feed: {FeedUrl}");
            try
            {
                json = System.Text.Encoding.UTF8.GetString(await Http.GetBytesWithRetryAsync(http, FeedUrl, ct));
            }
            catch (HttpRequestException ex)
            {
                Console.WriteLine($"Could not download the feed ({ex.Message}).");
                Console.WriteLine("Workaround: open the URL above in a browser, save it as data/fda-feed.json, then run:");
                Console.WriteLine("  dotnet run --project src/RegDocs.Ingest -- manifest --json data/fda-feed.json");
                return 1;
            }
        }

        var all = Parse(json);
        var devices = all.Where(c => c.Center.Contains(DeviceCenter, StringComparison.OrdinalIgnoreCase)).ToList();
        Console.WriteLine($"Feed: {all.Count} guidances with a PDF, {devices.Count} from CDRH");

        var pools = Buckets.ToDictionary(b => b.Name, _ => new List<Candidate>());
        foreach (var c in devices)
            pools[Buckets.First(b => b.Matches(c)).Name].Add(c);

        var selected = new List<ManifestRow>();
        Console.WriteLine();
        Console.WriteLine($"  {"bucket",-20} {"available",9} {"selected",9}");
        foreach (var b in Buckets)
        {
            var picks = pools[b.Name].OrderByDescending(c => c.IssueDate, StringComparer.Ordinal).Take(b.Cap).ToList();
            selected.AddRange(picks.Select(c => ToRow(c, b.Name)));
            Console.WriteLine($"  {b.Name,-20} {pools[b.Name].Count,9} {picks.Count,9}");
        }

        // Outliers: recent final drug (CDER) and food guidances. Questions about them should be answered
        // from those docs and never mixed up with device rules.
        if (outliers > 0)
        {
            var drugs = (outliers + 1) / 2;
            var picks = Newest(all, "Drug Evaluation", drugs)
                .Concat(Newest(all, "Human Foods", outliers - drugs)).ToList();
            selected.AddRange(picks.Select(c => ToRow(c, "outlier")));
            Console.WriteLine($"  {"outlier",-20} {"",9} {picks.Count,9}");
        }

        Csv.Write(outPath, ManifestRow.Header, selected.Select(r => r.ToFields()));
        Console.WriteLine();
        Console.WriteLine($"Wrote {selected.Count} rows to {outPath}");
        Console.WriteLine("Review it (set include=n to drop rows, or add your own), then run: download");
        return 0;
    }

    private static IEnumerable<Candidate> Newest(List<Candidate> all, string center, int count) =>
        all.Where(c => c.Center.Contains(center, StringComparison.OrdinalIgnoreCase)
                       && !c.Center.Contains(DeviceCenter, StringComparison.OrdinalIgnoreCase)
                       && c.Status.Equals("Final", StringComparison.OrdinalIgnoreCase))
           .OrderByDescending(c => c.IssueDate, StringComparer.Ordinal)
           .Take(count);

    private static ManifestRow ToRow(Candidate c, string bucket) => new()
    {
        DocId = "fda-" + c.MediaId, Bucket = bucket, Title = c.Title, IssueDate = c.IssueDate, Status = c.Status,
        Center = c.Center, Topics = c.Topics, DocType = c.DocType, Docket = c.Docket,
        PageUrl = c.PageUrl, PdfUrl = c.PdfUrl, Include = "y",
    };

    private static List<Candidate> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement
            : doc.RootElement.GetProperty("data");

        var result = new List<Candidate>();
        var seen = new HashSet<string>();

        foreach (var e in root.EnumerateArray())
        {
            string S(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

            var docType = Text(S("field_communication_type"));
            if (docType is not ("Guidance Document" or "Special Controls Document")) continue;

            var mediaHref = Href(S("field_associated_media_2"));
            var mediaId = Regex.Match(mediaHref, @"/media/(\d+)/download").Groups[1].Value;
            if (mediaId.Length == 0 || !seen.Add(mediaId)) continue; // no PDF, or a duplicate entry

            var titleHtml = S("title");
            result.Add(new Candidate(
                MediaId: mediaId,
                Title: Text(titleHtml),
                IssueDate: IsoDate(S("field_issue_datetime")),
                Status: Text(S("field_final_guidance_1")),
                Center: Text(S("field_center")),
                Topics: Text(S("topics-product")),
                DocType: docType,
                Docket: Text(S("field_docket_number")),
                PageUrl: Absolute(Href(titleHtml)),
                PdfUrl: Absolute($"/media/{mediaId}/download")));
        }
        return result;
    }

    private static string Text(string html) =>
        Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", " ")), @"\s+", " ").Trim();

    private static string Href(string html) => Regex.Match(html, "href=\"([^\"]+)\"").Groups[1].Value;

    private static string Absolute(string url) =>
        string.IsNullOrEmpty(url) || url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? url : FdaBase + url;

    private static string IsoDate(string s) =>
        DateTime.TryParseExact(s.Trim(), "MM/dd/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
            ? dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : s.Trim();
}
