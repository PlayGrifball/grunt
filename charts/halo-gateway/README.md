# halo-gateway chart

Deploys the Grunt Halo gateway (`ghcr.io/playgrifball/halo-gateway`). It runs one Halo Infinite account behind an HTTP
API for the grif environments. See [GATEWAY.md](../../GATEWAY.md) for what the gateway does.

```bash
helm install halo-gateway oci://ghcr.io/playgrifball/charts/halo-gateway -n halo-gateway -f values.yaml
```

Images and charts are published only by GitHub Actions on `main`, signed keyless with cosign:

- `gateway-image.yml` builds `sha-<commit>`, `main` and `latest` for amd64 and arm64.
- `helm-chart.yml` publishes `halo-gateway:0.2.<run>`.

To verify an image:

```bash
cosign verify ghcr.io/playgrifball/halo-gateway:<tag> \
  --certificate-identity https://github.com/PlayGrifball/grunt/.github/workflows/gateway-image.yml@refs/heads/main \
  --certificate-oidc-issuer https://token.actions.githubusercontent.com
```

## What it deploys

- **Deployment**, always 1 replica with `Recreate`. The file token store has a single writer, and the host refuses to
  start while another instance holds the lock on the volume.
  - Runs as non-root (UID 1654) with a read-only root filesystem. `/data` (tokens) and `/tmp` are the only writable paths.
  - CPU and memory limits are set.
  - Probes: startup and liveness on `/health/live`, which runs no checks, so a Halo outage or missing sign-in never
    restarts the pod. Readiness is on `/health/ready`, which runs the Halo account check. That check is at worst
    *Degraded*, which still counts as ready.
  - X-Forwarded-*/X-Real-IP are trusted only from `forwardedHeaders.knownIPNetworks` (default: the cluster pod
    network, matching Traefik's `X-Real-IP`); anything else keeps the connection's own peer address. Set it to the
    real cluster pod CIDR if a non-default one is used.
- **PVC** for the token file. It's annotated `helm.sh/resource-policy: keep` and `argocd.argoproj.io/sync-options:
  Prune=false,Delete=false`, so it survives uninstalls, Argo prunes and source switches. Losing it means signing in again.
- **Service** (ClusterIP 8080).
- **NetworkPolicy**:
  - Ingress only from `networkPolicy.consumerNamespaces` (listed by name: NetworkPolicies can't match a `grif-*` prefix),
    and the ingress controller (for the public route). Nothing scrapes the gateway: there is no `/metrics`.
  - Egress: DNS, public HTTPS 443 (Microsoft login, Xbox Live, Halo), the OTLP collector and, with `oidc` and
    `networkPolicy.oidcTo`, the OpenID Connect provider.
- **Optional**:
  - A Traefik IngressRoute (Traefik v3 `PathRegexp`, case-insensitive like ASP.NET routing) with two routes:
    - the admin page, `(?i)^/+admin(/|$)` (its OIDC callback `/admin/signin-oidc` and the `/admin/halo/*` API included),
      with `ingressRoute.admin.middlewares`, e.g. a forward-auth gate in front of the page's own OIDC check;
    - everything else (the consumer API behind its keys, the OAuth callback), with `ingressRoute.middlewares` (rate limit,
      security headers, ...).

    The health endpoints, `(?i)^/+health(/|$)`, are never routed.
  - A PrometheusRule on the **OTLP-pushed** metrics (needs `telemetry.enabled`):
    - `HaloGatewayReauthRequired`
    - `HaloGatewayUpstreamErrors`
    - `HaloGatewayUpstreamThrottled`
    - `HaloGatewayQuotaExhausted`
    - `HaloGatewayMetricsAbsent` (no pushed series for `absentFor`: down, or its export is broken; replaces an `up` alert)

    Pushed series are selected by `k8s_namespace_name="<namespace>"` and `job=~"(.+/)?<service name>"`, the labels an
    OpenTelemetry Collector gives them when its Prometheus exporter (or remote write) promotes resource attributes
    (`resource_to_telemetry_conversion`). `prometheusRule.selector` replaces that matcher for other pipelines.
## Homelab values

```yaml
image:
  tag: sha-<commit>            # or let Argo CD Image Updater follow main/latest by digest
oauth:
  redirectUrl: https://halo-gateway.<domain>/oauth/callback
  existingSecret: halo-gateway-oauth        # keys ClientId, ClientSecret (the Microsoft app)
admin:
  existingSecret: halo-gateway-admin        # key AdminKey
apiKeys:
  - { name: grif-prod,    environment: prod,    priority: 0, permitsPerMinute: 600, secretName: halo-gateway-keys, secretKey: grif-prod }
  - { name: grif-test,    environment: test,    priority: 1, permitsPerMinute: 240, secretName: halo-gateway-keys, secretKey: grif-test }
  - { name: grif-preview, environment: preview, priority: 2, permitsPerMinute: 120, secretName: halo-gateway-keys, secretKey: grif-preview }
globalQuota:
  permitsPerMinute: 1200
  reservedFraction: 0.25      # the last 25% of the global budget is for priority-0 (prod) keys
telemetry:
  enabled: true
  endpoint: http://<otel collector>:4317
  environment: homelab
networkPolicy:
  consumerNamespaces: [grif-prod, grif-test, grif-preview1, grif-preview2, grif-preview3, grif-preview4, grif-preview5, grif-preview6]
  ingressController:
    namespaceSelector: { matchLabels: { kubernetes.io/metadata.name: <traefik namespace> } }
    podSelector: { matchLabels: { app.kubernetes.io/name: traefik } }
  telemetryTo:
    - namespaceSelector: { matchLabels: { kubernetes.io/metadata.name: <collector namespace> } }
ingressRoute:
  enabled: true
  host: halo-gateway.<domain>
  middlewares: [{ name: <rate limit> }, { name: security-headers, namespace: traefik }]
  admin:
    middlewares: [{ name: <forward-auth>, namespace: <ns> }, { name: security-headers, namespace: traefik }]
  tls: { certResolver: <resolver> }   # or secretName
prometheusRule: { enabled: true }   # alerts on the pushed metrics (telemetry above)
# The browser admin page (optional; its route is ingressRoute's admin route)
oidc:
  enabled: true
  authority: https://auth.<domain>/application/o/halo-gateway/
  existingSecret: halo-gateway-oidc         # key ClientSecret
  clientId: halo-gateway
  allowedGroups: [<admin group>]
```

If the public host goes through Cloudflare, it needs the same origin setup as the other proxied hosts.

## Giving a grif environment a key

1. Generate a key, e.g. `openssl rand -base64 32`.
2. Add it to the `halo-gateway-keys` secret under the environment's name, and add an `apiKeys` entry with its priority
   and quota. If you'd rather not keep the clear key in the gateway namespace, use `keySha256: <sha256 hex>` instead.
3. Give the environment the same key as `HaloGateway:ApiKey`, plus `HaloGateway:BaseAddress:
   http://halo-gateway.halo-gateway.svc:8080/`, and register `services.AddHaloGatewayClient(configuration.GetSection("HaloGateway"))`
   (package `Surprenant.Grunt.Gateway.Client`).
4. Add its namespace to `networkPolicy.consumerNamespaces`.

## One-time sign-in (and after a `HaloGatewayReauthRequired` alert)

1. On the Microsoft app registration, add the redirect URI `https://halo-gateway.<domain>/oauth/callback`. This single URI
   serves every environment.

With the admin page (`oidc`, and `ingressRoute`): open `https://halo-gateway.<domain>/admin`, sign in with the
identity provider, press **Sign in with Microsoft**, sign in with the Halo account and approve; the browser comes back
to `/admin` showing the status. **Refresh now** and **Sign out / clear tokens** (with a confirmation) are there too.

Without it:

2. Run `kubectl -n halo-gateway port-forward svc/halo-gateway 8080:8080`.
3. Run `curl -H "X-Admin-Key: $ADMIN_KEY" http://localhost:8080/admin/halo/signin`, open the returned URL, sign in with the
   Halo account and approve. The browser lands on the public callback and shows "Signed in". The link works once, for
   15 minutes.
4. Run `curl -H "X-Admin-Key: $ADMIN_KEY" http://localhost:8080/admin/halo/status` and check for `"status":"Authorized"`.

If the callback isn't reachable, use `POST /admin/halo/code` with `{"code": "<the redirect URL from the browser>"}`.
`DELETE /admin/halo/tokens` clears the tokens and forces a new sign-in.

The gateway refreshes the token every 30 minutes. When Microsoft rejects the refresh token:

- `/health/ready` reports Degraded with the reason.
- `grunt_gateway_halo_reauth_required` goes to 1.
- `HaloGatewayReauthRequired` fires after 15 minutes.

## Tests

`helm-chart.yml` runs:

- `helm lint --strict` and `helm unittest` (`tests/`).
- kubeconform against the oldest and newest supported Kubernetes versions.
- `e2e/run.sh` on k3d with NetworkPolicies enforced. It covers readiness, API and admin keys, the 502 through the factory
  while not signed in, Degraded health, metrics, blocked namespaces, a token on the volume surviving a scale-down, egress
  to login.live.com, and a Recreate restart.
