# ADR-0035: Permits and alert-raised jobs on the technician's phone (Phase 12d)
Status: Proposed. Builds on [ADR-0013](0013-offline-sync.md) and [ADR-0031](0031-work-orders-from-alerts.md).

## Decisions
- **The offline bundle carries the permits tied to each job** (requested, approved, issued or started; not closed, rejected or cancelled) when the caller may read permits, with their type, place, planned window and status as of the download. The Work module learns of them through `IWorkPermitLookup`, answered by the Compliance module; it never reads the permit tables.
- **Permits are read-only on the phone and never queued.** Approving, issuing, starting and closing a permit are safety records that depend on the current state and on other people's signatures; replaying them hours later from a queue could start work under a permit that has since been cancelled. The page shows when the status was fetched, says plainly that it may be out of date and that permits are changed only online (with a link to the permit when online), and warns, without blocking, when a permit for the job has not been started.
- **A job raised by an alert says so** (`sourceKind` on work orders, shown on the job page and the work order page: "Raised automatically: an energy alarm").
- **The AI summary is offered on the job page when online** (`ai.use`, which technicians have, and only when the organisation has summaries on). Offline the button is not shown. The assignee/vendor suggestions are a dispatcher's tool and are not in the phone app.

## Consequences
- A permit fetched in the morning can be wrong by noon; the warning text is the control, not a technical block. Starting a job is not blocked by an unstarted permit because many jobs start with the permit being issued on site.
- The bundle grows by a few small rows per job.
