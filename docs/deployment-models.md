# Deployment models

| Aspect | SaaS | On-premise |
|---|---|---|
| Tenancy | Multi-tenant (shared DB or dedicated DB per tenant) | Single tenant |
| Packaging | Managed on Azure (portable), IaC via Bicep/Terraform | Docker images, Helm chart, docker-compose |
| Database | Azure SQL | Customer SQL Server (2022+) |
| Identity | Entra ID, Google, OIDC | Customer OIDC/AD FS/Entra; local accounts fallback |
| Storage | Blob / S3 | Local volume, MinIO or S3-compatible |
| Messaging | Service Bus | RabbitMQ or SQL-backed queue |
| Secrets | Key Vault | Environment secrets / Vault / Kubernetes secrets |
| Licensing | Plans and entitlements, usage metering | Signed license file (modules, users, expiry) |
| Updates | Continuous, zero-downtime | Versioned releases, migrator job, upgrade guide, rollback backup |
| Telemetry | Central OpenTelemetry | Local OTLP endpoint; opt-in anonymous usage |
| Data residency | Region selection per tenant | Customer controlled |

## How one codebase serves both
- Config switch `Deployment:Mode = SaaS | OnPrem`, applied in composition root only; domain code is unaware.
- Every infrastructure concern (storage, queue, email, secrets, identity, license) is an interface with swappable adapters.
- Same EF migrations for both; the migrator runs as a one-shot container/job.
- CI builds and tests both modes (including tenant-isolation tests) and publishes images plus on-prem bundle.
- Air-gapped support: no mandatory outbound calls; offline license validation; bundled images.
- Hybrid option: on-prem core with optional SaaS portal/mobile relay (future, ADR needed).
