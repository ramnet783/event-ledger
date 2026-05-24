using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EventGateway.Data;
using EventGateway.Models;
using EventGateway.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace EventGateway.Tests;

public class ResiliencyTests
{
    private static object Payload(string? eventId = null, string? accountId = null) => new
    {
        eventId = eventId ?? Guid.NewGuid().ToString(),
        accountId = accountId ?? Guid.NewGuid().ToString(),
        type = "CREDIT",
        amount = 100m,
        currency = "USD",
        eventTimestamp = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task PostEvent_AccountServiceThrows_Returns503AndEventStoredAsPending()
    {
        var mock = new Mock<IAccountServiceClient>();
        mock.Setup(c => c.ApplyTransactionAsync(It.IsAny<EventRecord>()))
            .ThrowsAsync(new HttpRequestException("connection refused"));

        var eventId = Guid.NewGuid().ToString();

        await using var factory = new FailingFactory(mock);
        var client = factory.CreateClient();

        var postResponse = await client.PostAsJsonAsync("/events", Payload(eventId: eventId));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, postResponse.StatusCode);
        var postBody = await postResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(eventId, postBody.GetProperty("eventId").GetString());

        // GET must work independently — Gateway DB read is always available
        var getResponse = await client.GetAsync($"/events/{eventId}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var getBody = await getResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("PENDING", getBody.GetProperty("status").GetString());
    }

    [Fact]
    public async Task PostEvent_AccountServiceDown_DuplicateSubmissionIsIdempotent()
    {
        // Even when Account Service is down the second identical submission must return
        // the already-stored PENDING event rather than attempting to apply it again.
        var mock = new Mock<IAccountServiceClient>();
        mock.Setup(c => c.ApplyTransactionAsync(It.IsAny<EventRecord>()))
            .ThrowsAsync(new HttpRequestException("connection refused"));

        var payload = Payload();

        await using var factory = new FailingFactory(mock);
        var client = factory.CreateClient();

        await client.PostAsJsonAsync("/events", payload);           // first: 503 + PENDING
        var second = await client.PostAsJsonAsync("/events", payload); // duplicate: 200

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var body = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("PENDING", body.GetProperty("status").GetString());

        // Account Service must only have been called once — the duplicate hits
        // the idempotency check before reaching the forwarding step
        mock.Verify(c => c.ApplyTransactionAsync(It.IsAny<EventRecord>()), Times.Once);
    }

    [Fact]
    public async Task PostEvent_AccountServiceDown_GetEventsStillWorks()
    {
        // Verifies the read path is fully decoupled from Account Service availability
        var mock = new Mock<IAccountServiceClient>();
        mock.Setup(c => c.ApplyTransactionAsync(It.IsAny<EventRecord>()))
            .ThrowsAsync(new HttpRequestException("connection refused"));

        var accountId = Guid.NewGuid().ToString();

        await using var factory = new FailingFactory(mock);
        var client = factory.CreateClient();

        await client.PostAsJsonAsync("/events", Payload(accountId: accountId));
        await client.PostAsJsonAsync("/events", Payload(accountId: accountId));

        var listResponse = await client.GetAsync($"/events?account={accountId}");
        var listBody = await listResponse.Content.ReadAsStringAsync();
        Assert.True(
            listResponse.StatusCode == HttpStatusCode.OK,
            $"Expected 200 OK but got {(int)listResponse.StatusCode}: {listBody}");

        var events = JsonSerializer.Deserialize<JsonElement[]>(listBody);
        Assert.NotNull(events);
        Assert.Equal(2, events!.Length);
    }

    [Fact]
    public async Task PostEvent_AccountServiceFails_MetricNotIncremented()
    {
        // Counter must only fire on successful apply — not on 503 paths
        long captured = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == "event_gateway.events_applied")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>(
            (_, measurement, _, _) => Interlocked.Add(ref captured, measurement));
        listener.Start();
        // Snapshot baseline after starting — parallel tests may have already incremented
        // the global meter. We only care that THIS test's failing call did not increment it.
        long baseline = Volatile.Read(ref captured);

        var mock = new Mock<IAccountServiceClient>();
        mock.Setup(c => c.ApplyTransactionAsync(It.IsAny<EventRecord>()))
            .ThrowsAsync(new HttpRequestException("connection refused"));

        await using var factory = new FailingFactory(mock);
        var client = factory.CreateClient();

        await client.PostAsJsonAsync("/events", Payload());

        Assert.Equal(baseline, Volatile.Read(ref captured));
    }

    [Fact]
    public async Task GetBalance_AccountServiceDown_Returns503WithClearError()
    {
        // Balance is proxied through the Gateway — when AS is unreachable the Gateway must
        // return 503 with a human-readable message rather than a raw connection error.
        var mock = new Mock<IAccountServiceClient>();
        mock.Setup(c => c.ApplyTransactionAsync(It.IsAny<EventRecord>()))
            .ThrowsAsync(new HttpRequestException("connection refused"));
        mock.Setup(c => c.GetBalanceAsync(It.IsAny<string>()))
            .ThrowsAsync(new HttpRequestException("connection refused"));

        var accountId = Guid.NewGuid().ToString();

        await using var factory = new FailingFactory(mock);
        var client = factory.CreateClient();

        var response = await client.GetAsync($"/accounts/{accountId}/balance");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("unavailable", body.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PostEvent_CircuitBreakerOpens_SubsequentCallsRejectedWithout_HittingAccountService()
    {
        // Verifies Polly's circuit breaker actually runs end-to-end:
        // after MinimumThroughput (3) failures with ≥50% failure rate the circuit opens
        // and the 4th+ calls are rejected immediately — the HTTP handler must not be
        // called again once the circuit is open.
        int handlerCallCount = 0;

        await using var factory = new CircuitBreakerFactory(() =>
        {
            Interlocked.Increment(ref handlerCallCount);
            return new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable);
        });
        var client = factory.CreateClient();

        // Make MinimumThroughput calls to open the circuit
        for (int i = 0; i < 3; i++)
        {
            await client.PostAsJsonAsync("/events", Payload());
        }

        int callsAfterThreshold = Volatile.Read(ref handlerCallCount);

        // One more call — should be short-circuited by Polly, handler not invoked
        await client.PostAsJsonAsync("/events", Payload());

        Assert.Equal(callsAfterThreshold, Volatile.Read(ref handlerCallCount));
    }

    // ── Factories shared by resiliency tests ─────────────────────────────

    private sealed class FailingFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath =
            Path.Combine(Path.GetTempPath(), $"gw-fail-{Guid.NewGuid()}.db");

        private readonly Mock<IAccountServiceClient> _mock;

        public FailingFactory(Mock<IAccountServiceClient> mock)
        {
            _mock = mock;
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

                var clientDescriptor = services.SingleOrDefault(
                    d => d.ServiceType == typeof(IAccountServiceClient));
                if (clientDescriptor is not null)
                {
                    services.Remove(clientDescriptor);
                }

                services.AddSingleton<IAccountServiceClient>(_ => _mock.Object);
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

    [Fact]
    public async Task PostEvent_ExceedsRateLimit_Returns429()
    {
        // Use a tight limit (2/window) so the test doesn't send 60+ real requests
        var mock = new Mock<IAccountServiceClient>();
        mock.Setup(c => c.ApplyTransactionAsync(It.IsAny<EventRecord>()))
            .Returns(Task.CompletedTask);

        await using var factory = new RateLimitFactory(permitLimit: 2, mock);
        var client = factory.CreateClient();

        var first = await client.PostAsJsonAsync("/events", Payload());
        var second = await client.PostAsJsonAsync("/events", Payload());
        var third = await client.PostAsJsonAsync("/events", Payload());

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.True(third.Headers.Contains("Retry-After"), "429 response must include Retry-After header");
        var body = await third.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("retry", body.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    private sealed class RateLimitFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath =
            Path.Combine(Path.GetTempPath(), $"gw-rl-{Guid.NewGuid()}.db");

        private readonly int _permitLimit;
        private readonly Mock<IAccountServiceClient> _mock;

        public RateLimitFactory(int permitLimit, Mock<IAccountServiceClient> mock)
        {
            _permitLimit = permitLimit;
            _mock = mock;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["RateLimit:PermitLimit"] = _permitLimit.ToString(),
                    ["RateLimit:WindowSeconds"] = "60",
                });
            });

            builder.ConfigureServices(services =>
            {
                var dbDescriptor = services.SingleOrDefault(
                    d => d.ServiceType == typeof(DbContextOptions<GatewayDbContext>));
                if (dbDescriptor is not null)
                    services.Remove(dbDescriptor);

                services.AddDbContext<GatewayDbContext>(opts =>
                    opts.UseSqlite($"Data Source={_dbPath}"));

                var clientDescriptor = services.SingleOrDefault(
                    d => d.ServiceType == typeof(IAccountServiceClient));
                if (clientDescriptor is not null)
                    services.Remove(clientDescriptor);

                services.AddSingleton<IAccountServiceClient>(_ => _mock.Object);
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && File.Exists(_dbPath))
                File.Delete(_dbPath);
        }
    }

    // Keeps AccountServiceClient + Polly pipeline intact; replaces only the primary
    // HttpMessageHandler so the circuit breaker state machine exercises real code paths.
    private sealed class CircuitBreakerFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath =
            Path.Combine(Path.GetTempPath(), $"gw-cb-{Guid.NewGuid()}.db");

        private readonly Func<HttpResponseMessage> _responseFactory;

        public CircuitBreakerFactory(Func<HttpResponseMessage> responseFactory)
        {
            _responseFactory = responseFactory;
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

                services.AddHttpClient<IAccountServiceClient, AccountServiceClient>()
                    .ConfigurePrimaryHttpMessageHandler(() => new FakeHttpHandler(_responseFactory));
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

        private sealed class FakeHttpHandler : HttpMessageHandler
        {
            private readonly Func<HttpResponseMessage> _factory;

            public FakeHttpHandler(Func<HttpResponseMessage> factory) => _factory = factory;

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(_factory());
        }
    }
}
