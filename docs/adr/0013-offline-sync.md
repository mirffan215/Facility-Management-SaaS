# ADR-0013: Technician PWA and the offline sync contract (Phase 2d)
Status: Proposed. Fulfils [ADR-0003](0003-technician-client.md) for the PWA: the contract below is what any other client would follow too.

## The contract (server)
- **Idempotency keys.** Any POST/PUT/DELETE may carry `Idempotency-Key` (8-100 of `A-Za-z0-9._:-`). The same key from the same person returns the
  first successful answer (header `Idempotency-Replayed: true`) and does the work once. A reused key with a different request (method, path, query,
  body hash; uploads by length) is refused with 422. A request still running answers 409 with `Retry-After`. **Only 2xx answers are
  remembered**; a refused or failed request frees the key so a corrected retry runs. Keys are per person and per tenant (row-level security),
  created only after authorisation, and purged after 7 days. A server that dies after doing the work but before storing the answer leaves a stale
  "in progress" row; after two minutes the same request may run again (accepted, narrow risk, because the alternative is a request that can never be retried).
- **Honest times.** Start, hold, resume and complete accept `X-Performed-At`: when the person did it on their phone. It is used (history, SLA
  response, hold/resume clocks) only within limits: never in the future, never before the work order's last change, never more than 7 days old; otherwise
  the server's time is used. The audit event records the claimed, applied and sync times. The phone corrects its own clock using the server time it
  received with its last download. Everything else (checklist answers, time entries, costs, comments, photos, signatures) is stamped with the server's time of receipt;
  time entries carry the worked-on day the person gave.
- **One download.** `GET /work-orders/offline/jobs` returns, for the caller's open jobs (approved to on hold, at most 50): the order with the actions allowed,
  history, comments, checklist, signatures, files list, time, costs, plus the server time and the tenant's "require signature" rule. It reuses the
  normal services, so the same visibility rules apply; a person without `workorders.work` gets 403.
- **Conflict rules** (as ADR-0003 foresaw): the **server wins on status** (a cancelled or reassigned job refuses the phone's actions); notes, comments and
  readings merge (appended). Nothing a phone sends can bypass a rule the screens obey: separation of duties, required checklist lines and signatures are
  all enforced when the queue is sent.

## The client
- **Local-first for field actions.** Every action on the job screen is first saved on the device (IndexedDB) as an operation with a unique id (also its
  idempotency key) and sent in order when a connection allows; the screen shows the server's last word with unsent operations laid on top, each marked
  "waiting to send". One code path whether online or not.
- **Failure handling.** A failure retrying cannot fix (400/403/404/409/413/415/422) sets that operation aside, visible with its reason and
  Retry / Discard, and the rest carry on. A connection problem, 5xx, 429 or "still processing" stops the run and keeps everything. A 401 keeps everything and
  asks the person to sign in; sending resumes after.
- **App shell.** A small hand-written service worker saves the app's own files (page and `/static/*`, never `/api`), serves the saved page when the network
  fails, and picks up new releases from the network first. Web app manifest and icons make it installable.
- **Sessions offline.** The token lasts about an hour. If it has expired and there is no connection, a phone that downloaded its jobs within 24 hours still opens them
  (and nothing else) and cannot send until the person signs in again. After 24 hours without a download, or after sign-out, it asks for a sign-in.
- **Photos** are scaled to 1600 px JPEG on the phone before being queued (storage, bandwidth), with a fallback to the original.
- **Sign-out** deletes the saved jobs and unsent work from the phone, after a warning when anything is unsent.

## Consequences and risks
- **Data at rest on the phone is not encrypted by the app** (browser storage). It holds the person's open jobs, costs, comments, signatures and photos. Use device
  encryption/MDM and screen locks; a lost phone with a valid or recent session exposes those jobs until the 24-hour window ends. Native clients could use the OS keystore.
- The offline screen covers field work only. Creating requests, approvals, setup and reports need a connection.
- Background Sync (sending while the app is closed) is not used; sending happens when the app is open and online, on reconnect, on focus, and every 30 seconds while work waits.
- Clock correction relies on the last download; a phone that never downloaded has no correction (the server limits still apply).
- Idempotency covers retries of the *same* operation, not two people editing the same thing; those follow the conflict rules above.
