# Phase 0: Foundations (summary)

## Delivered
| Area | What exists |
|---|---|
| Solution | `Fms.sln`: BuildingBlocks, `Fms.Api` host, unit and API test projects; central package management; warnings as errors; NuGet audit on |
| Tenancy | `ITenantContext`/`TenantContext` (tenant cannot be switched, explicit platform scope); `TenantDbContext` with named global query filter, TenantId stamping, cross-tenant write and TenantId-change refusal |
| Audit | `AuditEvent` + `IAppendOnly`: updates and deletes refused on save |
| Storage | `IBlobBackend`/`IFileStore`; tenant-prefixed keys, traversal rejected; in-memory and local-disk (on-prem) backends |
| Cache | `TenantCache` prefixes every key with the tenant |
| Deployment mode | `Deployment:Mode = SaaS | OnPrem`; on-prem requires `SingleTenantId` and `LocalStoragePath` (fails at startup otherwise); license provider abstraction |
| API | `/health/live`, `/health/ready`, `/api/v1/system/info`, `/api/v1/auth/providers`, `/api/v1/me`, RFC 7807 problem details |
| Sign-in | OIDC bearer authentication for multiple providers (Entra ID, Google, generic OIDC), server-side tenant mapping, forged `fms_*` claims stripped, fallback policy requires sign-in and a tenant (ADR-0004) |
| Web | React/TS/Vite shell, i18n (English, Arabic), RTL via `dir`, logical CSS, translation-key parity test |
| Database | `Fms.Platform` module: `Tenants`, `TenantIdentityMappings`, `Users`, `AuditEvents`; EF Core migration; **SQL Server row-level security** (SESSION_CONTEXT filter and block predicates) on tenant tables; **append-only trigger** on `AuditEvents`; DB-backed tenant directory; users created on first sign-in, deactivated users refused; `/health/ready` checks the DB |
| Access control | Permission catalog, system roles per tenant kept in sync by the migrator, `[RequirePermission]` endpoint policies, `GET /roles`, `GET /users`, `PUT /users/{id}/roles` (no self role changes, tenant-scoped roles only, audited in the same transaction), first administrator via `FMS_INITIAL_ADMIN`, `IAuditPort` |
| Migrator | `Fms.Migrator` one-shot host: applies migrations, seeds the on-prem tenant, creates the least-privileged app login; idempotent |
| Packaging | `Dockerfile.api`, `web/Dockerfile`, `docker-compose.onprem.yml`, CI workflow |

## Tests
86 .NET tests (29 unit, 17 API, 40 integration on real SQL Server via Testcontainers) and 2 web tests pass locally. Covered: cross-tenant read, write, storage, cache and tenant-switch
attempts; signed-token acceptance; and rejection of unsigned, forged-signature, expired, wrong-audience, wrong-issuer and
unmapped-tenant tokens, plus forged `fms_tid` claims. Token tests use a local signing key, not a real identity provider.
Integration tests connect as a restricted login (not the owner). Disabling the security policy and trigger makes 9 of the
isolation tests fail, so they do guard those controls. They need Docker.

## Not done yet (known gaps, still Phase 0 or later)
- **Web sign-in**: the React app has no login screen yet (needs PKCE client, e.g. oidc-client-ts, tested against a real provider).
- **Custom roles**: only the five system roles exist; tenant-defined roles and per-site/building scoped permissions are later; a token proves identity only. Tenant onboarding is manual (rows in `Tenants`
  and `TenantIdentityMappings`); no admin API or UI. Without a connection string the API falls back to the config-backed directory
  (`Authentication:TenantMappings`) for local dev and tests. Sign-in has not been tried against real Entra/Google.
- **On-prem local accounts** (sites with no identity provider) are not supported.
- **Azure Blob / S3 backends, durable queue, outbox, worker and migrator hosts**, OpenTelemetry/Serilog, Bicep/Helm.
- **Licensing**: on-prem provider is a placeholder that reports unlicensed; signed license validation is roadmap phase 8.
- **Docker images and the compose stack were not built here**: this sandbox's proxy certificate is untrusted inside build containers, so
  NuGet/npm fail there. The migrator and API were run directly against a SQL Server container instead. CI builds the images.
- Hijri/Gregorian date handling and Arabic documents are not started.
