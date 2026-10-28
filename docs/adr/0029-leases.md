# ADR-0029: Leases (Phase 9d)
Status: Proposed. Builds on [ADR-0016](0016-vendors-contracts-purchasing.md) (the pattern for contract dates and alerts).

## Decisions
- **A lease record, not a lease accounting system.** It holds the premises, the other party, the term, notice dates, the rent terms and a computed rent schedule. It issues no invoices, posts nothing to accounting and does **not** do IFRS 16 or ASC 842 measurement (right-of-use asset, lease liability, discounting). The schedule is what the lease says is due; finance does the rest.
- **Two kinds:** *Occupied* (the organisation rents from a landlord) and *Let out* (the organisation is the landlord). The same record serves both; reports show payable and receivable rent separately.
- **Lifecycle:** *Draft → Active → Terminated*. *Run out* is not stored: an active lease past its end date is shown as run out until someone extends or terminates it, because only a person knows whether it was renewed. A draft can be deleted with a reason; anything that was in force is kept.
- **Rent schedule is computed, not stored.** One instalment falls due at the start of each period (monthly, quarterly, half-yearly, yearly). The rent **rises by a percentage every N months, compounded and rounded to cents at each step**; periods starting inside the rent-free months cost nothing; a last period that does not fill the frequency is pro-rated by days. Computing keeps one source of truth: change a term and the schedule follows. The rule is covered by unit tests with worked figures.
- **Agreed terms are locked once active.** Dates, kind, currency, rent, frequency, rise, rent-free months and deposit cannot be edited on an active lease (the API refuses); the lease is **extended** (new end date and a reason) or **terminated** (a reason and date). Both are audited with the old and new values. Descriptive fields, notice periods, the break clause and warning lead time can still be edited.
- **Deadlines and alerts.** Three dates matter: the date by which notice of non-renewal must be given (end minus notice days), the break notice date (break date minus notice days) and the end. Managers are warned when one comes within the lease's lead time (default 90 days), when it changes to the next date, weekly while it is near, and when the lease has run out. The alert key includes the date, so extending a lease restarts the warnings. A worker runs twice a day.
- **Amendments by replacement.** A rent review is not modelled as an amendment. To change rent, end the lease and create a new one, or extend; this is a stated limit.

## Consequences
- No lease documents are attached yet (the storage pieces exist in Compliance and can be reused).
- The monitor records the last warning on the lease row, which changes its version; a screen left open across a warning will ask the user to reload before saving.
- Rent dates use the server's UTC calendar; no currency conversion; no service charge, VAT or indexation to published indices.
