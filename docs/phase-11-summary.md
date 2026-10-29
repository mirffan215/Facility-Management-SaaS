# Phase 11: Closing gaps

Design records: [ADR-0031](adr/0031-work-orders-from-alerts.md) (work orders from alerts), [ADR-0032](adr/0032-security-follow-ups.md) (security follow-ups). Scope chosen by the customer: link alerts to work orders; rehearse the deployment; AI feedback and quality; security follow-ups.

## Built
| Slice | What |
|---|---|
| 11a Alerts to work orders | Energy alarms and failed inspections can raise **one** work order per cause (unique source on the work order), by a switch on the rule or obligation or by a button; the work order is linked from the alarm and the failed result. Permits left in force past their end are warned about daily. Leases can carry files. |
| 11b Deployment rehearsal | `scripts/rehearse-onprem.sh` installs the Compose bundle with the bundled SQL Server, upgrades it (with the verified backup), enters data, rolls back and checks the data is gone; a CI job runs it. **It found five defects, now fixed** (below). A checklist for the first Helm and Azure deployment is in `docs/runbooks/first-deployment-checklist.md`. |
| 11c AI feedback | The person says whether they used a suggestion or found a summary helpful (once, append-only, no words kept); the AI page shows the accept rate. A request being written shows open requests of the last 30 days about the same asset or place with similar words ("already reported?"), by word matching, no model. |
| 11d Security | ClamAV upload scanning (fails closed); AES-256-GCM encryption of visitors' e-mail and phone with key rotation and `--encrypt-existing`; a database-shared rate counter for the secret-address routes; a ZAP baseline workflow. |

## The rehearsal's findings (all fixed)
1. **First install never started the bundled SQL Server**: the migrator did not depend on it and waited forever for a database that was not running.
2. **The compose project name came from the folder**, so an upgrade run from the next version's folder would have created a second, empty installation. It is now fixed as `fms`.
3. **A first install that stopped half way could not be run again**, because the version was written to `.env` before the work was done.
4. **The web container could not start under the hardening settings**: its rendered configuration folder was a root-owned tmpfs and nginx runs as its own user.
5. **The pre-upgrade backup failed**: the backup folder belonged to root and SQL Server runs as its own user. (The script refused to go on and changed nothing, as designed.)
None could have been found by the existing tests: they need a running stack.

## Tests
Unit: a key ring (round trip, a different value every time, tamper and wrong column refused, rotation, bad keys stop start-up), the ClamAV protocol against a stand-in daemon (clean, infected, unreachable fail closed and open, errors not trusted), word matching for similar requests. Integration (real SQL Server): work orders from failures and from alarms (once, by hand, by switch, other organisations cannot ask), overdue permit warnings once a day, lease documents (type by content, audited, reason to remove, isolation), AI feedback (once, only the asker, failed calls refuse, append-only), similar requests, encrypted columns stored unreadable and read back (rotation, earlier plain values, isolation), the migrator's `--encrypt-existing` against a real database, and the shared rate limit across two servers. Browser: lease documents added to the lease step; the AI step checks feedback is recorded. Scripts: the full install, upgrade and rollback.

## Not done / known gaps
- **Not run anywhere yet:** the ZAP workflow; the new CI rehearsal job with the real Dockerfiles (it was run here with images compiled on the development machine, because container builds could not reach the package registries behind this environment's proxy).
- **Still never deployed:** the Helm chart and the Azure template (a checklist is written). Your own SQL Server, offline bundles with images, a real identity provider and a real license were not part of the rehearsal.
- Only visitors' contact details are encrypted at the column level; users' sign-in e-mail, vendor details and lease terms are not.
- Work orders from alerts are raised once and not retried by the next check; a failure before the switch was on gets none (use the button).
- AI: no suggested assignee or vendor; duplicate hints are word matches, which miss different wording and Arabic spelling variants; suggestions still not in the mobile app.
- The shared rate counter adds one write per secret-route request and is off by default.
- Keys for encryption must be backed up apart from the database; a lost key means lost contact details.
