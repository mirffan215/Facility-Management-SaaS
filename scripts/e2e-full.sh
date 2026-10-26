#!/usr/bin/env bash
# Full-stack browser test: real SQL Server, real migrator, real API, real web build, and a fake OpenID Connect provider (web/e2e/fake-idp.mjs).
# Needs Docker, the .NET SDK, Node and a Playwright Chromium. Usage: scripts/e2e-full.sh [playwright args]
set -euo pipefail
cd "$(dirname "$0")/.."
ROOT=$(pwd)
SA_PASSWORD='E2e#Passw0rd!2026'
APP_PASSWORD='E2eApp#Passw0rd!2026'
TENANT=11111111-2222-3333-4444-555555555555
SQL_PORT=${SQL_PORT:-14339}
PIDS=()
cleanup() { for p in "${PIDS[@]:-}"; do kill "$p" 2>/dev/null || true; done; docker rm -f fms-e2e-sql >/dev/null 2>&1 || true; rm -rf "$STORAGE"; }
STORAGE=$(mktemp -d)
LOGS=${E2E_LOGS:-/tmp/fms-e2e}
mkdir -p "$LOGS"
trap cleanup EXIT

docker rm -f fms-e2e-sql >/dev/null 2>&1 || true
docker run -d --name fms-e2e-sql -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD="$SA_PASSWORD" -p "$SQL_PORT":1433 mcr.microsoft.com/mssql/server:2022-latest >/dev/null
echo "waiting for SQL Server..."
for i in $(seq 1 60); do
  docker exec fms-e2e-sql /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "$SA_PASSWORD" -Q 'SELECT 1' >/dev/null 2>&1 && break
  sleep 2
done

dotnet build Fms.sln -c Debug -v q >/dev/null
ConnectionStrings__Migrator="Server=localhost,$SQL_PORT;Database=Fms;User Id=sa;Password=$SA_PASSWORD;TrustServerCertificate=True" \
  Deployment__Mode=OnPrem Deployment__SingleTenantId=$TENANT Deployment__LocalStoragePath="$STORAGE" Deployment__TenantName=E2E \
  FMS_APP_LOGIN=fms_app FMS_APP_LOGIN_PASSWORD="$APP_PASSWORD" FMS_INITIAL_ADMIN=fake:e2e-admin \
  dotnet run --project src/Host/Fms.Migrator --no-build > "$LOGS/migrator.log" 2>&1 || { cat "$LOGS/migrator.log"; exit 1; }

node web/e2e/fake-idp.mjs > "$LOGS/idp.log" 2>&1 & PIDS+=($!)

ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://localhost:5080 \
ConnectionStrings__Default="Server=localhost,$SQL_PORT;Database=Fms;User Id=fms_app;Password=$APP_PASSWORD;TrustServerCertificate=True" \
Deployment__Mode=OnPrem Deployment__SingleTenantId=$TENANT Deployment__LocalStoragePath="$STORAGE" Storage__Provider=Local \
Authentication__AllowInsecureAuthority=true Authentication__Providers__0__Name=fake Authentication__Providers__0__Authority=http://localhost:9099 \
Authentication__Providers__0__Audience=api://fms Authentication__Providers__0__ClientId=fms-web \
Facilities__PublicBaseUrl=http://localhost:5173 \
  dotnet src/Host/Fms.Api/bin/Debug/net10.0/Fms.Api.dll > "$LOGS/api.log" 2>&1 & PIDS+=($!)

(cd web && npm run build >/dev/null && exec npx vite preview --port 5173 --strictPort) > "$LOGS/web.log" 2>&1 & PIDS+=($!)

for url in http://localhost:9099/__health http://localhost:5080/health/ready http://localhost:5173/; do
  for i in $(seq 1 60); do curl -fs "$url" >/dev/null 2>&1 && break; sleep 1; done
done

cd web
# Use the preinstalled browsers on dev boxes that have them; CI installs its own with `npx playwright install chromium`.
if [ -z "${PLAYWRIGHT_BROWSERS_PATH:-}" ] && [ -d /opt/pw-browsers ]; then export PLAYWRIGHT_BROWSERS_PATH=/opt/pw-browsers; fi
# Chromium on Linux needs a UTF-8 locale to name downloads with non-ASCII (e.g. Arabic) file names.
export LANG=${LANG:-C.UTF-8} LC_ALL=${LC_ALL:-C.UTF-8}
npx playwright test -c playwright.full.config.ts "$@"
