namespace AccountService.Models;

/// <summary>A single CREDIT or DEBIT transaction applied to an account.</summary>
public class Transaction
{
    /// <summary>Gets or sets the internal surrogate key for this transaction record.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Gets or sets the upstream event ID — used as the idempotency key (UNIQUE index).</summary>
    public string EventId { get; set; } = string.Empty;

    /// <summary>Gets or sets the account this transaction belongs to.</summary>
    public string AccountId { get; set; } = string.Empty;

    /// <summary>Gets or sets the transaction type: CREDIT or DEBIT.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Gets or sets the transaction amount — always positive; sign is determined by Type.</summary>
    public decimal Amount { get; set; }

    /// <summary>Gets or sets the ISO 4217 currency code (e.g. USD).</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>Gets or sets when the event originally occurred — used for balance ordering, not arrival order.</summary>
    public DateTimeOffset EventTimestamp { get; set; }

    /// <summary>Gets or sets when this transaction was received by the Account Service.</summary>
    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
}
