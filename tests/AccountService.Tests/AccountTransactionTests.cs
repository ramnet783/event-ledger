using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AccountService.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AccountService.Tests;

public class AccountTransactionTests : IClassFixture<AccountTransactionTests.Factory>
{
    private readonly HttpClient _client;

    public AccountTransactionTests(Factory factory)
    {
        _client = factory.CreateClient();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static object Payload(
        string? eventId = null,
        string type = "CREDIT",
        decimal amount = 100m,
        DateTimeOffset? eventTimestamp = null) => new
        {
            eventId = eventId ?? Guid.NewGuid().ToString(),
            type,
            amount,
            currency = "USD",
            eventTimestamp = eventTimestamp ?? DateTimeOffset.UtcNow,
        };

    // ── POST /accounts/{id}/transactions — validation ─────────────────────

    [Fact]
    public async Task PostTransaction_ValidCredit_Returns201WithTransactionBody()
    {
        var accountId = Guid.NewGuid().ToString();

        var response = await _client.PostAsJsonAsync(
            $"/accounts/{accountId}/transactions", Payload(amount: 250m));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("CREDIT", body.GetProperty("type").GetString());
        Assert.Equal(250m, body.GetProperty("amount").GetDecimal());
        Assert.Equal(accountId, body.GetProperty("accountId").GetString());
    }

    [Fact]
    public async Task PostTransaction_ValidDebit_Returns201()
    {
        var accountId = Guid.NewGuid().ToString();

        var response = await _client.PostAsJsonAsync(
            $"/accounts/{accountId}/transactions", Payload(type: "DEBIT"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task PostTransaction_MissingEventId_Returns400()
    {
        var response = await _client.PostAsJsonAsync(
            $"/accounts/{Guid.NewGuid()}/transactions",
            new { eventId = "", type = "CREDIT", amount = 100m, currency = "USD", eventTimestamp = DateTimeOffset.UtcNow });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("eventId is required", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task PostTransaction_InvalidType_Returns400()
    {
        var response = await _client.PostAsJsonAsync(
            $"/accounts/{Guid.NewGuid()}/transactions", Payload(type: "TRANSFER"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("type must be CREDIT or DEBIT", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task PostTransaction_ZeroAmount_Returns400()
    {
        var response = await _client.PostAsJsonAsync(
            $"/accounts/{Guid.NewGuid()}/transactions", Payload(amount: 0m));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("amount must be greater than 0", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task PostTransaction_NegativeAmount_Returns400()
    {
        var response = await _client.PostAsJsonAsync(
            $"/accounts/{Guid.NewGuid()}/transactions", Payload(amount: -50m));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostTransaction_MissingBody_Returns400WithErrorMessage()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/accounts/{Guid.NewGuid()}/transactions");
        request.Content = new StringContent(string.Empty, System.Text.Encoding.UTF8, "application/json");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Request body is required", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task PostTransaction_MissingCurrency_Returns400()
    {
        var response = await _client.PostAsJsonAsync(
            $"/accounts/{Guid.NewGuid()}/transactions",
            new { eventId = Guid.NewGuid().ToString(), type = "CREDIT", amount = 100m, currency = (string?)null, eventTimestamp = DateTimeOffset.UtcNow });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("currency is required", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task PostTransaction_LowercaseType_Returns400()
    {
        var response = await _client.PostAsJsonAsync(
            $"/accounts/{Guid.NewGuid()}/transactions", Payload(type: "credit"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("type must be CREDIT or DEBIT", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task PostTransaction_MissingEventTimestamp_Returns400()
    {
        var response = await _client.PostAsJsonAsync(
            $"/accounts/{Guid.NewGuid()}/transactions",
            new { eventId = Guid.NewGuid().ToString(), type = "CREDIT", amount = 100m, currency = "USD", eventTimestamp = (DateTimeOffset?)null });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("eventTimestamp is required", body.GetProperty("error").GetString());
    }

    // ── POST — idempotency ────────────────────────────────────────────────

    [Fact]
    public async Task PostTransaction_DuplicateEventId_Returns200WithIdenticalBody()
    {
        var accountId = Guid.NewGuid().ToString();
        var eventId = Guid.NewGuid().ToString();
        var payload = Payload(eventId: eventId, amount: 75m);

        var first = await _client.PostAsJsonAsync($"/accounts/{accountId}/transactions", payload);
        var second = await _client.PostAsJsonAsync($"/accounts/{accountId}/transactions", payload);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>();
        var secondBody = await second.Content.ReadFromJsonAsync<JsonElement>();

        // Both responses must represent the same stored transaction
        Assert.Equal(
            firstBody.GetProperty("id").GetString(),
            secondBody.GetProperty("id").GetString());
        Assert.Equal(
            firstBody.GetProperty("eventId").GetString(),
            secondBody.GetProperty("eventId").GetString());
    }

    [Fact]
    public async Task PostTransaction_DuplicateEventId_DifferentAccount_Returns200WithOriginal()
    {
        // eventId is a global idempotency key — the account in the URL is irrelevant for dedup
        var eventId = Guid.NewGuid().ToString();

        var first = await _client.PostAsJsonAsync(
            $"/accounts/{Guid.NewGuid()}/transactions", Payload(eventId: eventId));
        var second = await _client.PostAsJsonAsync(
            $"/accounts/{Guid.NewGuid()}/transactions", Payload(eventId: eventId));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
    }

    // ── GET /accounts/{id}/balance ────────────────────────────────────────

    [Fact]
    public async Task GetBalance_AccountNotFound_Returns404()
    {
        var response = await _client.GetAsync($"/accounts/{Guid.NewGuid()}/balance");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetBalance_CreditsMinusDebits_ReturnsCorrectBalance()
    {
        var accountId = Guid.NewGuid().ToString();

        await _client.PostAsJsonAsync($"/accounts/{accountId}/transactions", Payload(amount: 500m, type: "CREDIT"));
        await _client.PostAsJsonAsync($"/accounts/{accountId}/transactions", Payload(amount: 150m, type: "DEBIT"));
        await _client.PostAsJsonAsync($"/accounts/{accountId}/transactions", Payload(amount: 75m, type: "CREDIT"));

        var response = await _client.GetAsync($"/accounts/{accountId}/balance");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        // 500 + 75 - 150 = 425
        Assert.Equal(425m, body.GetProperty("balance").GetDecimal());
    }

    [Fact]
    public async Task GetBalance_OutOfOrderArrival_ProducesCorrectBalance()
    {
        // Transactions arrive with timestamps in reverse chronological order.
        // Balance is the algebraic sum and must be the same regardless of arrival order.
        var accountId = Guid.NewGuid().ToString();
        var now = DateTimeOffset.UtcNow;

        await _client.PostAsJsonAsync($"/accounts/{accountId}/transactions",
            Payload(amount: 30m, type: "DEBIT", eventTimestamp: now.AddHours(-1)));

        await _client.PostAsJsonAsync($"/accounts/{accountId}/transactions",
            Payload(amount: 200m, type: "CREDIT", eventTimestamp: now.AddHours(-3)));

        await _client.PostAsJsonAsync($"/accounts/{accountId}/transactions",
            Payload(amount: 70m, type: "DEBIT", eventTimestamp: now.AddHours(-2)));

        var response = await _client.GetAsync($"/accounts/{accountId}/balance");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        // 200 CREDIT - 30 DEBIT - 70 DEBIT = 100
        Assert.Equal(100m, body.GetProperty("balance").GetDecimal());
    }

    // ── GET /accounts/{id} ────────────────────────────────────────────────

    [Fact]
    public async Task GetAccount_NotFound_Returns404()
    {
        var response = await _client.GetAsync($"/accounts/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetAccount_ReturnsAccountWithTransactions()
    {
        var accountId = Guid.NewGuid().ToString();
        await _client.PostAsJsonAsync($"/accounts/{accountId}/transactions", Payload(amount: 300m));
        await _client.PostAsJsonAsync($"/accounts/{accountId}/transactions", Payload(amount: 100m, type: "DEBIT"));

        var response = await _client.GetAsync($"/accounts/{accountId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(accountId, body.GetProperty("accountId").GetString());
        Assert.Equal(2, body.GetProperty("transactions").GetArrayLength());
    }

    // ── GET /health ───────────────────────────────────────────────────────

    [Fact]
    public async Task GetHealth_ReturnsHealthy()
    {
        var response = await _client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("healthy", body.GetProperty("status").GetString());
        Assert.Equal("ok", body.GetProperty("database").GetString());
    }

    // ── Factory ───────────────────────────────────────────────────────────

    public sealed class Factory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath =
            Path.Combine(Path.GetTempPath(), $"as-test-{Guid.NewGuid()}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureServices(services =>
            {
                var existing = services.SingleOrDefault(
                    d => d.ServiceType == typeof(DbContextOptions<AccountDbContext>));
                if (existing is not null)
                {
                    services.Remove(existing);
                }

                services.AddDbContext<AccountDbContext>(opts =>
                    opts.UseSqlite($"Data Source={_dbPath}"));
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && File.Exists(_dbPath))
            {
                File.Delete(_dbPath);
            }
        }
    }
}
