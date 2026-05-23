# Spec 002 — Account Service

## Purpose

The Account Service manages account state: balances and transaction history. It is an internal service called only by the Event Gateway.

## User Stories

- **As the Gateway**, I can apply a transaction (CREDIT or DEBIT) to an account so that the balance is updated.
- **As the Gateway**, I can re-apply the same transaction safely, knowing the balance will not be double-counted.
- **As the Gateway**, I can query an account's current balance.
- **As the Gateway**, I can retrieve account details with recent transactions.
- **As an operator**, I can query `/health` to verify the Account Service is healthy.

## Functional Requirements

### FR-1: Apply Transaction (`POST /accounts/{accountId}/transactions`)
- Accept `{ eventId, type, amount, currency, eventTimestamp }`.
- Create the account record if it does not already exist.
- Detect duplicate `eventId` (idempotency) and return `200 OK` with the existing transaction.
- Persist the transaction to the Account Service's local SQLite database.
- Return `201 Created` with the transaction on success.
- Return `400` for invalid `type`, non-positive `amount`.

### FR-2: Balance (`GET /accounts/{accountId}/balance`)
- Compute balance as `sum(CREDIT amounts) - sum(DEBIT amounts)`.
- Result is correct regardless of transaction arrival order (computed from stored data).
- Return `200 OK` with `{ "accountId": "...", "balance": 0.00, "currency": "USD" }`.
- Return `404` if account not found.

### FR-3: Account Details (`GET /accounts/{accountId}`)
- Return account metadata and the 20 most recent transactions ordered by `eventTimestamp` descending.
- Return `404` if account not found.

### FR-4: Health (`GET /health`)
- Return `200 OK` with database connectivity status.

## Non-Functional Requirements

- **NFR-1 Structured logging**: JSON format with `traceId`, `timestamp`, `level`, `service`.
- **NFR-2 Trace propagation**: Extract `traceparent` from inbound headers and use that trace context for all logs and spans.

## Acceptance Checklist

- [ ] CREDIT increases balance; DEBIT decreases balance
- [ ] Balance is correct regardless of transaction arrival order
- [ ] Duplicate `eventId` returns `200` without changing balance
- [ ] Account is auto-created on first transaction
- [ ] `GET /accounts/{id}/balance` returns `404` for unknown account
- [ ] Logs include `traceId` from propagated `traceparent`
