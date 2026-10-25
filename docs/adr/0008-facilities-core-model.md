# ADR-0008: Facilities core model (locations, assets, meters, documents)
Status: Proposed

## Decisions
- **Own module, own schema.** `Fms.Facilities` has its own DbContext, tables in the `facilities` schema and its own migrations
  history. Its tables are under the same row-level-security policy as the platform's. Audit events and outbox messages are written
  through `PendingWrites`, so they commit in the same transaction whichever module context saves (see "Consequences").
- **One `Locations` table, four types.** Site > Building > Floor > Space (a Space may also sit directly under a Building). Allowed
  parent types are fixed in code. A materialised path (`/siteId/buildingId/.../`) makes "everything under X" one indexed prefix
  query and moving a subtree a bounded rewrite. Codes are unique among *active* locations (reusable once archived).
- **Assets are identified three ways, for different jobs.** `Id` (system), `Code` (the number people use; unique forever, even
  after retirement, so a printed number can never mean two things) and `TagCode` (what the QR/barcode label encodes; 10 Crockford
  base32 characters, re-issuable if a label is lost, the old one then stops resolving). Scanning normalises case, separators and
  look-alike characters.
- **Lifecycle is a small state machine.** Planned > Active > Under maintenance / Out of service > Retired (final). Out of service
  and Retired need a reason. An assembly cannot be retired while its parts are in use; a retired asset is a closed record
  (no edits, meters, files or label reissue). Locations and classes with live assets cannot be archived.
- **Custom fields are definitions plus validated JSON.** Definitions (key, English/Arabic labels, type, choices, required, optionally
  scoped to an asset class) are data; values are validated on every save and stored as JSON on the row. Keys, targets and types are
  immutable once created. Values under an archived definition are kept untouched when the row is saved. Changing an asset's class
  drops values of fields that no longer apply; the audit record keeps the old JSON.
- **Meter readings are evidence.** Readings are append-only in EF *and* by database trigger. A wrong reading is *voided* (a separate
  append-only record with a reason, requiring `meters.manage`), not edited; voided readings stop affecting checks, trends and the
  latest value. Cumulative meters cannot go down unless the reading is flagged as a reset, and a backdated reading must fit between
  its neighbours in time. Readings carry a client-generated id, so retried or offline-synced submissions are stored once.
- **Files are never trusted.** The type is detected from content against an allow-list (PDF, PNG, JPEG, WebP, DOCX, XLSX, plain
  text); HTML/SVG-as-markup, executables, archives and macro-enabled or embedding Office files are refused. The storage key is built
  only from ids, never from the file name; the display name is sanitised (path parts, control and bidi-override characters removed,
  extension forced to match the detected type). Downloads are attachments with `nosniff` and a sandboxing CSP, and each download is
  audited. Deleting hides a file but keeps its bytes (soft delete with a reason). A scanner seam (`IFileScanner`) exists; the default
  does not scan.
- **Optimistic concurrency everywhere a person edits.** Each editable row has a row version the client must send back; a stale write
  is a 409 and changes nothing.

## Consequences
- `PendingWrites` assumes the caller saves exactly one module context per unit of work, or accepts that audit rows land with the first
  context that saves. A failed save clears the tracker so a half-built audit row cannot leak into a later save in the same scope.
- Free-text search uses `LIKE`, which is fine for thousands of assets per tenant but not millions; a full-text index is the next step
  if a tenant outgrows it.
- Facilities tables carry `TenantId` without a foreign key to the platform's `Tenants` table (modules stay independently migratable);
  row-level security and the tenant stamp keep it correct.
- File bytes are write-once and kept after soft delete, so storage only grows until a retention policy is defined.
