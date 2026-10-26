# ADR-0016: Vendors, contracts and purchasing (Phase 5a, 5b)
Status: Proposed. Builds on [ADR-0010](0010-work-orders-and-requests.md) and [ADR-0015](0015-inventory.md).

## Decisions
- **Two modules, two schemas** (`Fms.Vendors` / `vendors`, `Fms.Purchasing` / `purchasing`), each with its own migrations and row-level security on every table. Modules talk through contracts in BuildingBlocks (`IVendorLookup`, `IInventoryCatalog`, `IWorkOrderLookup`), never across schemas.
- **Vendors** have a code, English/Arabic name, tax and registration numbers, contacts, a status (`Active`, `OnHold`, `Blocked`, with a reason) and archive/restore. A blocked or archived vendor cannot be given work or orders.
- **Contracts** are AMC / SLA / service / supply agreements with a period, value, renewal notice, **coverage** (a site, an asset class or one asset) and per-priority **service targets** (response and resolution minutes). Status is computed from dates (`Upcoming`, `Active`, `Expiring`, `Expired`) or `Terminated` (with a reason). Contract documents are stored through the storage abstraction, type-checked from content, hashed and tenant-scoped.
- **Expiry alerts.** A background monitor tells people with `vendors.manage` once when a contract enters its renewal-notice window and once when it has ended; each is recorded on the contract so it is not repeated.
- **Work given to a vendor.** A work order may carry an assigned vendor and optionally a contract. Whether the contract is in force and covers the work order's place or asset is shown; the contract's targets for that priority are shown. **They are informational**: Phase 5 does not run SLA clocks against vendor contracts.
- **Purchasing flow** (the customer's decision): request → approval by amount → purchase order → goods receipt. Approval levels come from per-tenant limits (defaults 5,000 / 50,000); `purchasing.approve1/2/3` each include the lower levels. **A requester can never approve their own request**, whatever their permissions. The order is issued from an approved request; its total may exceed the approved total by at most the tenant's tolerance (default 10%). Over that, it needs a new approval.
- **Goods receipt** is append-only (code and database trigger). Quantities cannot exceed what was ordered. Stock lines reach Inventory through the outbox message `purchasing.goods_received`, handled idempotently by reference `GR:{receiptId}` at the order's unit price, so a repeat delivery does not double-count and an Inventory outage does not lose a receipt.
- **Three-way match** (order, receipt, vendor invoice) is out of scope: invoices and payment belong to the accounting system.

## Consequences
- One currency per order; amounts are compared as given, with no conversion.
- No supplier price lists, quotes comparison or catalog yet; prices are typed per request line.
- Receipts cannot be reversed; a mistake is corrected by a stock adjustment, which has its own approval.
