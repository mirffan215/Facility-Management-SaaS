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
