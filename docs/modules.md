# Modules

| Module | Key capabilities |
|---|---|
| Core / Locations | Tenant, organizations, sites, buildings, floors, spaces, cost centers, calendars, holidays, geo/timezones |
| Assets | Asset register, hierarchy and classification, QR/NFC tags, warranty, depreciation info, meters, documents, lifecycle and disposal, BOM/spare-part links |
| Helpdesk / Requests | Portal, mobile, email and phone intake, categories, priority, SLA, approval, satisfaction survey, recurring requests |
| Work Orders | Corrective/emergency/planned, assignment and dispatch, crews, skills, checklists, labor/material/contractor cost, time tracking, signatures, photos, closure codes, re-open |
| Preventive Maintenance | Time/meter/condition-based schedules, PM templates and task libraries, auto-generation, forecasting calendar, compliance KPIs |
| Inventory | Stores, items, stock levels, reorder points, issue/return/transfer, reservations on WOs, stock take, valuation, barcode scanning |
| Vendors & Contracts | Vendor register and prequalification, AMC/SLA contracts, rate cards, contract expiry alerts, purchase requests/orders, vendor portal, performance scoring |
| Space Management | Floor plans, occupancy and allocation, move management, space chargeback |
| Bookings | Rooms, desks, parking, resources, calendar sync (M365/Google), check-in, rules and approvals |
| Visitors | Pre-registration, host notification, badge/QR, check-in/out, watchlist, contractor induction |
| Reporting | Fixed KPI pack per area with period and site filters, CSV export, read-only OData BI feed with revocable tokens (Phase 7). Not yet: MTBF, scheduled exports, report builder |
| Administration | Roles, workflow designer (versioned rules inside fixed safety limits), SLA, custom fields, audit viewer with export, Administration hub, integrations (Phase 7). Not yet: template editor, licensing (Phase 8) |

Later modules: Energy & Utilities (IoT), Inspections & Compliance (HSE, permits-to-work), Projects/CAPEX,
Lease & Property, Cleaning & Soft Services, Security Rounds, BIM/Digital Twin, AI (predictive maintenance, triage).

## Key business controls
- Approval limits by role and cost; requester cannot approve own request or purchase.
- Closed work orders locked; changes require reopen with reason (audited).
- Cost postings immutable once exported to ERP; corrections via reversing entries.
