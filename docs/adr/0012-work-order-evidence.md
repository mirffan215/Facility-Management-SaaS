# ADR-0012: Checklists, time, costs, signatures and files (Phase 2c)
Status: Proposed

## Decisions
- **Evidence is separate from the lifecycle.** The work order's status says where it is; checklist answers, time, costs, signatures and files are the
  record of what was done. Each has its own table in the `work` schema, under the same row-level security as everything else.
- **Money and sign-off are append-only.** Time entries, cost lines, voids and signatures cannot be updated or deleted: EF refuses, and database
  triggers refuse even raw SQL. A wrong entry is *voided* with a reason (a separate append-only row, one per entry) and re-entered; totals ignore voided
  entries but the record keeps both. This is the same pattern as meter readings.
- **The labour rate is frozen on each time entry.** The tenant sets one hourly rate; every entry stores the rate in force when it was recorded, so
  changing the rate never rewrites history. Rate zero means labour is not costed. Other costs are quantity x unit cost per line, rounded to the
  currency's cent once per line (unit cost keeps four decimals), summed without further rounding. One currency per tenant (code in settings).
- **Who may record, and when.** Only the assignee holding `workorders.work`, or a manager, records checklist answers, time, costs and signatures.
  Checklist answers are open while the work is in progress or on hold; time, costs and signatures also until the work is *checked* (late receipts
  after completion are normal; after verification they would invalidate the check). Photos and files can be added by the worker, dispatchers and the
  requester while the order is open. Time and costs are internal: only people who can read all work orders see them.
- **Checklists are copied, not linked.** A template is a reusable list; attaching it copies its lines onto the work order, so later template edits
  never change work already planned or done. Lines are *Pending / Done / Failed / Not applicable*; a failure needs a note. **Completion is refused
  while a required line is Pending** (a *failed* required line counts as answered: the work is done and the failure is on record for the verifier).
- **Signatures** are drawn PNGs (checked by content, size-limited) in write-once storage, with the printed name, role (technician or customer),
  who recorded them and when. A tenant can **require the technician's signature before completion**. A customer signs on the technician's device;
  there is no customer login. A drawn signature proves little by itself; its value is the record of who was present and the hash/time/recorder trail.
- **Files** use the platform's content sniffing allow-list (moved from Facilities to BuildingBlocks so both modules share it): type comes from
  bytes, names are sanitised and made to match the type, markup is kept only as inert plain text, downloads are attachments with a locked-down
  content-security policy and are audited. Removal is a soft delete with a reason; the bytes are kept. The malware scanner is still the same seam.
- **Photos on a phone.** The file input asks for the camera (`capture`), uploads go through the API like any file; images are shown in the app by
  fetching them with the token (never by a public URL).

## Consequences
- Thumbnails are the original image downloaded at full size (limit 15 MB each). Server-side thumbnails are a later optimisation.
- A signature needs a pointing device. A tenant that requires signatures excludes keyboard-only workers; a typed acknowledgement is a possible alternative.
- Costs are not posted anywhere: ERP posting (Dynamics 365 first) belongs to the purchasing/vendor phase, and once costs are exported they must become immutable with reversing entries.
