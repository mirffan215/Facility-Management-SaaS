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
cleanup() { for p in "${PIDS[@]:-}"; do kill "$p" 2>/dev/null || true; done; docker rm -f fms-e2e-sql fms-e2e-web >/dev/null 2>&1 || true; rm -rf "$STORAGE"; }
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
# Licenses for the run, signed with the development key: the browser test swaps these to see the installation go read-only and recover.
LICENSE_DIR="$LOGS/license"; rm -rf "$LICENSE_DIR"; mkdir -p "$LICENSE_DIR"
LIC="dotnet tools/Fms.LicenseTool/bin/Debug/net10.0/fms-license.dll"
KEY=tools/license/dev-2026.private.b64
YESTERDAY=$(date -u -d 'yesterday' +%F 2>/dev/null || date -u -v-1d +%F)
$LIC sign --key $KEY --key-id dev-2026 --customer "E2E Estates" --edition Professional --modules '*' --expires 2099-12-31 --max-users 50 --tenant $TENANT --out "$LICENSE_DIR/valid.license" >/dev/null
$LIC sign --key $KEY --key-id dev-2026 --customer "E2E Estates" --edition Professional --modules '*' --expires "$YESTERDAY" --not-before 2020-01-01 --grace-days 30 --tenant $TENANT --out "$LICENSE_DIR/grace.license" >/dev/null
$LIC sign --key $KEY --key-id dev-2026 --customer "E2E Estates" --edition Professional --modules '*' --expires 2021-01-01 --not-before 2020-01-01 --grace-days 0 --tenant $TENANT --out "$LICENSE_DIR/expired.license" >/dev/null
cp "$LICENSE_DIR/valid.license" "$LICENSE_DIR/fms.license"
export E2E_LICENSE_DIR="$LICENSE_DIR"

ConnectionStrings__Migrator="Server=localhost,$SQL_PORT;Database=Fms;User Id=sa;Password=$SA_PASSWORD;TrustServerCertificate=True" \
  Deployment__Mode=OnPrem Deployment__SingleTenantId=$TENANT Deployment__LocalStoragePath="$STORAGE" Deployment__TenantName=E2E \
  FMS_APP_LOGIN=fms_app FMS_APP_LOGIN_PASSWORD="$APP_PASSWORD" FMS_INITIAL_ADMIN=fake:e2e-admin \
  dotnet run --project src/Host/Fms.Migrator --no-build > "$LOGS/migrator.log" 2>&1 || { cat "$LOGS/migrator.log"; exit 1; }

node web/e2e/fake-idp.mjs > "$LOGS/idp.log" 2>&1 & PIDS+=($!)

ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://0.0.0.0:5080 \
ConnectionStrings__Default="Server=localhost,$SQL_PORT;Database=Fms;User Id=fms_app;Password=$APP_PASSWORD;TrustServerCertificate=True" \
Deployment__Mode=OnPrem Deployment__SingleTenantId=$TENANT Deployment__LocalStoragePath="$STORAGE" Storage__Provider=Local \
Authentication__AllowInsecureAuthority=true Authentication__Providers__0__Name=fake Authentication__Providers__0__Authority=http://localhost:9099 \
Authentication__Providers__0__Audience=api://fms Authentication__Providers__0__ClientId=fms-web Authentication__Providers__0__Scope="openid profile email offline_access" \
License__Path="$LICENSE_DIR/fms.license" Facilities__PublicBaseUrl=http://localhost:5173 Erp__AllowFakeAdapter=true Calendar__AllowFakeAdapter=true \
Ai__Provider=Claude Ai__ApiKey=e2e-ai-key Ai__Endpoint=http://localhost:9099 \
  dotnet src/Host/Fms.Api/bin/Debug/net10.0/Fms.Api.dll > "$LOGS/api.log" 2>&1 & PIDS+=($!)

# The worker delivers notifications (outbox) and watches SLA clocks.
DOTNET_ENVIRONMENT=Development \
ConnectionStrings__Default="Server=localhost,$SQL_PORT;Database=Fms;User Id=fms_app;Password=$APP_PASSWORD;TrustServerCertificate=True" \
Deployment__Mode=OnPrem Deployment__SingleTenantId=$TENANT Deployment__LocalStoragePath="$STORAGE" Storage__Provider=Local Erp__AllowFakeAdapter=true Calendar__AllowFakeAdapter=true \
  dotnet src/Host/Fms.Worker/bin/Debug/net10.0/Fms.Worker.dll > "$LOGS/worker.log" 2>&1 & PIDS+=($!)

# The web tier is the production nginx image (headers, content-security policy, /api proxy) unless WEB_TIER=vite is set for a quicker run.
(cd web && npm run build >/dev/null)
if [ "${WEB_TIER:-nginx}" = "vite" ]; then
  (cd web && exec node node_modules/vite/bin/vite.js preview --port 5173 --strictPort) > "$LOGS/web.log" 2>&1 & PIDS+=($!)
else
  docker rm -f fms-e2e-web >/dev/null 2>&1 || true
  docker run -d --name fms-e2e-web --add-host=host.docker.internal:host-gateway -p 5173:8080 \
    -e API_UPSTREAM=http://host.docker.internal:5080 -e CSP_CONNECT_SRC=http://localhost:9099 -e NGINX_ENVSUBST_FILTER='API_UPSTREAM|CSP_CONNECT_SRC' \
    -v "$ROOT/web/nginx.conf.template:/etc/nginx/templates/default.conf.template:ro" -v "$ROOT/web/dist:/usr/share/nginx/html:ro" "${WEB_IMAGE:-nginxinc/nginx-unprivileged:alpine}" >/dev/null
fi

for url in http://localhost:9099/__health http://localhost:5080/health/ready http://localhost:5173/; do
  for i in $(seq 1 60); do curl -fs "$url" >/dev/null 2>&1 && break; sleep 1; done
done

# ZAP_BASELINE=1 runs an OWASP ZAP baseline scan (passive, spidering the signed-out web app and the API's anonymous surface) against the stack instead of the browser tests.
# The report lands in $LOGS. Findings do not fail the run (-I): they are read and triaged, see docs/security/zap.md.
if [ -n "${ZAP_BASELINE:-}" ]; then
  mkdir -p "$LOGS/zap" && chmod 777 "$LOGS/zap"
  docker run --rm --network host -v "$LOGS/zap":/zap/wrk:rw ghcr.io/zaproxy/zaproxy:stable \
    zap-baseline.py -t http://localhost:5173 -J zap.json -r zap.html -w zap.md -I || true
  echo "ZAP report: $LOGS/zap/zap.html"
  exit 0
fi

cd web
# Use the preinstalled browsers on dev boxes that have them; CI installs its own with `npx playwright install chromium`.
if [ -z "${PLAYWRIGHT_BROWSERS_PATH:-}" ] && [ -d /opt/pw-browsers ]; then export PLAYWRIGHT_BROWSERS_PATH=/opt/pw-browsers; fi
# Chromium on Linux needs a UTF-8 locale to name downloads with non-ASCII (e.g. Arabic) file names.
export LANG=${LANG:-C.UTF-8} LC_ALL=${LC_ALL:-C.UTF-8}
npx playwright test -c playwright.full.config.ts "$@"
