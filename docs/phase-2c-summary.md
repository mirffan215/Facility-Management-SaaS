# Phase 2c: Checklists, time, costs, signatures and photos

Builds on [Phase 2a](phase-2a-summary.md) and [2b](phase-2b-summary.md). Design record: [ADR-0012](adr/0012-work-order-evidence.md).

## Built
| Area | What |
|---|---|
| Checklists | Templates (Setup > Checklists, English and Arabic names, required lines, reorder) copied onto a work order; answered as Done / Failed (with a note) / Not applicable; **completion blocked while a required line is unanswered** |
| Time | Entries with day, minutes, note; hourly rate frozen per entry; void with reason |
| Costs | Material / contractor / other lines with supplier or PO reference, quantity, unit cost; void with reason; summary (labour, materials, contractor, other, total) in the tenant's currency |
| Signatures | Drawn on a pad (finger, pen, mouse), technician and customer, with printed name; optional setting to require the technician's signature before completion |
| Photos and files | Upload from the camera or disk (before / after / other), content-checked, audited downloads, removal with reason, thumbnails behind the sign-in |
| Settings | Currency, labour rate, require completion signature (Setup > Work orders) |
| Plumbing | File sniffing and scanning moved to `BuildingBlocks.Storage`, shared by Facilities and Work |

## Tests
- Unit: who may record and when, the time/cost windows, rounding of lines and labour (27 new).
- Integration (real SQL Server): checklist lifecycle and completion gate; template changes do not alter copies; time and cost recording, validation,
  rate freezing, rounding, voids, windows (late receipts yes, after verification no); signature rules including required signature; photo content
  checking, visibility, downloads, removal, closed-order locking; audit trail; per-tenant isolation; row-level security and append-only triggers
  on every ledger table, tried with raw SQL.
- Browser (full stack): a technician refused completion until the checklist is answered, then records time and a cost (total checked), adds a photo,
  draws a signature and completes; accessibility scans of every new tab and screen in English and Arabic.

## Not done / known gaps
- **Not offline.** Everything here needs a connection; Phase 2d (technician PWA with offline sync) is next.
- **No timers** (start/stop clock) for time, no separate rate per person or skill, one currency per tenant.
- **No ERP posting** of costs, no budgets or approval limits on cost; cost lines are free text (no stock or vendor links yet).
- **Checklist lines are pass/fail only** (no numeric readings or choice lists), and are attached manually (no default checklist per category or asset class).
- **Signatures need a pointing device**; no typed-acknowledgement alternative. Drawn signatures are not cryptographic.
- **Thumbnails are full-size images**; no server-side resizing. Malware scanning is still a seam.
- Time and cost screens show staff only; there is no customer-facing report or PDF job sheet yet.
