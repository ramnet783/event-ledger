namespace AccountService.Models;

public class Account
{
    public string AccountId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public ICollection<Transaction> Transactions { get; set; } = new List<Transaction>();
}
