using System.Diagnostics.Metrics;
using System.Text.Json;
using EventGateway.Data;
using EventGateway.Diagnostics;
using EventGateway.Models;
using EventGateway.Services;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Http.Resilience;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Polly;
using Serilog;
using Serilog.Formatting.Compact;

var builder = WebApplication.CreateBuilder(args);

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(new CompactJsonFormatter())
    .Enrich.FromLogContext()
    .Enrich.WithProperty("service", "event-gateway")
    .Enrich.With<ActivityEnricher>()
    .CreateLogger();

// Custom metric — counts events successfully applied, tagged by CREDIT/DEBIT type.
// OTel picks this up via AddMeter below and exports to the console exporter.
var meter = new Meter("EventLedger.Gateway", "1.0.0");
var eventsApplied = meter.CreateCounter<long>(
    "event_gateway.events_applied",
    description: "Number of events successfully applied to the Account Service, by transaction type");

builder.Host.UseSerilog();

builder.Services.AddDbContext<GatewayDbContext>(opts =>
    opts.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")
        ?? "Data Source=gateway.db"));

var resourceBuilder = ResourceBuilder.CreateDefault().AddService("event-gateway");

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .SetResourceBuilder(resourceBuilder)
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddConsoleExporter())
    .WithMetrics(metrics => metrics
        .SetResourceBuilder(resourceBuilder)
        .AddMeter("EventLedger.Gateway")
        .AddConsoleExporter());

// HttpClient with circuit breaker + timeout via Polly 8
// Timeout fires after 5s → counts as failure toward circuit threshold
// Circuit opens after ≥50% failure rate over 3+ calls within 10s → stays open 30s
builder.Services.AddHttpClient<IAccountServiceClient, AccountServiceClient>(client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["AccountService:BaseUrl"] ?? "http://localhost:5001");
})
.AddResilienceHandler("account-service-pipeline", pipeline =>
{
    pipeline.AddTimeout(TimeSpan.FromSeconds(5));

    pipeline.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
    {
        MinimumThroughput = 3,
        SamplingDuration = TimeSpan.FromSeconds(10),
        FailureRatio = 0.5,
        BreakDuration = TimeSpan.FromSeconds(30),
        OnOpened = args =>
        {
            Log.Warning("Circuit breaker OPENED — Account Service will not be called for {BreakDuration}s",
                args.BreakDuration.TotalSeconds);
            return ValueTask.CompletedTask;
        },
        OnClosed = args =>
        {
            Log.Information("Circuit breaker CLOSED — Account Service calls resuming");
            return ValueTask.CompletedTask;
        },
    });
});

builder.Services.AddHealthChecks()
    .AddDbContextCheck<GatewayDbContext>("database");

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
    db.Database.EnsureCreated();
}

// Outer: logs every request with method, path, status, and elapsed time
app.UseSerilogRequestLogging();

// Inner: catches any unhandled exception, logs it, and returns structured JSON 500
app.UseExceptionHandler(exceptionApp =>
    exceptionApp.Run(async context =>
    {
        var feature = context.Features.Get<IExceptionHandlerFeature>();
        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
        logger.LogError(feature?.Error, "Unhandled exception on {Method} {Path}",
            context.Request.Method, context.Request.Path);
        context.Response.StatusCode = 500;
        await context.Response.WriteAsJsonAsync(new { error = "An unexpected error occurred" });
    }));

app.MapGet("/health", async (HealthCheckService hcs) =>
{
    var report = await hcs.CheckHealthAsync();
    var ok = report.Status == HealthStatus.Healthy;
    var body = new { status = ok ? "healthy" : "unhealthy", database = ok ? "ok" : "unavailable" };
    return ok ? Results.Ok(body) : Results.Json(body, statusCode: 503);
});

// POST /events — validate, deduplicate, persist, forward to Account Service
app.MapPost("/events", async (
    EventRequest req,
    GatewayDbContext db,
    IAccountServiceClient accountClient,
    ILogger<Program> logger) =>
{
    if (string.IsNullOrWhiteSpace(req.EventId))
    {
        return Results.BadRequest(new { error = "eventId is required" });
    }

    if (string.IsNullOrWhiteSpace(req.AccountId))
    {
        return Results.BadRequest(new { error = "accountId is required" });
    }

    if (req.Type != "CREDIT" && req.Type != "DEBIT")
    {
        return Results.BadRequest(new { error = "type must be CREDIT or DEBIT" });
    }

    if (req.Amount <= 0)
    {
        return Results.BadRequest(new { error = "amount must be greater than 0" });
    }

    if (string.IsNullOrWhiteSpace(req.Currency))
    {
        return Results.BadRequest(new { error = "currency is required" });
    }

    // Idempotency — return original event without calling Account Service again
    var existing = await db.Events.FindAsync(req.EventId);
    if (existing != null)
    {
        logger.LogInformation("Duplicate event {EventId} received — returning original", req.EventId);
        return Results.Ok(existing);
    }

    var ev = new EventRecord
    {
        EventId = req.EventId!,
        AccountId = req.AccountId!,
        Type = req.Type!,
        Amount = req.Amount,
        Currency = req.Currency!,
        EventTimestamp = req.EventTimestamp,
        Metadata = req.Metadata != null ? JsonSerializer.Serialize(req.Metadata) : null,
        ReceivedAt = DateTimeOffset.UtcNow,
        Status = "PENDING",
    };

    db.Events.Add(ev);

    try
    {
        await db.SaveChangesAsync();
    }
    catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("UNIQUE") == true)
    {
        // Race condition: two identical requests arrived simultaneously
        logger.LogWarning("Concurrent duplicate for {EventId} — returning existing event", req.EventId);
        existing = await db.Events.FindAsync(req.EventId);
        return Results.Ok(existing);
    }

    // Forward to Account Service — circuit breaker + timeout active on this call
    try
    {
        await accountClient.ApplyTransactionAsync(ev);
        ev.Status = "APPLIED";
        await db.SaveChangesAsync();
        eventsApplied.Add(1, new KeyValuePair<string, object?>("type", ev.Type));
        logger.LogInformation("Event {EventId} applied to account {AccountId}", ev.EventId, ev.AccountId);
        return Results.Created($"/events/{ev.EventId}", ev);
    }
    catch (Exception ex)
    {
        // Circuit open, timeout, or Account Service error — event saved as PENDING
        // GET endpoints still work; balance not yet applied (documented gap)
        logger.LogWarning(ex, "Account Service unavailable for event {EventId} — stored as PENDING", ev.EventId);
        return Results.Json(
            new { error = "Account Service unavailable", eventId = ev.EventId },
            statusCode: 503);
    }
});

// GET /events/{id} — reads only from Gateway DB, works when Account Service is down
app.MapGet("/events/{id}", async (string id, GatewayDbContext db) =>
{
    var ev = await db.Events.FindAsync(id);
    return ev is null
        ? Results.NotFound(new { error = "Event not found" })
        : Results.Ok(ev);
});

// GET /events?account= — ordered by eventTimestamp, not arrival order
app.MapGet("/events", async (string? account, GatewayDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(account))
    {
        return Results.BadRequest(new { error = "account query parameter is required" });
    }

    // SQLite can't translate DateTimeOffset in ORDER BY — sort after loading
    var events = (await db.Events
        .Where(e => e.AccountId == account)
        .ToListAsync())
        .OrderBy(e => e.EventTimestamp)
        .ToList();

    return Results.Ok(events);
});

app.Run();

/// <summary>Entry point — required for WebApplicationFactory test access.</summary>
public partial class Program
{
}
