# Phase 6: Spaces, bookings and visitors

Builds on [1](phase-1-summary.md) (locations), [4](phase-4-summary.md) and [5](phase-5-summary.md). Design records: [ADR-0019](adr/0019-spaces-and-bookings.md) and [ADR-0020](adr/0020-visitors.md). Delivered in three slices.

## Built
| Slice | Area | What |
|---|---|---|
| 6a | Spaces | Kind and capacity per space; floor plans (PNG, JPEG, WebP) with pinned spaces and assets, replaced by newer drawings without losing pins; allocation of spaces to people and teams for a period, never above capacity; release and cancel with history; occupancy by floor, building, site or space, on any day |
| 6b | Bookings | Bookable rooms, desks, parking and equipment with their own rules (hours and days in the resource's time zone, shortest, longest, how far ahead, buffer); no double booking; optional approval (never by the person who asked); check-in with automatic release of no-shows; weekly series (all or nothing); my bookings; a secret calendar address for a calendar app (one way); calendar connection interface with an in-memory adapter |
| 6c | Visitors | Pre-registration; a pass (link and QR code) that needs no account; front desk: scan or type the code, check in with an optional badge number, printable badge, check out, who is on site; host told on arrival; watchlist (name matching that survives spelling variants, security-only override with a reason); contractor induction; visitor details removed after a retention period |
| All | Access and language | `spaces.read/manage`, `bookings.read/book/approve/manage`, `visitors.host/reception/security`; new system role **Reception**; menus **Spaces**, **Bookings**, **Visitors**; English and Arabic, RTL |

API (under `/api/v1`): `spaces/{id}`, `spaces/occupancy/{id}`, `spaces/floors/{id}[/plan|/pins]`, `spaces/plans/{id}/image`, `allocations[...]`; `resources[...]`, `bookings[...]` (`availability`, `mine`, `calendar-feed`, `calendar-sync`, `{id}/cancel|approve|reject|check-in`), `calendar/{secret}`; `visits[...]` (`mine`, `on-site`, `lookup`, `{id}/check-in|check-out|cancel|pass`, `watchlist`, `settings`, `pass/{code}[/qr.svg]`).

A fix found on the way and applied everywhere: a request body the server cannot read (broken JSON, an unknown enum name) now answers 400 with a problem document instead of 500.

## Tests
All suites green at the end of the phase: 415 unit, 20 API, 301 integration, 71 web, 34 browser steps.

- Unit: allocation arithmetic (peak occupancy, overlap, utilisation), booking rules (opening hours judged in the resource's zone, lengths, steps, past and advance limits, midnight, buffers, check-in window, no-show, weekly series across a clock change), the iCalendar writer (escaping, injection, folding of multi-byte text), visitor rules (name matching in Latin and Arabic, check-in window, no-show, retention, finding the code in what a scanner typed).
- Integration (real SQL Server): permissions and validation; capacity never exceeded, including eight parallel requests for three places; room never double booked, including eight parallel requests for one slot and a series that is all or nothing; maker-checker on approval; cancel, check-in and no-show; the calendar address (hash only, one address at a time, revoke, only one's own bookings, nobody else's organisation); calendar sync through the outbox; pass codes (hash only, renewal, anonymous view shows nothing private); desk flow; watchlist (spelling variants in Latin and Arabic, reason hidden from reception, security override recorded, removal kept); induction; retention and no-show housekeeping; isolation between organisations by API and by row-level security; database constraints. The migrator test now also checks the new tables are under row-level security and that the Reception role exists.
- Browser (full stack through the production web tier): set up a floor plan, pin, capacity and allocation that cannot overfill; set up resources, book without a clash, cancel, request something needing approval, make and stop a calendar address; expect a visitor, open the pass with no account, check in with a badge and out, stop a watchlisted name and let it in as security; accessibility scans of every new screen and the public pass page in English and Arabic.

## Not done / known gaps
- **Spaces:** allocations are by date, not hour; floor plans are plain images (no scale, layers, CAD or BIM); no move management or chargeback; no link between an allocation and a booking.
- **Bookings:** no editing a booking (cancel and rebook); weekly recurrence only; no Microsoft 365 or Google adapter (the interface and an in-memory adapter exist; the in-memory one forgets on restart and is refused unless `Calendar:AllowFakeAdapter` is on); the calendar feed is one way and only the person's own bookings; notices go in-app and by email, not to Teams.
- **Visitors:** no kiosk or self check-in; the pass address is given to the host to forward (the system does not email the visitor); no identity-document capture; watchlist matching is by name only; day filters use the server's UTC calendar with a wide window; nobody is checked out automatically; no emergency muster report beyond the on-site list.
- All three are online-only; none appears in the technician's offline app.
