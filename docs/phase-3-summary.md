# Phase 3: Preventive maintenance

Builds on [2a](phase-2a-summary.md)–[2d](phase-2d-summary.md). Design record: [ADR-0014](adr/0014-preventive-maintenance.md). Delivered in three slices.

## Built
| Slice | Area | What |
|---|---|---|
| 3a | Templates | Name (English/Arabic), instructions, priority, category, estimated minutes, checklist; archive/restore (a template used by an active schedule cannot be archived) |
| 3a | Schedules | For one asset or every asset of a class; fixed or floating or meter-only; interval in days/weeks/months/years; start date; lead days; optional technician; archive/restore |
| 3a | Generation | Worker creates preventive work orders ahead of time, once per plan, safe with several workers; checklist copied from the template; assigned when the schedule names someone; no SLA clocks; notifications as for any new work order |
| 3a | Completion | Finishing or cancelling the work order records the outcome (completed on time/late, skipped) and moves the plan to its next date; reopen/complete again does not move it twice |
| 3a | Forecast | Calendar list by month: work orders already created, projected visits, overdue marked, filter by asset class, 30–365 days; "Create due work orders now" for managers |
| 3b | Meter triggers | "Every N units on meter X" alone or with a calendar rule; baselines, replaced-meter handling, missing-meter flag, estimated due date on the forecast |
| 3c | Compliance | On-time rate, late/skipped/overdue/upcoming counts overall, by month, by template, by asset class; planned vs other work share; bars with numeric tables |
| All | Access | `pm.read`, `pm.manage`; **Preventive** menu item; English and Arabic (RTL, Hijri/Gregorian month names) |

API (under `/api/v1/pm`): `templates`, `schedules`, `schedules/{id}/plans`, `forecast`, `compliance`, `run`.

## Tests
- Unit: recurrence maths (month-end clamping, fixed vs floating, projection limits) – 12.
- Integration (real SQL Server) – 12 new: validation and permissions; creation ahead of time exactly once (also under 5 parallel runs); assignment; checklist copy; no SLA; audit marker; fixed schedule advance and on-time flag; reopen does not double-advance; floating counts from completion and cancel = skipped; asset-class schedules follow assets in and out; forecast (open, projected, overdue, limits); meter interval, reset of the baseline at service, combined calendar+meter, missing meter, replaced meter, forecast estimate; compliance counts and planned share; tenant isolation and row-level security on the four new tables.
- Web: translation key checks for the new screens (en/ar identical); browser journey step: create a template and a schedule, generate, see it on the forecast, open the compliance tab, and axe scans of all four tabs in English and Arabic.

## Not done / known gaps
- **Condition-based triggers** (from sensor or inspection readings) are not built; only calendar and meter. (`condition` appears in the early data-model sketch.)
- **Calendar view is a list by month**, not a drag-and-drop grid; no rescheduling of a single visit (change the schedule, or finish/cancel the work order).
- **Skipping or deferring one visit** is done by cancelling its work order (recorded as skipped); there is no "defer with reason" shortcut.
- **One zone for "today"** (tenant default work calendar), not per site.
- **Meter matched by name**; no schedule-level choice between several meters.
- **No change history of schedules beyond the audit log**; compliance is by due date, so a changed schedule shows its past as it was.
- **Technician load levelling** (spreading visits across people or days) and **crew/skill assignment** are not part of this phase.
- Not tried at large scale: generation visits every active plan each pass (tested with a handful; thousands of plans are expected to be fine but untested).
- One browser test (offline, session expired) failed once in a full run and passed on rerun; it is unrelated to this phase and looks timing-sensitive.
