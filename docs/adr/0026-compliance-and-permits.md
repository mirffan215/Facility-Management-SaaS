# ADR-0026: Compliance and permits to work (Phase 9a)
Status: Proposed. Builds on [ADR-0005](0005-database-tenant-isolation.md) and [ADR-0011](0011-sla-and-notifications.md).

## Decisions
- **Two parts in one module.** *Obligations* are things that must be done on a cycle and proved (a fire alarm test, a lift certificate). *Permits to work* are authorisations for one dangerous job in one place for a limited time.
- **Obligations.** An obligation has a category, an optional place or asset, a repeat interval in months (or none for a one-off), a lead time for warnings and an optional owner. **Completions are append-only** (application and database trigger): the next due date is the last passing completion's date plus the interval, or the certificate's own valid-until date when it has one. Status is worked out from dates and the last result: *Scheduled*, *Compliant*, *Due soon*, *Overdue*, *Failed*. A failed result shows the obligation as *Failed* and does not move the due date until a later completion passes. Evidence (certificates, reports) is stored through the storage abstraction under the tenant's path, up to 15 MB and ten files per completion, and is downloaded with no caching.
- **Permits: a fixed lifecycle with configurable types.** The states and who may move them are fixed in code: *Requested, Approved, Rejected, Issued, Started, Closed, Cancelled*. What varies is the **permit type**: a code, English and Arabic names, the longest allowed duration, the precautions to confirm, whether isolation (lock-out) must be recorded, and whether the permit is exclusive in its place.
- **Segregation of duties is not configurable.** The approver cannot be the requester; the issuer cannot be the requester; the person who starts the work cannot be the issuer; closing needs a statement that the area is safe. Every step is an append-only event with person and time.
- **Exclusive permits.** Two exclusive permits cannot be in force at the same place at overlapping times. The check runs under an application lock per place so two requests cannot both pass. A permit that is past its planned end but not closed **still blocks**, because the work may still be going on.
- **Warnings.** A daily worker (twice a day) tells the owner, or the managers when there is no owner, when an obligation becomes due soon, overdue or failed, then again weekly while overdue. A stable source id (obligation, state, day) means a rerun after a crash does not notify twice. Permits send in-app notices at the main steps (requested, approved and so on).

## Consequences
- A failed inspection or an overdue permit **does not open a work order** by itself; people are told and decide. Linking to the work module is future work.
- Permits that are past their planned end raise a figure on the dashboard but no worker alert.
- Not provided: electronic signatures on permits, offline permit handling, a jurisdiction-specific permit template library.
