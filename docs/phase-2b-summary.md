# Phase 2b: Service levels and notifications

Builds on [Phase 2a](phase-2a-summary.md). Design record: [ADR-0011](adr/0011-sla-and-notifications.md).

## Built
| Area | What |
|---|---|
| Working calendars | Time zone, weekly hours, holidays, sites that follow it, default calendar. Setup > Service levels |
| SLA policies | Response/resolution targets per priority; default policy and per-category policies |
| SLA clocks | Start at approval, response = work started, pause on hold, due times in working minutes, recomputed on priority change |
| SLA states | On track / at risk / paused / met / breached, shown on the list (new "SLA breached" view) and the detail screen |
| Monitor | Worker job raises "response breached", "resolution at risk", "resolution breached" once each, to dispatchers and the assignee |
| Notifications | In-app bell with unread count, notifications page (mark read / all), lifecycle events for requesters, approvers, dispatchers, assignees, verifiers |
| Email | `IEmailSender` (None / Smtp), bilingual messages, queued through the outbox with retry; configured on the worker |
| Fixes found on the way | A stale-version update that changed only holidays/sites/targets was not detected (now always writes the row); the worker did not start with development validation (no `ICurrentUser`) |

## Tests
- Unit: business-time arithmetic (weekends, holidays, DST, round trip), SLA states, pause/resume, who-is-notified rules, bilingual text (39 new).
- Integration (real SQL Server): calendar and policy setup with validation and versions; clocks start at approval on the site's calendar; hold pauses;
  priority change recomputes; emergencies; category policy over default; per-tenant isolation and row-level security of the new tables; breached filter;
  monitor raises each alert once and never for held or finished work; notices and email via the real outbox; double delivery notifies once;
  mail outage retries without loss or duplicates; own-notices-only API; SMTP delivery against an in-process SMTP server.
- Browser (full stack, with the worker running): administrator sets up a calendar and policy, the request raises a notice for approvers, approval
  starts the clock ("On track"), the requester is notified and opens a notice to reach the request; accessibility scan of the new screens in
  English and Arabic.

## Not done / known gaps
- **Escalation is notification only.** No automatic reassignment, multi-level chains, or "manager of the assignee".
- **No per-user notification preferences** (channels, language, quiet hours). Everyone with an email gets emails when the provider is on.
- **Email provider**: SMTP only (built-in client), exercised against a fake server, not a real mail service. Microsoft Graph and a
  transactional provider are not implemented. The email text is not customisable per tenant.
- **No Teams or mobile push** (the sink is the place to add them).
- **Not tried against a real worker over time**: the one-minute monitor loop is tested by calling it, not by waiting.
- Calendars have one opening interval per day (no lunch break); holidays are entered by hand (no Hijri generator or import).
- Targets are in minutes; the screen has no "business hours" unit helper. Reports and an SLA dashboard belong to the reporting phase.
- Phase 2c (checklists, costs, time, signatures, photos) and 2d (technician PWA with offline sync) remain.
