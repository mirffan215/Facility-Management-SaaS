#!/usr/bin/env bash
# Loads the images shipped in ./images into the local Docker engine (for servers without internet access) after checking them.
set -euo pipefail
cd "$(dirname "$0")"
. ./lib.sh
[ -d images ] || die "no images/ folder in this bundle (an online bundle pulls from the registry instead)."
( cd images && sha256sum -c SHA256SUMS ) || die "an image file does not match its checksum. Re-download the bundle."
for f in images/*.tar; do say "Loading $f"; docker load -i "$f" >/dev/null; done
