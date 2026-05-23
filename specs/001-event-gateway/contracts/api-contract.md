# API Contract — Event Gateway

## POST /events

### Request
```json
{
  "eventId": "evt-001",
  "accountId": "acct-123",
  "type": "CREDIT",
  "amount": 150.00,
  "currency": "USD",
  "eventTimestamp": "2026-05-15T14:02:11Z",
  "metadata": { "source": "mainframe-batch", "batchId": "B-9042" }
}
```

### Responses
| Status | Condition |
|--------|-----------|
| 201 Created | New event accepted and applied |
| 200 OK | Duplicate `eventId` — returns original event |
| 400 Bad Request | Validation failure — body: `{ "error": "..." }` |
| 503 Service Unavailable | Account Service unreachable — body: `{ "error": "...", "eventId": "..." }` |

## GET /events/{id}
| Status | Condition |
|--------|-----------|
| 200 OK | Event found |
| 404 Not Found | Event not found |

## GET /events?account={accountId}
| Status | Condition |
|--------|-----------|
| 200 OK | Array of events sorted by `eventTimestamp` ascending |
| 400 Bad Request | Missing `account` query param |

## GET /health
```json
{ "status": "healthy", "database": "ok" }
```
