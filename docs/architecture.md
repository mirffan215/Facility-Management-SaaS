# Architecture

## Stack (aligned with VPMS)
- Backend: ASP.NET Core 10 Web API, Clean Architecture, modular monolith
- Frontend: React + TypeScript (Vite), RTL/Arabic, i18n; technician app as an offline-first PWA
- Database: SQL Server / Azure SQL, EF Core 10 (migrations in source control)
- Auth: Microsoft Entra ID, Google, generic OIDC; on-prem adds LDAP/AD FS via OIDC bridge
- Storage: abstraction over Azure Blob, S3-compatible, and local file system/MinIO (on-prem)
- Background: durable queue abstraction + .NET Worker Services (Azure Service Bus / RabbitMQ / SQL-backed outbox)
- Email: Microsoft Graph, SMTP, transactional provider behind an interface
- Observability: structured logging, OpenTelemetry, health checks
- API: versioned REST, OpenAPI, RFC 7807 problem details
- IaC: Bicep or Terraform (SaaS); Helm chart + docker-compose (on-prem). Containers for everything.

## Layers per module
`Domain` (entities, rules, no dependencies) -> `Application` (use cases, CQRS handlers, interfaces) ->
`Infrastructure` (EF Core, adapters) -> `Api`. Modules communicate through public contracts and integration events
(outbox pattern), never through each other's tables.

## Solution layout
```
src/
  BuildingBlocks/        tenancy, auditing, outbox, workflow, files, notifications, licensing
  Modules/
    Core/ (org, sites, spaces, assets)   Helpdesk/   WorkOrders/   Maintenance/
    Inventory/   VendorsContracts/   Spaces/ (space mgmt, bookings)   Visitors/
    Reporting/   Administration/
  Host.Api/  Host.Worker/  Host.Migrator/
web/  (admin app, technician PWA, portal)
tests/ (unit, integration w/ Testcontainers, API, tenant-isolation)
infra/ (bicep|terraform, helm, compose)
docs/
```

## Tenancy
- Hierarchy: Tenant (customer/group) -> Organization/Legal entity -> Site -> Building -> Floor -> Space.
- Every row carries `TenantId`; EF global query filters enforced; tenant-scoped storage paths, cache keys, job payloads.
- SaaS: shared database with row-level isolation by default; optional dedicated database per tenant for large customers.
- On-prem: same schema, one tenant, `Deployment:Mode=OnPrem` disables tenant onboarding and enables licensing.
- Automated tests must prove cross-tenant access fails.

## Cross-cutting
- **Authorization**: RBAC plus scoped permissions (by site/building/discipline); policy-based checks.
- **Workflow engine**: data-driven, versioned (request approval, WO approval, purchase approval, visitor pre-approval).
- **SLA engine**: calendars, response/resolution timers, escalation, pause rules.
- **Audit**: immutable append-only log of entity changes and security events.
- **Custom fields & forms**: per-tenant metadata on assets, requests, work orders (JSON column + validation schema).
- **Notifications**: email, in-app, push, SMS/WhatsApp, Teams-ready.
- **Offline sync**: technician PWA caches assigned work orders, checklists and asset data; conflict rules are
  server-wins for status, merge for notes/readings, with idempotent client-generated IDs.
- **Integration**: webhooks, REST/OData read APIs, ERP adapter interface (`IErpAdapter`, Dynamics 365 first),
  IoT ingestion (MQTT/HTTP) behind `ITelemetryIngestor`, QR/barcode/NFC asset tags.
- **Feature flags / entitlements**: modules and limits enabled per plan (SaaS) or license file (on-prem).

## Security
OWASP ASVS L2, least privilege, secrets in Key Vault (or environment/secret store on-prem), encryption at rest and in
transit, column-level encryption for sensitive fields, ISO 27001 / SOC 2 aligned controls.
