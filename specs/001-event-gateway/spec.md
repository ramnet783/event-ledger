# Spec 001 — Event Gateway API

## Purpose

The Event Gateway is the public-facing entry point for all transaction event submissions. It validates, deduplicates, stores, and forwards events to the Account Service for balance application.

## User Stories

- **As an upstream system**, I can submit a financial transaction event so that the account balance is updated.
- **As an upstream system**, I can re-submit the same event safely, knowing it will not create a duplicate or alter the balance a second time.
- **As a client**, I can retrieve a single event by its ID to confirm it was received.
- **As a client**, I can list all events for an account in chronological order regardless of arrival order.
- **As an operator**, I can query `/health` to determine if the Gateway and its dependencies are healthy.

## Functional Requirements

### FR-1: Event Submission (`POST /events`)
- Accept JSON body per the Event Payload schema.
- Validate all required fields; return `400 Bad Request` with a descriptive error on failure.
- Detect duplicate `eventId` and return `200 OK` with the original event (idempotent).
- Persist new events to the Gateway's local SQLite database.
- Forward valid, non-duplicate events to the Account Service via `POST /accounts/{accountId}/transactions`.
- If the Account Service is unavailable (circuit open, timeout, or error), return `503 Service Unavailable`.
- Return `201 Created` with the event body on success.

### FR-2: Event Retrieval (`GET /events/{id}`)
- Return `200 OK` with the event if found.
- Return `404 Not Found` if the event does not exist.
- Must work when Account Service is unavailable (reads only from Gateway's local DB).

### FR-3: Account Event Listing (`GET /events?account={accountId}`)
- Return `200 OK` with an array of events ordered by `eventTimestamp` ascending.
- Require the `account` query parameter; return `400` if missing.
- Must work when Account Service is unavailable.

### FR-4: Health (`GET /health`)
- Return `200 OK` with `{ "status": "healthy" }` plus database connectivity check.
- Return `503` if database is unreachable.

## Non-Functional Requirements

- **NFR-1 Structured logging**: JSON format, include `traceId`, `timestamp`, `level`, `service`.
- **NFR-2 Trace propagation**: Generate a W3C `traceparent` header on every inbound request; propagate it to Account Service.
- **NFR-3 Circuit breaker**: Polly circuit breaker on Account Service HttpClient (50% failure ratio over 10s, break for 30s, min throughput 3).
- **NFR-4 Timeout**: 5-second timeout on Account Service calls.

## Acceptance Checklist

- [ ] Duplicate `eventId` returns `200` with original event body
- [ ] Missing required field returns `400` with field-level error message
- [ ] `amount <= 0` returns `400`
- [ ] Unknown `type` returns `400`
- [ ] `GET /events?account=X` returns events sorted by `eventTimestamp`
- [ ] `GET /events/{id}` returns `404` for unknown ID
- [ ] `GET /events/{id}` works when Account Service is down
- [ ] `POST /events` returns `503` when circuit is open
- [ ] Logs include `traceId` on every request
- [ ] `traceparent` header is sent to Account Service
