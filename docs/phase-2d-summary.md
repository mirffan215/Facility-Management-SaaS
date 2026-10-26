# Phase 2d: Technician PWA with offline sync

Completes Phase 2 (helpdesk and work orders). Builds on [2a](phase-2a-summary.md), [2b](phase-2b-summary.md), [2c](phase-2c-summary.md). Design record: [ADR-0013](adr/0013-offline-sync.md).

## Built
| Area | What |
|---|---|
| Server | Idempotency keys for retried writes (per person, 2xx only, purged after 7 days); `X-Performed-At` honoured within limits for start/hold/resume/complete; `GET /work-orders/offline/jobs` bundle |
| App shell | Service worker keeps the app on the device, installable (manifest, icons); reloading offline works |
| My jobs | Technician's start page: assigned open jobs kept on the phone, priority-ordered, with "waiting to send" marks, last update time, refresh |
| Job screen | Start / hold / resume / complete, checklist, time, parts and costs, photos (camera, scaled down), drawn signature, comments, all usable offline; completion blocked locally for the same reasons as on the server |
| Sync | Ordered queue with idempotency keys, retry on connection problems, set-aside list for work the server refused (Retry / Discard), sign-in prompt when the session ended, corrected phone clock |
| Sessions | An expired session with no connection still opens the saved jobs for up to 24 hours; signing out clears the phone (with a warning if work is unsent) |
| Also | Landing: field staff (work permission, no manage permission) start on My jobs; the notifications bell and list now refresh together |

## Tests
- Unit (web): overlay of unsent work on a job, local completion rules, the sync engine (order, keys, corrected times, network drop and resume, refusals set aside and retried, try-later statuses,
  expired session, photos, one run at a time, separate people on one device, stored clock offset) - 16; server time-claim limits - 6.
- Integration (real SQL Server): retry with the same key does the work once and replays the answer; different request with the same key refused; refusals not remembered; keys belong to one person;
  eight parallel duplicates do the work once; uploads retry-safe; invalid keys; time claims clamped; the bundle contains only the caller's open jobs with what is needed, nothing for others, nothing across tenants.
- Browser (full stack through the production web tier, real browser going offline): a technician reloads offline, works a whole job (start, checklist, time, cost, comment, photo, signature, complete) with nothing reaching the server,
  survives a reload, and on reconnecting everything arrives exactly once with the start time of the moment it was done (not of the sync); work refused because the job was cancelled meanwhile is set aside and shown;
  an expired session with no connection still opens saved jobs, blocks everything else, and sends after signing in; accessibility scans of the job screens in English and Arabic.

## Not done / known gaps
- **No Background Sync**: work is sent only while the app is open. Closing the app with unsent work leaves it on the phone until the next time it is opened online.
- **Device storage is not encrypted by the app** (ADR-0013). **No remote wipe** of a lost phone's saved jobs.
- **Offline covers field work only**: creating requests, approvals, set-up, reports and asset browsing need a connection. New work assigned while offline appears after the next download.
- **Job list refresh is whole-bundle** (no delta by version token); up to 50 jobs.
- **Labour cost is not shown offline** (the rate is not downloaded); costs show once synced. Server-side thumbnails still do not exist, so a saved photo list online downloads full images.
- A request that dies mid-flight on the server after doing its work can, after two minutes, be done again by a retry (narrow; see ADR-0013).
- Signatures still need a pointing device. The camera attribute asks the phone for its camera; behaviour of the camera picker differs per phone and was only tried with a scripted file in a desktop browser.
- Not tried on real phones, real flaky networks, or iOS Safari (its storage limits and service-worker behaviour differ).
