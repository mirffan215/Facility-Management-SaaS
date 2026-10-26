# ADR-0011: Service levels and notifications (Phase 2b)
Status: Proposed

## Decisions
- **What an SLA measures.** Two clocks per work order, both in *working minutes* of a calendar: **response** (until work starts) and **resolution**
  (until it is completed). They start when the work is *authorised* (approval, or creation for emergencies and tenants without approval),
  not when it is reported: time spent waiting for a decision is the organisation's, not the technician's. **Starting work is the response.**
  Completing stops the resolution clock; verification and closure are not on the clock.
- **On hold stops the resolution clock.** Putting work on hold records the working minutes left; resuming restores exactly that many from
  the resume moment. The response clock is unaffected (work can only be held after it has started). Reopening does not restart the clocks:
  a reopened order that is past its deadline stays breached.
- **Working time, per site.** A *calendar* is a time zone, one opening interval per weekday (or closed) and holidays. A site follows one
  calendar; a site with none follows the tenant's default calendar; with none configured, every minute counts. A work order uses the
  calendar of the site its location belongs to, **fixed when the clocks start** (later calendar edits do not move running deadlines).
  Arithmetic is done in UTC per day, so daylight-saving changes cannot add or remove working minutes. Weekends are just closed days
  (Friday/Saturday for Saudi sites); Hijri holidays are entered by date.
- **Policies.** Targets (response and resolution minutes) per priority. The tenant's default policy covers everything; a policy for a category
  replaces it for that category. Policy changes apply to work whose clocks start afterwards. A priority change while clocks run recomputes
  the deadlines from the original start.
- **States.** `None, OnTrack, AtRisk (75% of the current resolution stretch used), Paused, Met, Breached`, computed by pure functions
  (`SlaRules`), never stored, so list, detail and monitor agree. A late response makes the order breached even if it is finished in time.
- **Monitor and escalation.** A worker job (every minute) finds clocks that need an alert and raises each alert **once** (flags on the work
  order, set in the same transaction as the outbox message): response breached, resolution at risk, resolution breached. Alerts go to
  dispatchers (holders of `workorders.assign`) and the assignee. Each tenant is processed in its own scope.
  Escalation is therefore *notification* in this phase; automatic re-assignment or multi-level escalation chains are not built.
- **Notifications go through the outbox.** A lifecycle action enqueues `workorder.event` in the same transaction as the change; a handler decides
  recipients (pure `NotificationPlanner`), and the `INotificationSink` writes in-app notices and queues one `email.send` message per notice.
  A notice is unique per (event, person), so at-least-once delivery cannot double-notify; an email is queued in the same transaction as its
  notice, so a mail outage retries (with the outbox's backoff and dead-lettering) without losing or repeating anything. The person who acted
  is never notified of their own action; requesters hear about their own requests only, not internal deadlines.
- **Language.** Notices store a type and data; the web app renders them in the reader's language. Emails are bilingual (English then Arabic)
  because the sender does not know the reader's language. Per-user language and channel preferences are a later addition.
- **Email behind an interface.** `IEmailSender` with `None` (default; logs that a mail was not sent, never its address or text) and `Smtp`
  (built-in client, TLS on by default, password from configuration/secret store). Microsoft Graph or a transactional provider can replace
  it without touching callers. Configuration is validated at startup.
- **Worker identity.** The worker registers an anonymous `ICurrentUser`; services that need a signed-in person cannot be misused from a job.

## Consequences
- Notices are rendered from stored data, so renaming a work order later shows the new title in old notices only if we re-read it; today they
  show the title at the time of the event.
- The monitor scans candidate work orders each minute using indexed date columns; very large tenants may need batching later.
- Teams, mobile push and per-user preferences plug in as additional sinks/consumers of the same events.
