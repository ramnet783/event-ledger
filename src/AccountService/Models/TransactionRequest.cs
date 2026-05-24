namespace AccountService.Models;

/// <summary>Inbound payload for POST /accounts/{accountId}/transactions.</summary>
/// <param name="EventId">Upstream event ID — used as the idempotency key.</param>
/// <param name="Type">Transaction type: CREDIT or DEBIT.</param>
/// <param name="Amount">Transaction amount — must be greater than zero.</param>
/// <param name="Currency">ISO 4217 currency code (e.g. USD).</param>
/// <param name="EventTimestamp">When the event originally occurred in the upstream system.</param>
public record TransactionRequest(
    string EventId,
    string Type,
    decimal Amount,
    string Currency,
    DateTimeOffset EventTimestamp);
