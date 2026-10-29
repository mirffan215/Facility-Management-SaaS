#!/usr/bin/env bash
# Tests the helper functions and the refusals of the on-premise scripts without Docker. Run in CI.
set -uo pipefail
cd "$(dirname "$0")/../deploy/onprem"
. ./lib.sh
fail=0
check() { if [ "$2" = "$3" ]; then echo "ok   $1"; else echo "FAIL $1: expected '$3' got '$2'"; fail=1; fi; }

lt() { version_lt "$1" "$2" && echo yes || echo no; }
check "1.0.0 < 1.0.1" "$(lt 1.0.0 1.0.1)" yes
check "1.2.0 < 1.10.0 (numeric, not text)" "$(lt 1.2.0 1.10.0)" yes
check "2.0.0 < 1.9.9" "$(lt 2.0.0 1.9.9)" no
check "same version is not lower" "$(lt 1.0.0 1.0.0)" no
vv() { valid_version "$1" && echo yes || echo no; }
check "0.8.0 valid" "$(vv 0.8.0)" yes
check "1.0.0-rc.1 valid" "$(vv 1.0.0-rc.1)" yes
check "latest invalid" "$(vv latest)" no
check "empty invalid" "$(vv '')" no
check "1.0 invalid" "$(vv 1.0)" no
check "path trick invalid" "$(vv '1.0.0/../x')" no

dir=$(mktemp -d); trap 'rm -rf "$dir"' EXIT
export ENV_FILE="$dir/.env"
printf 'A=one\nB="two words"\nFMS_VERSION=0.7.0\n' > "$ENV_FILE"
check "env_get plain" "$(env_get A)" one
check "env_get quoted" "$(env_get B)" "two words"
check "env_get missing" "$(env_get NOPE)" ""
env_set FMS_VERSION 0.8.0; env_set NEWKEY x
check "env_set replaces" "$(env_get FMS_VERSION)" 0.8.0
check "env_set adds" "$(env_get NEWKEY)" x
check "env_set keeps others" "$(env_get A)" one

# Refusals: run the real scripts in a scratch copy.
unset ENV_FILE
work=$(mktemp -d); cp -r . "$work/b"; cd "$work/b"; rm -f .env
out=$(./upgrade.sh --yes 2>&1); check "upgrade refuses without VERSION" "$(echo "$out" | grep -c 'VERSION file missing')" 1
echo 0.7.0 > VERSION
out=$(./upgrade.sh --yes 2>&1); check "upgrade refuses without .env" "$(echo "$out" | grep -c 'no .env file')" 1
printf 'FMS_VERSION=0.8.0\nFMS_TENANT_ID=x\nFMS_PUBLIC_URL=u\nFMS_AUTH_AUTHORITY=a\nFMS_AUTH_CLIENT_ID=c\nFMS_APP_LOGIN_PASSWORD=p\n' > .env
out=$(./upgrade.sh --yes 2>&1); check "upgrade refuses a downgrade" "$(echo "$out" | grep -c 'refusing to go from 0.8.0 down to 0.7.0')" 1
echo 0.8.0 > VERSION
out=$(./upgrade.sh --yes 2>&1); check "upgrade refuses the same version" "$(echo "$out" | grep -c 'already installed')" 1
echo 0.8.0 > .installed-version
out=$(./install.sh 2>&1); check "install refuses a finished installation" "$(echo "$out" | grep -c 'already holds an installed system')" 1
rm -f .installed-version
sed -i 's/^FMS_VERSION=.*/FMS_VERSION=0.7.9/' .env
out=$(./install.sh 2>&1); check "install refuses an .env that names another version" "$(echo "$out" | grep -c 'Use ./upgrade.sh')" 1
sed -i 's/^FMS_VERSION=.*/FMS_VERSION=0.8.0/' .env
out=$(./rollback.sh x.bak 0.9.0 2>&1); check "rollback must run from the target version's folder" "$(echo "$out" | grep -c 'not 0.9.0')" 1
out=$(./rollback.sh '../etc/passwd' 0.8.0 2>&1); check "rollback rejects a path as backup name" "$(echo "$out" | grep -c 'plain file name')" 1
rm -rf "$work"
exit $fail
