namespace AccountService.Models;

/// <summary>Represents a financial account — auto-created on first transaction.</summary>
public class Account
{
    /// <summary>Gets or sets the unique account identifier.</summary>
    public string AccountId { get; set; } = string.Empty;

    /// <summary>Gets or sets when the account was first created (first transaction received).</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Gets or sets the transactions applied to this account.</summary>
    public ICollection<Transaction> Transactions { get; set; } = new List<Transaction>();
}
