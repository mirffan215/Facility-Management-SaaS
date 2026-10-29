#!/usr/bin/env bash
# First installation from the bundle. Usage: ./install.sh
set -euo pipefail
cd "$(dirname "$0")"
. ./lib.sh

[ -f .env ] || { cp .env.example .env; die "created .env from the example. Fill it in, then run ./install.sh again."; }
BUNDLE_VERSION=$(cat VERSION 2>/dev/null || true)
[ -n "$BUNDLE_VERSION" ] || die "VERSION file missing: run this from the unpacked bundle."
# A finished installation leaves .installed-version behind: refuse to install over it. A .env that names a version but has no such file is a first install that
# stopped half way (the version is written before the work starts), and running the same version again is how it is retried.
[ ! -f .installed-version ] || die "this folder already holds an installed system ($(cat .installed-version)). Use ./upgrade.sh."
CURRENT=$(env_get FMS_VERSION)
[ -z "$CURRENT" ] || [ "$CURRENT" = "$BUNDLE_VERSION" ] || die "this looks like an existing installation (FMS_VERSION is $CURRENT). Use ./upgrade.sh."
env_set FMS_VERSION "$BUNDLE_VERSION"
need_env
command -v docker >/dev/null || die "Docker is not installed."
mkdir -p license

if [ -d images ]; then say "Loading images (offline bundle)"; ./load-images.sh; fi
[ -f license/fms.license ] || echo "NOTE: no license file in ./license/fms.license. The system will start but be read-only until you add one (see LICENSING.md)."

say "Setting up the database"
compose --profile migrate run --rm migrator
say "Starting"
compose up -d api worker web
PORT=$(env_get FMS_HTTP_PORT); PORT=${PORT:-8080}
wait_ready "http://localhost:$PORT" 60 || die "the system did not become ready. Look at: docker compose logs api"
echo "$BUNDLE_VERSION" > .installed-version
say "Installed version $BUNDLE_VERSION. Open $(env_get FMS_PUBLIC_URL) (or http://localhost:$PORT) and sign in as the administrator."
