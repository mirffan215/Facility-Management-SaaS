# ADR-0017: Accounting integration (Phase 5c)
Status: Proposed. Builds on [ADR-0006](0006-outbox-and-worker.md) and [ADR-0016](0016-vendors-contracts-purchasing.md).

## Decisions
- **Interface first.** Everything the system knows about the accounting system sits behind `IErpAdapter` (BuildingBlocks): `SendAsync` (idempotent upsert of a vendor, purchase order or goods receipt) and `VerifyAsync` (read back for reconciliation). The first real adapter will be Dynamics 365 Finance through its **documented OData data entities only; never its database**. It is deferred until a sandbox exists (the customer's decision), so this phase ships the contract and a **fake adapter**.
- **The fake adapter is opt-in.** It keeps its records in memory per process and is selectable only when `Erp:AllowFakeAdapter=true` (tests, e2e, demos). It is refused otherwise, so a production tenant cannot silently "sync" into a void.
- **Outbox then queue.** A change in Vendors or Purchasing writes an `erp.export` outbox message in the same transaction. A handler turns it into an `ErpExportItem` with idempotency key `{Entity}:{EntityId}:{Revision}`; a newer revision supersedes an older one still waiting. A worker claims due items (`UPDLOCK, READPAST` with a lease, so several workers never send the same item), sends them and records the result.
- **Retries.** A failed send retries with a doubling delay up to 8 attempts, then moves to `DeadLetter`. People with `integration.manage` can retry a dead item by hand. Errors shown are the adapter's message, never stack traces or credentials.
- **References come back.** On success the adapter's reference is written back to the vendor or order (`erp.vendor.synced`, `erp.order.synced` messages, idempotent) and shown on screen.
- **Reconciliation.** `VerifyAsync` compares what the adapter holds with what was sent and records `Matched` / `Mismatch` / `Missing` with the time checked.
- **Settings.** One row per tenant: on/off, adapter name, company in the accounting system. Turning it on does not back-fill earlier records; only later changes are sent.

## Consequences
- Because the fake adapter is per process, reconciling through the API against records the worker sent is meaningless; the end-to-end test checks the reference and status only.
- Customer, invoice and payment flows from the accounting system into this one are not part of Phase 5.
