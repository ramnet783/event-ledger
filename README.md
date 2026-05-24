# Event Ledger

A spec-driven financial transaction system built on two ASP.NET Core 8 minimal-API microservices.

```
                         ┌──────────────────────────┐
Upstream systems  ──────▶│     Event Gateway        │────▶ Account Service
                         │  :5000 (public-facing)   │       :5001 (internal)
                         └──────────────────────────┘
```

## Services

| Service | Port | Responsibility |
|---------|------|----------------|
| **Event Gateway** | 5000 | Validate, deduplicate, persist, and forward transaction events |
| **Account Service** | 5001 | Apply transactions; maintain account balances |

Both services use SQLite for persistence. No external infrastructure is required.

### How they interact

When a client submits an event to the Gateway, the Gateway validates it, writes it to its own database with `Status = PENDING`, then forwards it to the Account Service. The Account Service applies the transaction and returns 201. The Gateway updates the event to `Status = APPLIED` and responds 201 to the caller.

If the Account Service is unavailable, the event remains `PENDING` in the Gateway's database and the caller receives a 503 with the `eventId` — no data is lost. Read operations (`GET /events/*`, `GET /accounts/{id}/balance`) are handled independently: event reads go directly to the Gateway's local database, and balance queries are proxied through the Gateway with a graceful 503 if the Account Service is unreachable.

---

## Assumptions

| Assumption | Rationale |
|------------|-----------|
| SQLite for persistence | Keeps the setup self-contained — no external database required to run or test. In production this would be replaced with a network-attached database. |
| No authentication | The Gateway would sit behind an API gateway or load balancer enforcing auth (mTLS, JWT, API key) in production. Adding it at the application layer was intentionally deferred to keep scope focused on transactional and resiliency behaviour. |
| In-process rate limiter | The fixed-window limiter is per-pod. In a horizontally scaled deployment, a shared Redis-backed limiter or API gateway layer would be needed for a true global limit. |
| PENDING events not auto-retried | Events that fail to reach Account Service stay `PENDING`. A background reconciliation job would pick these up in production. Callers can retry with the same `eventId` safely. |
| Gateway crash between apply and status update | If the Gateway crashes after Account Service applies a transaction but before updating status to `APPLIED`, the event remains `PENDING` in the Gateway DB. This is the two-phase commit problem — resolving it fully requires an outbox pattern or saga, which is out of scope here. |
| Currency not ISO-validated | The `currency` field accepts any non-empty string. ISO 4217 validation would be added in production. |

---

## Prerequisites

| Path | Requirement |
|------|-------------|
| Docker Compose | [Docker Desktop](https://www.docker.com/products/docker-desktop/) (Mac/Windows) or Docker Engine + Compose plugin (Linux) |
| Local (.NET CLI) | [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8) |

No other external dependencies (database, message broker, etc.) are required.

---

## Quick Start — Docker Compose

```bash
docker compose up --build
```

The gateway is available at `http://localhost:5000` and the account service at `http://localhost:5001`.

Both services expose a `/health` endpoint and are monitored by Docker health checks (10 s interval, 3 retries). The Gateway waits for the Account Service to pass its health check before starting.

A Jaeger all-in-one container also starts automatically. Open **http://localhost:16686** in your browser, select `event-gateway` or `account-service` from the Service dropdown, and click **Find Traces** to see distributed traces across both services.

### Submit an event

```bash
curl -s -X POST http://localhost:5000/events \
  -H 'Content-Type: application/json' \
  -d '{
    "eventId": "evt-001",
    "accountId": "acc-abc",
    "type": "CREDIT",
    "amount": 500.00,
    "currency": "USD",
    "eventTimestamp": "2024-01-15T10:00:00Z"
  }' | jq
```

### Check the balance

Via the Gateway (graceful 503 if Account Service is down):

```bash
curl -s http://localhost:5000/accounts/acc-abc/balance | jq
```

Or directly on the Account Service:

```bash
curl -s http://localhost:5001/accounts/acc-abc/balance | jq
```

### Retrieve an event

```bash
curl -s http://localhost:5000/events/evt-001 | jq
```

---

## Quick Start — Local (.NET CLI)

**Prerequisites:** .NET 8 SDK

```bash
# Terminal 1 — Account Service
cd src/AccountService
dotnet run

# Terminal 2 — Event Gateway
cd src/EventGateway
AccountService__BaseUrl=http://localhost:5001 dotnet run
```

The gateway's default Account Service URL is `http://localhost:5001`. Override with the
`AccountService__BaseUrl` environment variable (or `appsettings.json`).

---

## Running Tests

```bash
dotnet test
```

53 integration tests — all run against real (in-memory temp-file) SQLite databases with no
external dependencies.

Test coverage includes:

- **Validation**: all required fields, type constraints, amount bounds
- **Idempotency**: duplicate `eventId` returns the original record; Account Service is not called again
- **Out-of-order events**: balance computed from the algebraic sum regardless of arrival order
- **Resiliency**: Account Service unavailable → 503 + PENDING status; read path (`GET /events*`) still returns 200
- **Circuit breaker**: Polly circuit opens after 3 failures; subsequent calls are rejected without hitting Account Service
- **Rate limiting**: 4th request within the configured window returns 429
- **Trace propagation**: OTel `Activity.Current` is active when the Gateway calls the Account Service

---

## API Reference

### Event Gateway (`POST /events`)

| Field | Type | Required | Notes |
|-------|------|----------|-------|
| `eventId` | string | yes | Global idempotency key |
| `accountId` | string | yes | Target account |
| `type` | string | yes | `CREDIT` or `DEBIT` |
| `amount` | decimal | yes | Must be > 0 |
| `currency` | string | yes | ISO 4217 (e.g. `USD`) |
| `eventTimestamp` | ISO 8601 | yes | When the event occurred upstream |
| `metadata` | object | no | Arbitrary key-value pairs |

**Status codes:** `201 Created` (applied), `200 OK` (duplicate), `400` (validation), `429` (rate limit exceeded — includes `Retry-After` header with seconds until window resets), `503` (Account Service unavailable)

### Event Gateway — read endpoints

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/events/{id}` | Retrieve event by ID (works when Account Service is down) |
| `GET` | `/events?account={id}` | List events for account, ordered by `eventTimestamp` ascending |
| `GET` | `/accounts/{id}/balance` | Proxy to Account Service balance; returns `503` with a clear message if Account Service is unreachable |
| `GET` | `/health` | Gateway + database health check |

### Account Service

| Method | Path | Description |
|--------|------|-------------|
| `POST` | `/accounts/{id}/transactions` | Apply transaction (idempotent on `eventId`) |
| `GET` | `/accounts/{id}/balance` | Current balance (credits − debits) |
| `GET` | `/accounts/{id}` | Account with 20 most recent transactions |
| `GET` | `/health` | Database health check |

---

## Architecture Decisions

### Idempotency at two layers

Every `eventId` is the primary key in the Gateway's `Events` table and is indexed with a UNIQUE
constraint in the Account Service's `Transactions` table. Duplicates are caught at the application
layer (a `FindAsync` before insert) with a `DbUpdateException` fallback for concurrent races.
This means re-submitting an event is always safe — neither service applies it twice.

### Gateway stores before forwarding

The Gateway persists the event with `Status = PENDING` before it calls the Account Service.
If the Account Service is unavailable (timeout, circuit open, or error) the event is not lost —
it stays as PENDING in the Gateway's database and the caller receives a `503` with the `eventId`.
A retry or reconciliation job can pick up PENDING events later. This is a documented gap: the
balance is not updated until the event transitions to APPLIED.

### Circuit breaker (Polly 8)

The Gateway's outbound `HttpClient` carries a Polly resilience pipeline:

- **Timeout**: 5 seconds per call
- **Circuit breaker**: opens after ≥ 50 % failure rate over ≥ 3 calls in a 10-second window;
  stays open for 30 seconds

When the circuit is open the Gateway returns 503 immediately without attempting the call,
protecting the Account Service from thundering-herd retries during an outage.

### Rate limiting

`POST /events` is protected by a fixed-window rate limiter (ASP.NET Core 8 built-in). The default
limit is **60 requests per minute** per client IP; requests that exceed the window return
`429 Too Many Requests`. The limit is configurable via `RateLimit:PermitLimit` and
`RateLimit:WindowSeconds` in configuration, which lets tests apply a tight limit (e.g. 2) without
sending 60+ real requests.

### Graceful degradation

`GET /events/{id}` and `GET /events?account=` read only from the Gateway's local database and
are never blocked by Account Service availability.

`GET /accounts/{id}/balance` is proxied through the Gateway via the same Polly resilience
pipeline used for event forwarding. When the Account Service is down or the circuit is open,
the Gateway returns `503 Service Unavailable` with `{"error": "Account Service unavailable — balance cannot be retrieved"}` rather than a raw connection error, giving callers a consistent
error contract regardless of which operation they are performing.

### Structured logging + distributed tracing

Both services use Serilog with `CompactJsonFormatter`. Every log line includes `traceId` and
`spanId` (stamped by a custom `ActivityEnricher` from `System.Diagnostics.Activity.Current`),
so logs and OTel traces can be correlated without a separate join. The Gateway also exposes a
custom OTel counter `event_gateway.events_applied` tagged by transaction type.

### SQLite for persistence

SQLite was chosen to keep the setup self-contained — no external database process is needed to
run or test the system. The schema is created via `EnsureCreated()` at startup. In production
you would swap the connection string for a network-attached database.

Note: SQLite's EF Core provider does not support `DateTimeOffset` in SQL `ORDER BY` clauses.
Both services load rows into memory after the `WHERE` filter and sort client-side, which is
acceptable at the expected data volumes.

### No authentication (documented decision)

The services have no authentication layer. In a production system, the Event Gateway would sit
behind an API gateway or load balancer that enforces authentication (e.g. mutual TLS, JWT
bearer, or API-key header validation). Adding auth at the application layer was intentionally
deferred to keep the scope focused on the transactional and resiliency behaviour specified in
the assignment.

---

## Project Structure

```
EventLedger.sln
├── src/
│   ├── EventGateway/       # Public-facing gateway service
│   └── AccountService/     # Internal balance service
├── tests/
│   ├── EventGateway.Tests/ # Integration tests (20 tests)
│   └── AccountService.Tests/ # Integration tests (14 tests)
├── specs/                  # Spec-kit API contracts and requirements
├── postman_collection.json # Ready-to-import Postman collection
└── docker-compose.yml
```

---

## Postman Collection

Import `postman_collection.json` into Postman. The collection targets `http://localhost:5000`
(gateway) and `http://localhost:5001` (account service) and includes example requests for all
endpoints.
