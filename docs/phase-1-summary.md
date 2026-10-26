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
299 .NET tests pass locally: 119 unit, 19 API, 161 integration on real SQL Server (including 28 storage-backend contract tests). The
main rules above are covered (not every limit: the 5,000-node tree cap and the asset-class depth limit are not), plus tenant isolation at three levels (service, API and raw SQL as the restricted login) for locations, assets,
meters and documents. Mutation checks (removing the retire-with-parts guard and the archive-with-assets guard) fail the tests as they should.

## Web app (Phase 1b)
Sign-in (OIDC + PKCE) and screens for everything above, in English and Arabic (right-to-left, Hijri/Gregorian toggle), permission-aware:
location tree (create, edit, move, archive), asset list with filters and CSV export, asset page (details, parts, meters with reading entry and
voiding, files, QR label with print), bulk import with check-first and a per-line problem report, setup (asset classes, custom fields), and
the `/a/<tag>` address that QR labels open. See ADR-0009 for the decisions and `web/` for the code.

Tested at three levels: 49 unit/component tests; a 17-step full-stack browser journey (`scripts/e2e-full.sh`: real SQL Server, migrator, API, the built web app served by the
production nginx configuration with its strict content-security policy, and a fake OpenID Connect provider doing real PKCE) covering sign-in, setup, locations, assets, meters, files, labels, QR scan, status, export,
import, Arabic/RTL/Hijri, a role-restricted user, no content-security-policy violations, sign-out and axe accessibility scans in both languages; and the 338 .NET tests. The accessibility
scan was checked by deliberately breaking colour contrast, which made it fail.

Bugs the browser tests found and fixed: required-field markers inside label text; language not remembered across reloads (and no right-to-left
on first load); built files colliding with the `/assets/<id>` page URL; nginx silently dropping the security headers on the main page; QR labels
having no page to open; an empty Arabic name pushed to the wrong edge; mixed Arabic/English breadcrumbs scrambling their order.

## Not done / known gaps
- **User and role administration** has a screen (Users): assign roles, deactivate and reactivate. Administrators cannot change their own roles
  or deactivate themselves, and only the Administrator role holds `users.manage`, so the last administrator cannot be locked out through the
  UI; roles themselves are fixed system roles (no custom-role editor). A new user with no role sees a clear "no access" message. No screens yet for editing/archiving meters, editing asset classes or custom-field definitions (create and archive only).
- **Silent renewal** uses refresh tokens (`offline_access` in the provider's `Scope`), proven against the fake provider. If a provider issues no
  refresh token, the session ends when the access token expires and the person signs in again. **Not yet tried against real Entra or Google**:
  see `docs/runbooks/entra-sign-in.md`. The nginx configuration is exercised for real by the browser test (the official nginx image with the built files mounted), but the web Docker image build itself was not run here.
- **No offline mode** for the technician PWA yet (the API supports idempotent offline sync; the screens do not use it). Dark mode is styled but
  not scanned for contrast. The JavaScript bundle is one file (about 148 KB gzipped); route-level splitting is a later optimisation.
- **No malware scanning.** `IFileScanner` is a seam; the default accepts everything. Do not accept files from untrusted uploaders until a
  real scanner is plugged in. There is no per-tenant storage quota, no retention/purge policy for kept bytes, and an upload whose database
  save fails leaves an orphaned (harmless, write-once) object.
- **Upload size limits** are enforced by counting real bytes in the service (tested) and by Kestrel's body limit (set per request); the
  Kestrel path itself is not exercised by the in-memory test host.
- Bulk import/export is CSV only (no Excel files); import is all-or-nothing.
- **Search is `LIKE`**, with no full-text index; the tree endpoint is capped at 5,000 nodes.
- **No events yet.** Meter readings and asset changes do not publish outbox messages, because nothing consumes them until Preventive
  Maintenance (Phase 3); an unconsumed message type would only fill the dead-letter list.
- **Time zones** are stored on sites but nothing uses them yet; dates are ISO, and Hijri display belongs to the UI.
- **No API rate limiting** anywhere yet.
- Still open from Phase 0: Bicep/Terraform and Helm, worker health endpoint, tenant onboarding tooling, and real identity-provider testing.
  CI (backend, web, Docker images, full-stack browser test) runs on GitHub and is green.
