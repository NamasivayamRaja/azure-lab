using AzureLab.Data;
using AzureLab.Messaging;
using AzureLab.Worker;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAzureLabServiceBus(builder.Configuration);

// A3: Liveness (/health/live & /health) has zero downstream dependencies (safe for App Service healthCheckPath).
//     Readiness (/health/ready) checks SQL connectivity.
builder.Services.AddHealthChecks()
    .AddCheck<SqlReadinessHealthCheck>("sql", failureStatus: HealthStatus.Unhealthy, tags: ["ready"]);

// A5: chaos.json is ONLY loaded in Development and cannot run outside Development.
if (builder.Environment.IsDevelopment())
{
    var chaosFile = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "chaos.json"));
    builder.Configuration.AddJsonFile(chaosFile, optional: true, reloadOnChange: true);
}
builder.Services.Configure<ChaosOptions>(builder.Configuration.GetSection("Chaos"));

// B1: Bind and validate OutboxOptions on startup.
builder.Services.AddOptions<OutboxOptions>()
    .Bind(builder.Configuration.GetSection(OutboxOptions.SectionName))
    .Validate(o => o.BatchSize is > 0 and <= 500, "Outbox:BatchSize must be between 1 and 500.")
    .Validate(o => o.PollInterval > TimeSpan.Zero, "Outbox:PollInterval must be > 0.")
    .Validate(o => o.LeaseDuration >= TimeSpan.FromSeconds(5), "Outbox:LeaseDuration must be >= 5s.")
    .Validate(o => o.MaxAttempts is >= 1 and <= 20, "Outbox:MaxAttempts must be between 1 and 20.")
    .ValidateOnStart();

builder.Services.Configure<ReconcilerOptions>(builder.Configuration.GetSection(ReconcilerOptions.SectionName));

builder.Services.AddScoped<RedeemHandler>();

builder.Services.AddServiceBusConsumer<RedeemRequestMessageHandler>(builder.Configuration, "Redeem");
builder.Services.AddServiceBusConsumer<LedgerMessageHandler>(builder.Configuration, "Ledger");
builder.Services.AddServiceBusConsumer<NotificationMessageHandler>(builder.Configuration, "Notification");

builder.Services.AddDatabase(builder.Configuration);
builder.Services.AddHostedService<OutboxDispatcher>();
builder.Services.AddHostedService<RedeemReconciler>();

var app = builder.Build();

app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = r => r.Tags.Contains("ready") });

app.Run();

internal sealed class SqlReadinessHealthCheck(IServiceScopeFactory scopes) : IHealthCheck
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
