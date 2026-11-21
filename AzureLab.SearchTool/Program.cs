using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.Search.Documents.Indexes;
using Azure.Storage.Blobs;
using AzureLab.Data;
using AzureLab.Rag;
using AzureLab.Rag.Ingestion;
using AzureLab.Rag.Providers.AzureAISearch;
using AzureLab.Rag.Providers.DocumentIntelligence;
using AzureLab.SearchTool;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    PrintUsage();
    return 0;
}

var command = args[0].ToLowerInvariant();
var indexOverride = GetOption(args, "--index");
var dimsOverride = GetOption(args, "--dimensions");
var eventIdOverride = GetOption(args, "--event-id");

var baseConfig = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: true)
    .AddUserSecrets<Program>(optional: true)
    .AddEnvironmentVariables()
    .Build();

if (command == "compare-extract")
{
    var files = args.Skip(1).Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
    if (files.Length == 0)
    {
        Console.Error.WriteLine("Usage: compare-extract <file1> [file2 ...]");
        return 2;
    }

    var diEndpoint = baseConfig["Ai:DocumentIntelligence:Endpoint"];
    if (string.IsNullOrWhiteSpace(diEndpoint) || !Uri.TryCreate(diEndpoint, UriKind.Absolute, out _))
    {
        Console.Error.WriteLine("Missing or invalid Ai:DocumentIntelligence:Endpoint.");
        return 2;
    }

    var cred = new DefaultAzureCredential();
    using var http = new HttpClient();
    var localExtractors = new IDocumentTextExtractor[] { new PdfTextExtractor(), new DocxTextExtractor() };

    AzureDocumentIntelligenceExtractor MakeAzure(string modelId) =>
        new(http, cred, Options.Create(new AiOptions
        {
            DocumentIntelligence = new DocumentIntelligenceOptions
            {
                Provider = DocumentExtractorProvider.Azure,
                Endpoint = diEndpoint,
                ModelId = modelId,
                TimeoutSeconds = 120,
                PollIntervalMs = 1000
            }
        }), NullLogger<AzureDocumentIntelligenceExtractor>.Instance);

    var azRead = MakeAzure("prebuilt-read");
    var azLayout = MakeAzure("prebuilt-layout");

    Console.WriteLine("| File | Extractor | Pages | Chars | Latency (ms) | Est. Cost (USD) | Status |");
    Console.WriteLine("|---|---|---:|---:|---:|---:|---|");

    var snippets = new List<(string File, string LocalText, string LayoutText)>();

    foreach (var path in files)
    {
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"File not found: {path}");
            return 2;
        }

        var name = Path.GetFileName(path);
        var ext = Path.GetExtension(path);

        // 1) Local
        var local = localExtractors.FirstOrDefault(e => e.CanHandle(ext));
        string localText = "";
        if (local is null)
        {
            Console.WriteLine($"| `{name}` | Local (PdfPig/OpenXml) | 0 | 0 | 0 | $0.0000 | Skipped (unsupported `{ext}`) |");
        }
        else
        {
            var sw = Stopwatch.StartNew();
            try
            {
                await using var fs = File.OpenRead(path);
                var r = await local.ExtractTextAsync(fs, ext);
                sw.Stop();
                localText = r.Text.Trim();
                var status = localText.Length == 0 ? "Empty (no text layer)" : "OK";
                Console.WriteLine($"| `{name}` | Local (PdfPig/OpenXml) | {r.PageCount} | {localText.Length} | {sw.ElapsedMilliseconds} | $0.0000 | {status} |");
            }
            catch (Exception ex)
            {
                sw.Stop();
                Console.WriteLine($"| `{name}` | Local (PdfPig/OpenXml) | 0 | 0 | {sw.ElapsedMilliseconds} | $0.0000 | Error: {ex.GetType().Name} |");
            }
        }

        // 2) Azure prebuilt-read ($1.50 / 1K pages = $0.0015/page)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                await using var fs = File.OpenRead(path);
                var r = await azRead.ExtractTextAsync(fs, ext);
                sw.Stop();
                Console.WriteLine($"| `{name}` | Azure `prebuilt-read` | {r.PageCount} | {r.Text.Trim().Length} | {sw.ElapsedMilliseconds} | ${r.PageCount * 0.0015:F4} | OK |");
            }
            catch (Exception ex)
            {
                sw.Stop();
                Console.WriteLine($"| `{name}` | Azure `prebuilt-read` | 0 | 0 | {sw.ElapsedMilliseconds} | $0.0000 | Error: {ex.Message} |");
            }
        }

        // 3) Azure prebuilt-layout ($10.00 / 1K pages = $0.0100/page)
        string layoutText = "";
        {
            var sw = Stopwatch.StartNew();
            try
            {
                await using var fs = File.OpenRead(path);
                var r = await azLayout.ExtractTextAsync(fs, ext);
                sw.Stop();
                layoutText = r.Text.Trim();
                Console.WriteLine($"| `{name}` | Azure `prebuilt-layout` | {r.PageCount} | {layoutText.Length} | {sw.ElapsedMilliseconds} | ${r.PageCount * 0.0100:F4} | OK |");
            }
            catch (Exception ex)
            {
                sw.Stop();
                Console.WriteLine($"| `{name}` | Azure `prebuilt-layout` | 0 | 0 | {sw.ElapsedMilliseconds} | $0.0000 | Error: {ex.Message} |");
            }
        }

        snippets.Add((name, localText, layoutText));
    }

    Console.WriteLine();
    Console.WriteLine("=== Extraction Snippets (first 400 chars) ===");
    foreach (var (file, loc, lay) in snippets)
    {
        Console.WriteLine($"\n--- {file} [Local] ---");
        Console.WriteLine(loc.Length == 0 ? "(empty / skipped)" : loc[..Math.Min(400, loc.Length)]);
        Console.WriteLine($"--- {file} [Azure prebuilt-layout] ---");
        Console.WriteLine(lay.Length == 0 ? "(empty / error)" : lay[..Math.Min(400, lay.Length)]);
    }

    return 0;
}

var endpoint = baseConfig["Ai:VectorStore:AzureAISearch:Endpoint"];
if (string.IsNullOrWhiteSpace(endpoint) || !Uri.TryCreate(endpoint, UriKind.Absolute, out var searchUri))
{
    Console.Error.WriteLine("Missing or invalid Ai:VectorStore:AzureAISearch:Endpoint.");
    return 2;
}

var indexName = indexOverride ?? baseConfig["Ai:VectorStore:AzureAISearch:IndexName"];
var semanticConfig = baseConfig["Ai:VectorStore:AzureAISearch:SemanticConfigurationName"]
                     ?? SearchIndexSchemaBuilder.DefaultSemanticConfigName;
var dimensions = dimsOverride is not null
    ? int.Parse(dimsOverride)
    : baseConfig.GetValue("Ai:Embedding:Dimensions", 1536);

var credential = new DefaultAzureCredential();
var indexClient = new SearchIndexClient(searchUri, credential);

switch (command)
{
    case "ensure-index":
    {
        if (string.IsNullOrWhiteSpace(indexName))
        {
            Console.Error.WriteLine("Index name required via --index or Ai:VectorStore:AzureAISearch:IndexName.");
            return 2;
        }

        var schema = SearchIndexSchemaBuilder.BuildIndex(indexName, dimensions, semanticConfig);
        await indexClient.CreateOrUpdateIndexAsync(schema);
        Console.WriteLine($"Index '{indexName}' ensured (dimensions={dimensions}, semantic='{semanticConfig}').");
        return 0;
    }

    case "delete-index":
    {
        if (string.IsNullOrWhiteSpace(indexName))
        {
            Console.Error.WriteLine("Index name required via --index or Ai:VectorStore:AzureAISearch:IndexName.");
            return 2;
        }

        try
        {
            await indexClient.DeleteIndexAsync(indexName);
            Console.WriteLine($"Index '{indexName}' deleted.");
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            Console.WriteLine($"Index '{indexName}' did not exist (404).");
        }
        return 0;
    }

    case "alias":
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: alias <aliasName> <targetIndexName>");
            return 2;
        }

        var aliasName = args[1];
        var targetIndex = args[2];
        var token = await credential.GetTokenAsync(new TokenRequestContext(["https://search.azure.com/.default"]), CancellationToken.None);
        using var http = new HttpClient { BaseAddress = searchUri };
        using var req = new HttpRequestMessage(
            HttpMethod.Put,
            $"/aliases('{Uri.EscapeDataString(aliasName)}')?api-version=2026-04-01");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        req.Content = JsonContent.Create(new { name = aliasName, indexes = new[] { targetIndex } });

        using var resp = await http.SendAsync(req);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync();
            Console.Error.WriteLine($"Alias update failed ({(int)resp.StatusCode}): {body}");
            return 1;
        }

        Console.WriteLine($"Alias '{aliasName}' -> '{targetIndex}' updated.");
        return 0;
    }

    case "reindex":
    {
        if (string.IsNullOrWhiteSpace(indexName))
        {
            Console.Error.WriteLine("Index name required via --index or Ai:VectorStore:AzureAISearch:IndexName.");
            return 2;
        }

        Guid? filterEventId = null;
        if (eventIdOverride is not null)
        {
            if (!Guid.TryParse(eventIdOverride, out var parsed))
            {
                Console.Error.WriteLine($"Invalid --event-id GUID: '{eventIdOverride}'.");
                return 2;
            }
            filterEventId = parsed;
        }

        // 1) Ensure target index exists (idempotent).
        var schema = SearchIndexSchemaBuilder.BuildIndex(indexName, dimensions, semanticConfig);
        await indexClient.CreateOrUpdateIndexAsync(schema);
        Console.WriteLine($"Ensured index '{indexName}' (dimensions={dimensions}).");

        // 2) Build full host reusing DocumentIngestionService + AzureAISearch vector store.
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddUserSecrets<Program>(optional: true);
        if (indexOverride is not null)
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ai:VectorStore:AzureAISearch:IndexName"] = indexOverride
            });
        }

        builder.Logging.SetMinimumLevel(LogLevel.Information);

        var labDb = builder.Configuration.GetConnectionString("LabDb");
        if (string.IsNullOrWhiteSpace(labDb))
        {
            Console.Error.WriteLine("Missing ConnectionStrings:LabDb.");
            return 2;
        }

        var blobConn = builder.Configuration["EventDocsStorage"];
        var blobUriStr = builder.Configuration["EventDocsStorage:blobServiceUri"];
        if (string.IsNullOrWhiteSpace(blobConn) && string.IsNullOrWhiteSpace(blobUriStr))
        {
            Console.Error.WriteLine("Missing EventDocsStorage or EventDocsStorage:blobServiceUri.");
            return 2;
        }

        builder.Services.AddDbContext<AppDbContext>(opt =>
            opt.UseSqlServer(labDb, sql => sql.EnableRetryOnFailure(maxRetryCount: 3)));

        builder.Services.AddSingleton(_ =>
            !string.IsNullOrWhiteSpace(blobConn)
                ? new BlobServiceClient(blobConn)
                : new BlobServiceClient(new Uri(blobUriStr!), new DefaultAzureCredential()));

        builder.Services.AddAzureLabRag(builder.Configuration);
        builder.Services.AddAzureLabRagIngestion();
        builder.Services.AddSingleton<IDocumentContentSource, BlobDocumentContentSource>();

        using var host = builder.Build();

        // Fail fast on AiOptions validation before touching SQL/OpenAI.
        _ = host.Services.GetRequiredService<IOptions<AiOptions>>().Value;

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ingestion = scope.ServiceProvider.GetRequiredService<DocumentIngestionService>();

        var query = db.EventDocuments.AsNoTracking()
            .Where(d => d.Status == "Accepted" && d.EventId != null);

        if (filterEventId is { } eid)
            query = query.Where(d => d.EventId == eid);

        var allAccepted = await query.ToListAsync();

        // Keep only the latest Accepted version per (EventId, BlobName) to avoid wasted embedding calls.
        var latestDocs = allAccepted
            .GroupBy(d => (d.EventId!.Value, d.BlobName))
            .Select(g => g.OrderByDescending(x => x.ProcessedUtc).ThenByDescending(x => x.Id).First())
            .OrderBy(d => d.EventId)
            .ThenBy(d => d.BlobName)
            .ToList();

        Console.WriteLine($"Found {latestDocs.Count} latest Accepted document(s) (out of {allAccepted.Count} total Accepted rows).");

        int stored = 0, superseded = 0, skipped = 0, failed = 0, totalChunks = 0;

        foreach (var doc in latestDocs)
        {
            var req = new IngestionRequest(
                DocumentKey: $"{doc.BlobName}|{doc.ETag}",
                BlobName: doc.BlobName,
                ETag: doc.ETag,
                EventId: doc.EventId,
                SourceVersionUtc: doc.ProcessedUtc);

            try
            {
                var result = await ingestion.IngestAsync(req, CancellationToken.None);
                switch (result.Outcome)
                {
                    case IngestionOutcome.Stored:
                        stored++;
                        totalChunks += result.ChunkCount;
                        Console.WriteLine($"  [Stored]     {doc.BlobName} ({result.ChunkCount} chunks)");
                        break;
                    case IngestionOutcome.Superseded:
                        superseded++;
                        Console.WriteLine($"  [Superseded] {doc.BlobName}");
                        break;
                    default:
                        skipped++;
                        Console.WriteLine($"  [Skipped]    {doc.BlobName} ({result.Outcome}: {result.Detail})");
                        break;
                }
            }
            catch (Exception ex)
            {
                failed++;
                Console.Error.WriteLine($"  [FAILED]     {doc.BlobName}: {ex.Message}");
            }
        }

        Console.WriteLine($"Re-index complete -> index='{indexName}', stored={stored} ({totalChunks} chunks), superseded={superseded}, skipped={skipped}, failed={failed}");
        return failed > 0 ? 1 : 0;
    }

    default:
        Console.Error.WriteLine($"Unknown command '{command}'.");
        PrintUsage();
        return 2;
}

static string? GetOption(string[] argv, string name)
{
    for (var i = 1; i < argv.Length - 1; i++)
    {
        if (string.Equals(argv[i], name, StringComparison.OrdinalIgnoreCase))
            return argv[i + 1];
    }
    return null;
}

static void PrintUsage()
{
    Console.WriteLine("""
        AzureLab.SearchTool — Deploy-time Azure AI Search schema & re-index CLI (Entra ID only)

        Commands:
          ensure-index [--index <name>] [--dimensions <n>]
          delete-index [--index <name>]
          alias <aliasName> <targetIndexName>
          reindex [--index <name>] [--event-id <guid>]

        Required env vars:
          Ai__VectorStore__AzureAISearch__Endpoint
          Ai__VectorStore__AzureAISearch__IndexName (or --index)
        Additional env vars for 'reindex':
          Ai__Enabled=true, Ai__Provider=AzureOpenAI, Ai__AzureOpenAI__Endpoint,
          Ai__Chat__Model, Ai__Chat__TimeoutSeconds, Ai__Embedding__Model,
          Ai__Embedding__Dimensions, Ai__Embedding__TimeoutSeconds,
          Ai__VectorStore__Provider=AzureAISearch,
          ConnectionStrings__LabDb, EventDocsStorage__blobServiceUri (or EventDocsStorage)
        """);
}
