# Phase 7: Reporting and administration

Builds on every earlier phase (each module contributes its own figures). Design records: [ADR-0021](adr/0021-reporting-and-bi-feed.md) (reports and BI feed) and [ADR-0022](adr/0022-workflow-designer-and-audit-viewer.md) (workflow designer and audit viewer). Delivered in four slices.

## Built
| Slice | Area | What |
|---|---|---|
| 7a | KPI pack | Reports page with figures for work orders, preventive maintenance, inventory, purchasing, vendors, spaces, bookings, visitors and facilities, for a chosen period (up to 400 days) and optionally one site; each person sees only the areas they may read; weekly trends; "no data" instead of a misleading 0%; CSV export |
| 7b | BI feed | Read-only OData v4 over flat datasets (no free text typed by people) for Power BI, Excel and similar; named access tokens shown once, stored as a hash, with expiry, last use and instant revocation; Basic or `X-Api-Key` access; page-size cap |
| 7c | Workflow designer | Setup, Workflow tab: approval on or off and which priorities skip it; verification on or off and which priorities need it; signature, time and photo on completion. Each publish is a numbered version with a reason, who and when; restoring publishes an old version again; two editors cannot overwrite each other. Eight fixed rules are listed and cannot be changed |
| 7d | Audit and admin | Audit trail page: filter by period, action, record type and id; paging; CSV export (itself audited, capped at 10,000 rows, flagged if truncated). Administration hub of links by permission, new menu entry |
| All | Access and language | `reports.read`, `reports.manage` (Facility Manager and Viewer read; Administrator both); English and Arabic, RTL |

API (under `/api/v1`): `reports/dashboard`, `reports/export.csv`, `reports/feed/tokens[...]`, `reports/feed/datasets`; anonymous `odata/...` (token required); `work-orders/workflow[/history|/publish|/restore]`; `audit`, `audit/actions`, `audit/export.csv`.

## Tests
All suites green at the end of the phase: 478 unit, 20 API, 328 integration, 74 web, 38 browser steps.

- Unit: report arithmetic (percent without denominator, averages, week starts, limits), CSV safety, OData parsing and refusals, workflow rule evaluation and validation (bitmasks, contradictions), audit CSV.
- Integration (real SQL Server): each provider's figures and permission gating, site filter, dataset paging; token lifecycle (hash only, expiry, revoke, last use), anonymous feed answers (same 401 for any wrong secret), no free text in datasets, isolation between organisations; workflow publish, conflict, restore, history append-only (application and database trigger), rules actually changing behaviour, fixed rules unchanged; audit search filters, paging, export, export audited, another organisation sees nothing. The migrator test now also checks the new tables.
- Browser: KPI pack and export; make a feed token, read it with Basic access, wrong and revoked tokens refused; publish and restore workflow versions; administration hub, audit trail, filter and CSV download; accessibility scans of the new screens in English and Arabic (one scrollable table needed keyboard access; fixed).

## Not done / known gaps
- No mean time between failures (needs failure classification), no report builder, saved or scheduled reports, no email of reports, no template editor (customer's decisions for this phase).
- Site filter: figures that cannot be tied to a location cover the whole organisation and say so.
- OData: only date `$filter` on the time column; no `$orderby`, `$expand` or joins; one scope per token (all datasets).
- Workflow rules are global per organisation (not per category or site); fixed rules cannot be relaxed.
- Audit viewer shows what modules chose to record; no retention or archiving of old entries yet; the day filter uses the server's UTC calendar.
- Reports and the audit viewer are online-only.
