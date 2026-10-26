# Phase 2a: Requests and work orders

Decisions agreed with the user: slice Phase 2 (this is 2a); fixed lifecycle with per-tenant switches; per-site calendars for SLA (2b, not built).
Design record: [ADR-0010](adr/0010-work-orders-and-requests.md).

## Built
| Area | What |
|---|---|
| Module | `Fms.Work` (schema `work`, own migrations, five tables under row-level security; history and comments also behind append-only triggers) |
| Requests | Raise a request (title, details, place and/or asset, category, priority), edit/cancel while it waits, `WO-` numbers per tenant |
| Lifecycle | approve/reject, assign, start, hold/resume, complete (closure code + notes), verify, close, reopen, cancel; rules in `WorkOrderRules` |
| Controls | no self-approval; verifier is not the worker; only assignee/manager works the job; emergency/planned only by dispatchers; reasons where required |
| Visibility | read-all vs own-requests-only; internal comments hidden from requesters |
| Setup | request categories (default priority, Arabic names), switches for approval and verification (Setup > Work orders) |
| API | `/api/v1/work-orders` (+ `/{id}`, action verbs, `/comments`, `/assets`, `/assignees`, `/categories`, `/settings`) |
| Web | list with views (open, mine, assigned to me, overdue, all) and filters, report form (scan-aware), detail with allowed actions, history, comments; English and Arabic/RTL |
| Roles | Facility Manager: all `workorders.*`; Technician: read, request, work; Requester: request; Viewer: read |

## Tests
- Unit: lifecycle matrix, maker-checker, segregation, toggles, overdue, numbering (34 new).
- Integration/API (real SQL Server): full lifecycle across roles, own-requests visibility and internal comments, toggles, validation, categories,
  filters and search, 12 parallel creations get distinct numbers, cross-tenant (read, act, assign, foreign location/asset), row-level security and
  append-only enforced by the database, migrator covers the new schema. Mutation checks (removing the self-approval rule, removing the
  visibility filter) make these tests fail.
- Browser (full stack): requester scans a label, request approved by an administrator, assigned, done and completed by a technician, verified and
  closed by someone else, requester sees the outcome but not the internal note; accessibility scan of the new screens in English and Arabic.

## Not done / known gaps (Phase 2 continues)
- **SLA engine, calendars, escalation alerts and notifications: done in 2b** (see [phase-2b-summary](phase-2b-summary.md)). The manual due date still only drives the "overdue" flag.
- Notifications: added in 2b.
- **Checklists, labour/material/contractor cost, time tracking, signatures, photos** (2c). Attachments on work orders are not built.
- **Technician PWA with offline sync** (2d): the web app is responsive but works online only.
- **Teams/crews/skills, recurring requests, email/phone intake, satisfaction survey**: not started.
- Assignment is to one person; the list of assignable people is everyone whose roles carry `workorders.work`.
- Editing from the screen covers title, details and priority; place, asset, category and due date are API-editable only.
- Search is `LIKE`; no full-text index. Lists are capped at 100 per page.
