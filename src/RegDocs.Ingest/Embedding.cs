using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Npgsql;
using OllamaSharp;
using Pgvector;
using Pgvector.Npgsql;

namespace RegDocs.Ingest;

/// <summary>Settings shared by `embed` and `search`. Every value can come from a flag or an environment
/// variable, so moving Ollama to another machine is one flag: --ollama http://192.168.1.50:11434</summary>
public sealed record EmbedOptions(
    string OllamaUrl, string Model, string ConnectionString, string Strategy, int BatchSize, int? Limit, int Dimensions)
{
    public const string DefaultDb = "Host=localhost;Port=5432;Database=regdocs;Username=regdocs;Password=regdocs";

    // nomic-embed-text was trained with task prefixes: stored passages and questions are marked differently,
    // so a question lands near the passage that answers it rather than near other questions.
    private bool IsNomic => Model.StartsWith("nomic-embed-text", StringComparison.OrdinalIgnoreCase);
    public string DocumentPrefix => IsNomic ? "search_document: " : "";
    public string QueryPrefix => IsNomic ? "search_query: " : "";

    public string[] Strategies => Strategy == "all" ? new[] { "fixed", "heading" } : new[] { Strategy };

    public static EmbedOptions From(Func<string, string?> opt) => new(
        OllamaUrl: opt("ollama") ?? Environment.GetEnvironmentVariable("OLLAMA_URL") ?? "http://localhost:11434",
        Model: opt("model") ?? Environment.GetEnvironmentVariable("EMBED_MODEL") ?? "nomic-embed-text",
        ConnectionString: opt("db") ?? Environment.GetEnvironmentVariable("REGDOCS_DB") ?? DefaultDb,
        Strategy: (opt("strategy") ?? "all").ToLowerInvariant(),
        BatchSize: int.TryParse(opt("batch"), out var b) && b > 0 ? b : 32,
        Limit: int.TryParse(opt("limit"), out var l) && l > 0 ? l : null,
        Dimensions: int.TryParse(opt("dims"), out var d) && d > 0 ? d : 768);

    public IEmbeddingGenerator<string, Embedding<float>> CreateGenerator() =>
        new OllamaApiClient(new Uri(OllamaUrl), Model); // OllamaSharp implements the Microsoft.Extensions.AI interface

    public EmbeddingGenerationOptions GenerationOptions => new() { ModelId = Model };
}

/// <summary>
/// `embed` command: reads data/chunks.{strategy}.jsonl, embeds each chunk through IEmbeddingGenerator
/// (Ollama) and upserts it into Postgres. Re-runs only embed chunks whose text or model changed.
/// </summary>
public static class Embedder
{
    public static async Task<int> RunAsync(IngestPaths paths, EmbedOptions o, CancellationToken ct)
    {
        Console.WriteLine($"Model {o.Model} at {o.OllamaUrl}, strategy {o.Strategy}, batch {o.BatchSize}" +
                          (o.Limit is int lim ? $", limit {lim} per strategy" : ""));

        using var generator = o.CreateGenerator();
        if (!await Checks.OllamaAsync(generator, o, ct)) return 1;
        if (!await Checks.DatabaseAsync(o.ConnectionString, ct)) return 1;
        await using var db = Db.Open(o.ConnectionString);

        var total = Stopwatch.StartNew();
        var embeddedTotal = 0;

        foreach (var strategy in o.Strategies)
        {
            var file = Path.Combine(paths.DataDir, $"chunks.{strategy}.jsonl");
            if (!File.Exists(file))
            {
                Console.WriteLine($"{strategy}: no {file}. Run 'chunk' first.");
                continue;
            }

            var chunks = File.ReadLines(file)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => JsonSerializer.Deserialize<ChunkRecord>(l, Extractor.JsonOpts)!)
                // Same cleaning `chunk` does; protects against chunk files written before the sanitizer existed
                // (Postgres rejects U+0000). Clean text is unchanged by it, so hashes stay stable.
                .Select(c => c with { Text = TextSanitizer.Clean(c.Text) })
                .ToList();

            // Re-chunking changes chunk ids and counts, so rows that are no longer in the file are deleted.
            var removed = await Db.DeleteMissingAsync(db, strategy, chunks.Select(c => c.ChunkId).ToArray(), ct);
            var existing = await Db.LoadEmbeddedAsync(db, strategy, ct);

            var todo = new List<ChunkRecord>();
            var current = new List<ChunkRecord>();
            foreach (var c in chunks)
            {
                var upToDate = existing.TryGetValue(c.ChunkId, out var e) && e.Hash == Hash(o.DocumentPrefix + c.Text) && e.Model == o.Model;
                (upToDate ? current : todo).Add(c);
            }
            if (o.Limit is int limit) todo = todo.Take(limit).ToList();

            Console.WriteLine();
            Console.WriteLine($"{strategy}: {chunks.Count} chunks, {current.Count} already embedded, {todo.Count} to embed, {removed} stale rows removed");

            // Unchanged text keeps its vector, but labels such as `section` may still have changed.
            await Db.RefreshMetadataAsync(db, current, ct);

            var sw = Stopwatch.StartNew();
            var done = 0;
            foreach (var batch in todo.Chunk(o.BatchSize))
            {
                ct.ThrowIfCancellationRequested();
                var inputs = batch.Select(c => o.DocumentPrefix + c.Text).ToList();
                var vectors = await generator.GenerateAsync(inputs, o.GenerationOptions, ct);
                if (vectors.Count != batch.Length)
                    throw new InvalidOperationException($"Asked for {batch.Length} embeddings, got {vectors.Count}");

                await Db.UpsertAsync(db, batch, vectors, o, ct);

                done += batch.Length;
                var perSecond = done / Math.Max(sw.Elapsed.TotalSeconds, 0.001);
                var eta = TimeSpan.FromSeconds((todo.Count - done) / Math.Max(perSecond, 0.001));
                Console.Write($"\r  {done}/{todo.Count}  {perSecond:F1} chunks/s  eta {eta:mm\\:ss}   ");
            }
            if (todo.Count > 0)
                Console.WriteLine($"\r  {done}/{todo.Count} embedded in {sw.Elapsed:mm\\:ss} ({done / Math.Max(sw.Elapsed.TotalSeconds, 0.001):F1} chunks/s)   ");
            embeddedTotal += done;
        }

        Console.WriteLine();
        foreach (var (strategy, rows, embedded) in await Db.CountsAsync(db, ct))
            Console.WriteLine($"  {strategy,-8} {rows,6} rows  {embedded,6} with embeddings");
        Console.WriteLine($"Embedded {embeddedTotal} chunks in {total.Elapsed:mm\\:ss}. Try: search \"what should a threat model include?\"");
        return 0;
    }

    internal static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
}

/// <summary>`search` command: embeds a question and prints the nearest chunks for each strategy.</summary>
public static class Searcher
{
    public static async Task<int> RunAsync(string question, EmbedOptions o, int k, CancellationToken ct)
    {
        using var generator = o.CreateGenerator();
        if (!await Checks.DatabaseAsync(o.ConnectionString, ct)) return 1;
        await using var db = Db.Open(o.ConnectionString);

        var sw = Stopwatch.StartNew();
        Embedding<float> q;
        try
        {
            q = (await generator.GenerateAsync([o.QueryPrefix + question], o.GenerationOptions, ct))[0];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine($"Could not embed the question with {o.Model} at {o.OllamaUrl}: {ex.Message}");
            Console.WriteLine($"Is the Ollama app running, and has the model been pulled? ollama pull {o.Model}");
            return 1;
        }
        var embedMs = sw.ElapsedMilliseconds;
        var queryVector = new Vector(q.Vector.ToArray());

        Console.WriteLine($"Q: {question}   (embedded in {embedMs} ms)");
        foreach (var strategy in o.Strategies)
        {
            sw.Restart();
            var hits = await Db.NearestAsync(db, queryVector, strategy, o.Model, k, ct);
            Console.WriteLine();
            Console.WriteLine($"── {strategy}  ({sw.ElapsedMilliseconds} ms)");
            if (hits.Count == 0) Console.WriteLine($"  no rows embedded with {o.Model}. Run 'embed' first.");

            var rank = 1;
            foreach (var h in hits)
            {
                Console.WriteLine($"{rank++,2}. {h.Score:F3}  {Short(h.Title, 80)}  p{h.PageStart}{(h.PageEnd != h.PageStart ? "-" + h.PageEnd : "")}");
                if (h.Section.Length > 0) Console.WriteLine($"    § {Short(h.Section, 100)}");
                Console.WriteLine($"    {Short(h.Text.Replace('\n', ' '), 160)}");
            }
        }
        return 0;
    }

    private static string Short(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}

/// <summary>Fail early with a message that says what to do, instead of a stack trace.</summary>
internal static class Checks
{
    public static async Task<bool> OllamaAsync(IEmbeddingGenerator<string, Embedding<float>> generator, EmbedOptions o, CancellationToken ct)
    {
        try
        {
            var probe = await generator.GenerateAsync([o.DocumentPrefix + "connection test"], o.GenerationOptions, ct);
            var dims = probe[0].Vector.Length;
            if (dims != o.Dimensions)
            {
                Console.WriteLine($"{o.Model} returns {dims} numbers per vector, but the table expects {o.Dimensions}. " +
                                  "Use --dims and change vector(768) in db/init/001_schema.sql, or use a 768-dimension model.");
                return false;
            }
            Console.WriteLine($"Ollama OK: {o.Model} returns {dims}-dimension vectors");
            return true;
        }
        catch (HttpRequestException ex)
        {
            Console.WriteLine($"Could not reach Ollama at {o.OllamaUrl} ({ex.Message}). Is the Ollama app running?");
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Ollama is up but refused the request, most often because the model hasn't been pulled.
            Console.WriteLine($"Ollama error for model '{o.Model}': {ex.Message}");
            Console.WriteLine($"Has the model been pulled? ollama pull {o.Model}");
            return false;
        }
    }

    public static async Task<bool> DatabaseAsync(string connectionString, CancellationToken ct)
    {
        try
        {
            await Db.EnsureSchemaAsync(connectionString, ct);
            return true;
        }
        catch (Exception ex) when (ex is NpgsqlException or SocketException)
        {
            Console.WriteLine($"Could not connect to Postgres ({ex.Message}).");
            Console.WriteLine("Start it from the repo root with: docker compose up -d");
            return false;
        }
    }
}

internal static class Db
{
    public sealed record Hit(string ChunkId, string Title, string Section, int PageStart, int PageEnd, string Text, double Score);

    public static NpgsqlDataSource Open(string connectionString)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        builder.UseVector(); // teaches Npgsql the pgvector `vector` type
        return builder.Build();
    }

    /// <summary>Applies db/init/001_schema.sql (idempotent), so schema changes reach an existing database too.
    /// Uses a plain connection: the vector type only exists once the extension has been created.</summary>
    public static async Task EnsureSchemaAsync(string connectionString, CancellationToken ct)
    {
        var file = Path.Combine(IngestPaths.RepoRoot(), "db", "init", "001_schema.sql");
        await using var plain = NpgsqlDataSource.Create(connectionString);
        if (File.Exists(file))
        {
            await using var cmd = plain.CreateCommand(await File.ReadAllTextAsync(file, ct));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        else
        {
            await using var cmd = plain.CreateCommand("SELECT 1");
            await cmd.ExecuteScalarAsync(ct);
        }
    }

    public static async Task<Dictionary<string, (string Hash, string Model)>> LoadEmbeddedAsync(NpgsqlDataSource db, string strategy, CancellationToken ct)
    {
        var result = new Dictionary<string, (string, string)>();
        await using var cmd = db.CreateCommand(
            "SELECT chunk_id, content_hash, embedding_model FROM chunks WHERE strategy = @s AND embedding IS NOT NULL");
        cmd.Parameters.AddWithValue("s", strategy);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            result[r.GetString(0)] = (r.GetString(1), r.IsDBNull(2) ? "" : r.GetString(2));
        return result;
    }

    public static async Task<int> DeleteMissingAsync(NpgsqlDataSource db, string strategy, string[] keepIds, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("DELETE FROM chunks WHERE strategy = @s AND NOT (chunk_id = ANY(@ids))");
        cmd.Parameters.AddWithValue("s", strategy);
        cmd.Parameters.AddWithValue("ids", keepIds);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private const string UpsertSql = """
        INSERT INTO chunks (chunk_id, strategy, doc_id, ordinal, section, page_start, page_end, tokens_est,
                            title, issue_date, status, bucket, docket, source_url, pdf_url, text,
                            content_hash, embedding, embedding_model, embedded_at)
        VALUES (@chunk_id, @strategy, @doc_id, @ordinal, @section, @page_start, @page_end, @tokens_est,
                @title, @issue_date, @status, @bucket, @docket, @source_url, @pdf_url, @text,
                @content_hash, @embedding, @model, now())
        ON CONFLICT (chunk_id) DO UPDATE SET
            strategy = EXCLUDED.strategy, doc_id = EXCLUDED.doc_id, ordinal = EXCLUDED.ordinal,
            section = EXCLUDED.section, page_start = EXCLUDED.page_start, page_end = EXCLUDED.page_end,
            tokens_est = EXCLUDED.tokens_est, title = EXCLUDED.title, issue_date = EXCLUDED.issue_date,
            status = EXCLUDED.status, bucket = EXCLUDED.bucket, docket = EXCLUDED.docket,
            source_url = EXCLUDED.source_url, pdf_url = EXCLUDED.pdf_url, text = EXCLUDED.text,
            content_hash = EXCLUDED.content_hash, embedding = EXCLUDED.embedding,
            embedding_model = EXCLUDED.embedding_model, embedded_at = EXCLUDED.embedded_at
        """;

    private const string MetadataSql = """
        UPDATE chunks SET doc_id = @doc_id, ordinal = @ordinal, section = @section,
            page_start = @page_start, page_end = @page_end, tokens_est = @tokens_est, title = @title,
            issue_date = @issue_date, status = @status, bucket = @bucket, docket = @docket,
            source_url = @source_url, pdf_url = @pdf_url
        WHERE chunk_id = @chunk_id
        """;

    /// <summary>One transaction per batch: a crash mid-run loses at most one batch, and the next run resumes.</summary>
    public static async Task UpsertAsync(NpgsqlDataSource db, ChunkRecord[] batch, GeneratedEmbeddings<Embedding<float>> vectors, EmbedOptions o, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        for (var i = 0; i < batch.Length; i++)
        {
            var c = batch[i];
            await using var cmd = new NpgsqlCommand(UpsertSql, conn, tx);
            AddMetadata(cmd, c);
            cmd.Parameters.AddWithValue("strategy", c.Strategy);
            cmd.Parameters.AddWithValue("text", c.Text);
            cmd.Parameters.AddWithValue("content_hash", Embedder.Hash(o.DocumentPrefix + c.Text));
            cmd.Parameters.AddWithValue("embedding", new Vector(vectors[i].Vector.ToArray()));
            cmd.Parameters.AddWithValue("model", o.Model);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public static async Task RefreshMetadataAsync(NpgsqlDataSource db, List<ChunkRecord> chunks, CancellationToken ct)
    {
        foreach (var batch in chunks.Chunk(500))
        {
            await using var conn = await db.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            foreach (var c in batch)
            {
                await using var cmd = new NpgsqlCommand(MetadataSql, conn, tx);
                AddMetadata(cmd, c);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
    }

    private static void AddMetadata(NpgsqlCommand cmd, ChunkRecord c)
    {
        cmd.Parameters.AddWithValue("chunk_id", c.ChunkId);
        cmd.Parameters.AddWithValue("doc_id", c.DocId);
        cmd.Parameters.AddWithValue("ordinal", c.Ordinal);
        cmd.Parameters.AddWithValue("section", c.Section);
        cmd.Parameters.AddWithValue("page_start", c.PageStart);
        cmd.Parameters.AddWithValue("page_end", c.PageEnd);
        cmd.Parameters.AddWithValue("tokens_est", c.TokensEst);
        cmd.Parameters.AddWithValue("title", c.Title);
        cmd.Parameters.AddWithValue("issue_date", DateOnly.TryParse(c.IssueDate, out var d) ? d : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("status", c.Status);
        cmd.Parameters.AddWithValue("bucket", c.Bucket);
        cmd.Parameters.AddWithValue("docket", c.Docket);
        cmd.Parameters.AddWithValue("source_url", c.SourceUrl);
        cmd.Parameters.AddWithValue("pdf_url", c.PdfUrl);
    }

    /// <summary>`&lt;=&gt;` is pgvector's cosine distance (0 = same direction, 2 = opposite);
    /// 1 − distance turns it into cosine similarity, where higher means closer in meaning.</summary>
    public static async Task<List<Hit>> NearestAsync(NpgsqlDataSource db, Vector query, string strategy, string model, int k, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            SELECT chunk_id, title, section, page_start, page_end, text, 1 - (embedding <=> @q) AS score
            FROM chunks
            WHERE strategy = @s AND embedding_model = @m
            ORDER BY embedding <=> @q
            LIMIT @k
            """);
        cmd.Parameters.AddWithValue("q", query);
        cmd.Parameters.AddWithValue("s", strategy);
        cmd.Parameters.AddWithValue("m", model);
        cmd.Parameters.AddWithValue("k", k);

        var hits = new List<Hit>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            hits.Add(new Hit(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetInt32(4), r.GetString(5), r.GetDouble(6)));
        return hits;
    }

    public static async Task<List<(string Strategy, long Rows, long Embedded)>> CountsAsync(NpgsqlDataSource db, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(
            "SELECT strategy, count(*), count(embedding) FROM chunks GROUP BY strategy ORDER BY strategy");
        var result = new List<(string, long, long)>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) result.Add((r.GetString(0), r.GetInt64(1), r.GetInt64(2)));
        return result;
    }
}
