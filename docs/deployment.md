# Deployment guide

Three supported ways to run the same four images (`fms-api`, `fms-worker`, `fms-web`, `fms-migrator`): **Docker Compose** (one server, on-premise), **Helm** (Kubernetes), **Azure Bicep** (Azure Container Apps, SaaS or single organisation). Choose by where it runs, not by features: they are the same product. Design reasons: [ADR-0025](adr/0025-packaging-and-upgrades.md).

## What you need in every case
- **SQL Server 2022+ or Azure SQL.** An owner login (used only to upgrade) and the application's own least-privileged login (created by the migrator).
- **An OpenID Connect identity provider** (Entra ID, Google, AD FS, Keycloak…). The web app is a public client (PKCE). The provider must enforce multi-factor sign-in; the product does not store passwords.
- **TLS in front of the web container.** Run it behind your load balancer, ingress controller or reverse proxy that terminates HTTPS and sets `X-Forwarded-For` and `X-Forwarded-Proto`.
- **A license file** (see [licensing](licensing.md)). Without one the system starts but is read-only.
- **Encryption at rest** for the database and the file storage is provided by your platform (TDE or disk/storage encryption). Turn it on.
- **Backups** of the database and the file storage. The upgrade script makes a database backup for the bundled SQL Server; for anything else, backups are yours.

## 1. Docker Compose (on-premise bundle)
Download `fms-onprem-<version>.tar.gz` (and check it: `sha256sum -c fms-onprem-<version>.tar.gz.sha256`). An offline bundle carries the images; an online bundle pulls them from the registry.
```
tar xzf fms-onprem-<version>.tar.gz && cd fms-onprem-<version>
cp .env.example .env        # fill it in (tenant id, address, sign-in, database)
cp /path/to/fms.license license/fms.license
./install.sh
```
`install.sh` loads the images (offline bundle), sets up the database, starts everything and waits until it is ready. Put your TLS proxy in front of port `FMS_HTTP_PORT` (default 8080). The first administrator is `FMS_INITIAL_ADMIN`; they make the others in **Users**.

With your own SQL Server: leave `MSSQL_SA_PASSWORD` empty and set `FMS_MIGRATOR_CONNECTION` and `FMS_APP_CONNECTION`. The bundled SQL Server (profile `bundled-sql`) is for evaluation and small sites.

Containers run read-only, without capabilities and without privilege escalation. Files live in the `files` volume: back it up with the database.

## 2. Kubernetes (Helm)
```
kubectl create namespace fms
kubectl -n fms create secret generic fms-database --from-literal=app='Server=...;Database=Fms;User Id=fms_app;Password=...;Encrypt=True' --from-literal=migrator='Server=...;Database=Fms;User Id=...;Password=...;Encrypt=True'
kubectl -n fms create secret generic fms-license --from-file=fms.license=./fms.license
helm install fms oci://ghcr.io/mirffan215/charts/fms --version <version> -n fms -f my-values.yaml
```
Required values: `deployment.tenantId` (OnPrem), `auth.providers`, `ingress.host`. See `deploy/helm/fms/values.yaml` for all settings and `ci-values.yaml` for a worked example. A pre-install/pre-upgrade **Job** runs the migrator before new pods start. Pods run non-root with a read-only root file system; `networkPolicy.enabled` restricts traffic to ingress → web → api. Files need a `ReadWriteMany` volume (or an object store via `storage.extraEnv`, see the settings in `StorageOptions`). Upgrade with `helm upgrade` (see [upgrading](upgrading.md)).

## 3. Azure (Bicep)
`deploy/azure/main.bicep` creates the Container Apps environment and apps, the migrator job, Azure SQL, a storage account (blobs, no shared keys), Key Vault (connection strings, read by a managed identity) and Log Analytics.
```
export FMS_SQL_ADMIN_PASSWORD=... FMS_SQL_APP_PASSWORD=...
az group create -n fms-prod -l westeurope
az deployment group create -g fms-prod -f deploy/azure/main.bicep -p deploy/azure/main.example.bicepparam
az containerapp job start -n fmsprod-migrate -g fms-prod     # run once now, and before each upgrade
```
Before production: restrict the SQL server's network (private endpoint or firewall rules; the template allows Azure services only), put a custom domain and certificate on the web app, and put the license file where the API reads it (an Azure Files mount or `License__Text` from a Key Vault secret). **This template is checked by the compiler in CI but has not been deployed by the development team.**

## Settings reference (environment names)
| Setting | Meaning |
|---|---|
| `Deployment__Mode` | `OnPrem` (one organisation, needs `Deployment__SingleTenantId`) or `SaaS` |
| `ConnectionStrings__Default` | the application login (API, worker) |
| `ConnectionStrings__Migrator` | the owner login (migrator only) |
| `Authentication__Providers__N__*` | `Name`, `Authority`, `Audience`, `ClientId`, `Scope`; multi-organisation providers also `IssuerPrefix` and `ExternalTenantClaim` |
| `License__Path` / `License__Text` | license file location, or its text |
| `Storage__Provider` | `Local`, `AzureBlob` (`Storage__AzureBlob__AccountUrl`, `__Container`) or `S3` |
| `Security__TrustedProxyCidrs__N` | networks whose forwarded client address is believed |
| `Security__RateLimit__*` | `Enabled`, `PerUserPerMinute`, `AnonymousPerMinute`, `SecretRoutesPerMinute` |
| `Email__*` | `Provider` (`None`/`Smtp`), `Host`, `Port`, `User`, `Password`, `From` |
| `Facilities__PublicBaseUrl` | the address QR labels point at |

## After installing
Sign in as the administrator, open **Administration → License** to check the license, and **Users** to add people. Health: `/health/live` and `/health/ready`. System and version: `/api/v1/system/info`. Logs are structured JSON on standard output.
