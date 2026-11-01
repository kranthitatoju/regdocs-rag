using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RegDocs.Ingest;

/// <summary>One line of data/chunks.{strategy}.jsonl. Carries everything retrieval and citations need,
/// so later steps never re-read the manifest.</summary>
public sealed record ChunkRecord(
    string ChunkId, string DocId, string Strategy, int Ordinal, string Section,
    int PageStart, int PageEnd, int Chars, int TokensEst,
    string Title, string IssueDate, string Status, string Bucket, string Docket,
    string SourceUrl, string PdfUrl, string Text);

public sealed record ChunkOptions(int TargetTokens = 800, double Overlap = 0.15)
{
    // Rough rule of thumb for English prose with common tokenizers. Good enough for sizing;
    // the embedding step can measure real token counts later.
    public const int CharsPerToken = 4;

    public int TargetChars => TargetTokens * CharsPerToken;
    public int MaxChars => (int)(TargetChars * 1.25);          // hard ceiling while looking for a sentence end
    public int OverlapChars => (int)(TargetChars * Overlap);
    public int MinSectionChars => 300;                          // smaller sections merge into the next one
}

/// <summary>A cleaned line of text and the PDF page it came from (kept so chunks can cite page ranges).</summary>
internal readonly record struct Line(int Page, string Text);

/// <summary>
/// `chunk` command: cleans page text, then writes two chunk sets from the same cleaned lines
/// so Week 3 evals can compare them:
///   fixed   ~TargetTokens per chunk with overlap, ending at a sentence boundary when possible
///   heading one chunk per document section (I. → A. → 1. → (1)), long sections split like `fixed`
/// </summary>
public static class Chunker
{
    public static int Run(IngestPaths paths, ChunkOptions options)
    {
        if (!File.Exists(paths.Pages))
        {
            Console.WriteLine($"No {paths.Pages}. Run 'extract' first.");
            return 1;
        }

        var docs = LoadPages(paths.Pages);
        Console.WriteLine($"Chunking {docs.Count} documents (target {options.TargetTokens} tokens ≈ {options.TargetChars} chars, " +
                          $"overlap {options.Overlap:P0})");

        var cleanStats = new Cleaner.Stats();
        var fixedChunks = new List<ChunkRecord>();
        var headingChunks = new List<ChunkRecord>();
        var headingsPerDoc = new List<int>();
        var docsWithoutHeadings = new List<string>();

        foreach (var pages in docs)
        {
            var meta = pages[0];
            var lines = Cleaner.Clean(pages, cleanStats);
            if (lines.Count == 0) continue;

            // Strategy 1: fixed-size windows over the whole document.
            var ordinal = 0;
            foreach (var window in Splitter.Fixed(lines, options))
                fixedChunks.Add(ToChunk(meta, "fixed", ordinal++, "", window));

            // Strategy 2: section-aware.
            var sections = Sections.Build(lines, out var headingCount);
            headingsPerDoc.Add(headingCount);
            if (headingCount == 0) docsWithoutHeadings.Add(meta.DocId);

            ordinal = 0;
            foreach (var section in Sections.MergeSmall(sections, options.MinSectionChars))
            {
                var size = section.Lines.Sum(l => l.Text.Length + 1);
                IEnumerable<List<Line>> parts = size <= options.MaxChars
                    ? new List<List<Line>> { section.Lines }
                    : Splitter.Fixed(section.Lines, options);
                foreach (var part in parts)
                    headingChunks.Add(ToChunk(meta, "heading", ordinal++, section.Path, part));
            }
        }

        Write(Path.Combine(paths.DataDir, "chunks.fixed.jsonl"), fixedChunks);
        Write(Path.Combine(paths.DataDir, "chunks.heading.jsonl"), headingChunks);

        var stats = new
        {
            GeneratedAt = DateTimeOffset.Now,
            Options = new { options.TargetTokens, options.Overlap, options.TargetChars, options.MaxChars, options.OverlapChars, options.MinSectionChars, ChunkOptions.CharsPerToken },
            Cleaning = cleanStats,
            Fixed = Describe(fixedChunks),
            Heading = Describe(headingChunks),
            HeadingDetection = new
            {
                MedianHeadingsPerDoc = Median(headingsPerDoc),
                DocsWithoutHeadings = docsWithoutHeadings,
            },
        };
        var statsPath = Path.Combine(paths.DataDir, "chunk-stats.json");
        File.WriteAllText(statsPath, JsonSerializer.Serialize(stats, new JsonSerializerOptions(Extractor.JsonOpts) { WriteIndented = true }));

        Console.WriteLine();
        Console.WriteLine($"Cleaning: dropped {cleanStats.RepeatedLinesDropped} repeated header/footer lines, {cleanStats.PageNumberLinesDropped} page-number lines, " +
                          $"{cleanStats.TocPagesDropped} table-of-contents pages; stripped margin line numbers in {cleanStats.LineNumberedDocs} docs");
        Console.WriteLine();
        Console.WriteLine($"  {"strategy",-9} {"chunks",7} {"p50 tok",8} {"p90 tok",8} {"max tok",8} {"<100 tok",9} {"per doc",8}");
        Print("fixed", stats.Fixed);
        Print("heading", stats.Heading);
        Console.WriteLine();
        Console.WriteLine($"Headings: median {stats.HeadingDetection.MedianHeadingsPerDoc} per doc; {docsWithoutHeadings.Count} docs with none (those fall back to fixed-size windows)");
        Console.WriteLine($"Stats: {statsPath}");
        return 0;
    }

    private static ChunkRecord ToChunk(PageRecord meta, string strategy, int ordinal, string section, IReadOnlyList<Line> lines)
    {
        var text = string.Join("\n", lines.Select(l => l.Text));
        return new ChunkRecord(
            ChunkId: $"{meta.DocId}:{strategy}:{ordinal:D4}",
            DocId: meta.DocId, Strategy: strategy, Ordinal: ordinal,
            Section: section.Length > 0 || strategy == "fixed" ? section : "(front matter)",
            PageStart: lines.Min(l => l.Page), PageEnd: lines.Max(l => l.Page),
            Chars: text.Length, TokensEst: (int)Math.Ceiling(text.Length / (double)ChunkOptions.CharsPerToken),
            Title: meta.Title, IssueDate: meta.IssueDate, Status: meta.Status, Bucket: meta.Bucket, Docket: meta.Docket,
            SourceUrl: meta.SourceUrl, PdfUrl: meta.PdfUrl, Text: text);
    }

    /// <summary>Reads pages.jsonl and groups pages by document, in file order.</summary>
    private static List<List<PageRecord>> LoadPages(string path)
    {
        var byDoc = new Dictionary<string, List<PageRecord>>();
        var order = new List<string>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var page = JsonSerializer.Deserialize<PageRecord>(line, Extractor.JsonOpts)!;
            if (!byDoc.TryGetValue(page.DocId, out var list))
            {
                byDoc[page.DocId] = list = [];
                order.Add(page.DocId);
            }
            list.Add(page);
        }
        return order.Select(id => byDoc[id].OrderBy(p => p.Page).ToList()).ToList();
    }

    private static void Write(string path, List<ChunkRecord> chunks)
    {
        var tmp = path + ".part";
        using (var w = new StreamWriter(tmp, false, new UTF8Encoding(false)))
            foreach (var c in chunks) w.WriteLine(JsonSerializer.Serialize(c, Extractor.JsonOpts));
        File.Move(tmp, path, overwrite: true);
    }

    private sealed record Distribution(int Chunks, int Docs, double ChunksPerDoc, int P50Tokens, int P90Tokens, int MaxTokens, int MeanTokens, int Under100Tokens);

    private static Distribution Describe(List<ChunkRecord> chunks)
    {
        if (chunks.Count == 0) return new Distribution(0, 0, 0, 0, 0, 0, 0, 0);
        var tokens = chunks.Select(c => c.TokensEst).OrderBy(t => t).ToList();
        var docs = chunks.Select(c => c.DocId).Distinct().Count();
        return new Distribution(
            chunks.Count, docs, Math.Round(chunks.Count / (double)docs, 1),
            Percentile(tokens, 0.5), Percentile(tokens, 0.9), tokens[^1], (int)tokens.Average(),
            tokens.Count(t => t < 100));
    }

    private static void Print(string name, Distribution d) =>
        Console.WriteLine($"  {name,-9} {d.Chunks,7} {d.P50Tokens,8} {d.P90Tokens,8} {d.MaxTokens,8} {d.Under100Tokens,9} {d.ChunksPerDoc,8}");

    private static int Percentile(List<int> sorted, double p) => sorted[(int)Math.Min(sorted.Count - 1, Math.Floor(p * sorted.Count))];

    private static double Median(List<int> values) =>
        values.Count == 0 ? 0 : values.OrderBy(v => v).ElementAt(values.Count / 2);
}

/// <summary>Removes text that would repeat in every chunk or confuse heading detection.</summary>
internal static class Cleaner
{
    public sealed class Stats
    {
        public int RepeatedLinesDropped { get; set; }
        public int PageNumberLinesDropped { get; set; }
        public int TocPagesDropped { get; set; }
        public int LineNumberedDocs { get; set; }
    }

    // FDA's standard running headers. Dropped even in short docs where the "repeats on most pages" rule can't kick in.
    private static readonly HashSet<string> AlwaysDrop = new(StringComparer.OrdinalIgnoreCase)
    {
        "Contains Nonbinding Recommendations", "Contain Nonbinding Recommendations",
        "Draft – Not for Implementation", "Draft - Not for Implementation",
    };

    private static readonly Regex Digits = new(@"\d+", RegexOptions.Compiled);
    private static readonly Regex TrailingLineNumber = new(@"\s+\d{1,4}$", RegexOptions.Compiled);
    private static readonly Regex PageNumber = new(@"^(page\s+)?\d{1,4}(\s+of\s+\d{1,4})?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DotLeader = new(@"(?:\.\s?){5,}\s*\d*\s*$", RegexOptions.Compiled); // "Introduction ........ 3"

    public static List<Line> Clean(IReadOnlyList<PageRecord> pages, Stats stats)
    {
        // Sanitize here as well as in `extract`, so pages.jsonl files written before the sanitizer existed
        // are cleaned without re-extracting.
        var pageLines = pages.Select(p => TextSanitizer.Clean(p.Text).Split('\n').Select(l => l.Trim()).ToList()).ToList();

        // 1. Draft guidances number every line in the margin ("...for comment purposes 7"). Detect it per
        //    document (most lines end in a number) and strip the last number from each line.
        var nonEmpty = pageLines.SelectMany(l => l).Where(l => l.Length > 0).ToList();
        var numbered = nonEmpty.Count(l => TrailingLineNumber.IsMatch(l) || PageNumber.IsMatch(l));
        if (nonEmpty.Count > 0 && numbered > nonEmpty.Count / 2)
        {
            stats.LineNumberedDocs++;
            pageLines = pageLines.Select(lines => lines.Select(l => TrailingLineNumber.Replace(l, "")).ToList()).ToList();
        }

        // 2. Running headers/footers: a line (digits ignored, so "Page 3" == "Page 4") that appears on
        //    more than half the pages of a document of 3+ pages.
        var pagesWithLine = new Dictionary<string, int>();
        foreach (var lines in pageLines)
            foreach (var key in lines.Where(l => l.Length > 0).Select(Key).Distinct())
                pagesWithLine[key] = pagesWithLine.GetValueOrDefault(key) + 1;
        bool IsRunningHeader(string line) =>
            AlwaysDrop.Contains(line) || (pages.Count >= 3 && pagesWithLine.GetValueOrDefault(Key(line)) > pages.Count / 2);

        var result = new List<Line>();
        for (var i = 0; i < pages.Count; i++)
        {
            var lines = pageLines[i];

            // 3. Table-of-contents pages: their entries look exactly like headings, and they carry no answers.
            if (lines.Count(l => DotLeader.IsMatch(l)) >= 3)
            {
                stats.TocPagesDropped++;
                continue;
            }

            foreach (var line in lines)
            {
                if (line.Length == 0) continue;
                if (PageNumber.IsMatch(line)) { stats.PageNumberLinesDropped++; continue; }
                if (IsRunningHeader(line)) { stats.RepeatedLinesDropped++; continue; }
                if (DotLeader.IsMatch(line)) continue; // stray TOC line on a mixed page
                result.Add(new Line(pages[i].Page, line));
            }
        }
        return result;
    }

    private static string Key(string line) => Digits.Replace(line, "#");
}

/// <summary>Fixed-size windows over a list of lines.</summary>
internal static class Splitter
{
    private static readonly Regex SentenceEnd = new(@"[.?!:;)”""]$", RegexOptions.Compiled);

    /// <summary>
    /// Adds whole lines until the window reaches the target size, then closes it at the next line that
    /// ends a sentence (or at the hard maximum). The next window starts with the last ~OverlapChars of the
    /// previous one, so a fact cut at a boundary still appears whole in one of the two chunks.
    /// </summary>
    public static IEnumerable<List<Line>> Fixed(List<Line> lines, ChunkOptions o)
    {
        var current = new List<Line>();
        var size = 0;
        var fresh = 0; // lines added since the last split (so we never emit a chunk that is only overlap)

        for (var i = 0; i < lines.Count; i++)
        {
            current.Add(lines[i]);
            size += lines[i].Text.Length + 1;
            fresh++;

            var isLast = i == lines.Count - 1;
            var closeHere = size >= o.MaxChars || (size >= o.TargetChars && SentenceEnd.IsMatch(lines[i].Text));
            if (isLast || !closeHere) continue;

            yield return current;

            var carry = new List<Line>();
            var carried = 0;
            for (var j = current.Count - 1; j > 0 && carried < o.OverlapChars; j--)
            {
                carry.Insert(0, current[j]);
                carried += current[j].Text.Length + 1;
            }
            current = carry;
            size = carried;
            fresh = 0;
        }

        if (fresh > 0) yield return current;
    }
}

/// <summary>Splits a document at its numbered headings and tracks the heading path (e.g. "IV. … > B. …").</summary>
internal static class Sections
{
    public sealed record Section(string Path, List<Line> Lines);

    // FDA guidance outline: I. → A. → 1. → (1); some docs use 5.1.2-style numbering instead.
    private static readonly Regex Roman = new(@"^(?<n>[IVX]{1,5})\.\s+(?<t>[A-Z(].*)$", RegexOptions.Compiled);
    private static readonly Regex Letter = new(@"^(?<n>[A-Z])\.\s+(?<t>[A-Z(].*)$", RegexOptions.Compiled);
    private static readonly Regex Decimal = new(@"^(?<n>\d{1,2}\.(?:\d{1,2}\.?){0,3})\s+(?<t>[A-Z].*)$", RegexOptions.Compiled);
    private static readonly Regex Paren = new(@"^\((?<n>\d{1,2})\)\s+(?<t>[A-Z].*)$", RegexOptions.Compiled);

    /// <summary>Where we are in the outline, used to tell Roman numerals from letters (I, V, X).</summary>
    private sealed class Outline
    {
        public int LastRoman;          // value of the last Roman-numeral heading (0 = none yet)
        public char LastLetter = '@';  // last letter heading in the current Roman section ('@' = the letter before 'A')
    }

    public static List<Section> Build(List<Line> lines, out int headingCount)
    {
        var sections = new List<Section>();
        var path = new List<(int Level, string Text)>();
        var current = new List<Line>();
        var currentPath = "";
        var outline = new Outline();
        headingCount = 0;

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var next = i + 1 < lines.Count ? lines[i + 1].Text : "";
            var heading = Match(line.Text, next, outline);
            if (heading is { } h)
            {
                if (current.Count > 0) sections.Add(new Section(currentPath, current));
                current = [];
                path.RemoveAll(p => p.Level >= h.Level);
                path.Add(h);
                currentPath = string.Join(" > ", path.Select(p => p.Text));
                headingCount++;
            }
            current.Add(line); // the heading line stays in its own section's text
        }
        if (current.Count > 0) sections.Add(new Section(currentPath, current));
        return sections;
    }

    /// <summary>
    /// Sections under minChars would make near-empty chunks, so consecutive small sections are grouped until
    /// the group is big enough. The group is labelled with the section holding the most body text:
    ///   bare parent heading + first child ("IV. Recommendations", "A. Device Description …") → the child's path,
    ///     which already includes the parent;
    ///   several tiny siblings ("4.2 …", "6.1 …") → the biggest one. The other headings stay in the chunk text.
    /// </summary>
    public static List<Section> MergeSmall(List<Section> sections, int minChars)
    {
        var result = new List<Section>();
        var group = new List<Section>();
        foreach (var s in sections)
        {
            group.Add(s);
            if (group.Sum(Size) < minChars) continue;
            result.Add(Combine(group));
            group = [];
        }
        if (group.Count > 0)
        {
            // Leftover small sections at the end of the document join the last chunk, keeping its label.
            if (result.Count > 0) result[^1] = result[^1] with { Lines = [.. result[^1].Lines, .. group.SelectMany(g => g.Lines)] };
            else result.Add(Combine(group));
        }
        return result;
    }

    private static Section Combine(List<Section> group) =>
        group.Count == 1
            ? group[0]
            : new Section(group.MaxBy(BodyChars)!.Path, group.SelectMany(g => g.Lines).ToList());

    private static int Size(Section s) => s.Lines.Sum(l => l.Text.Length + 1);

    // A section's first line is its heading (except front matter, which has no path).
    private static int BodyChars(Section s) => s.Lines.Skip(s.Path.Length > 0 ? 1 : 0).Sum(l => l.Text.Length + 1);

    private static (int Level, string Text)? Match(string line, string next, Outline o)
    {
        var m = Roman.Match(line);
        if (m.Success && LooksLikeTitle(m.Groups["t"].Value))
        {
            var n = m.Groups["n"].Value;
            var value = RomanValue(n);
            var nextRoman = value > o.LastRoman && value <= o.LastRoman + 2; // allow one missed heading

            // I, V and X are also letters. Treat them as a letter when they continue the letter sequence
            // ("I." after "H.") or when they can't be the next Roman numeral ("I." inside section III,
            // e.g. because "G." and "H." weren't detected).
            if (n.Length == 1 && (o.LastLetter == (char)(n[0] - 1) || !nextRoman))
            {
                o.LastLetter = n[0];
                return (2, line);
            }
            o.LastRoman = value; // multi-letter numerals (II, IV) are always Roman; appendices may restart them
            o.LastLetter = '@';
            return (1, line);
        }

        m = Letter.Match(line);
        if (m.Success && LooksLikeTitle(m.Groups["t"].Value))
        {
            o.LastLetter = m.Groups["n"].Value[0];
            return (2, line);
        }

        // Numbered list items often wrap: "1. A complete description of every module and component of the"
        // + "system, including…". A real heading is followed by a new paragraph, so a lowercase next line
        // means this is a wrapped sentence. (Not applied to I./A. headings, whose long titles do wrap.)
        var wrapped = next.Length > 0 && char.IsLower(next[0]);

        m = Decimal.Match(line);
        if (m.Success && !wrapped && LooksLikeTitle(m.Groups["t"].Value))
        {
            var depth = m.Groups["n"].Value.Split('.', StringSplitOptions.RemoveEmptyEntries).Length;
            return (2 + depth, line); // "1." → 3, "1.2" → 4 ...
        }

        m = Paren.Match(line);
        if (m.Success && !wrapped && LooksLikeTitle(m.Groups["t"].Value))
            return (7, line);

        return null;
    }

    private static int RomanValue(string numeral)
    {
        static int V(char c) => c switch { 'I' => 1, 'V' => 5, 'X' => 10, _ => 0 };
        var total = 0;
        for (var i = 0; i < numeral.Length; i++)
            total += i + 1 < numeral.Length && V(numeral[i + 1]) > V(numeral[i]) ? -V(numeral[i]) : V(numeral[i]);
        return total;
    }

    /// <summary>Headings are short and don't read like sentences. Filters out numbered list items and footnotes.</summary>
    private static bool LooksLikeTitle(string t)
    {
        var words = t.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        if (t.Length is < 2 or > 100 || words > 14) return false;
        if (t.EndsWith(',') || t.Contains(';')) return false; // "2. Electrical testing; and" is a list item
        if (t.EndsWith('.') && words > 6) return false;
        return true;
    }
}
