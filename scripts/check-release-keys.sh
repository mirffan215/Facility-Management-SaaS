#!/usr/bin/env bash
# A release must not trust the development license key (its private half is in this repository).
# Usage: scripts/check-release-keys.sh
set -euo pipefail
cd "$(dirname "$0")/.."
file=src/BuildingBlocks/Fms.BuildingBlocks/Licensing/trusted-keys.json
if grep -Eq '"keyId"[[:space:]]*:[[:space:]]*"dev-' "$file"; then
  echo "ERROR: $file trusts a development key (key id starting with dev-). Replace it with the production public key before releasing." >&2
  exit 1
fi
if ! grep -q '"keyId"' "$file"; then
  echo "ERROR: $file trusts no key, so no license could ever be accepted." >&2
  exit 1
fi
echo "License keys OK: only production keys are trusted."
