#!/usr/bin/env bash
# Go back to an earlier version by restoring the backup made before the upgrade. Everything entered after that backup is lost.
#   ./rollback.sh <backup file name in the sqlbackup volume> <version to return to>   (bundled SQL Server)
# With your own SQL Server restore the backup yourself, then run:  ./rollback.sh --restored <version to return to>
# Run this from the OLD version's bundle folder (it holds the old compose file and scripts).
set -euo pipefail
cd "$(dirname "$0")"
. ./lib.sh

if [ "${1:-}" = "--restored" ]; then BACKUP=""; TARGET="${2:-}"; else BACKUP="${1:-}"; TARGET="${2:-}"; fi
valid_version "$TARGET" || die "usage: ./rollback.sh <backup file> <version>   or   ./rollback.sh --restored <version>"
[ "$(cat VERSION 2>/dev/null)" = "$TARGET" ] || die "this folder is version $(cat VERSION 2>/dev/null || echo unknown), not $TARGET. Run rollback from the bundle of the version you are returning to."
need_env

if [ -n "$BACKUP" ]; then
  [[ "$BACKUP" =~ ^[A-Za-z0-9._-]+\.bak$ ]] || die "backup name must be a plain file name ending in .bak"
  uses_bundled_sql || die "your database is external: restore the backup there, then use --restored."
  read -r -p "Restore $BACKUP and return to $TARGET? Everything since that backup is LOST. Type RESTORE to continue: " ans
  [ "$ans" = "RESTORE" ] || die "cancelled."
  compose stop web api worker || true
  compose exec -T sql /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "$(env_get MSSQL_SA_PASSWORD)" -b \
    -Q "ALTER DATABASE [Fms] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; RESTORE DATABASE [Fms] FROM DISK = N'/var/opt/mssql/backup/$BACKUP' WITH REPLACE, CHECKSUM; ALTER DATABASE [Fms] SET MULTI_USER;" \
    || die "the restore failed. The backup file is untouched; fix the cause and run again."
fi

env_set FMS_VERSION "$TARGET"
compose up -d api worker web
PORT=$(env_get FMS_HTTP_PORT); PORT=${PORT:-8080}
wait_ready "http://localhost:$PORT" 90 || die "version $TARGET did not become ready. See: docker compose logs api"
echo "$TARGET" > .installed-version
say "Back on $TARGET."
