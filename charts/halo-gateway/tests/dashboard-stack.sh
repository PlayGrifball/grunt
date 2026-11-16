#!/usr/bin/env bash
# The chart's dashboard against a real gateway: Prometheus (its OTLP receiver), the gateway pushing its metrics to it,
# and Grafana with the dashboard the chart renders provisioned; a few gateway calls for traffic (they fail without a
# Halo account, which still makes request, cache, budget and failure metrics); then dashboard-grafana.mjs. Prometheus
# also evaluates the chart's alerts (prometheus-rules.mjs) every 5 s, and the checker fails unless each one evaluates
# cleanly. With SCREENSHOT=<file.png>, then a screenshot of the dashboard (dashboard-screenshot.mjs, in the Playwright
# image).
#
#   GATEWAY_IMAGE=halo-gateway:e2e [SCREENSHOT=halo-gateway.png] charts/halo-gateway/tests/dashboard-stack.sh
#
# Needs docker, helm and node. Grafana, Prometheus and Playwright are pinned by digest. Removes what it started.
set -euo pipefail

DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CHART="$(dirname "$DIR")"
GATEWAY_IMAGE="${GATEWAY_IMAGE:-halo-gateway:e2e}"
PROMETHEUS_IMAGE=quay.io/prometheus/prometheus:v3.15.1-distroless@sha256:451a845bae1d51622b41af1780eb2ad911bbb79a9855bc8e9bb67fabbc03f934
GRAFANA_IMAGE=docker.io/grafana/grafana:13.2.4-distroless@sha256:ae3d3add290adbe6a88412229f5d76fd7bb82743050881698642d3d62f277e35
PLAYWRIGHT_VERSION=1.65.0
PLAYWRIGHT_IMAGE=mcr.microsoft.com/playwright:v$PLAYWRIGHT_VERSION-noble@sha256:9beb546099e11f52d62e505537914870e01a67e0e6b00ae41da4775324e73dbe
NET=grunt-dashboard
WORK="$(mktemp -d)"

cleanup() {
  local rc=$?
  if [[ "$rc" != 0 ]]; then
    for c in gateway grafana prometheus; do echo "::group::$c logs"; docker logs --tail 80 "$c" 2>&1 || true; echo "::endgroup::"; done
  fi
  if [[ "${KEEP:-}" != 1 ]]; then
    docker rm -f grafana gateway prometheus > /dev/null 2>&1 || true
    docker network rm "$NET" > /dev/null 2>&1 || true
  fi
}
trap cleanup EXIT

docker network create "$NET" > /dev/null

# Prometheus takes OTLP and keeps the resource attribute the dashboard and alerts select on (as a collector promoting
# resource attributes does), and evaluates the chart's alerts often enough for the checker to see them evaluated
node "$DIR/prometheus-rules.mjs" > "$WORK/rules.yml"
cat > "$WORK/prometheus.yml" <<'EOF'
global:
  scrape_interval: 15s
  evaluation_interval: 5s
rule_files: [/etc/prometheus/rules.yml]
otlp:
  promote_resource_attributes: [k8s.namespace.name]
EOF
docker run -d --name prometheus --network "$NET" -p 19090:9090 -v "$WORK/prometheus.yml:/etc/prometheus/prometheus.yml:ro" \
  -v "$WORK/rules.yml:/etc/prometheus/rules.yml:ro" "$PROMETHEUS_IMAGE" --config.file=/etc/prometheus/prometheus.yml --web.enable-otlp-receiver > /dev/null

docker run -d --name gateway --network "$NET" -p 18080:8080 \
  -e Gateway__ApiKeys__0__Name=ci -e Gateway__ApiKeys__0__Key=dashboard-key -e Gateway__ApiKeys__0__Environment=ci \
  -e Gateway__AdminKey=admin-key -e Gateway__Auth__TokenPath=/tmp/grunt/tokens.json \
  -e ClientConfiguration__ClientId=ci -e ClientConfiguration__ClientSecret=ci -e ClientConfiguration__RedirectUrl=https://ci.invalid/oauth/callback \
  -e OTEL_EXPORTER_OTLP_ENDPOINT=http://prometheus:9090/api/v1/otlp -e OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf \
  -e OTEL_SERVICE_NAME=halo-gateway -e OTEL_RESOURCE_ATTRIBUTES=k8s.namespace.name=halo-gateway -e OTEL_METRIC_EXPORT_INTERVAL=5000 \
  "$GATEWAY_IMAGE" > /dev/null

# The dashboard as the chart renders it, $sel filled in
mkdir -p "$WORK/provisioning/datasources" "$WORK/provisioning/dashboards" "$WORK/dashboards"
helm template halo-gateway "$CHART" -n halo-gateway --set oauth.redirectUrl=https://ci.invalid/oauth/callback \
  --set telemetry.enabled=true --set telemetry.endpoint=http://prometheus:4317 --set grafanaDashboard.enabled=true \
  --show-only templates/grafana-dashboard.yaml \
  | node -e 'let s = ""; process.stdin.on("data", d => s += d).on("end", () => {
      const json = s.split("halo-gateway.json: |-\n")[1].split("\n").map(l => l.slice(4)).join("\n");
      JSON.parse(json); process.stdout.write(json); })' > "$WORK/dashboards/halo-gateway.json"
cat > "$WORK/provisioning/datasources/prometheus.yaml" <<'EOF'
apiVersion: 1
datasources:
  - { name: Prometheus, type: prometheus, uid: prometheus, url: http://prometheus:9090, access: proxy, isDefault: true }
EOF
cat > "$WORK/provisioning/dashboards/chart.yaml" <<'EOF'
apiVersion: 1
providers:
  - { name: chart, type: file, options: { path: /var/lib/grafana/dashboards } }
EOF
chmod -R a+rX "$WORK"
docker run -d --name grafana --network "$NET" -p 13000:3000 \
  -e GF_AUTH_ANONYMOUS_ENABLED=true -e GF_AUTH_ANONYMOUS_ORG_ROLE=Admin -e GF_AUTH_DISABLE_LOGIN_FORM=true \
  -v "$WORK/provisioning/datasources:/etc/grafana/provisioning/datasources:ro" \
  -v "$WORK/provisioning/dashboards:/etc/grafana/provisioning/dashboards:ro" \
  -v "$WORK/dashboards:/var/lib/grafana/dashboards:ro" "$GRAFANA_IMAGE" > /dev/null

# Traffic once the gateway answers: lookups that fail without a Halo account (budget, cache, failure metrics)
for i in $(seq 1 60); do curl -fs http://localhost:18080/health/live > /dev/null && break; sleep 2; done
for path in users/by-gamertag/Grunt users/by-gamertag/Major%20Nelson 'users?xuids=1' medals stats/matches/00000000-0000-0000-0000-000000000001; do
  curl -s -o /dev/null -w "%{http_code} $path\n" -H 'X-Api-Key: dashboard-key' "http://localhost:18080/halo/v1/$path"
done

node "$DIR/dashboard-grafana.mjs" --url http://localhost:13000 --timeout "${TIMEOUT:-180}" \
  --prometheus http://localhost:19090 --rules "$WORK/rules.yml"

# The image has the browser but not the package: installed first, at the image's version, outside the checkout
if [[ -n "${SCREENSHOT:-}" ]]; then
  mkdir -p "$WORK/screenshot"
  docker run --rm --network host -v "$DIR:/tests:ro" -v "$WORK/screenshot:/out" "$PLAYWRIGHT_IMAGE" \
    sh -c "mkdir -p /pw && cd /pw && npm install --silent --no-audit --no-fund playwright@$PLAYWRIGHT_VERSION > /dev/null &&
           node /tests/dashboard-screenshot.mjs http://localhost:13000 /out/dashboard.png"
  cp "$WORK/screenshot/dashboard.png" "$SCREENSHOT"
fi
