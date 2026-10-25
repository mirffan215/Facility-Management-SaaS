# ADR-0005: Database-enforced tenant isolation
Status: Proposed

## Decision
- Defense in depth: EF global query filters (application) plus SQL Server **row-level security** (database). Every tenant-owned
  table has a `TenantId` column and is added to one security policy by migration (`ProtectTenantTable`): a filter predicate and
  block predicates for insert, update and delete.
- The tenant reaches the database through `SESSION_CONTEXT`, set each time a connection is opened (so pooled connections
  never carry a previous tenant). No tenant means no rows. Platform scope (migrations, schedulers) sees all rows and must
  set `TenantId` explicitly on writes.
- The application connects as a login with read/write only. It cannot alter the policy, drop the trigger or change schema;
  migrations run under a separate owner login (the migrator).
- `AuditEvents` is append-only in the database (INSTEAD OF UPDATE/DELETE trigger) as well as in EF.
- Platform tables (`Tenants`, `TenantIdentityMappings`) are not under RLS because they must be read before a tenant is known.
  They are written only by platform operations.

## Consequences
- The policy is SCHEMABINDING: changing a protected table's `TenantId` column needs the table unprotected first.
- A dedicated database per tenant (SaaS enterprise option) can reuse the same schema; RLS then protects only against bugs.
- Integration tests must run as the restricted login, never the owner.
