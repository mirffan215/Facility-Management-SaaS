# ADR-0007: Pluggable storage and portable observability
Status: Proposed

## Decision
- **Storage** is one interface (`IBlobBackend`) with adapters selected by configuration: local disk (on-prem default), Azure Blob,
  S3 and S3-compatible stores (MinIO, Ceph), and in-memory (tests). The cloud SDKs live in `Fms.BuildingBlocks.Cloud`, outside
  the core. Every backend is wrapped by `TenantScopedFileStore`, so keys stay inside `{tenantId}/...` and traversal is refused.
- Objects are **write-once** (If-None-Match on Azure and S3, create-new on disk). Corrections are new objects, which keeps
  evidence such as invoices and inspection photos immutable. All four adapters pass one shared contract test suite.
- Azure uses managed identity (`AccountUrl`) in production; connection strings and static S3 keys are for development only.
  S3 writes request server-side encryption (AES256).
- **Logs** are structured JSON on stdout (Serilog), enriched with `Service` and, per request, `TenantId`. Tokens and request
  bodies are not logged. **Traces and metrics** use OpenTelemetry and are exported over OTLP only when
  `OTEL_EXPORTER_OTLP_ENDPOINT` is set, so air-gapped on-prem installs send nothing out.
- Outbox metrics (`fms.outbox.claimed|succeeded|retried|dead_lettered`) are the first custom meters; all `Fms.*` meters and
  activity sources are exported automatically.

## Consequences
- A store that ignores `If-None-Match` would silently allow overwrites. AWS S3, MinIO, Azure and the S3Mock used in tests enforce
  it; verify any other S3-compatible store with the contract tests before supporting it.
- Server-side encryption keys are the provider's. Customer-managed keys per tenant need a follow-up.
