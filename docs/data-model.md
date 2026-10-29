# Domain and data model (core entities)

All tables include `TenantId`, `Id` (GUID), `CreatedAt/By`, `UpdatedAt/By`, `RowVersion`.

```
Tenant 1-* Organization 1-* Site 1-* Building 1-* Floor 1-* Space
Asset (self-hierarchy; AssetClass, Status, SiteId, SpaceId, Warranty, Meters, Documents, CustomFields)
Meter 1-* MeterReading
ServiceRequest -> (spawns) WorkOrder
WorkOrder 1-* WorkOrderTask 1-* ChecklistItemResult
WorkOrder 1-* LaborEntry | MaterialUsage | ContractorCost | Attachment | StatusHistory
PmSchedule (trigger: calendar|meter|condition) -> generates WorkOrder; PmTemplate 1-* PmTask
Item (SKU) 1-* StockLevel(Store) ; StockMovement (issue/receipt/transfer/adjust)
Vendor 1-* Contract 1-* ContractLine ; PurchaseRequest -> PurchaseOrder -> Receipt
Booking(Resource, Window, Attendees, Status) ; Resource(Type, SpaceId, Rules)
Visit(Visitor, Host, Window, Status, Badge)
User, Role, Permission, Team, Skill, Shift ; SlaPolicy, Calendar
AuditEvent (append-only) ; OutboxMessage ; Notification ; WorkflowDefinition/Instance
```

Work order lifecycle: `Draft -> Requested -> Approved -> Assigned -> InProgress -> OnHold -> Completed -> Verified -> Closed`
(plus `Cancelled`, `Reopened`). Transitions are workflow-configurable per tenant within fixed safety rules.

Sensitive fields (vendor bank details, ID numbers of visitors) use column-level encryption.

## Implemented in Phase 1 (schema `facilities`)
```
Location (Site > Building > Floor > Space; materialised Path)          CustomFieldDefinition (Target, Key, Type, Choices, AssetClassId?)
AssetClass (nested)  1-* Asset (Code, TagCode, Status, Criticality, ParentAssetId?, LocationId, CustomFieldsJson, RowVersion)
Asset 1-* AssetDocument (hash, detected type, soft delete)
Asset 1-* Meter (Cumulative | Gauge)  1-* MeterReading (append-only)  1-0..1 MeterReadingVoid (append-only)
```
See ADR-0008 for the rules behind these.

## Implemented in Phase 2a (schema `work`)
```
WorkOrder (Number, Title, Type, Priority, Status, LocationId, AssetId?, CategoryId?, RequestedBy, AssignedTo?, DueAt?, ClosureCode?, RowVersion)
WorkOrder 1-* WorkOrderEvent (append-only history)   WorkOrder 1-* WorkOrderComment (append-only, IsInternal)
WorkCategory (Code, Name, NameAr, DefaultPriority)   WorkSettings (RequireApproval, RequireVerification; one row per tenant)
```

Phase 2b adds, in `work`: `WorkCalendar` (+ `WorkCalendarHoliday`, `WorkCalendarSite`), `SlaPolicy` 1-* `SlaTarget`, and SLA columns on `WorkOrder`
(policy, calendar, start, response/resolution due, responded, pause, alert flags). In `dbo`: `Notification` (per person; unique per event and person).

Phase 2c adds, in `work`: `ChecklistTemplate` 1-* `ChecklistTemplateItem`; `WorkOrderChecklistItem` (a copy, with result); append-only `WorkOrderTimeEntry`,
`WorkOrderCostLine`, `WorkOrderVoid` (one per cancelled entry) and `WorkOrderSignature`; `WorkOrderAttachment` (soft delete). Settings gain currency, labour rate
and "require completion signature".

Phase 2d adds, in `dbo`: `IdempotencyKey` (per person, request hash, stored answer for 2xx, purged after 7 days).

Phase 3 adds, in `work`: `PmTemplate`, `PmSchedule` (target asset or class, time basis Fixed/Floating/None, interval, start, lead days, meter name and interval, assignee),
`PmPlan` (one schedule on one asset: next due, last completed, open work order, meter and baseline) and `PmOccurrence` (one generated visit: due date, trigger Time/Meter,
outcome Open/Completed/Skipped, on-time flag; the history compliance is computed from). All four are tenant-protected by row-level security. Work orders gain nothing: generated ones are `Type = Planned`.

Phase 4 adds, in `inventory`: `Store`, `Item`, `StockLevel` (per store and item: on hand, reserved, average cost, min/max, low-notified flag; check constraints keep quantities sane),
append-only `StockMovement` (signed quantity, unit cost, balance after, work order / transfer / stock take, reason), `Reservation` (per work order, store and item), `PmTemplatePart`,
`StockTake` 1-* `StockTakeLine`. All eight tables are under row-level security; movements are also protected by a database trigger. Work order parts cost is derived from movements, not copied.

Phase 5 adds, in `vendors`: `Vendor`, `VendorContact`, `Contract` 1-* `ContractCoverage` / `ContractSla` / `ContractDocument`, and `VendorUser` (a vendor person's link to one vendor). Work orders gain `AssignedVendorId` and `VendorContractId`.
In `purchasing`: `PurchasingSettings`, `PurchaseRequest` 1-* line, `PurchaseOrder` 1-* line, append-only `GoodsReceipt` (also trigger-protected). In `integration`: `ErpSettings` and `ErpExportItem` (idempotency key, status, attempts, lease, reference, reconcile state).
In the platform schema: `GuestIdentity` (provider and subject mapped to one tenant) and `GuestInvitation` (token hash only; tenant-owned, under row-level security). All new tenant tables are under row-level security.

Phase 6 adds, in `spaces`: `SpaceProfile` (kind and capacity of a Space location), `FloorPlan` (image, superseded not replaced), `PlanPin` (a position as fractions of the image), `Allocation` (person or team, head count, dates; released or cancelled ones stay).
In `bookings`: `BookableResource` (rules, time zone), `Booking` (UTC start and end, status, series), `CalendarLink` (the event's reference in the connected calendar, kept apart so syncing never changes a booking's row version), `CalendarFeedToken` (hash of a person's secret calendar address), `CalendarSyncSettings`.
In `visitors`: `Visit` (host and site names copied in; hash of the pass code; check-in, badge, induction, watchlist override and check-out facts; anonymised after retention), `WatchlistEntry` (never deleted: removal keeps who, when and why), `VisitorSettings`. All new tables are under row-level security.

Phase 7 adds, in `reporting`: `FeedToken` (name, hash of the secret, prefix for recognition, created/last-used/revoked facts; under row-level security). In `work`: `WorkflowVersion` (numbered rules as JSON, note, who, when; append-only with an INSTEAD OF trigger) and new columns on `WorkSettings` (bitmasks of priorities that skip approval or need verification, completion signature/time/photo requirements). Reports and the audit viewer read existing data and add no other tables.

Phase 11 adds: `work.WorkOrders.SourceKind/SourceId` (what raised a work order, unique per organisation), `energy.AlarmRules.CreateWorkOrder/WorkOrderRequesterId`, `compliance.Obligations.CreateWorkOrderOnFail` and `compliance.Permits.LastOverdueAlertOn`, `leases.Documents` (files with a hash and a deletion reason), `ai.Feedback` (append-only: used or set aside, no words), and `dbo.RateLimitWindows` (the shared counter for secret-address routes, not owned by any organisation). Visitors' e-mail and phone are encrypted when a key is configured (the columns are wider to hold the stored form).

The documented `Draft`, `Reopened` states are not used: a request starts `Requested` (or `Approved`), and reopening returns work to `Assigned`/`Approved` (ADR-0010).

Phase 9 adds four schemas, all under row-level security. In `compliance`: `Obligation` (category, place or asset, interval, owner, newest result kept for lists), `Completion` (append-only, with INSTEAD OF trigger), `CompletionDocument`, `PermitType`, `Permit` (type facts copied in so a later change to the type never rewrites a permit) and `PermitEvent` (append-only). In `energy`: `EnergyMeter`, `EnergyReading` and `ReadingVoid` (both append-only), `Tariff`, `AlarmRule`, `AlarmEvent` (append-only) and `Device` (hash of the secret, the meters it may post for). In `projects`: `Project`, `Phase`, `ProjectTask`, `Milestone`, `BudgetLine` and `ProjectLink` (a work order or purchase order by id and number; unique while active; no money is copied). In `leases`: `Lease` (terms, notice and break dates, rent terms and the last warning given). The rent schedule is computed from the lease and not stored.
