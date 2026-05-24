namespace EventGateway.Models;

/// <summary>Persisted record of a financial transaction event received by the Gateway.</summary>
public class EventRecord
{
    /// <summary>Gets or sets the unique identifier for this event — used as the idempotency key.</summary>
    public string EventId { get; set; } = string.Empty;

    /// <summary>Gets or sets the account this event belongs to.</summary>
    public string AccountId { get; set; } = string.Empty;

    /// <summary>Gets or sets the event type: CREDIT or DEBIT.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Gets or sets the transaction amount. Must be greater than zero.</summary>
    public decimal Amount { get; set; }

    /// <summary>Gets or sets the ISO 4217 currency code (e.g. USD).</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>Gets or sets when the event originally occurred in the upstream system.</summary>
    public DateTimeOffset EventTimestamp { get; set; }

    /// <summary>Gets or sets optional JSON-serialized metadata from the upstream system.</summary>
    public string? Metadata { get; set; }

    /// <summary>Gets or sets when the Gateway received this event.</summary>
    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Gets or sets the processing status: PENDING (saved, not yet applied) or APPLIED (Account Service confirmed).</summary>
    public string Status { get; set; } = "PENDING";
}
