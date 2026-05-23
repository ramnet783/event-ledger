# Event Ledger — Governing Constitution

## Principles

1. **Spec before code.** Every feature begins as a specification. The spec defines *what* and *why*; the plan defines *how*.
2. **Services are sovereign.** Each service owns its own database, its own data model, and its own process. No shared state.
3. **Correctness over convenience.** Idempotency and out-of-order tolerance are not optional. The system must be correct even when upstream producers are not.
4. **Fail loudly at boundaries, gracefully at runtime.** Validate all external input strictly. Degrade gracefully when downstream services are unavailable.
5. **Observability is not optional.** Every service must emit structured logs, expose a health endpoint, and propagate trace context.
6. **Tests prove behaviour.** Tests target externally observable behaviour (HTTP responses, database state), not implementation internals.
7. **No implicit coupling.** Services communicate only via the defined REST contract. Any future change to the contract must update the spec first.
