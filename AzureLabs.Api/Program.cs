using Azure.Messaging.ServiceBus;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using AzureLab.Data;
using AzureLab.Messaging;
using AzureLab.Rag;
using AzureLab.Rag.Abstractions;
using AzureLab.Rag.Query;
using AzureLabs.Api.Ask;
using AzureLabs.Api.Storage;
using AzureLabs.Api.Storage.Sas;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using System.Security.Cryptography;
using System.Text;
using AzureLab.Assistant;
using AzureLabs.Api.Assistant;
using AzureLabs.Api.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Identity.Web;
using Polly;

var builder = WebApplication.CreateBuilder(args);

// A5: chaos.json is ONLY loaded in Development and cannot run outside Development.
if (builder.Environment.IsDevelopment())
{
    var chaosFile = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "chaos.json"));
    builder.Configuration.AddJsonFile(chaosFile, optional: true, reloadOnChange: true);
}

// Add services to the container.
builder.Services.AddBlobStorage(builder.Configuration);
builder.Services.AddSingleton<EventDocumentStore>();

builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddSingleton<ISasService>(sp => 
{
    var options = sp.GetRequiredService<IOptions<BlobStorageOptions>>().Value;
    return options.UsesManagedIdentity
        ? ActivatorUtilities.CreateInstance<UserDelegationSasService>(sp)
        : ActivatorUtilities.CreateInstance<AccountKeySasService>(sp);
});


builder.Services.AddAzureLabServiceBus(builder.Configuration);
builder.Services.AddDatabase(builder.Configuration);
builder.Services.AddAzureLabRag(builder.Configuration);
builder.Services.AddAzureLabRagQuery();

// Day 6: assistant (tools run through ports; adapters live here)
builder.Services.AddSingleton(sp =>
{
    var q = sp.GetRequiredService<IOptions<AiOptions>>().Value.Query;
    return new ToolLimits(MaxQueryChars: q.MaxQuestionChars, MaxOutputTokens: q.MaxAnswerTokens);
});
builder.Services.AddScoped<IRedeemStatusReader, EfRedeemStatusReader>();
builder.Services.AddScoped<IEventDocumentCatalog, EfEventDocumentCatalog>();
builder.Services.AddScoped<IEventPassageSearch, RagPassageSearch>();
builder.Services.AddScoped<AssistantService>();
builder.Services.AddScoped<IRedeemProposals, EfRedeemProposals>();
builder.Services.AddScoped<ProposalConfirmationService>();
builder.Services.AddScoped<IEventAccessService, EfEventAccessService>();

// Step 6: Standard resilience pipeline on redeem-loopback (Polly v8).
// POST /offers/{id}/redeem is idempotent when X-Correlation-ID is sent.
builder.Services.AddHttpClient(ProposalConfirmationService.ClientName, c =>
{
    c.Timeout = Timeout.InfiniteTimeSpan; // managed by Polly TotalRequestTimeout (must stay < Lease = 30s)
})
.AddStandardResilienceHandler(options =>
{
    options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(3);
    options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(10);

    options.Retry.MaxRetryAttempts = 2;
    options.Retry.BackoffType = DelayBackoffType.Exponential;
    options.Retry.UseJitter = true;
    options.Retry.Delay = TimeSpan.FromMilliseconds(250);

    options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
    options.CircuitBreaker.FailureRatio = 0.5;
    options.CircuitBreaker.MinimumThroughput = 3;
    options.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(15);
});
builder.Services.AddSingleton<IntentExtractor>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"));
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("ApiAccess", policy => policy.RequireAssertion(ctx =>
    {
        if (ctx.User.IsInRole("Api.Service") || ctx.User.HasClaim("roles", "Api.Service"))
            return true;

        var scopes = ctx.User.FindAll("http://schemas.microsoft.com/identity/claims/scope")
            .Concat(ctx.User.FindAll("scp"))
            .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return scopes.Contains("api.access", StringComparer.Ordinal);
    }));
});

builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks()
    .AddCheck<ApiSqlReadinessHealthCheck>("sql", failureStatus: HealthStatus.Unhealthy, tags: ["ready"]);

AppContext.SetSwitch("Azure.Experimental.EnableActivitySource", true);

var otelBuilder = builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("azurelab-api"))
    .WithTracing(t =>
    {
        t.AddSource(RagTelemetry.Name)
            .AddSource("Azure.Messaging.ServiceBus.*")
            .AddAspNetCoreInstrumentation();
        if (builder.Environment.IsDevelopment()) t.AddConsoleExporter();
    })
    .WithMetrics(m => m.AddMeter(RagTelemetry.Name));                      // read locally with dotnet-counters

if (!string.IsNullOrWhiteSpace(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
{
    otelBuilder.UseAzureMonitor();
    builder.Services.Configure<OpenTelemetry.Logs.OpenTelemetryLoggerOptions>(o => o.IncludeScopes = true);
}
var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
// A3: /health and /health/live have zero downstream dependencies (safe for App Service healthCheckPath);
//     /health/ready verifies SQL connectivity.
app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = r => r.Tags.Contains("ready") });
app.MapAskEndpoint();

if (app.Environment.IsDevelopment())
{
    app.MapAssistantEndpoint();
    app.MapProposalEndpoints();
    app.MapProposalSelfTest();
    app.MapSecurityLab();
    app.MapAssistantEval();
    app.MapFailureLab();
    // DEV PROBE 1: the exact tool schemas the model receives (generated by AIFunctionFactory)
    app.MapGet("/dev/assistant/tools", (ToolLimits limits) =>
        new AssistantTools(null!, null!, null!, null!, limits, new AssistantRunState())
            .CreateTools()
            .OfType<AIFunctionDeclaration>()
            .Select(t => new { t.Name, t.Description, Schema = t.JsonSchema }));

    // DEV PROBE 2: the raw IChatClient with the production system prompt + tools, NO function-invocation loop
    app.MapGet("/dev/assistant/rawchat", async (IChatClient chat, ToolLimits limits, string q, CancellationToken ct) =>
    {
        var tools = new AssistantTools(null!, null!, null!, null!, limits, new AssistantRunState()).CreateTools();
        var options = new ChatOptions
        {
            Temperature = 0f,
            MaxOutputTokens = limits.MaxOutputTokens,
            AllowMultipleToolCalls = false,
            Tools = tools
        };
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, AssistantService.SystemPrompt),
            new(ChatRole.User, q)
        };
        var resp = await chat.GetResponseAsync(messages, options, ct);
        return new
        {
            finish = resp.FinishReason?.ToString(),
            textLen = resp.Text?.Length ?? 0,
            contents = resp.Messages.SelectMany(m => m.Contents).Select(c =>
                c is FunctionCallContent f
                    ? $"FunctionCall:{f.Name}({(f.Arguments is null ? "" : string.Join(",", f.Arguments.Select(a => $"{a.Key}={a.Value}")))})"
                    : c.GetType().Name),
            usage = resp.Usage
        };
    });

    // DEV PROBE 3 (v2): OllamaSharp vs plain HttpClient, identical bytes, same process
    app.MapGet("/dev/assistant/tap", async (IOptions<AiOptions> ai, ToolLimits limits, string q, int? n, CancellationToken ct) =>
    {
        var tools = new AssistantTools(null!, null!, null!, null!, limits, new AssistantRunState()).CreateTools();
        var results = new List<object?>();
        var timeout = TimeSpan.FromSeconds(ai.Value.Chat.TimeoutSeconds);
        for (var i = 0; i < Math.Clamp(n ?? 1, 1, 10); i++)
        {
            var tap = new BodyTapHandler();
            using var http = new HttpClient(tap) { BaseAddress = new Uri(ai.Value.Ollama.Endpoint), Timeout = timeout };
            IChatClient chat = new OllamaSharp.OllamaApiClient(http, ai.Value.Chat.Model);
            var options = new ChatOptions
            {
                Temperature = 0f,
                MaxOutputTokens = limits.MaxOutputTokens,
                AllowMultipleToolCalls = false,
                Tools = tools
            };
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, AssistantService.SystemPrompt),
                new(ChatRole.User, q)
            };
            await chat.GetResponseAsync(messages, options, ct);          // A: OllamaSharp
            var body = tap.LastRequest!;
            var rawA = tap.LastResponse ?? "";

            using var plain = new HttpClient { BaseAddress = new Uri(ai.Value.Ollama.Endpoint), Timeout = timeout };
            using var content = new StringContent(body, Encoding.UTF8);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            using var resp2 = await plain.PostAsync("/api/chat", content, ct);   // B: same bytes, plain HttpClient
            var rawB = await resp2.Content.ReadAsStringAsync(ct);

            results.Add(new
            {
                run = i + 1,
                a_ollamaSharp_toolCall = rawA.Contains("\"tool_calls\""),
                b_plainHttp_toolCall = rawB.Contains("\"tool_calls\""),
                requestSha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)))[..12],
                requestChars = body.Length,
                headers = i == 0 ? tap.LastRequestHeaders : null
            });
        }
        return results;
    });

    // DEV PROBE 4: forward a posted request body to Ollama with chosen headers
    app.MapPost("/dev/assistant/replay", async (HttpRequest req, IOptions<AiOptions> ai, string variant, CancellationToken ct) =>
    {
        using var sr = new StreamReader(req.Body, Encoding.UTF8);
        var body = await sr.ReadToEndAsync(ct);

        using var http = new HttpClient
        {
            BaseAddress = new Uri(ai.Value.Ollama.Endpoint),
            Timeout = TimeSpan.FromSeconds(ai.Value.Chat.TimeoutSeconds)
        };
        using var content = new StringContent(body, Encoding.UTF8);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat") { Content = content };
        if (variant == "ps")   // mimic Invoke-RestMethod: no charset on Content-Type, PowerShell-style User-Agent
        {
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Microsoft Windows 10.0; en-US) WindowsPowerShell/5.1");
        }
        else
        {
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        }

        using var resp = await http.SendAsync(request, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);
        var m = System.Text.RegularExpressions.Regex.Match(raw, "\"eval_count\":(\\d+)");
        return new
        {
            variant,
            status = (int)resp.StatusCode,
            toolCall = raw.Contains("\"tool_calls\""),
            evalCount = m.Success ? m.Groups[1].Value : null
        };
    });

    app.MapAssistantSelfTest();
    app.MapIntentEndpoint();
    app.MapIntentSelfTest();
}

app.MapPost("/offers/{offerId:guid}/redeem", async (
    Guid offerId,
    AppDbContext db,
    HttpContext http,
    IConfiguration config,
    IHostEnvironment env,
    IOptions<ServiceBusOptions> options,
    ILogger<Program> logger) =>
{
    var userId = http.User.GetCallerObjectId();
    if (string.IsNullOrWhiteSpace(userId))
        return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Unauthenticated.");

    if (env.IsDevelopment() && string.Equals(config["Chaos:Mode"], "ApiSlow", StringComparison.OrdinalIgnoreCase))
    {
        var slowSec = int.TryParse(config["Chaos:SlowSeconds"], out var s) && s > 0 ? s : 5;
        await Task.Delay(TimeSpan.FromSeconds(slowSec), http.RequestAborted);
    }

    var callerCorrelationId = http.Request.Headers["X-Correlation-ID"].FirstOrDefault();
    var correlationId = !string.IsNullOrWhiteSpace(callerCorrelationId) ? callerCorrelationId : Guid.NewGuid().ToString();
    System.Diagnostics.Activity.Current?.SetTag("correlation.id", correlationId);
    using var logScope = logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId });

    // Idempotent POST when caller supplies X-Correlation-ID (makes Polly HTTP retries on redeem-loopback safe)
    if (!string.IsNullOrWhiteSpace(callerCorrelationId))
    {
        var existing = await db.RedeemTransactions.AsNoTracking()
            .Where(t => t.CorrelationId == callerCorrelationId && t.OfferId == offerId && t.UserId == userId)
            .Select(t => new { t.Id, t.Status })
            .FirstOrDefaultAsync(http.RequestAborted);

        if (existing is not null)
        {
            http.Response.Headers["X-Correlation-ID"] = correlationId;
            return Results.Accepted(
                $"/offers/{offerId}/redeem/{existing.Id}",
                new { RedeemTransactionId = existing.Id, existing.Status });
        }
    }

    var txId = Guid.NewGuid();
    var now = DateTimeOffset.UtcNow;

    var transaction = new RedeemTransaction
    {
        Id = txId,
        OfferId = offerId,
        UserId = userId,
        Status = RedeemTransactionStatus.Pending,
        RequestedAtUtc = now,
        CorrelationId = correlationId
    };

    var payload = new RedeemRequested(txId, offerId, Guid.Parse("F878DD09-415E-48EA-A4E6-9091A29FCE80"), userId, now);

    var strategy = db.Database.CreateExecutionStrategy();
    await strategy.ExecuteAsync(async ct =>
    {
        db.ChangeTracker.Clear();
        db.RedeemTransactions.Add(transaction);
        db.OutboxMessages.Add(entity: Outbox.Create(
            messageId: txId,
            destination: options.Value.RedeemQueue,
            subject: MessageTypes.RedeemRequested,
            payload: payload,
            correlationId: correlationId,
            properties: new Dictionary<string, string>()
            {
                ["EventId"] = payload.EventId.ToString(),
                ["OfferId"] = payload.OfferId.ToString(),
                ["SchemaVersion"] = "1"
            }));
        await db.SaveChangesAsync(ct);
    }, http.RequestAborted);

    http.Response.Headers["X-Correlation-ID"] = correlationId;
    logger.LogInformation("Redeem accepted RedeemTransactionId={RedeemTransactionId} OfferId={OfferId} CorrelationId={CorrelationId}", txId, offerId, correlationId);

    return Results.Accepted(
        $"/offers/{offerId}/redeem/{txId}",
        new { RedeemTransactionId = txId, transaction.Status });
})
.RequireAuthorization("ApiAccess")
.WithName("RedeemOffer")
.Produces(StatusCodes.Status202Accepted);

if (app.Environment.IsDevelopment())
{
    app.MapGet("/sb/ping", async (ServiceBusClient client, IOptions<ServiceBusOptions> options) =>
    {
        await using var receiver = client.CreateReceiver(options.Value.RedeemQueue);
        var peekedMessage = await receiver.PeekMessageAsync();
        return Results.Ok(new { connected = true, queue = options.Value.RedeemQueue, hasMessages = peekedMessage != null });
    });

    app.MapPost("/dev/sb/malformed", async (ServiceBusClient client,
        IOptions<ServiceBusOptions> o) =>
    {
        await using var sender = client.CreateSender(o.Value.RedeemQueue);
        await sender.SendMessageAsync(new ServiceBusMessage("not-json{")
        {
            MessageId = Guid.NewGuid().ToString(),
            CorrelationId = "malformed-demo",
            Subject = MessageTypes.RedeemRequested,
            ContentType = "application/json"
        });
        return Results.Accepted();
    });


    // Admin endpoint to resubmit a message from the DLQ back to the main queue
    app.MapPost("/admin/dlq/resubmit/{messageId?}", async (
        string? messageId,
        ServiceBusClient client,
        IOptions<ServiceBusOptions> o) =>
    {
        var queueName = o.Value.RedeemQueue;

        await using var dlqReceiver = client.CreateReceiver(queueName, new ServiceBusReceiverOptions
        {
            SubQueue = SubQueue.DeadLetter
        });

        await using var queueSender = client.CreateSender(queueName);

        ServiceBusReceivedMessage? msg = null;

        if (!string.IsNullOrEmpty(messageId))
        {
            // Peek until we find the specific message
            var peeked = await dlqReceiver.PeekMessagesAsync(50);
            msg = peeked.FirstOrDefault(m => m.MessageId == messageId);
            if (msg is null)
                return Results.NotFound($"MessageId {messageId} not found in DLQ");

            // We need to receive it (lock it) before we can complete it
            msg = await dlqReceiver.ReceiveMessageAsync(TimeSpan.FromSeconds(5));
            if (msg?.MessageId != messageId)
                return Results.Problem("Could not lock the requested message");
        }
        else
        {
            // Just take the next one
            msg = await dlqReceiver.ReceiveMessageAsync(TimeSpan.FromSeconds(5));
            if (msg is null)
                return Results.NotFound("No messages in DLQ");
        }

        // Resubmit to the main queue
        var newMsg = new ServiceBusMessage(msg)
        {
            // Optional: clear dead-letter properties if you want a clean message
        };

        await queueSender.SendMessageAsync(newMsg);
        await dlqReceiver.CompleteMessageAsync(msg);   // remove from DLQ

        return Results.Ok(new
        {
            Resubmitted = true,
            MessageId = msg.MessageId,
            OriginalReason = msg.DeadLetterReason
        });
    });

    app.MapGet("/dev/ai/ping", async (IChatClient chat, ITextEmbedder embedder, CancellationToken ct) =>
    {
        var vector = await embedder.EmbedQueryAsync("ping", ct);
        var reply = await chat.GetResponseAsync("Reply with the single word: pong", cancellationToken: ct);
        return Results.Ok(new { embeddingDimensions = vector.Length, chat = reply.Text });
    });

    app.MapPost("/dev/rag/selftest", async (ITextEmbedder embedder, IDocumentVectorStore store, CancellationToken ct) =>
    {
        var eventId = Guid.NewGuid();
        var blob = $"{eventId}/rules.pdf";
        var texts = new[]
        {
            "The refund policy allows cancellation up to 48 hours before the event.",
            "Parking is available in lot B next to the main entrance.",
            "Vegetarian and vegan meals are served at the gala dinner."
        };
        var vectors = await embedder.EmbedDocumentsAsync(texts, ct);
        var chunks = texts.Select((t, i) => new ChunkToStore(
            i, t, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(t))).ToLowerInvariant(), vectors[i])).ToList();

        var t0 = DateTimeOffset.UtcNow;
        var doc = new DocumentToStore($"{blob}|etag2", blob, eventId, embedder.ModelName, embedder.Dimensions, t0, chunks);

        var first = await store.ReplaceDocumentAsync(doc, ct);
        var duplicate = await store.ReplaceDocumentAsync(doc, ct);   // same message delivered twice

        var q = await embedder.EmbedQueryAsync("Can I get my money back if I cancel?", ct);
        var hits = await store.SearchAsync(eventId, q, 3, ct);
        var otherEventHits = await store.SearchAsync(Guid.NewGuid(), q, 3, ct);   // isolation

        // A delayed, OLDER version arrives after the newer one
        var stale = doc with { DocumentKey = $"{blob}|etag1", SourceVersionUtc = t0.AddMinutes(-5) };
        var staleOutcome = await store.ReplaceDocumentAsync(stale, ct);

        return Results.Ok(new
        {
            eventId,
            first,
            duplicate,
            staleOutcome,
            otherEventHits = otherEventHits.Count,
            hits = hits.Select(h => new { h.ChunkIndex, Similarity = Math.Round(h.Similarity, 3), h.Content })
        });
    });

    app.MapGet("/dev/rag/probe", async (Guid eventId, string q, ITextEmbedder embedder,
    IDocumentVectorStore store, CancellationToken ct) =>
    {
        var v = await embedder.EmbedQueryAsync(q, ct);
        var hits = await store.SearchAsync(eventId, v, 5, ct);
        return Results.Ok(hits.Select(h => new { h.ChunkIndex, Similarity = Math.Round(h.Similarity, 3), h.Content }));
    });

    app.MapEvalEndpoint();
}

app.Run();

internal sealed class ApiSqlReadinessHealthCheck(IServiceScopeFactory scopes) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Database.CanConnectAsync(cancellationToken)
            ? HealthCheckResult.Healthy("SQL reachable")
            : HealthCheckResult.Unhealthy("SQL unreachable");
    }
}