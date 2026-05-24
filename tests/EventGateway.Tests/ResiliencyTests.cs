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

    // ── Factory shared by all resiliency tests ────────────────────────────

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
}
