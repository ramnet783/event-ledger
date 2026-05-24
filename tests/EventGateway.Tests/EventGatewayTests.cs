using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EventGateway.Data;
using EventGateway.Models;
using EventGateway.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace EventGateway.Tests;

public class EventGatewayTests : IClassFixture<EventGatewayTests.Factory>
{
    private readonly Factory _factory;
    private readonly HttpClient _client;

    public EventGatewayTests(Factory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static object ValidPayload(string? eventId = null, string? accountId = null) => new
    {
        eventId = eventId ?? Guid.NewGuid().ToString(),
        accountId = accountId ?? Guid.NewGuid().ToString(),
        type = "CREDIT",
        amount = 100m,
        currency = "USD",
        eventTimestamp = DateTimeOffset.UtcNow,
    };

    // ── POST /events — success path ───────────────────────────────────────

    [Fact]
    public async Task PostEvent_ValidRequest_Returns201AndStatusApplied()
    {
        var response = await _client.PostAsJsonAsync("/events", ValidPayload());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("APPLIED", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task PostEvent_ValidDebit_Returns201()
    {
        var response = await _client.PostAsJsonAsync("/events", new
        {
            eventId = Guid.NewGuid().ToString(),
            accountId = Guid.NewGuid().ToString(),
            type = "DEBIT",
            amount = 50m,
            currency = "USD",
            eventTimestamp = DateTimeOffset.UtcNow,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    // ── POST /events — idempotency ────────────────────────────────────────

    [Fact]
    public async Task PostEvent_DuplicateEventId_Returns200WithIdenticalBody()
    {
        var payload = ValidPayload();

        var first = await _client.PostAsJsonAsync("/events", payload);
        var second = await _client.PostAsJsonAsync("/events", payload);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>();
        var secondBody = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            firstBody.GetProperty("eventId").GetString(),
            secondBody.GetProperty("eventId").GetString());
        Assert.Equal(
            firstBody.GetProperty("status").GetString(),
            secondBody.GetProperty("status").GetString());
    }

    // ── POST /events — validation ─────────────────────────────────────────

    [Theory]
    [InlineData(null,    "acc-1",  "CREDIT",  100,  "USD",  "eventId is required")]
    [InlineData("",      "acc-1",  "CREDIT",  100,  "USD",  "eventId is required")]
    [InlineData("evt-1", null,     "CREDIT",  100,  "USD",  "accountId is required")]
    [InlineData("evt-1", "acc-1",  "WIRE",    100,  "USD",  "type must be CREDIT or DEBIT")]
    [InlineData("evt-1", "acc-1",  "CREDIT",  0,    "USD",  "amount must be greater than 0")]
    [InlineData("evt-1", "acc-1",  "CREDIT",  -10,  "USD",  "amount must be greater than 0")]
    [InlineData("evt-1", "acc-1",  "CREDIT",  100,  null,   "currency is required")]
    public async Task PostEvent_InvalidInput_Returns400WithErrorMessage(
        string? eventId, string? accountId, string? type, decimal amount, string? currency, string expectedError)
    {
        var response = await _client.PostAsJsonAsync("/events", new
        {
            eventId,
            accountId,
            type,
            amount,
            currency,
            eventTimestamp = DateTimeOffset.UtcNow,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(expectedError, body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task PostEvent_MissingEventTimestamp_Returns400()
    {
        var response = await _client.PostAsJsonAsync("/events", new
        {
            eventId = Guid.NewGuid().ToString(),
            accountId = Guid.NewGuid().ToString(),
            type = "CREDIT",
            amount = 100m,
            currency = "USD",
            eventTimestamp = (DateTimeOffset?)null,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("eventTimestamp is required", body.GetProperty("error").GetString());
    }

    // ── GET /events/{id} ─────────────────────────────────────────────────

    [Fact]
    public async Task GetEvent_ExistingId_Returns200WithEvent()
    {
        var eventId = Guid.NewGuid().ToString();
        await _client.PostAsJsonAsync("/events", ValidPayload(eventId: eventId));

        var response = await _client.GetAsync($"/events/{eventId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(eventId, body.GetProperty("eventId").GetString());
    }

    [Fact]
    public async Task GetEvent_NonExistentId_Returns404()
    {
        var response = await _client.GetAsync($"/events/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── GET /events?account= ─────────────────────────────────────────────

    [Fact]
    public async Task GetEvents_MissingAccountParam_Returns400()
    {
        var response = await _client.GetAsync("/events");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetEvents_ReturnsOnlyEventsForRequestedAccount()
    {
        var accountA = Guid.NewGuid().ToString();
        var accountB = Guid.NewGuid().ToString();

        await _client.PostAsJsonAsync("/events", ValidPayload(accountId: accountA));
        await _client.PostAsJsonAsync("/events", ValidPayload(accountId: accountA));
        await _client.PostAsJsonAsync("/events", ValidPayload(accountId: accountB));

        var response = await _client.GetAsync($"/events?account={accountA}");
        var events = await response.Content.ReadFromJsonAsync<JsonElement[]>();

        Assert.NotNull(events);
        Assert.Equal(2, events.Length);
        Assert.All(events, e => Assert.Equal(accountA, e.GetProperty("accountId").GetString()));
    }

    [Fact]
    public async Task GetEvents_OrderedByEventTimestampAscending()
    {
        var accountId = Guid.NewGuid().ToString();
        var now = DateTimeOffset.UtcNow;

        // Post in reverse chronological order — oldest timestamp last
        await _client.PostAsJsonAsync("/events", new
        {
            eventId = Guid.NewGuid().ToString(),
            accountId,
            type = "CREDIT",
            amount = 100m,
            currency = "USD",
            eventTimestamp = now.AddHours(-1),
        });
        await _client.PostAsJsonAsync("/events", new
        {
            eventId = Guid.NewGuid().ToString(),
            accountId,
            type = "DEBIT",
            amount = 40m,
            currency = "USD",
            eventTimestamp = now.AddHours(-3),
        });

        var response = await _client.GetAsync($"/events?account={accountId}");
        var events = await response.Content.ReadFromJsonAsync<JsonElement[]>();

        Assert.NotNull(events);
        Assert.Equal(2, events.Length);
        var t0 = events[0].GetProperty("eventTimestamp").GetDateTimeOffset();
        var t1 = events[1].GetProperty("eventTimestamp").GetDateTimeOffset();
        Assert.True(t0 < t1, "Events must be returned in ascending eventTimestamp order");
    }

    // ── GET /health ───────────────────────────────────────────────────────

    [Fact]
    public async Task GetHealth_ReturnsHealthy()
    {
        var response = await _client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("healthy", body.GetProperty("status").GetString());
    }

    // ── Tracing ───────────────────────────────────────────────────────────

    [Fact]
    public async Task PostEvent_OtelActivityActive_TraceIdIsPropagatedToAccountServiceCall()
    {
        // When the Gateway calls the Account Service, an active OTel span must exist.
        // AddHttpClientInstrumentation attaches a traceparent header to any outbound HTTP
        // request while a span is active — this test verifies the span is present so that
        // the header propagation mechanism has something to work with.
        string? capturedTraceId = null;

        _factory.AccountClientMock
            .Setup(c => c.ApplyTransactionAsync(It.IsAny<EventRecord>()))
            .Callback<EventRecord>(_ => capturedTraceId = Activity.Current?.TraceId.ToString())
            .Returns(Task.CompletedTask);

        await _client.PostAsJsonAsync("/events", ValidPayload());

        Assert.NotNull(capturedTraceId);
        Assert.NotEqual("00000000000000000000000000000000", capturedTraceId);
    }

    // ── Factory ───────────────────────────────────────────────────────────

    public sealed class Factory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath =
            Path.Combine(Path.GetTempPath(), $"gw-test-{Guid.NewGuid()}.db");

        public Mock<IAccountServiceClient> AccountClientMock { get; } = new();

        public Factory()
        {
            AccountClientMock
                .Setup(c => c.ApplyTransactionAsync(It.IsAny<EventRecord>()))
                .Returns(Task.CompletedTask);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureServices(services =>
            {
                var dbDescriptor = services.SingleOrDefault(
                    d => d.ServiceType == typeof(DbContextOptions<GatewayDbContext>));
                if (dbDescriptor is not null)
                {
                    services.Remove(dbDescriptor);
                }

                services.AddDbContext<GatewayDbContext>(opts =>
                    opts.UseSqlite($"Data Source={_dbPath}"));

                // Replace the typed HttpClient with a mock so tests are isolated
                // from a running Account Service instance
                var clientDescriptor = services.SingleOrDefault(
                    d => d.ServiceType == typeof(IAccountServiceClient));
                if (clientDescriptor is not null)
                {
                    services.Remove(clientDescriptor);
                }

                services.AddSingleton<IAccountServiceClient>(_ => AccountClientMock.Object);
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
