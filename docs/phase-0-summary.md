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
| Packaging | `Dockerfile.api`, `web/Dockerfile`, `docker-compose.onprem.yml`, CI workflow |

## Tests
46 .NET tests (29 unit, 17 API) and 2 web tests pass locally. Covered: cross-tenant read, write, storage, cache and tenant-switch
attempts; signed-token acceptance; and rejection of unsigned, forged-signature, expired, wrong-audience, wrong-issuer and
unmapped-tenant tokens, plus forged `fms_tid` claims. Token tests use a local signing key, not a real identity provider.

## Not done yet (known gaps, still Phase 0 or later)
- **Web sign-in**: the React app has no login screen yet (needs PKCE client, e.g. oidc-client-ts, tested against a real provider).
- **Users and roles**: no user table, roles or permissions; a token proves identity only. Tenant directory is configuration-backed
  (`Authentication:TenantMappings`), not yet database-backed. Sign-in has not been tried against real Entra/Google.
- **On-prem local accounts** (sites with no identity provider) are not supported.
- **SQL Server**: tenant isolation tests use the EF in-memory provider. No migrations, no SQL Server context, no Testcontainers
  tests, no SQL row-level security yet.
- **Azure Blob / S3 backends, durable queue, outbox, worker and migrator hosts**, OpenTelemetry/Serilog, Bicep/Helm.
- **Licensing**: on-prem provider is a placeholder that reports unlicensed; signed license validation is roadmap phase 8.
- **Docker images and compose file were not built or run here** (no Docker in this environment); CI builds the images.
- Hijri/Gregorian date handling and Arabic documents are not started.
