# Vision and scope

## Goal
An enterprise facility management platform covering the full lifecycle of buildings, assets, maintenance, spaces and
services, deployable as multi-tenant SaaS or installed on-premise from the same codebase.

## Users
Facility managers, technicians (mobile, often offline), requesters/employees, contractors/vendors, store keepers,
reception/security, finance/procurement, executives (dashboards), tenant/system administrators.

## Principles
1. One codebase; the deployment model is configuration, never a fork (ADR-0002).
2. Tenant isolation at every layer (SaaS); on-premise runs the same code as a single tenant.
3. Auditable: append-only audit trail on work orders, assets, approvals and cost.
4. Offline-capable field work (mobile/PWA with sync).
5. Localization first: Arabic/RTL, Hijri/Gregorian, multi-currency, multi-timezone, i18n for all strings.
6. Integrations behind interfaces (ERP, IoT/BMS, identity, email, storage) so either deployment can swap providers.
7. Simple, well-tested modular monolith before any service split.

## In scope for phase 1
Core hierarchy and assets, help desk and work orders, preventive maintenance, inventory, vendors and contracts,
space management, visitors, bookings. Later: energy/IoT, compliance & inspections, projects, lease management, CAFM
integrations, BIM/floorplan, AI features.

## Non-goals (v1)
Building automation control (read-only IoT ingestion only), payroll, full accounting (ERP integration instead).
