# Phase 1: Core and Assets (summary)

## Delivered
| Area | What exists |
|---|---|
| Foundation change | Audit and outbox writes are buffered (`PendingWrites`) and saved by whichever module context commits, so every module gets "change and audit in one transaction"; row-level security helpers are schema-aware; enums travel as strings; `nosniff` on every response |
| Locations | Site > Building > Floor > Space tree with Arabic and English names, IANA time zone on sites, area, move (subtree path rewrite), archive/restore, search, paging, subtree queries, whole-tree endpoint |
| Custom fields | Per-tenant definitions for locations and assets (text, number, date, yes/no, choice), bilingual labels, optional asset-class scope, validated values |
| Asset classes | Nested categories with archive rules |
| Assets | Register with code, classification, location, parent/part hierarchy (max depth 6), criticality, manufacturer/model/serial, install and warranty dates (state: none/active/expiring soon/expired), status lifecycle with reasons, QR/barcode tag with lookup and re-issue, QR label as SVG, filtered and paged listing |
| Meters | Cumulative and gauge meters per asset, plausible range, append-only readings, reset handling, backdated-reading checks, idempotent retries, offline batch (200 per call), voiding with reason |
| Documents | Upload, list, audited download and soft delete per asset (see ADR-0008 for the safety rules) |
| Permissions | `locations.read/manage`, `assets.read/manage`, `meters.read/record/manage`, `documents.read/manage`, `customfields.manage`; system roles updated (Facility Manager everything above; Technician reads plus records readings; Requester reads locations; Viewer reads) |
| Migrations | Facilities context with its own history in schema `facilities`; the migrator applies Platform then Facilities; every facilities table is under row-level security; readings and voids also have append-only triggers |

## API (all under `/api/v1`, all require sign-in and a tenant)
| Resource | Endpoints |
|---|---|
| Locations | `GET /locations` (filters: parentId, underId, type, q, includeArchived, page), `GET /locations/tree`, `GET/PUT /locations/{id}`, `POST /locations`, `POST /locations/{id}/move\|archive\|restore` |
| Custom fields | `GET /custom-fields`, `POST /custom-fields`, `PUT /custom-fields/{id}`, `POST /custom-fields/{id}/archive\|restore` |
| Asset classes | `GET/POST /asset-classes`, `PUT /asset-classes/{id}`, `POST /asset-classes/{id}/archive\|restore` |
| Assets | `GET /assets` (locationId, includeSublocations, classId, status, criticality, parentAssetId, q, warrantyExpiresBefore, includeRetired, page), `GET /assets/{id}`, `GET /assets/by-tag/{tag}`, `GET /assets/{id}/qr.svg`, `POST /assets`, `PUT /assets/{id}`, `POST /assets/{id}/status\|reissue-tag` |
| Meters | `GET/POST /assets/{id}/meters`, `GET/PUT /meters/{id}`, `POST /meters/{id}/archive\|restore`, `GET/POST /meters/{id}/readings`, `POST /meters/readings/batch`, `POST /meter-readings/{id}/void` |
| Documents | `GET/POST /assets/{id}/documents` (multipart: `file`, `kind`, `description`), `GET /documents/{id}/download`, `POST /documents/{id}/delete` |

Failures are RFC 7807 problem details: 400 validation (with per-field `errors`), 401, 403, 404, 409 (duplicate code, stale row version,
rule conflicts), 413 (file too large), 415 (file type not allowed).

## Tests
299 .NET tests pass locally: 119 unit, 19 API, 161 integration on real SQL Server (including 28 storage-backend contract tests). Every
rule above has a test, plus tenant isolation at three levels (service, API and raw SQL as the restricted login) for locations, assets,
meters and documents. Mutation checks (removing the retire-with-parts guard and the archive-with-assets guard) fail the tests as they should.

## Not done / known gaps
- **No web screens yet.** The React app still has no sign-in, so locations, assets and meters can only be used through the API. The
  sign-in screen and these screens are the next step.
- **No malware scanning.** `IFileScanner` is a seam; the default accepts everything. Do not accept files from untrusted uploaders until a
  real scanner is plugged in. There is no per-tenant storage quota, no retention/purge policy for kept bytes, and an upload whose database
  save fails leaves an orphaned (harmless, write-once) object.
- **Upload size limits** are enforced by counting real bytes in the service (tested) and by Kestrel's body limit (set per request); the
  Kestrel path itself is not exercised by the in-memory test host.
- **No bulk import/export** (CSV/Excel) for locations and assets, which a real rollout will need on day one.
- **Search is `LIKE`**, with no full-text index; the tree endpoint is capped at 5,000 nodes.
- **No events yet.** Meter readings and asset changes do not publish outbox messages, because nothing consumes them until Preventive
  Maintenance (Phase 3); an unconsumed message type would only fill the dead-letter list.
- **Time zones** are stored on sites but nothing uses them yet; dates are ISO, and Hijri display belongs to the UI.
- **No API rate limiting** anywhere yet.
- Still open from Phase 0: Bicep/Terraform and Helm, worker health endpoint, tenant onboarding tooling, real identity-provider testing,
  and CI has not yet run (Docker image builds are unverified in this sandbox).
