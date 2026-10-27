# FMS: Enterprise Facility Management System

One product, two delivery models: multi-tenant **SaaS** and customer-hosted **on-premise** (single-tenant).
Status: design phase. See [docs/](docs/).

- [Vision and scope](docs/vision.md)
- [Architecture](docs/architecture.md)
- [Modules](docs/modules.md)
- [Domain and data model](docs/data-model.md)
- [Deployment models: SaaS vs on-premise](docs/deployment-models.md)
- [Roadmap](docs/roadmap.md)
- [Phase 0 summary](docs/phase-0-summary.md)
- [Phase 1 summary](docs/phase-1-summary.md) (locations, assets, meters, documents, API list)
- [Phase 2a summary](docs/phase-2a-summary.md) (requests and work orders: lifecycle, controls, API, gaps)
- [Phase 2b summary](docs/phase-2b-summary.md) (service levels, working calendars, notifications and email)
- [Phase 2c summary](docs/phase-2c-summary.md) (checklists, time and costs, signatures, photos)
- [Phase 2d summary](docs/phase-2d-summary.md) (technician PWA, offline sync, retry-safe writes)
- [Phase 3 summary](docs/phase-3-summary.md) (preventive maintenance: schedules, generation, meter triggers, forecast, compliance)
- [Phase 4 summary](docs/phase-4-summary.md) (inventory: stores, stock ledger, parts on work orders, reorder, stock takes)
- [Phase 5 summary](docs/phase-5-summary.md) (vendors, contracts, purchasing, accounting connection, vendor portal)
- [Phase 6 summary](docs/phase-6-summary.md) (spaces, bookings, visitors)
- [Phase 7 summary](docs/phase-7-summary.md) (reports, BI feed, workflow designer, audit viewer, administration hub)
- [ADRs](docs/adr/)

## Quick start
```
dotnet test Fms.sln
dotnet run --project src/Host/Fms.Api            # SaaS mode, http://localhost:5080
cd web && npm ci && npm run dev                  # http://localhost:5173 (proxies /api to :5080)
scripts/e2e-full.sh                              # full-stack browser test (needs Docker, .NET, Node, Chromium)
cp .env.example .env && docker compose -f docker-compose.onprem.yml up --build   # on-prem topology
```

## Sign-in configuration (example)
```
Authentication__Providers__0__Name=entra
Authentication__Providers__0__Authority=https://login.microsoftonline.com/common/v2.0
Authentication__Providers__0__IssuerPrefix=https://login.microsoftonline.com/
Authentication__Providers__0__ExternalTenantClaim=tid
Authentication__Providers__0__Audience=api://<api-app-id>
Authentication__Providers__0__ClientId=<spa-client-id>
Authentication__Providers__0__Scope="openid profile email offline_access api://<api-app-id>/access_as_user"
Authentication__TenantMappings__0__Provider=entra
Authentication__TenantMappings__0__ExternalTenantId=<customer Entra directory id>
Authentication__TenantMappings__0__TenantId=<FMS tenant id>
```

## Email notifications (optional, worker)
In-app notifications always work. Email is off (`Email__Provider=None`) until configured on the **worker**:
```
Email__Provider=Smtp
Email__Host=smtp.example.com
Email__Port=587
Email__UseSsl=true
Email__User=<account>
Email__Password=<from your secret store>
Email__From=fms@example.com
```
Messages are bilingual (English and Arabic). The worker also watches SLA clocks once a minute and raises the alerts.

## Storage configuration (examples)
```
Storage__Provider=Local                       # on-prem default; uses Deployment__LocalStoragePath
Storage__Provider=AzureBlob  Storage__AzureBlob__AccountUrl=https://<account>.blob.core.windows.net  Storage__AzureBlob__Container=fms
Storage__Provider=S3         Storage__S3__Bucket=fms  Storage__S3__Region=eu-west-1
Storage__Provider=S3         Storage__S3__Bucket=fms  Storage__S3__ServiceUrl=http://minio:9000  Storage__S3__ForcePathStyle=true
```
Set `OTEL_EXPORTER_OTLP_ENDPOINT` to export traces and metrics; logs are JSON on stdout.

## Facilities settings
```
Facilities__PublicBaseUrl=https://fms.example.com     # QR labels encode https://fms.example.com/a/<tag>
Facilities__MaxDocumentBytes=26214400                  # default 25 MB
Facilities__MaxDocumentsPerAsset=200
```

## Web app sign-in
Register the web app at your identity provider as a **public client using authorization code + PKCE** with redirect URI
`https://<your host>/auth/callback`, then add to the API's provider settings (see "Sign-in configuration"):
```
Authentication__Providers__0__ClientId=<web app client id>
Authentication__Providers__0__Scope=openid profile email api://<api-app-id>/access_as_user
```
The web container takes `API_UPSTREAM` (default `http://api:8080`) and `CSP_CONNECT_SRC` (set it to your provider's origin, e.g.
`https://login.example.com`, to tighten the content-security policy).
