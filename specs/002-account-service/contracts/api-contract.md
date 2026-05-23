# API Contract — Account Service

## POST /accounts/{accountId}/transactions

### Request
```json
{
  "eventId": "evt-001",
  "type": "CREDIT",
  "amount": 150.00,
  "currency": "USD",
  "eventTimestamp": "2026-05-15T14:02:11Z"
}
```

### Responses
| Status | Condition |
|--------|-----------|
| 201 Created | Transaction applied |
| 200 OK | Duplicate `eventId` — returns original transaction |
| 400 Bad Request | Validation failure |

## GET /accounts/{accountId}/balance
```json
{ "accountId": "acct-123", "balance": 150.00, "currency": "USD" }
```
| Status | Condition |
|--------|-----------|
| 200 OK | Balance returned |
| 404 Not Found | Account not found |

## GET /accounts/{accountId}
```json
{
  "accountId": "acct-123",
  "createdAt": "2026-05-15T14:02:11Z",
  "transactions": [...]
}
```

## GET /health
```json
{ "status": "healthy", "database": "ok" }
```
