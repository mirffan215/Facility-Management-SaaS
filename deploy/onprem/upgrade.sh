#!/usr/bin/env bash
# Upgrade an installation to the version in this (new) bundle.
#   ./upgrade.sh [--backup-confirmed] [--yes]
# Steps: check -> back up -> stop the app -> migrate -> start -> smoke test. The old version is never started against a migrated database
# (the migrator refuses), so going back means ./rollback.sh with the backup this script makes (or the one you confirmed).
set -euo pipefail
cd "$(dirname "$0")"
. ./lib.sh

CONFIRMED=0; YES=0
for a in "$@"; do case "$a" in --backup-confirmed) CONFIRMED=1;; --yes) YES=1;; *) die "unknown option $a";; esac; done

NEW=$(cat VERSION 2>/dev/null || true); valid_version "$NEW" || die "VERSION file missing or invalid: run this from the new bundle."
[ -f .env ] || die "no .env file here. Copy it from the old installation folder."
OLD=$(env_get FMS_VERSION); valid_version "$OLD" || die "FMS_VERSION in .env is missing: is this an installed system? (use ./install.sh for a new one)"
need_env

say "Checking"
[ "$OLD" != "$NEW" ] || die "version $NEW is already installed."
! version_lt "$NEW" "$OLD" || die "refusing to go from $OLD down to $NEW. To go back, use ./rollback.sh with a backup taken before the upgrade."
FREE_KB=$(df -Pk . | awk 'NR==2{print $4}')
[ "$FREE_KB" -gt 5242880 ] || die "less than 5 GB free on this disk; free some space first."
command -v docker >/dev/null || die "Docker is not installed."
[ -d images ] && ./load-images.sh

LICENSE_FILE=license/fms.license
[ -f "$LICENSE_FILE" ] && say "License file present." || echo "NOTE: no license file; the system will be read-only after the upgrade until one is installed."

say "What will change"
FMS_VERSION="$NEW" compose --profile migrate run --rm migrator --check || die "the database cannot be upgraded by this version (see above). Nothing was changed."

if [ "$YES" -ne 1 ]; then read -r -p "Upgrade $OLD -> $NEW now? The system is unavailable for a few minutes. [y/N] " ans; [ "$ans" = "y" ] || die "cancelled."; fi

BACKUP=""
if uses_bundled_sql; then
  say "Backing up the database (inside the bundled SQL Server)"
  BACKUP="fms-before-$NEW-$(date -u +%Y%m%d-%H%M%S).bak"
  # A new volume is owned by root, and SQL Server runs as its own user: give it the backup folder before it writes there.
  compose exec -T -u 0 sql sh -c 'mkdir -p /var/opt/mssql/backup && chown mssql:root /var/opt/mssql/backup && chmod 770 /var/opt/mssql/backup' || die "could not prepare the backup folder; nothing was changed."
  compose exec -T sql /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "$(env_get MSSQL_SA_PASSWORD)" -b \
    -Q "BACKUP DATABASE [Fms] TO DISK = N'/var/opt/mssql/backup/$BACKUP' WITH COMPRESSION, CHECKSUM, INIT" || die "the backup failed; nothing was changed."
  compose exec -T sql /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "$(env_get MSSQL_SA_PASSWORD)" -b \
    -Q "RESTORE VERIFYONLY FROM DISK = N'/var/opt/mssql/backup/$BACKUP' WITH CHECKSUM" || die "the backup could not be verified; nothing was changed."
  say "Backup verified: $BACKUP (volume sqlbackup)"
elif [ "$CONFIRMED" -eq 1 ]; then
  say "Using your confirmation that a verified database backup was taken just now."
else
  die "this installation uses your own SQL Server. Take a full backup of the Fms database, verify it, then run again with --backup-confirmed."
fi

say "Stopping the application"
compose stop web api worker
say "Migrating the database"
env_set FMS_VERSION "$NEW"
if ! compose --profile migrate run --rm migrator; then
  env_set FMS_VERSION "$OLD"
  echo "The migration FAILED. The database may be partly changed. Restore the backup: ./rollback.sh ${BACKUP:-<your backup>} $OLD" >&2
  exit 1
fi
say "Starting version $NEW"
compose up -d api worker web
PORT=$(env_get FMS_HTTP_PORT); PORT=${PORT:-8080}
if ! wait_ready "http://localhost:$PORT" 90; then
  echo "The new version did not become ready. See: docker compose logs api. To go back: ./rollback.sh ${BACKUP:-<your backup>} $OLD" >&2
  exit 1
fi

say "Smoke test"
INFO=$(curl -fsS "http://localhost:$PORT/api/v1/system/info") || die "system info did not answer. To go back: ./rollback.sh ${BACKUP:-<your backup>} $OLD"
echo "$INFO"
echo "$INFO" | grep -q "\"version\":\"$NEW" || die "the running version is not $NEW. To go back: ./rollback.sh ${BACKUP:-<your backup>} $OLD"
echo "$NEW" > .installed-version
say "Upgraded to $NEW. Keep $BACKUP until you are happy with the new version."
