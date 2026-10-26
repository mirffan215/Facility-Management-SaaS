# ADR-0010: Requests and work orders (Phase 2a)
Status: Proposed

## Decisions
- **One record from report to closure.** A request and the job that fulfils it are the same `WorkOrder`; a request is simply a work order
  in `Requested`. This avoids a second entity to keep in step and gives the requester one place to follow progress.
- **Own module, own schema** (`Fms.Work`, schema `work`, own migrations history), like Facilities. It never joins across schemas: places and
  assets are validated and named through `IFacilityLookup`, people through `IUserDirectory` (both defined in BuildingBlocks, implemented by
  the owning module). Work order rows hold plain ids for location, asset and people.
- **Fixed lifecycle with per-tenant switches** (chosen over a data-driven workflow engine for now).
  `Requested -> Approved -> Assigned -> InProgress <-> OnHold -> Completed -> Verified -> Closed`, plus `Cancelled`. A tenant can turn off
  *approval* (requests start `Approved`) and *verification* (`Completed` can be closed directly). Emergencies are never held for approval.
  The rules are pure functions (`WorkOrderRules`) used by both the service and the API, so "what may I do?" has one answer; the API returns
  the allowed actions with each work order and the screen shows exactly those buttons.
- **Reopen has no status of its own.** Reopening finished work (manager, with a reason) returns it to `Assigned` (or `Approved` if nobody was
  assigned) and clears completion and verification; the history keeps the earlier outcome. Closed work is otherwise read-only.
- **Controls from the financial-controls playbook, applied here.** Maker-checker: nobody approves or rejects their own request, whatever
  permissions they hold. Segregation of duties: whoever was assigned or completed the work cannot verify it. Only the assignee (or a
  manager) works the job. Only dispatchers raise emergency or planned work, because those skip approval or schedule work.
- **Visibility.** `workorders.read` sees every work order of the tenant; a person with only `workorders.request` sees and acts on only the
  requests they raised (others are 404, not 403). Internal comments are invisible to the latter and they cannot write them.
- **Evidence is append-only.** `WorkOrderEvents` (one line per action: who, when, from/to, reason) and `WorkOrderComments` cannot be updated
  or deleted, enforced by EF and by database triggers. Every action also writes an audit event in the same transaction.
- **Numbering.** `WO-000001...` per tenant, allocated under an application lock held for the creating transaction so parallel requests
  queue rather than collide; a unique index is the backstop and an ambiguous commit is verified before retry.
- **Least privilege for reporters.** A requester may not browse the asset register, but must be able to report against an asset (often by
  scanning its label). `GET /work-orders/assets` returns a thin id/code/name/place for them, so asset export, documents and meters stay
  closed. Scanning a label as a requester opens the report form with the asset already selected.
- **Closure.** Completing needs a closure code (fixed list) and notes; reasons are required for reject, hold, reopen and cancel.

## Consequences
- Changing the lifecycle shape (new states or per-tenant steps) is a code change and a migration of existing rows' meaning; revisit with
  the workflow engine in the Reporting & Admin phase if tenants need it.
- (Phase 2b now publishes `workorder.event` messages; see ADR-0011.) Earlier text: no outbox messages were published, and an unconsumed message type would
  only fill the dead-letter list.
- Assignment is to one person. Teams, crews, skills and shifts are not modelled yet.
