# Phase 5: Vendors, contracts, purchasing, accounting connection and vendor portal

Builds on [3](phase-3-summary.md) and [4](phase-4-summary.md). Design records: [ADR-0016](adr/0016-vendors-contracts-purchasing.md), [ADR-0017](adr/0017-accounting-integration.md), [ADR-0018](adr/0018-vendor-portal-and-guest-identity.md). Delivered in four slices.

## Built
| Slice | Area | What |
|---|---|---|
| 5a | Vendors | Register with English/Arabic names, tax and registration numbers, contacts, status (Active / On hold / Blocked with a reason), archive and restore |
| 5a | Contracts | AMC / SLA / service / supply contracts: period, value, renewal notice, coverage (site, asset class or asset), service targets per priority, documents, termination with reason; computed status (Upcoming, Active, Expiring, Expired) and expiry notices (in-app and bilingual email) |
| 5a | Work orders | Give a work order to a vendor, optionally under a contract; shows whether the contract is in force and covers the place or asset, and its response/resolution targets (informational) |
| 5b | Purchasing | Request → approval by amount (three levels, nobody approves their own) → purchase order (price tolerance against the approved total) → goods receipt, which puts stock into Inventory at the order price exactly once |
| 5c | Accounting connection | `IErpAdapter` interface, outbox-fed idempotent export queue, retries with back-off then dead-letter, manual retry, reference written back, reconciliation, fake adapter (opt-in). The Dynamics 365 Finance adapter itself is not built |
| 5d | Vendor portal | Staff invite a vendor's person; they sign in with their own account, accept, and see only their vendor's orders (can confirm new ones), jobs (can write messages), contracts and contract files |
| All | Access and language | `vendors.read/manage`, `purchasing.read/request/approve1-3/order/receive/manage`, `integration.read/manage`, `portal.use` (system role **Vendor**); menus **Vendors**, **Purchasing**, **Accounting**, and **My business** for vendor people; English and Arabic, RTL |

API (under `/api/v1`): `vendors[...]`, `vendors/{id}/contacts`, `vendors/{id}/portal-access`, `vendors/{id}/invitations[...]`, `vendors/{id}/portal-users/{userId}/active`, `contracts[...]`, `work-orders/{id}/vendor[...]`, `purchasing/settings|requests|orders[...]`, `integration/erp/...`, and the vendor-only `portal/{me|contracts|orders|work-orders}` plus `portal/invitations/accept`.

## Tests
All suites green at the end of the phase: 325 unit, 20 API, 267 integration, 68 web, 31 browser steps.

- Unit: contract rules (status, coverage, targets, validity) 14; purchasing rules (approval level, tolerance, quantities) 22; ERP retry/back-off and adapter behaviour 14.
- Integration (real SQL Server): vendors and contracts (permissions, validation, versions, coverage, documents, expiry notices, isolation between tenants by API and by row-level security); the purchasing flow (levels, self-approval refused, tolerance, over-receiving refused, receipt reaches stock once even if delivered twice, append-only receipts); the export queue (idempotency, superseding, claiming by two workers, retry, dead-letter, write-back, reconcile); the portal (invitation stored as a hash only, revoke, expiry, one organisation per sign-in, every API route refused to a vendor user except the portal, vendor-versus-vendor isolation for orders, jobs, contracts and files, access ending when a person is deactivated or the vendor is blocked). The migrator test now also checks the new tables are under row-level security.
- Browser (full stack through the production web tier): create a vendor and a contract, give a work order under it; a technician asks, a different person approves, the order is made and received; turn on the accounting connection and watch the worker send a vendor and the reference come back; invite a vendor person, accept the link, see only their job, send a message, be refused the buyer's screens; accessibility scans in English and Arabic.

## Not done / known gaps
- **No Dynamics 365 adapter yet.** Until a sandbox exists there is only the interface and the fake adapter, which forgets everything on restart. Only changes after the connection is turned on are sent (no back-fill). Nothing flows from accounting back into the system except the references.
- **No three-way match, invoices or payments**; no supplier price lists, quotes or catalog; one currency per order and no conversion.
- **Contract targets are not enforced**: vendor SLA clocks, penalties and performance scoring are not built. Expiry notices go to people with `vendors.manage`; there is no renewal workflow.
- **Portal**: the invitation link is shown once and passed on by the inviter (not emailed); one organisation per sign-in; no self-registration, vendor documents with expiry (insurance, licences), invoices or payment status; vendors cannot complete or reassign work. The portal is online-only.
- Receipts cannot be reversed (correct with a stock adjustment).
