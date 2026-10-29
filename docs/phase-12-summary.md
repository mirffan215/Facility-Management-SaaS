# Phase 12: Suggestions and field gaps

Design records: [ADR-0033](adr/0033-suggestions-and-early-warnings.md) (suggestions and early warnings), [ADR-0034](adr/0034-vendor-contact-encryption.md) (vendor contact encryption), [ADR-0035](adr/0035-permits-on-the-phone.md) (permits on the phone). Scope chosen by the customer: assignee and vendor suggestions; predictive maintenance from meters; more encrypted fields; the technician app's gaps.

## Built
| Slice | What |
|---|---|
| 12a Suggestions | In the Assign and Give-to-vendor dialogs: up to three people (by their finished work on the same asset, category and place in the last 180 days, less their open jobs) and up to three vendors (contract in force covering the job, resolution target for the priority, past jobs). Each shows its numbers; a button picks the person or vendor and contract, and nothing is saved until the dispatcher confirms. A rule, not a model, so it needs no AI switch. |
| 12b Early warnings | On the work orders page: assets whose repairs are coming closer together (four or more in a year, recent gaps half the earlier median or less) and service meters running at least 1.5 times faster than the month before, with the days left to the next service. Each line gives the numbers and an expected date. |
| 12c Encryption | Vendor and vendor-contact e-mail and phone are encrypted like visitors' details. The migrator's `--encrypt-existing` covers all three tables and re-protects after a key rotation. Checked first: nothing searches these columns. |
| 12d Technician app | The job on the phone shows the permits tied to it (status as of the last download, labelled as possibly out of date, changed only online), says when an alert raised the job, and offers the AI summary when online. Work orders carry `sourceKind` in the API. |

## Tests
Unit: ranking by weights, ties and vendor order; failure-gap and meter-pace rules at their boundaries, including a replaced meter. Integration (real SQL Server): suggestions with the numbers, what is not experience (cancelled, duplicate, old, still open), vendors only with an in-force covering contract, roles and isolation; early warnings for both rules with their numbers, what is not counted, roles and isolation; vendor and contact details stored unreadable and read back, rotation, earlier plain values, other organisations; the migrator against a real database for all three tables including rotation; the offline bundle's permits (in play only, gone once cancelled) and source. Browser: the suggestions group in the Assign dialog (with an accessibility check), the permit and its warning on the offline job screen.

## Not done / known gaps
- **The thresholds are fixed constants**, not tenant settings, and rest on little data: a hint is a prompt, not a finding. The meter hint will fire every spring on seasonal equipment.
- **Suggestions favour whoever has history**, and never suggest a newcomer. They are not in the phone app (a dispatcher's tool).
- **Permits cannot be worked from the phone**, by design (ADR-0035); a technician offline sees an old status and a warning.
- Still not encrypted at the column level: users' sign-in e-mail, invitation e-mail, lease terms. A lost key means lost vendor and visitor contact details.
- Carried over: the ZAP workflow has not been run; the Helm chart and Azure template have never been deployed; the Claude provider has not run against the real API.
