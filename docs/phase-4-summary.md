# Phase 4: Inventory

Builds on [2a–2d](phase-2a-summary.md) and [3](phase-3-summary.md). Design record: [ADR-0015](adr/0015-inventory.md). Delivered in three slices.

## Built
| Slice | Area | What |
|---|---|---|
| 4a | Items and stores | Items (code, English/Arabic name, unit, barcode), stores (optionally tied to a site); archive/restore (blocked while stock remains) |
| 4a | Stock | Per store and item: on hand, reserved, available, average cost, value. Receive (with cost and reference), transfer between stores, direct adjustment (reason, approve permission). Never below zero; movement history per item and store |
| 4b | Work orders | **Parts** tab: reserve, issue, return, release. Stock held for one work order is not available to another. Parts cost flows into the work order's cost summary. Reservations let go when the work order is cancelled, verified or closed |
| 4b | Preventive maintenance | A template lists the parts each visit needs; they are reserved when the visit's work order is created (shortages recorded, the visit never blocked) |
| 4c | Reorder | Minimum and maximum per item and store, low-stock filter, suggested order quantity, one notice (in-app and bilingual email) per dip |
| 4c | Stock take | Start per store (frozen quantities), enter counts, add things found, submit, approve by a different person, send back, cancel; differences posted as movements |
| All | Access and language | `inventory.read/manage/issue/approve`; **Inventory** menu; English and Arabic, RTL |

API (under `/api/v1`): `inventory/stores`, `inventory/items`, `inventory/stock`, `inventory/movements`, `inventory/stock/{receive|transfer|adjust|levels}`, `inventory/stock-takes[...]`, `inventory/pm-templates/{id}/parts`, `work-orders/{id}/parts[/reserve|release|issue|return]`.

## Tests
- Unit: the stock arithmetic (available, weighted average, low, suggested order, quantity and cost validity) – 25.
- Integration (real SQL Server) – 11 new: permissions, validation, uniqueness and optimistic versions; archive rules; weighted average over receipts; transfers carrying cost; no negative stock; adjustment rules; the ledger; the append-only trigger (even the application login cannot update or delete a movement); tenant isolation by API and by row-level security; reserve/issue/return arithmetic with two competing work orders and the cost summary; ten parallel issues against five units; release on cancellation through the outbox; refusal for ended and foreign work orders; PM template parts including a shortage; low-stock notice once per dip and not to viewers; stock take (frozen sheet, second-person approval, differences only, send back, reservation guard). The migrator test now also checks Inventory's tables are all under row-level security.
- Browser (full stack): create a store and an item, receive stock, set a reorder level, reserve and issue parts on a work order and see the parts cost, be refused when asking for more than exists, run a stock take through to the "someone else must approve" refusal and cancel it; accessibility scans of all four Inventory tabs and the parts tab in English and Arabic.

## Not done / known gaps
- **No purchasing**: reorder only advises. Purchase requests/orders, supplier prices and receiving against orders are Phase 5.
- **No FIFO/batch/serial/expiry**, no bins or locations within a store, no unit conversion, no revaluation.
- **One currency** per tenant; costs carry no currency code.
- **Parts are online-only**: the technician's offline screen does not issue parts (they are recorded when online). Barcode scanning uses typing in the item search (a scanner that types works; the camera is not used).
- **Reservations are per store**: a need larger than any single store's availability cannot be split across stores in one step.
- **Stock take is whole-store**: no cycle counts by item group, no blind counts (the frozen system quantity is shown beside the entry).
- **No stock report pack** (valuation by period, usage by asset, ABC): that belongs to the reporting phase. Parts by asset can be read from the movement ledger by work order.
- Reservation release on end-of-life depends on the outbox worker running; until it runs the hold remains (and shows).
