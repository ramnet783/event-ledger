using AccountService.Data;
using AccountService.Diagnostics;
using AccountService.Models;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Formatting.Compact;

var builder = WebApplication.CreateBuilder(args);

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(new CompactJsonFormatter())
    .Enrich.FromLogContext()
    .Enrich.WithProperty("service", "account-service")
    .Enrich.With<ActivityEnricher>()
    .CreateLogger();

builder.Host.UseSerilog();

builder.Services.AddDbContext<AccountDbContext>(opts =>
    opts.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")
        ?? "Data Source=account.db"));

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("account-service"))
        .AddAspNetCoreInstrumentation()
        .AddOtlpExporter());

builder.Services.AddHealthChecks()
    .AddDbContextCheck<AccountDbContext>("database");

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AccountDbContext>();
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

// Idempotent on eventId — duplicate submissions return the original transaction unchanged
app.MapPost("/accounts/{accountId}/transactions", async (
    string accountId,
    TransactionRequest? req,
    AccountDbContext db,
    ILogger<Program> logger) =>
{
    if (req is null)
    {
        return Results.BadRequest(new { error = "Request body is required" });
    }

    if (string.IsNullOrWhiteSpace(req.EventId))
    {
        return Results.BadRequest(new { error = "eventId is required" });
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

    if (req.EventTimestamp is null)
    {
        return Results.BadRequest(new { error = "eventTimestamp is required" });
    }

    var existing = await db.Transactions.FirstOrDefaultAsync(t => t.EventId == req.EventId);
    if (existing != null)
    {
        logger.LogInformation("Duplicate transaction {EventId} for account {AccountId} — skipping", req.EventId, accountId);
        return Results.Ok(existing);
    }

    // Auto-create account on first transaction
    if (!await db.Accounts.AnyAsync(a => a.AccountId == accountId))
    {
        db.Accounts.Add(new Account { AccountId = accountId });
        logger.LogInformation("Auto-created account {AccountId}", accountId);
    }

    var tx = new Transaction
    {
        EventId = req.EventId,
        AccountId = accountId,
        Type = req.Type,
        Amount = req.Amount,
        Currency = req.Currency,
        EventTimestamp = req.EventTimestamp!.Value,
        ReceivedAt = DateTimeOffset.UtcNow,
    };

    db.Transactions.Add(tx);

    try
    {
        await db.SaveChangesAsync();
    }
    catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("UNIQUE") == true)
    {
        // Race condition: two identical requests arrived simultaneously — return the winner
        logger.LogWarning("Concurrent duplicate for {EventId} — returning existing transaction", req.EventId);
        existing = await db.Transactions.FirstOrDefaultAsync(t => t.EventId == req.EventId);
        return Results.Ok(existing);
    }

    logger.LogInformation("Applied {Type} {Amount} {Currency} to {AccountId} via event {EventId}",
        tx.Type, tx.Amount, tx.Currency, accountId, tx.EventId);

    return Results.Created($"/accounts/{accountId}/transactions/{tx.Id}", tx);
});

// Balance computed as SUM(CREDITs) - SUM(DEBITs) — correct regardless of arrival order
app.MapGet("/accounts/{accountId}/balance", async (string accountId, AccountDbContext db) =>
{
    if (!await db.Accounts.AnyAsync(a => a.AccountId == accountId))
    {
        return Results.NotFound(new { error = $"Account {accountId} not found" });
    }

    var transactions = await db.Transactions
        .Where(t => t.AccountId == accountId)
        .ToListAsync();

    var currency = transactions.FirstOrDefault()?.Currency ?? "USD";
    var balance = transactions.Sum(t => t.Type == "CREDIT" ? t.Amount : -t.Amount);

    return Results.Ok(new { accountId, balance, currency });
});

// Account details with 20 most recent transactions ordered by event timestamp
app.MapGet("/accounts/{accountId}", async (string accountId, AccountDbContext db) =>
{
    var account = await db.Accounts.FirstOrDefaultAsync(a => a.AccountId == accountId);
    if (account is null)
    {
        return Results.NotFound(new { error = $"Account {accountId} not found" });
    }

    // SQLite can't translate DateTimeOffset in ORDER BY — sort after loading
    account.Transactions = (await db.Transactions
        .Where(t => t.AccountId == accountId)
        .ToListAsync())
        .OrderByDescending(t => t.EventTimestamp)
        .Take(20)
        .ToList();

    return Results.Ok(account);
});

app.Run();

/// <summary>Entry point — required for WebApplicationFactory test access.</summary>
public partial class Program
{
}
