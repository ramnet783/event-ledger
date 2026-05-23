namespace AccountService.Models;

public record TransactionRequest(
    string EventId,
    string Type,
    decimal Amount,
    string Currency,
    DateTimeOffset EventTimestamp
);
