# ADR-0033: Assignee and vendor suggestions, and early warnings (Phase 12a, 12b)
Status: Proposed. Builds on [ADR-0030](0030-ai-assistance.md).

## Decisions
- **Suggestions and early warnings are arithmetic, not a model, and need no AI switch.** Both are worked out from data the system already holds, are cheap, run in the customer's own database and say exactly why. They therefore work for every organisation, including those that keep AI off, and they cannot send work-order text to any outside provider. (A model could be added later behind the same endpoints; nothing here prevents it.)
- **Suggested people** (`GET /work-orders/{id}/suggestions`, needs `workorders.assign`). Candidates are people who may work on orders. From the last 180 days of orders they finished (completed, verified or closed, not cancelled, not closed as duplicate or not required) we count how many were on the same asset, in the same category and in the same place; the score is `5 × asset + 3 × category + 1 × place − 2 × open jobs`. The top three are shown with those counts and their open jobs. Ties are broken by fewer open jobs, then name. Nothing is assigned.
- **Suggested vendors**: through the vendors module's own lookup (`IVendorLookup.OffersAsync`), contracts in force today that cover the order's asset or place, for vendors that can receive work; the contract's target for the order's priority is shown. Ranked by shorter resolution target, then more past jobs of the same kind. The Work module never reads vendor tables.
- **Early warnings** (`GET /work-orders/predictions`, needs `workorders.manage` or `workorders.assign`). Two rules:
  1. *Repairs coming closer together*: at least four repair orders on one asset in the last year; the mean of the last two gaps is at most half the median of the earlier gaps (and the earlier median is at least a day). The hint shows the numbers and an expected date (last repair + the recent gap).
  2. *A service meter running faster*: for assets whose preventive-maintenance schedule is driven by a running-total meter, the daily rate in the last 14 days compared with the 30 days before; a ratio of 1.5 or more is a hint, with the days left to the next service at the new pace. A fall in the reading (a replaced meter) is skipped, not counted. At least three days of readings recently and seven before are required.
- Thresholds are constants in `Assignment.cs` and `Predictive.cs`, covered by unit tests at their boundaries. They are not tenant settings yet.

## Consequences
- A person with a lot of history on an asset is always suggested first; someone new never is. That is intended (it is a prompt, not a policy) but dispatchers should know it.
- Statistics on few events are weak. The minimums keep the hints rare; they do not make them right. Each line shows its numbers so a person can judge.
- Meter hints cover only meters that drive a schedule; other meters are not scanned.
- The seasonal swing in a meter (cooling in summer) will trigger the pace hint every spring. No seasonal model.
