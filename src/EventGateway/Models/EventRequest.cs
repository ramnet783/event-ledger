namespace EventGateway.Models;

/// <summary>Inbound payload for POST /events.</summary>
/// <param name="EventId">Unique identifier for the event — used as the idempotency key.</param>
/// <param name="AccountId">The account this event belongs to.</param>
/// <param name="Type">Transaction type: CREDIT or DEBIT.</param>
/// <param name="Amount">Transaction amount — must be greater than zero.</param>
/// <param name="Currency">ISO 4217 currency code (e.g. USD).</param>
/// <param name="EventTimestamp">When the event originally occurred in the upstream system.</param>
/// <param name="Metadata">Optional additional context from the upstream system.</param>
public record EventRequest(
    string? EventId,
    string? AccountId,
    string? Type,
    decimal Amount,
    string? Currency,
    DateTimeOffset? EventTimestamp,
    Dictionary<string, object?>? Metadata);
