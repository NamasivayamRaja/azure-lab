using Azure.Identity;
using AzureLab.Data;
using AzureLab.Functions.Storage;
using AzureLab.Messaging;
using AzureLab.Rag;
using AzureLab.Rag.Ingestion;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

var cfg = builder.Configuration;

builder.Services
    .AddApplicationInsightsTelemetryWorkerService()
    .ConfigureFunctionsApplicationInsights();

// The SDK adds a rule that only sends warning and above to Application Insights. Remove it so host.json/appsettings decide.
builder.Logging.Services.Configure<LoggerFilterOptions>(o =>
{
    var rule = o.Rules.FirstOrDefault(r =>
        r.ProviderName == "Microsoft.Extensions.Logging.ApplicationInsights.ApplicationInsightsLoggerProvider");

    if (rule is not null) o.Rules.Remove(rule);
});

// A1: Fail-fast validation (no secret values logged or thrown)
var sbConnection = cfg["ServiceBusConnection"];
var sbFqdn = cfg["ServiceBusConnection:fullyQualifiedNamespace"];
if (string.IsNullOrWhiteSpace(sbConnection) && string.IsNullOrWhiteSpace(sbFqdn))
{
    throw new InvalidOperationException(
        "Missing Service Bus configuration: provide either 'ServiceBusConnection' (local emulator) or 'ServiceBusConnection:fullyQualifiedNamespace' (Managed Identity).");
}

var blobConnection = cfg["EventDocsStorage"];
var blobServiceUri = cfg["EventDocsStorage:blobServiceUri"];
Uri? parsedBlobUri = null;
if (string.IsNullOrWhiteSpace(blobConnection) &&
    (string.IsNullOrWhiteSpace(blobServiceUri) || !Uri.TryCreate(blobServiceUri, UriKind.Absolute, out parsedBlobUri)))
{
    throw new InvalidOperationException(
        "Missing Blob Storage configuration: provide either 'EventDocsStorage' (local Azurite) or a valid absolute URI in 'EventDocsStorage:blobServiceUri' (Managed Identity).");
}

var labDbConnection = cfg.GetConnectionString("LabDb");
if (string.IsNullOrWhiteSpace(labDbConnection))
{
    throw new InvalidOperationException("Missing SQL configuration: 'ConnectionStrings:LabDb' is required.");
}

builder.Services.AddAzureClients(clients =>
{
    if (!string.IsNullOrWhiteSpace(sbConnection))
        clients.AddServiceBusClient(sbConnection);
    else
        clients.AddServiceBusClientWithNamespace(sbFqdn!);

    // Fixed A1 bug: test blobConnection (not sbConnection!)
    if (!string.IsNullOrWhiteSpace(blobConnection))
        clients.AddBlobServiceClient(blobConnection);
    else
        clients.AddBlobServiceClient(parsedBlobUri!);

    clients.UseCredential(new DefaultAzureCredential());
});

builder.Services.AddDbContext<AppDbContext>(options =>
{
    options.UseSqlServer(labDbConnection,
        sql => sql.EnableRetryOnFailure(maxRetryCount: 3));
});

builder.Services.AddAzureLabRag(builder.Configuration);

builder.Services.AddAzureLabRagIngestion();
builder.Services.AddSingleton<IDocumentContentSource, BlobDocumentContentSource>();
builder.Services.AddSingleton<DocumentEventPublisher>();

builder.Build().Run();
