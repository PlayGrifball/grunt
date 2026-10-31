#!/usr/bin/env bash
# End-to-end test of the chart on a real cluster that enforces NetworkPolicies (k3s/k3d do): ./run.sh
#
# Runs GATEWAY_IMAGE (repository:tag, default halo-gateway:e2e, already imported into the cluster) and checks:
#   1. install: the pod starts non-root with a read-only root filesystem and gets ready (/health/live, /health/ready);
#   2. from a consumer namespace (grif-e2e): no API key -> 401; a valid key reaches the generated controller and
#      HaloInfiniteClientFactory, which has no token yet -> 502 with X-Halo-Upstream-Status: 0;
#      admin status needs the admin key and reports ReauthRequired; /health says Degraded (still 200);
#      /metrics shows grunt_gateway_halo_reauth_required 1;
#   3. from a namespace not listed (other-e2e): the gateway is unreachable (NetworkPolicy);
#   4. persistence + egress: a token written to the volume while the gateway is scaled down is read after it
#      comes back, and its refresh reaches login.live.com over HTTPS (allowed egress), which rejects the fake
#      refresh token -> "refresh token was rejected" (an egress block would say HttpRequestException instead);
#   5. a rollout restart (Recreate) comes back ready: the old pod released the volume and the instance lock.
set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
chart="$here/.."
image="${GATEWAY_IMAGE:-halo-gateway:e2e}"
ns=halo-gateway
consumer=grif-e2e
other=other-e2e
release=hg
svc="http://$release-halo-gateway.$ns.svc:8080"

step() { echo; echo "::group::$*"; }
end() { echo "::endgroup::"; }
fail() { echo "::error::$*"; dump; exit 1; }
dump() {
  kubectl -n "$ns" get all,pvc,networkpolicy -o wide || true
  kubectl -n "$ns" describe pods || true
  kubectl -n "$ns" logs deploy/$release-halo-gateway --tail=200 || true
}

step "Install"
kubectl create namespace "$ns"
kubectl create namespace "$consumer"
kubectl create namespace "$other"
kubectl -n "$ns" create secret generic halo-gateway-keys --from-literal=grif-e2e=e2e-consumer-key
kubectl -n "$ns" create secret generic halo-gateway-admin --from-literal=AdminKey=e2e-admin-key
kubectl -n "$ns" create secret generic halo-gateway-oauth \
  --from-literal=ClientId=00000000-0000-0000-0000-000000000000 --from-literal=ClientSecret=not-a-secret
helm install "$release" "$chart" -n "$ns" --wait --timeout 4m \
  --set image.repository="${image%:*}" --set image.tag="${image##*:}" --set image.pullPolicy=Never \
  --set oauth.redirectUrl=https://halo-gateway.e2e.example/oauth/callback \
  --set oauth.existingSecret=halo-gateway-oauth \
  --set admin.existingSecret=halo-gateway-admin \
  --set 'apiKeys[0].name=grif-e2e' --set 'apiKeys[0].environment=e2e' --set 'apiKeys[0].priority=0' \
  --set 'apiKeys[0].secretName=halo-gateway-keys' --set 'apiKeys[0].secretKey=grif-e2e' \
  --set "networkPolicy.consumerNamespaces={$consumer}" \
  --set config.Gateway.Auth.InitialDelay=00:00:01 \
  || fail "helm install failed"
kubectl -n "$ns" get pods -o wide
end

client() { # namespace name
  kubectl -n "$1" run "$2" --image=curlimages/curl:8.11.1 --restart=Never --command -- sleep 3600 >/dev/null
  kubectl -n "$1" wait --for=condition=Ready "pod/$2" --timeout=120s >/dev/null
}
curl_in() { # namespace pod curl-args...
  local n=$1 p=$2; shift 2
  kubectl -n "$n" exec "$p" -- curl -sS --max-time 10 "$@"
}
status_of() { curl_in "$consumer" c -o /dev/null -w '%{http_code}' "$@"; }

step "Consumer namespace"
client "$consumer" c
[ "$(status_of "$svc/health/live")" = 200 ] || fail "/health/live is not 200"
[ "$(status_of "$svc/health/ready")" = 200 ] || fail "/health/ready is not 200"
[ "$(status_of "$svc/halo/v1/medals")" = 401 ] || fail "no API key should be 401"
[ "$(status_of -H 'X-Api-Key: wrong' "$svc/halo/v1/medals")" = 401 ] || fail "a wrong API key should be 401"
headers=$(curl_in "$consumer" c -D - -o /dev/null -H 'X-Api-Key: e2e-consumer-key' "$svc/halo/v1/medals")
echo "$headers"
grep -q '^HTTP/1.1 502' <<<"$headers" || fail "an unsigned gateway should answer 502 through the factory"
grep -qi '^x-halo-upstream-status: 0' <<<"$headers" || fail "missing X-Halo-Upstream-Status: 0"
[ "$(status_of "$svc/admin/halo/status")" = 401 ] || fail "admin status without the admin key should be 401"
[ "$(status_of -H 'X-Api-Key: e2e-consumer-key' "$svc/admin/halo/status")" = 401 ] || fail "a consumer key must not open admin endpoints"
for _ in $(seq 1 30); do
  status=$(curl_in "$consumer" c -H 'X-Admin-Key: e2e-admin-key' "$svc/admin/halo/status")
  grep -q '"status":"ReauthRequired"' <<<"$status" && break
  sleep 2
done
echo "$status"
grep -q '"status":"ReauthRequired"' <<<"$status" || fail "admin status should report ReauthRequired"
[ "$(curl_in "$consumer" c "$svc/health")" = Degraded ] || fail "/health should be Degraded until signed in"
curl_in "$consumer" c "$svc/metrics" | grep -E '^grunt_gateway_halo_reauth_required(\{[^}]*\})? 1' \
  || fail "/metrics should show grunt_gateway_halo_reauth_required 1"
signin=$(curl_in "$consumer" c -H 'X-Admin-Key: e2e-admin-key' "$svc/admin/halo/signin")
grep -q 'login.live.com' <<<"$signin" || fail "sign-in URL should point at login.live.com"
[ "$(status_of "$svc/oauth/callback?code=x&state=forged")" = 400 ] || fail "a forged OAuth state should be 400"
end

step "Namespace not listed"
client "$other" o
if kubectl -n "$other" exec o -- curl -sS --max-time 5 -o /dev/null "$svc/health/live"; then
  fail "the gateway should be unreachable from $other"
fi
echo "Blocked, as expected."
end

step "Persistence and egress"
kubectl -n "$ns" scale deploy/$release-halo-gateway --replicas=0
kubectl -n "$ns" wait --for=delete pod -l app.kubernetes.io/instance=$release --timeout=120s || true
kubectl -n "$ns" apply -f - <<YAML
apiVersion: v1
kind: Pod
metadata:
  name: seed-token
spec:
  restartPolicy: Never
  securityContext: { runAsUser: 1654, runAsGroup: 1654, fsGroup: 1654, runAsNonRoot: true, seccompProfile: { type: RuntimeDefault } }
  containers:
    - name: seed
      image: busybox:1.37
      command: [sh, -c, 'echo "{\"token_type\":\"bearer\",\"expires_in\":3600,\"access_token\":\"fake\",\"refresh_token\":\"fake-refresh\"}" > /data/tokens.json && ls -l /data']
      securityContext: { allowPrivilegeEscalation: false, capabilities: { drop: [ALL] } }
      volumeMounts: [{ name: data, mountPath: /data }]
  volumes:
    - name: data
      persistentVolumeClaim: { claimName: $release-halo-gateway-data }
YAML
kubectl -n "$ns" wait --for=jsonpath='{.status.phase}'=Succeeded pod/seed-token --timeout=120s || fail "seeding the token failed"
kubectl -n "$ns" logs seed-token
kubectl -n "$ns" delete pod seed-token --wait
kubectl -n "$ns" scale deploy/$release-halo-gateway --replicas=1
kubectl -n "$ns" rollout status deploy/$release-halo-gateway --timeout=180s || fail "the gateway did not come back"
for _ in $(seq 1 30); do
  status=$(curl_in "$consumer" c -H 'X-Admin-Key: e2e-admin-key' "$svc/admin/halo/status" || true)
  grep -q 'refresh token was rejected\|HttpRequestException' <<<"$status" && break
  sleep 2
done
echo "$status"
grep -q '"tokenStored":true' <<<"$status" || fail "the token written to the volume was not read"
grep -q 'refresh token was rejected' <<<"$status" || fail "the refresh should reach login.live.com and be rejected (egress?)"
end

step "Rollout restart"
kubectl -n "$ns" rollout restart deploy/$release-halo-gateway
kubectl -n "$ns" rollout status deploy/$release-halo-gateway --timeout=180s || fail "restart did not complete"
[ "$(status_of "$svc/health/ready")" = 200 ] || fail "not ready after restart"
end

echo "Chart e2e passed."
