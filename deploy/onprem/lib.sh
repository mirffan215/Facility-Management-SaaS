#!/usr/bin/env bash
# Shared helpers for install.sh, upgrade.sh and rollback.sh. Sourced, never run.

die() { echo "ERROR: $*" >&2; exit 1; }
say() { echo "==> $*"; }

# True when $1 (x.y.z) is lower than $2.
version_lt() {
  [ "$1" != "$2" ] && [ "$(printf '%s\n%s\n' "$1" "$2" | sort -V | head -n1)" = "$1" ]
}

valid_version() { [[ "$1" =~ ^[0-9]+\.[0-9]+\.[0-9]+([-+][0-9A-Za-z.-]+)?$ ]]; }

# Reads KEY from .env (no quotes handling beyond stripping surrounding ones).
env_get() { local v; v=$(grep -E "^$1=" "${ENV_FILE:-.env}" 2>/dev/null | tail -n1 | cut -d= -f2-); v="${v%\"}"; v="${v#\"}"; printf '%s' "$v"; }

# Sets KEY=VALUE in .env, keeping everything else.
env_set() {
  local key="$1" value="$2" file="${ENV_FILE:-.env}"
  if grep -qE "^${key}=" "$file" 2>/dev/null; then
    local tmp; tmp=$(mktemp); sed "s|^${key}=.*|${key}=${value}|" "$file" > "$tmp" && cat "$tmp" > "$file"; rm -f "$tmp"
  else
    printf '%s=%s\n' "$key" "$value" >> "$file"
  fi
}

need_env() {
  [ -f "${ENV_FILE:-.env}" ] || die "no .env file. Copy .env.example to .env and fill it in."
  local missing=()
  for k in FMS_TENANT_ID FMS_PUBLIC_URL FMS_AUTH_AUTHORITY FMS_AUTH_CLIENT_ID FMS_APP_LOGIN_PASSWORD; do
    [ -n "$(env_get "$k")" ] || missing+=("$k")
  done
  [ "${#missing[@]}" -eq 0 ] || die "these settings are empty in .env: ${missing[*]}"
}

# Uses the bundled SQL Server when the .env has its password; otherwise the database is external.
uses_bundled_sql() { [ -n "$(env_get MSSQL_SA_PASSWORD)" ] && [ -z "$(env_get FMS_MIGRATOR_CONNECTION)" ]; }

compose() {
  local profiles=()
  uses_bundled_sql && profiles+=(--profile bundled-sql)
  ${FMS_DOCKER:-docker} compose "${profiles[@]}" "$@"
}

wait_ready() {
  local url="$1" tries="${2:-60}" i
  for i in $(seq 1 "$tries"); do
    curl -fsS "$url/health/ready" >/dev/null 2>&1 && return 0
    sleep 2
  done
  return 1
}
