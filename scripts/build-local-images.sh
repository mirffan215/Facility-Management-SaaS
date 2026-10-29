#!/usr/bin/env bash
# Builds the four images as <registry>/fms-{api,worker,migrator,web}:<version> for a rehearsal on this machine.
#   scripts/build-local-images.sh <version> [--host]
# Default: the real Dockerfiles. --host: compile with the machine's own .NET and npm, then put the output on the same base images with the same user and entry points
# (for machines whose container builds cannot reach the package registries, for example behind a TLS-inspecting proxy).
set -euo pipefail
cd "$(dirname "$0")/.."
ROOT=$(pwd)
V="${1:?usage: build-local-images.sh <version> [--host]}"
HOST=0; [ "${2:-}" = "--host" ] && HOST=1
REG="${FMS_REGISTRY:-local}"

if [ "$HOST" -eq 0 ]; then
  for n in api worker migrator; do docker build -q -f "Dockerfile.$n" --build-arg VERSION="$V" -t "$REG/fms-$n:$V" . >/dev/null; done
  docker build -q -t "$REG/fms-web:$V" web >/dev/null
  echo "built $V with the Dockerfiles"; exit 0
fi

W=$(mktemp -d)
trap 'rm -rf "$W"' EXIT
(cd web && npm run build >/dev/null)
for n in Api Worker Migrator; do
  l=$(echo "$n" | tr 'A-Z' 'a-z')
  dotnet publish "src/Host/Fms.$n/Fms.$n.csproj" -c Release -o "$W/$l/app" /p:UseAppHost=false /p:Version="$V" -v q >/dev/null
  case $n in
    Api) base=aspnet; extra=$'ENV ASPNETCORE_URLS=http://+:8080\nEXPOSE 8080'; entry='["dotnet", "Fms.Api.dll"]';;
    Worker) base=aspnet; extra=''; entry='["dotnet", "Fms.Worker.dll"]';;
    Migrator) base=runtime; extra=''; entry='["dotnet", "Fms.Migrator.dll"]';;
  esac
  printf 'FROM mcr.microsoft.com/dotnet/%s:10.0\nWORKDIR /app\nCOPY app .\nUSER $APP_UID\n%s\nENTRYPOINT %s\n' "$base" "$extra" "$entry" > "$W/$l/Dockerfile"
  docker build -q -t "$REG/fms-$l:$V" "$W/$l" >/dev/null
done
mkdir -p "$W/web"; cp -r web/dist "$W/web/dist"; cp web/nginx.conf.template "$W/web/"
cat > "$W/web/Dockerfile" <<'D'
FROM nginxinc/nginx-unprivileged:alpine
COPY nginx.conf.template /etc/nginx/templates/default.conf.template
COPY dist /usr/share/nginx/html
ENV API_UPSTREAM=http://api:8080 \
    CSP_CONNECT_SRC="https:" \
    NGINX_ENVSUBST_FILTER="API_UPSTREAM|CSP_CONNECT_SRC"
D
docker build -q -t "$REG/fms-web:$V" "$W/web" >/dev/null
echo "built $V from this machine's build output"
