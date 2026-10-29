#!/usr/bin/env bash
# Rehearses the on-premise bundle on this machine, for real: build two versions, make their bundles, install the older with the bundled SQL Server, upgrade to the newer
# (with the backup the script takes), enter some data, roll back and check the data entered after the backup is gone.  scripts/rehearse-onprem.sh [--host-build]
# Needs Docker, the .NET SDK and Node. Uses port 18080 (RHEARSAL_PORT to change) and the compose project name "fms": do not run it where a real installation lives.
set -euo pipefail
cd "$(dirname "$0")/.."
ROOT=$(pwd)
OLD=0.1.0; NEW=0.1.1
PORT=${REHEARSAL_PORT:-18080}
export FMS_REGISTRY=local
HOSTFLAG=""; [ "${1:-}" = "--host-build" ] && HOSTFLAG="--host"
WORK=$(mktemp -d)
cleanup() { for v in $OLD $NEW; do (cd "$WORK/fms-onprem-$v" 2>/dev/null && docker compose --profile bundled-sql down -v >/dev/null 2>&1) || true; done; rm -rf "$WORK"; }
trap cleanup EXIT
step() { echo; echo "##### $*"; }
fail() { echo "REHEARSAL FAILED: $*" >&2; exit 1; }
version_is() { curl -fsS "http://localhost:$PORT/api/v1/system/info" | grep -q "\"version\":\"$1\"" || fail "the running version is not $1"; }
sql() { docker exec -i fms-sql-1 /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "$SA" -h -1 -b "$@"; }

step "Building images $OLD and $NEW"
scripts/build-local-images.sh "$OLD" $HOSTFLAG
scripts/build-local-images.sh "$NEW" $HOSTFLAG
step "Making the bundles"
scripts/make-onprem-bundle.sh "$OLD" >/dev/null
scripts/make-onprem-bundle.sh "$NEW" >/dev/null
tar -C "$WORK" -xzf "dist/fms-onprem-$OLD.tar.gz"; tar -C "$WORK" -xzf "dist/fms-onprem-$NEW.tar.gz"

SA='Reh3arsal#Passw0rd!2026'
cd "$WORK/fms-onprem-$OLD"
cp .env.example .env
for kv in "FMS_REGISTRY=local" "FMS_TENANT_ID=$(cat /proc/sys/kernel/random/uuid)" "FMS_PUBLIC_URL=http://localhost:$PORT" "FMS_HTTP_PORT=$PORT" "FMS_AUTH_CLIENT_ID=rehearsal" \
          "MSSQL_SA_PASSWORD=$SA" "FMS_APP_LOGIN_PASSWORD=Reh3arsalApp#Passw0rd!2026" "FMS_INITIAL_ADMIN=corp:rehearsal-admin"; do
  k=${kv%%=*}; v=${kv#*=}; sed -i "s|^$k=.*|$k=$v|" .env
done

step "Installing $OLD"
./install.sh
version_is "$OLD"
curl -fsS "http://localhost:$PORT/" >/dev/null || fail "the web tier does not answer"
[ "$(curl -s -o /dev/null -w '%{http_code}' "http://localhost:$PORT/api/v1/work-orders")" = 401 ] || fail "the API did not refuse a request without a sign-in"
[ "$(sql -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM Fms.dbo.Tenants" | tr -d ' \r\n')" = 1 ] || fail "the tenant was not seeded"

step "Upgrading to $NEW"
cp .env "$WORK/fms-onprem-$NEW/.env"
(cd "$WORK/fms-onprem-$NEW" && ./upgrade.sh --yes)
version_is "$NEW"
BACKUP=$(ls "$WORK"/fms-onprem-* >/dev/null 2>&1; docker exec fms-sql-1 sh -c 'ls -t /var/opt/mssql/backup/*.bak | head -1' | xargs -n1 basename)
[ -n "$BACKUP" ] || fail "no backup was made"
sql -Q "USE Fms; CREATE TABLE dbo.RehearsalMarker (Note nvarchar(50)); INSERT INTO dbo.RehearsalMarker VALUES (N'after the upgrade');" >/dev/null

step "Rolling back to $OLD with $BACKUP"
(cd "$WORK/fms-onprem-$OLD" && echo RESTORE | ./rollback.sh "$BACKUP" "$OLD")
version_is "$OLD"
if sql -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM Fms.dbo.RehearsalMarker" >/dev/null 2>&1; then fail "data entered after the backup survived the rollback"; fi

step "A second install over a finished one is refused"
if (cd "$WORK/fms-onprem-$OLD" && ./install.sh >/dev/null 2>&1); then fail "install ran over an installed system"; fi
echo; echo "REHEARSAL PASSED: install, upgrade with backup, and rollback all worked."
