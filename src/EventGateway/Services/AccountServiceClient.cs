using System.Net.Http.Json;
using System.Text.Json;
using EventGateway.Models;

namespace EventGateway.Services;

/// <summary>Defines the contract for communicating with the Account Service.</summary>
public interface IAccountServiceClient
{
    /// <summary>Applies a transaction to the Account Service for the given event.</summary>
    /// <param name="ev">The event record to forward.</param>
    /// <returns>A task that completes when the Account Service confirms the transaction.</returns>
    /// <exception cref="HttpRequestException">Thrown when the Account Service returns a non-success status.</exception>
    Task ApplyTransactionAsync(EventRecord ev);

    /// <summary>Retrieves the current balance for an account from the Account Service.</summary>
    /// <param name="accountId">The account identifier.</param>
    /// <returns>The balance response proxied from the Account Service.</returns>
    /// <exception cref="HttpRequestException">Thrown when the Account Service returns a non-success status.</exception>
    Task<JsonElement> GetBalanceAsync(string accountId);
}

/// <summary>
/// HTTP client for the Account Service, wrapped with a Polly circuit breaker and timeout.
/// Registered via IHttpClientFactory — the resilience pipeline is configured in Program.cs.
/// </summary>
public class AccountServiceClient(HttpClient httpClient, ILogger<AccountServiceClient> logger)
    : IAccountServiceClient
{
    /// <inheritdoc cref="IAccountServiceClient.ApplyTransactionAsync"/>
    public async Task ApplyTransactionAsync(EventRecord ev)
    {
        var payload = new
        {
            eventId = ev.EventId,
            type = ev.Type,
            amount = ev.Amount,
            currency = ev.Currency,
            eventTimestamp = ev.EventTimestamp,
        };

        var response = await httpClient.PostAsJsonAsync(
            $"/accounts/{ev.AccountId}/transactions", payload);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            logger.LogWarning(
                "Account Service returned {StatusCode} for event {EventId}: {Body}",
                (int)response.StatusCode,
                ev.EventId,
                body);
            throw new HttpRequestException(
                $"Account Service returned {(int)response.StatusCode}", null, response.StatusCode);
        }
    }

    /// <inheritdoc cref="IAccountServiceClient.GetBalanceAsync"/>
    public async Task<JsonElement> GetBalanceAsync(string accountId)
    {
        var response = await httpClient.GetAsync($"/accounts/{accountId}/balance");

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            logger.LogWarning(
                "Account Service returned {StatusCode} for balance query on {AccountId}: {Body}",
                (int)response.StatusCode,
                accountId,
                body);
            throw new HttpRequestException(
                $"Account Service returned {(int)response.StatusCode}", null, response.StatusCode);
        }

        return (await response.Content.ReadFromJsonAsync<JsonElement>())!;
    }
}
