#!/usr/bin/env bash
# Runs one slice of the integration tests so CI can run the slices in parallel (each slice starts its own SQL Server container).
# Usage: scripts/integration-shard.sh <index starting at 1> <count> [extra dotnet test args]
# Test classes are sorted and dealt out round-robin, so a new test class is picked up by exactly one slice without editing CI.
# SHARD_DRY_RUN=1 prints the classes of the slice and does not run them.
set -euo pipefail
index=${1:?slice index (1-based)}; count=${2:?slice count}; shift 2
project=tests/Fms.IntegrationTests

classes=$(dotnet test "$project" --no-build "$@" --list-tests 2>/dev/null \
  | grep -oE '^\s+Fms\.IntegrationTests\.[A-Za-z0-9_]+\.' | tr -d ' ' | sort -u)
[ -n "$classes" ] || { echo "No integration test classes found" >&2; exit 1; }

mine=$(echo "$classes" | awk -v i="$index" -v n="$count" '(NR-1) % n == i-1')
[ -n "$mine" ] || { echo "Slice $index of $count has no test classes"; exit 0; }
echo "Slice $index of $count:"; echo "$mine" | sed 's/^/  /'
[ "${SHARD_DRY_RUN:-}" = 1 ] && exit 0

filter=$(echo "$mine" | sed 's/^/FullyQualifiedName~/' | paste -sd'|' -)
dotnet test "$project" --no-build "$@" --filter "$filter"
