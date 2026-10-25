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
- [ADRs](docs/adr/)

## Quick start
```
dotnet test Fms.sln
dotnet run --project src/Host/Fms.Api            # SaaS mode, http://localhost:5080
cd web && npm ci && npm run dev                  # http://localhost:5173
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
Authentication__TenantMappings__0__Provider=entra
Authentication__TenantMappings__0__ExternalTenantId=<customer Entra directory id>
Authentication__TenantMappings__0__TenantId=<FMS tenant id>
```
