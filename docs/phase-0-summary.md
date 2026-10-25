# Phase 0: Foundations (summary)

## Delivered
| Area | What exists |
|---|---|
| Solution | `Fms.sln`: BuildingBlocks, `Fms.Api` host, unit and API test projects; central package management; warnings as errors; NuGet audit on |
| Tenancy | `ITenantContext`/`TenantContext` (tenant cannot be switched, explicit platform scope); `TenantDbContext` with named global query filter, TenantId stamping, cross-tenant write and TenantId-change refusal |
| Audit | `AuditEvent` + `IAppendOnly`: updates and deletes refused on save |
| Storage | `IBlobBackend`/`IFileStore`; tenant-prefixed keys, traversal rejected, write-once objects; local disk, Azure Blob, S3/S3-compatible and in-memory backends chosen by `Storage:Provider`; one contract suite runs against all four (ADR-0007) |
| Cache | `TenantCache` prefixes every key with the tenant |
| Deployment mode | `Deployment:Mode = SaaS | OnPrem`; on-prem requires `SingleTenantId` and `LocalStoragePath` (fails at startup otherwise); license provider abstraction |
| API | `/health/live`, `/health/ready`, `/api/v1/system/info`, `/api/v1/auth/providers`, `/api/v1/me`, RFC 7807 problem details |
| Sign-in | OIDC bearer authentication for multiple providers (Entra ID, Google, generic OIDC), server-side tenant mapping, forged `fms_*` claims stripped, fallback policy requires sign-in and a tenant (ADR-0004) |
| Web | React/TS/Vite shell, i18n (English, Arabic), RTL via `dir`, logical CSS, translation-key parity test |
| Database | `Fms.Platform` module: `Tenants`, `TenantIdentityMappings`, `Users`, `AuditEvents`; EF Core migration; **SQL Server row-level security** (SESSION_CONTEXT filter and block predicates) on tenant tables; **append-only trigger** on `AuditEvents`; DB-backed tenant directory; users created on first sign-in, deactivated users refused; `/health/ready` checks the DB |
| Access control | Permission catalog, system roles per tenant kept in sync by the migrator, `[RequirePermission]` endpoint policies, `GET /roles`, `GET /users`, `PUT /users/{id}/roles` (no self role changes, tenant-scoped roles only, audited in the same transaction), first administrator via `FMS_INITIAL_ADMIN`, `IAuditPort` |
| Outbox and worker | Transactional outbox (`IOutbox.Enqueue` commits with the business change), `Fms.Worker` host: row-locked batch claim, per-message tenant scope, at-least-once delivery, exponential backoff, dead-lettering, lease recovery after a crash, retention purge (ADR-0006) |
| Migrator | `Fms.Migrator` one-shot host: applies migrations, seeds the on-prem tenant, creates the least-privileged app login; idempotent |
| Observability | Serilog JSON logs with `Service` and per-request `TenantId` (no tokens), OpenTelemetry traces and metrics, OTLP export only when an endpoint is configured, outbox counters |
| Packaging | `Dockerfile.api`, `Dockerfile.migrator`, `Dockerfile.worker`, `web/Dockerfile`, `docker-compose.onprem.yml`, CI workflow |

## Tests
131 .NET tests (33 unit, 19 API, 79 integration on real SQL Server via Testcontainers) and 2 web tests pass locally. Covered: cross-tenant read, write, storage, cache and tenant-switch
attempts; signed-token acceptance; and rejection of unsigned, forged-signature, expired, wrong-audience, wrong-issuer and
unmapped-tenant tokens, plus forged `fms_tid` claims. Token tests use a local signing key, not a real identity provider.
Integration tests connect as a restricted login (not the owner). Disabling the security policy and trigger makes 9 of the
isolation tests fail, so they do guard those controls. They need Docker.

## Not done yet (known gaps)
- **Web sign-in**: the React app has no login screen yet (needs a PKCE client, e.g. oidc-client-ts, tested against a real provider).
- **Never tried against real services**: sign-in has only been tested with locally signed tokens (not real Entra or Google); Azure
  Blob only against Azurite; S3 only against S3Mock (the MinIO image was not pullable here); no OTLP collector was run.
- **Tenant onboarding** is manual (rows in `Tenants` and `TenantIdentityMappings`); no admin API or UI. Without a connection
  string the API falls back to the config-backed tenant directory (`Authentication:TenantMappings`) for local dev and tests.
- **Custom roles**: only the five system roles exist; tenant-defined roles and per-site/building scoped permissions come later.
- **On-prem local accounts** (sites with no identity provider) are not supported.
- **Queue**: the SQL outbox is the only queue; Azure Service Bus is not built. No dead-letter inspection/replay tool.
- **Worker** has no health endpoint. The parallel-worker test proves exactly-once claiming, not the lock hints' no-blocking benefit.
- **Bicep/Terraform and Helm** are not written.
- **Licensing**: the on-prem provider is a placeholder that reports unlicensed and gates nothing; signed licenses are roadmap phase 8.
- **Docker images and the compose stack were not built here**: this sandbox's proxy certificate is untrusted inside build
  containers, so NuGet/npm fail there. The migrator and API were run directly against SQL Server instead. CI builds the images.
- **Localization**: Hijri/Gregorian dates and Arabic documents are not started (the UI shell has Arabic and RTL).
- Storage keys use the file name as given; file-name length and type limits belong with the first upload endpoint.
