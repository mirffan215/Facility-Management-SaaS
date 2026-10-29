# ADR-0031: Work orders raised from alerts (Phase 11a)
Status: Proposed. Builds on [ADR-0010](0010-work-orders-and-requests.md), [ADR-0026](0026-compliance-and-permits.md) and [ADR-0027](0027-energy-and-device-readings.md).

## Decisions
- **A cause, not a copy.** A work order raised because of something in another module (an energy alarm, a failed inspection) records what kind of thing and which one (`SourceKind`, `SourceId`) with a **unique index** per organisation. Asking again for the same cause returns the same work order, also when two callers ask at once (the second one's insert is refused and it reads the first). This is what makes the daily and hourly checks safe to repeat and to run twice.
- **A narrow door.** Other modules ask the Work module through one contract in the building blocks (`IWorkOrderFromSource`): ensure a work order for this cause, or find the ones already raised. There are no cross-schema joins. The Work module creates the order with its normal rules (numbering under the same lock, workflow settings, the response clock, the audit entry, the creation event).
- **Off unless chosen, and always possible by hand.** An alarm *rule* or an *obligation* carries a switch ("also raise a work order"), off by default. With it on, the check raises the work order when the alarm is raised or the inspection fails. With it off, a manager (`energy.manage`, `compliance.manage`) can raise one from the alarm or the failed result with a button. Both give the same work order.
- **Raised in a person's name.** A work order needs a requester. The check raises it in the name of the person who switched the rule on (energy) or recorded the failure (compliance); a manual raise is in the name of the manager who clicked.
- **The alarm or failure comes first.** The alarm and the failed result are saved before any work order is attempted. If raising the work order fails, it is logged and the alarm stands; a manager can raise the order by hand. It is not retried by the next check, to avoid raising work for an alarm that was cleared in between.
- **Place needed.** A work order needs a place. A rule on a meter with no place, or an obligation with no place or asset, cannot have the switch on (refused with a message).
- **Overdue permits** get a warning, not a work order: a permit still in force past its planned end is told to the people who can issue permits and to the people who issued and started it, once a day until it is closed.
- **Lease documents.** Leases can carry files (up to 20 of 15 MB) with the same checks as inspection certificates: type from content, the malware scan hook, audited downloads, removal only with a reason.

## Consequences
- A failure that happened before the switch was turned on does not retroactively get a work order (use the button).
- If an alarm raises, clears and raises again, it is a new cause and a new work order.
- Work orders raised this way are ordinary: they can be cancelled, merged by hand, closed; their link back stays on the alarm or failure.
