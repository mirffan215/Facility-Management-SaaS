# ADR-0028: Projects (Phase 9c)
Status: Proposed. Builds on [ADR-0010](0010-work-orders-and-requests.md) and [ADR-0016](0016-vendors-contracts-purchasing.md).

## Decisions
- **A project is a plan, a budget and the work charged to it.** It has a code, dates, a manager, an optional place, a currency, phases, tasks (to do, in progress, blocked, done), milestones and budget lines by category (design, construction, equipment, furniture, professional fees, contingency, other). Status moves *Planning, Active, On hold, Completed, Cancelled*; on hold and cancelled need a reason. **A project cannot be completed while a task or milestone is open.**
- **Cost comes from the modules that already hold it.** A project does not copy money. A person links a work order or a purchase order **by its number** and the category it is charged to. Projects asks the Work and Purchasing modules for the figures through two small contracts in the building blocks (`IWorkCostLookup`, `IPurchaseOrderLookup`); there are no cross-schema joins. A work order's cost is the total of its recorded costs; a purchase order is **spent as goods arrive and committed for what is still to come**; a cancelled order is neither.
- **Currency.** Totals add only items in the project's currency. An item in another currency is listed as "not counted" with the reason, never converted silently.
- **One home for each item.** A work order or purchase order can be linked to one project at a time (a unique index on active links). Unlinking needs a reason and keeps the record.
- **Budget vs committed vs spent** is shown per category and in total; going over budget is flagged, not blocked.
- **Closed projects are frozen**: no further plan, budget or link changes.
- **No scheduling engine.** No task dependencies, critical path, resource levelling or Gantt chart; the plan is a list with dates.

## Consequences
- Percent done is the share of tasks done, not effort-weighted.
- A work order's cost changes after the fact show in the project's figures at once (they are read live), so a closed project's numbers can move if an old work order is corrected.
- No project approvals or stage gates, and no export to a project accounting system.
