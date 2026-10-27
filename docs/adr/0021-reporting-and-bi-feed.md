# ADR-0021: Reporting and the BI feed (Phase 7a, 7b)
Status: Proposed. Builds on [ADR-0005](0005-database-tenant-isolation.md) (isolation) and [ADR-0018](0018-vendor-portal-and-guest-identity.md) (secret addresses instead of accounts).

## Decisions
- **A fixed KPI pack, not a report builder** (the customer's decision). Each module contributes its own figures through `IReportProvider` (in BuildingBlocks); a separate `reporting` module only assembles them. No module reads another's tables, so there are no cross-schema joins and each provider answers only what its own permission allows. A person sees the sections for the areas they may read, nothing else.
- **Filters:** a period (at most 400 days, default the last 30), optionally one site. A site narrows by the locations under it (`IFacilityLookup.LocationsUnderAsync`); figures that cannot be tied to a site are marked as covering everything. Percentages without a denominator are shown as "no data", never as 0 or 100.
- **CSV export** of the same figures, with spreadsheet formula characters neutralised at the start of any text cell.
- **BI feed = read-only OData v4 over a small set of datasets.** Each module offers flat, typed datasets through `IDatasetCatalog` / `IDatasetProvider`. The feed contains **no free text typed by people** (titles, notes, names), only ids, codes, enums, numbers and times, so connecting a BI tool cannot leak narrative or personal data. Supported: service document, `$metadata`, `$top`, `$skip`, `$select`, `$count`, a date `$filter` on the dataset's time column only, `@odata.nextLink`. Everything else is refused with a clear error rather than ignored, because a silently ignored `$filter` gives wrong numbers. Page size is capped (1000).
- **Revocable tokens.** `reports.manage` makes a named token (`fmsf_` plus 256 random bits), **shown once**; only its SHA-256 is stored. A token has an expiry date. A token can be revoked and then stops working immediately; last-use time is recorded. The feed accepts Basic authentication (the secret as the password, which Power BI and Excel support) or `X-Api-Key`. The anonymous routes answer 401 for any missing, wrong or revoked secret, with the same body. A token belongs to one organisation and sees only that organisation's rows (row-level security applies, as for any request). Creating and revoking are audited.
- **Large tables are read through the module's own database context**, never the reporting one, so the same query filters and row-level security apply as in the screens.

## Consequences
- No ad-hoc report design, saved reports, scheduled report emails or charts beyond the pack's own (a deliberate scope decision; the datasets let a BI tool do that).
- No mean-time-between-failures figure yet (needs failure classification on work orders).
- OData is deliberately minimal; a client that sends `$filter` on other columns, `$orderby` or `$expand` is told so. Revisit if a customer needs more.
- A token has a single scope (read all datasets the organisation has). Per-dataset scopes can be added without changing the URLs.
