#!/usr/bin/env bash
# The chart's dashboard against a real gateway: Prometheus (its OTLP receiver), the gateway pushing its metrics to it,
# and Grafana with the dashboard the chart renders provisioned; a few gateway calls for traffic (they fail without a
# Halo account, which still makes request, cache, budget and failure metrics); then two checks:
#   - dashboard-grafana.mjs, through Grafana's query API: every query runs, the panels get data, and the chart's alerts
#     (prometheus-rules.mjs, evaluated by Prometheus every 5 s) each evaluate cleanly;
#   - dashboard-browser.mjs, in the Playwright image: the dashboard opened in Grafana's own frontend, so the queries are
#     the ones Grafana sends after interpolating $sel and $__rate_interval itself; with SCREENSHOT=<file.png>, then a
#     screenshot of it.
# The gateway's service name has a dot in it (SERVICE_NAME, default grunt.gateway), so the chart's selector carries a
# regex metacharacter through Grafana's interpolation.
#
#   GATEWAY_IMAGE=halo-gateway:e2e [SCREENSHOT=halo-gateway.png] charts/halo-gateway/tests/dashboard-stack.sh
#
# Needs docker (local or remote: files go in with docker cp, ports are published on 127.0.0.1 at random), helm, curl
# and node. Grafana, Prometheus and Playwright are pinned by digest. Its containers and network are named
# grunt-dashboard-<pid>-<random>-*, and it removes only those, unless KEEP=1. HEALTH_TIMEOUT (seconds, default 120): how
# long the gateway gets to answer /health/live.
set -euo pipefail

DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CHART="$(dirname "$DIR")"
GATEWAY_IMAGE="${GATEWAY_IMAGE:-halo-gateway:e2e}"
SERVICE_NAME="${SERVICE_NAME:-grunt.gateway}"
HEALTH_TIMEOUT="${HEALTH_TIMEOUT:-120}"
PROMETHEUS_IMAGE=quay.io/prometheus/prometheus:v3.15.1-distroless@sha256:451a845bae1d51622b41af1780eb2ad911bbb79a9855bc8e9bb67fabbc03f934
GRAFANA_IMAGE=docker.io/grafana/grafana:13.2.4-distroless@sha256:ae3d3add290adbe6a88412229f5d76fd7bb82743050881698642d3d62f277e35
PLAYWRIGHT_VERSION=1.65.0
PLAYWRIGHT_IMAGE=mcr.microsoft.com/playwright:v$PLAYWRIGHT_VERSION-noble@sha256:9beb546099e11f52d62e505537914870e01a67e0e6b00ae41da4775324e73dbe
PREFIX="grunt-dashboard-$$-$RANDOM"
WORK="$(mktemp -d)"
STARTED=()
NETWORK=

cleanup() {
  local rc=$?
  if [[ "$rc" != 0 ]]; then
    for c in "${STARTED[@]}"; do echo "::group::$c logs"; docker logs --tail 80 "$c" 2>&1 || true; echo "::endgroup::"; done
  fi
  if [[ "${KEEP:-}" == 1 ]]; then
    echo "KEEP=1: left ${STARTED[*]} ${NETWORK:+and network $NETWORK}"
  else
    if [[ ${#STARTED[@]} -gt 0 ]]; then docker rm -f "${STARTED[@]}" > /dev/null 2>&1 || true; fi
    if [[ -n "$NETWORK" ]]; then docker network rm "$NETWORK" > /dev/null 2>&1 || true; fi
  fi
  rm -rf "$WORK"
}
trap cleanup EXIT

# Creates container $PREFIX-<name> (the rest: docker create's arguments), remembered for the cleanup.
create() {
  local name="$PREFIX-$1"
  shift
  docker create --name "$name" "$@" > /dev/null
  STARTED+=("$name")
}
# The host address a container's port is published on.
address() { docker port "$PREFIX-$1" "$2/tcp" | head -n1; }

docker network create "$PREFIX" > /dev/null
NETWORK="$PREFIX"

# Prometheus takes OTLP and keeps the resource attribute the dashboard and alerts select on (as a collector promoting
# resource attributes does), and evaluates the chart's alerts often enough for the checker to see them evaluated
SERVICE_NAME="$SERVICE_NAME" node "$DIR/prometheus-rules.mjs" > "$WORK/rules.yml"
cat > "$WORK/prometheus.yml" <<'EOF'
global:
  scrape_interval: 15s
  evaluation_interval: 5s
rule_files: [/etc/prometheus/rules.yml]
otlp:
  promote_resource_attributes: [k8s.namespace.name]
EOF
chmod a+r "$WORK"/*.yml
create prometheus --network "$NETWORK" --network-alias prometheus -p 127.0.0.1::9090 "$PROMETHEUS_IMAGE" \
  --config.file=/etc/prometheus/prometheus.yml --web.enable-otlp-receiver
docker cp -q "$WORK/prometheus.yml" "$PREFIX-prometheus:/etc/prometheus/prometheus.yml"
docker cp -q "$WORK/rules.yml" "$PREFIX-prometheus:/etc/prometheus/rules.yml"
docker start "$PREFIX-prometheus" > /dev/null

create gateway --network "$NETWORK" -p 127.0.0.1::8080 \
  -e Gateway__ApiKeys__0__Name=ci -e Gateway__ApiKeys__0__Key=dashboard-key -e Gateway__ApiKeys__0__Environment=ci \
  -e Gateway__AdminKey=admin-key -e Gateway__Auth__TokenPath=/tmp/grunt/tokens.json \
  -e ClientConfiguration__ClientId=ci -e ClientConfiguration__ClientSecret=ci -e ClientConfiguration__RedirectUrl=https://ci.invalid/oauth/callback \
  -e OTEL_EXPORTER_OTLP_ENDPOINT=http://prometheus:9090/api/v1/otlp -e OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf \
  -e OTEL_SERVICE_NAME="$SERVICE_NAME" -e OTEL_RESOURCE_ATTRIBUTES=k8s.namespace.name=halo-gateway -e OTEL_METRIC_EXPORT_INTERVAL=5000 \
  "$GATEWAY_IMAGE"
docker start "$PREFIX-gateway" > /dev/null

# The dashboard as the chart renders it: $sel, uid, title and the rate panels' minimum interval filled in
mkdir -p "$WORK/dashboards"
helm template halo-gateway "$CHART" -n halo-gateway --set oauth.redirectUrl=https://ci.invalid/oauth/callback \
  --set telemetry.enabled=true --set telemetry.endpoint=http://prometheus:4317 --set-string telemetry.serviceName="$SERVICE_NAME" \
  --set telemetry.metricExportInterval=5000 --set grafanaDashboard.enabled=true \
  --show-only templates/grafana-dashboard.yaml > "$WORK/configmap.yaml"
read -r DASHBOARD SELECTOR < <(node -e '
  const fs = require("fs");
  const [, file, body] = fs.readFileSync(process.argv[1], "utf8").match(/\n  (\S+\.json): \|-\n((?: {4}.*\n?)+)/);
  const json = body.split("\n").map(l => l.slice(4)).join("\n");
  const d = JSON.parse(json);
  fs.writeFileSync(`${process.argv[2]}/${file}`, json);
  console.log(d.uid, d.templating.list.find(v => v.name === "sel").query);' "$WORK/configmap.yaml" "$WORK/dashboards")
echo "Dashboard $DASHBOARD, \$sel = $SELECTOR"
cat > "$WORK/datasources.yaml" <<'EOF'
apiVersion: 1
datasources:
  - { name: Prometheus, type: prometheus, uid: prometheus, url: http://prometheus:9090, access: proxy, isDefault: true }
EOF
cat > "$WORK/dashboards.yaml" <<'EOF'
apiVersion: 1
providers:
  - { name: chart, type: file, options: { path: /etc/grafana/dashboards } }
EOF
chmod -R a+rX "$WORK"
create grafana --network "$NETWORK" -p 127.0.0.1::3000 \
  -e GF_AUTH_ANONYMOUS_ENABLED=true -e GF_AUTH_ANONYMOUS_ORG_ROLE=Admin -e GF_AUTH_DISABLE_LOGIN_FORM=true "$GRAFANA_IMAGE"
docker cp -q "$WORK/datasources.yaml" "$PREFIX-grafana:/etc/grafana/provisioning/datasources/prometheus.yaml"
docker cp -q "$WORK/dashboards.yaml" "$PREFIX-grafana:/etc/grafana/provisioning/dashboards/chart.yaml"
docker cp -q "$WORK/dashboards" "$PREFIX-grafana:/etc/grafana/dashboards"
docker start "$PREFIX-grafana" > /dev/null

GATEWAY="http://$(address gateway 8080)"
GRAFANA="http://$(address grafana 3000)"
PROMETHEUS="http://$(address prometheus 9090)"

# Traffic once the gateway answers: lookups that fail without a Halo account (budget, cache, failure metrics)
deadline=$((SECONDS + HEALTH_TIMEOUT))
until curl -fs "$GATEWAY/health/live" > /dev/null; do
  if (( SECONDS >= deadline )); then
    echo "::error::The gateway never answered $GATEWAY/health/live in ${HEALTH_TIMEOUT}s"
    exit 1
  fi
  sleep 2
done
for path in users/by-gamertag/Grunt users/by-gamertag/Major%20Nelson 'users?xuids=1' medals stats/matches/00000000-0000-0000-0000-000000000001; do
  curl -s -o /dev/null -w "%{http_code} $path\n" -H 'X-Api-Key: dashboard-key' "$GATEWAY/halo/v1/$path"
done

node "$DIR/dashboard-grafana.mjs" --url "$GRAFANA" --uid "$DASHBOARD" --timeout "${TIMEOUT:-180}" \
  --prometheus "$PROMETHEUS" --rules "$WORK/rules.yml"

# The image has the browser but not the package: installed first, at the image's version, outside the checkout
create browser --network host -e GRAFANA="$GRAFANA" -e DASHBOARD="$DASHBOARD" -e SELECTOR="$SELECTOR" \
  -e SCREENSHOT_ARGS="${SCREENSHOT:+--screenshot /out/dashboard.png}" "$PLAYWRIGHT_IMAGE" \
  sh -c "mkdir -p /pw /out && cd /pw && npm install --silent --no-audit --no-fund playwright@$PLAYWRIGHT_VERSION > /dev/null &&
         node /tests/dashboard-browser.mjs --url \"\$GRAFANA\" --uid \"\$DASHBOARD\" --selector \"\$SELECTOR\" \$SCREENSHOT_ARGS"
docker cp -q "$DIR" "$PREFIX-browser:/tests"
docker start -a "$PREFIX-browser"
if [[ -n "${SCREENSHOT:-}" ]]; then docker cp -q "$PREFIX-browser:/out/dashboard.png" "$SCREENSHOT"; fi
