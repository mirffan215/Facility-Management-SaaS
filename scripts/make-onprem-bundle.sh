#!/usr/bin/env bash
# Builds the on-premise bundle: compose file, scripts, guides and (optionally) the images, as one archive with checksums.
#   scripts/make-onprem-bundle.sh <version> [--with-images] [--release]
# --with-images  saves fms-api, fms-worker, fms-migrator and fms-web (already built or pulled as $FMS_REGISTRY/<name>:<version>) for servers without internet.
# --release      also refuses to build when the build trusts a development license key.
set -euo pipefail
cd "$(dirname "$0")/.."
VERSION="${1:?usage: make-onprem-bundle.sh <version> [--with-images] [--release]}"; shift
[[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+([-+][0-9A-Za-z.-]+)?$ ]] || { echo "version must look like 1.2.3" >&2; exit 1; }
IMAGES=0; RELEASE=0
for a in "$@"; do case "$a" in --with-images) IMAGES=1;; --release) RELEASE=1;; *) echo "unknown option $a" >&2; exit 1;; esac; done
[ "$RELEASE" -eq 0 ] || scripts/check-release-keys.sh

REGISTRY="${FMS_REGISTRY:-ghcr.io/mirffan215}"
OUT="dist/fms-onprem-$VERSION"
rm -rf "$OUT"; mkdir -p "$OUT/license"
cp deploy/onprem/docker-compose.yml deploy/onprem/.env.example deploy/onprem/*.sh "$OUT/"
cp docs/deployment.md "$OUT/DEPLOYMENT.md"; cp docs/licensing.md "$OUT/LICENSING.md"; cp docs/upgrading.md "$OUT/UPGRADING.md"
cp docs/security/threat-model.md "$OUT/SECURITY-threat-model.md"
printf '%s\n' "$VERSION" > "$OUT/VERSION"
printf 'Put the license file here as fms.license (see LICENSING.md).\n' > "$OUT/license/README.txt"

if [ "$IMAGES" -eq 1 ]; then
  mkdir -p "$OUT/images"
  for name in api worker migrator web; do
    docker save -o "$OUT/images/fms-$name-$VERSION.tar" "$REGISTRY/fms-$name:$VERSION"
  done
  ( cd "$OUT/images" && sha256sum ./*.tar | sed 's# \./# #' > SHA256SUMS )
fi

( cd "$OUT" && find . -type f ! -name SHA256SUMS -print0 | sort -z | xargs -0 sha256sum > MANIFEST.sha256 )
tar -C dist -czf "dist/fms-onprem-$VERSION.tar.gz" "fms-onprem-$VERSION"
( cd dist && sha256sum "fms-onprem-$VERSION.tar.gz" > "fms-onprem-$VERSION.tar.gz.sha256" )
echo "Built dist/fms-onprem-$VERSION.tar.gz ($([ "$IMAGES" -eq 1 ] && echo 'with images' || echo 'online bundle: images are pulled from the registry'))"
