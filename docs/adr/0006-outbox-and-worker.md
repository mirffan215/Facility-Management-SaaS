# ADR-0006: SQL outbox and worker
Status: Proposed

## Decision
- Background work and integration events go through a **transactional outbox** table. `IOutbox.Enqueue` adds a row to the caller's
  unit of work, so the message exists if and only if the business change committed.
- A worker host claims due messages with one atomic `UPDATE ... OUTPUT` using `UPDLOCK, READPAST` (many workers, no double
  claim, no blocking) and a lease. A crashed worker's lease expires and the message is retried.
- Delivery is **at least once**. Handlers must be idempotent and may use the message id as an idempotency key.
- Each message runs in a fresh DI scope with the **sender's tenant** set, so tenant filters and row-level security apply to
  handler code exactly as in a request. Only claiming and bookkeeping use platform scope.
- Failures back off exponentially (default 5 s doubling, capped at 30 min) and become dead letters after 8 attempts. Dead letters
  are kept for inspection; finished messages are purged after 14 days.
- The same database is the queue on-prem and in SaaS: no extra infrastructure for on-prem customers. A broker (Azure Service Bus)
  can be added later behind the same `IOutbox`/handler contract for high-volume SaaS; that needs its own ADR.

## Consequences
- No ordering guarantee across messages. Handlers needing order must carry a sequence and tolerate reordering.
- Throughput is bounded by SQL Server; fine for facility-management workloads, to be measured before the first large tenant.
- Poison messages stop at the dead-letter state; an operator tool to inspect and replay them is still to be built.
