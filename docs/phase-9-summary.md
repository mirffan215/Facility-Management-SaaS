# Phase 9: Compliance and permits, energy, projects, leases

Design records: [ADR-0026](adr/0026-compliance-and-permits.md), [ADR-0027](adr/0027-energy-and-device-readings.md), [ADR-0028](adr/0028-projects.md), [ADR-0029](adr/0029-leases.md). Delivered in four slices (9a to 9d). **AI features were deferred** at the customer's choice and are not part of this phase.

## Built
| Slice | Module | What |
|---|---|---|
| 9a | Compliance and permits | Obligations (inspections and certificates) with category, place or asset, repeat interval, owner and warning lead time; append-only completions with result, certificate dates and evidence files; status *Scheduled, Compliant, Due soon, Overdue, Failed*. Permits to work with configurable types (duration limit, precautions, isolation, exclusive) and a fixed lifecycle *Requested, Approved, Issued, Started, Closed* (or Rejected, Cancelled) with separation of roles and an append-only event trail; exclusive permits cannot overlap in one place. Warnings by worker and in-app notices. |
| 9b | Energy and utilities | Meters (cumulative or interval, roll-over), append-only readings with voids, entry by hand, by CSV (check, then import) and by **devices that post with their own revocable secret**; consumption by day and cost from dated tariffs; alarm rules (high daily use, no reading) that raise and clear once; dashboard figures and data feed. |
| 9c | Projects | Projects with phases, tasks, milestones and budget lines; status with reasons; work orders and purchase orders linked by number and charged to a budget category; budget vs committed vs spent by category with an over-budget flag; completion blocked while work is open. |
| 9d | Leases | Leases rented or let out: parties, premises, term, notice and break dates, renewal option, rent with frequency, yearly rise, rent-free months and deposit; computed rent schedule; locked terms once active, extension and termination with reasons; warnings before notice, break and end dates and when a lease has run out. |
| All | Access and language | `compliance.read/manage`, `permits.read/request/approve/issue/work`, `energy.read/record/manage`, `projects.read/manage`, `leases.read/manage`; Facility Manager holds all, Technician the permit and reading rights it needs, Viewer reads. Four new license modules (`compliance`, `energy`, `projects`, `leases`). English and Arabic, RTL. |

API (under `/api/v1`): `compliance/...`, `permits/...`, `energy/...` (and anonymous `energy/ingest` with a device secret), `projects/...`, `leases/...`. All appear in the OpenAPI document.

## Fix found on the way
Four earlier modules (Visitors, Spaces, Bookings, Reporting) did not call the base model builder, so their tenant filter indexes and query filters were not applied the way the other modules' were. This was found while adding the new modules, fixed, and each got a migration (`TenantFilterIndexes`). Row-level security in the database was never affected; the application-side filter was the gap, and tenant-isolation tests pass for all modules.

## Tests
Added in this phase: unit tests for obligation status and permit rules, energy arithmetic (roll-over, intervals, tariffs, alarm evaluation), project money, and lease dates and rent (worked figures); integration tests against real SQL Server for each module (roles, validation, concurrency, append-only history by application and by database trigger, the daily/hourly checks including no double notification, reports and data feed without free text, and that **another organisation sees and changes none of it**); the migrator test counts the new tables and their row-level security; the browser journey has a step per module with accessibility scans in English and Arabic.

## Not done / known gaps
- **Alarms, failed inspections and overdue permits notify people but never open work orders**; permits past their planned end have no worker alert (only a dashboard figure).
- **Leases:** no accounting, invoicing, IFRS 16 or ASC 842; no document attachments; rent reviews are modelled by extension or a new lease, not amendments; no VAT, service charge or index-linked rent; no currency conversion.
- **Projects:** no dependencies, critical path or Gantt; percent done is tasks done, not effort; costs are read live from work orders and purchase orders.
- **Energy:** no BACnet, Modbus or MQTT (a gateway posts over HTTPS); consumption days use the server's UTC calendar; no carbon or emissions factors.
- **Compliance:** no electronic signatures or offline use for permits; no jurisdiction template library.
- The daily checks record the last warning on the row, which changes its version, so a form left open across a warning asks the person to reload.
- As before: the Helm chart, Bicep template and on-premise scripts have not been deployed; rate limits are per process.
- **AI** (predictive maintenance, triage) is deferred to a later phase.
