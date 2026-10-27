# ADR-0022: Workflow designer and audit viewer (Phase 7c, 7d)
Status: Proposed. Builds on [ADR-0010](0010-work-orders-and-requests.md) and [ADR-0012](0012-work-order-evidence.md).

## Decisions
- **Rules inside fixed limits** (the customer's decision), not a free state-machine designer. An organisation chooses: whether approval is required and which priorities skip it; whether verification is required and for which priorities; and whether completion needs a signature, time spent or a photo. The lifecycle itself is code, so it can be tested and reasoned about.
- **Fixed rules that no setting can change** (listed on the screen so nobody wonders why a switch is missing): nobody approves their own request; whoever verifies is never the worker; required checklist items before completion; closure code and notes; emergencies skip approval; closed work orders are locked; hold, reject, reopen and cancel need a reason; every change is audited.
- **Versioned, append-only history.** Each publish is a numbered version (rules, note, who, when) in `WorkflowVersion`, protected by the append-only trigger like other history. **Restoring is publishing an old version again** as a new version with a reason; history is never edited. Publishing needs the version the editor saw (`expectedVersion`) so two administrators cannot overwrite each other unnoticed (409). The older three-switch settings screen still works and writes a version too, so history is complete.
- **Rules apply to work from now on.** A work order already in progress keeps the path it was created under where that matters (initial status is decided at creation); verification and evidence rules apply when the step is reached.
- **Priorities are stored as a bitmask** in settings for cheap evaluation; the API and history use named priorities. Validation rejects contradictions (waiving approval while approval is off; waiving every priority, which is the same as turning it off).
- **The audit viewer is search and export only.** `audit.read` can filter by period (default 7 days, maximum 366), actor, action, record type and id, page through results, and export to CSV (capped at 10,000 rows with a "truncated" flag; formula characters neutralised). The export itself is audited. Nothing on the screen or in the API can change or delete an entry. Other organisations' entries are not visible (global query filter plus row-level security, covered by tests).
- **An Administration hub** is a page of links, each shown only if the person holds the permission for its destination. It adds no new permission and no new data.

## Consequences
- No custom statuses, transitions or per-category flows, no template editor, no scheduled report emails (customer's decision for this phase).
- Verification-by-priority and approval-waiver rules cannot depend on category, site or cost; add if asked.
- The audit trail records what the modules chose to record; the viewer cannot show what was never written. Retention and archiving of old entries is a Phase 8 hardening topic.
