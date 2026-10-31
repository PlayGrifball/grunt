# Halo gateway

The gateway runs one Halo/Xbox account behind an HTTP API. Apps (for example each GrifballWebApp environment) use the
`HaloGatewayClient` (an `IHaloInfiniteClient`) instead of signing in to Halo themselves.

```
app ── HaloGatewayClient ──HTTP + X-Api-Key──▶ Grunt.Gateway.Host
                                                 ├─ API key auth, per-key quotas, response cache
                                                 └─ generated HaloInfiniteController
                                                      └─ IHaloInfiniteClient = HaloInfiniteClientFactory
                                                           (tokens, 401/403 auth retry, 429/5xx backoff) ──▶ Halo
```

## Projects

| Project | Package / output | Role |
|:--------|:-----------------|:-----|
| `Grunt/Grunt` | `Surprenant.Grunt` | Core library. Holds `[GatewayExposed]`, the assembly markers, `HaloRequestPolicy` (retry/backoff) and `HaloGatewayProtocol` (wire contract). The gateway adds no ASP.NET Core dependency here. |
| `Grunt/Grunt.SourceGenerator` | analyzer | `InterfaceGenerator` (interface + factory) and `GatewayGenerator` (controller + client). |
| `Grunt/Grunt.Gateway` | `Surprenant.Grunt.Gateway` (not packed yet) | ASP.NET Core library: the generated `HaloInfiniteController`, result mapping, cache filter, metrics. |
| `Grunt/Grunt.Gateway.Client` | `Surprenant.Grunt.Gateway.Client` | Generated `HaloGatewayClient` plus its HTTP transport. |
| `Grunt/Grunt.Gateway.Host` | Docker image | The service: auth, quotas, caching, Halo account sign-in and refresh, health, OpenTelemetry. |

## How the generator picks its output

`GatewayGenerator` is in the same analyzer assembly as the interface generator. Each project that references it as an analyzer
chooses what it gets with an assembly attribute that names the client type:

```csharp
// Grunt.Gateway/GatewayAssembly.cs
[assembly: GenerateGatewayController(typeof(HaloInfiniteClient))]   // emits HaloInfiniteController

// Grunt.Gateway.Client/GatewayClientAssembly.cs
[assembly: GenerateGatewayClient(typeof(HaloInfiniteClient))]       // emits HaloGatewayClient
```

The generator reads `HaloInfiniteClient`'s methods and their `[GatewayExposed]` attributes from the referenced
`Surprenant.Grunt` assembly, so the core library never has to know about ASP.NET Core or the gateway projects. Both attributes
take `RoutePrefix` (default `halo/v1`, and it must match on both sides) and `Namespace` (default: the assembly name).
In the core project the generator finds no marker and emits nothing.

To see the generated code, build with `-p:EmitCompilerGeneratedFiles=true` and look under
`obj/.../generated/Grunt.SourceGenerator/`. `Grunt/Tests/Snapshots/` holds the current output, and a test fails when the
output changes.

## Exposing a method

```csharp
[GatewayExposed("stats/matches/{matchId}", Cache = GatewayCachePolicy.Immutable)]
public async Task<HaloApiResultContainer<MatchStats, HaloApiErrorContainer>> StatsGetMatchStats(string matchId)
```

- Parameters named in the route bind from the route. Other simple parameters (string, numbers, bool, enums, Guid, dates) and
  collections of them (`List<T>`, `IEnumerable<T>`, arrays) bind from the query string. A single complex parameter is sent as
  the JSON body, and that needs `HttpMethod = "POST"` (or PUT/PATCH).
- `HttpMethod` defaults to GET. `Name` sets the action name, which overloads need.
- `Cache`: `None`, `ShortTtl` (for `CacheSeconds`, default 60) or `Immutable` (for the gateway's `ImmutableTtl`).
- Only expose read-only methods. Never expose auth/token, write or UGC-publishing methods.

Generator errors:

| Id | Meaning |
|:---|:--------|
| GRUNTGW001 | Generic methods can't be exposed |
| GRUNTGW002 | A complex parameter on a GET/DELETE method, or more than one complex parameter |
| GRUNTGW003 | Duplicate action name (set `Name`) |
| GRUNTGW004 | A route placeholder with no matching parameter, a non-simple route parameter, or a malformed template |
| GRUNTGW005 | The return type isn't `Task<HaloApiResultContainer<T, HaloApiErrorContainer>>` |
| GRUNTGW006 | The client's `I{Client}` interface wasn't found |
| GRUNTGW007 | Unsupported HTTP method |

### Exposed today (`halo/v1/...`)

| Route | Method | Cache | Why |
|:------|:-------|:------|:----|
| `GET stats/matches/{matchId}` | `StatsGetMatchStats(string)` | immutable | GrifballWebApp health check and match import |
| `GET stats/matches/{matchId:guid}` | `StatsGetMatchStats(Guid)` (`StatsGetMatchStatsByGuid`) | immutable | GrifballWebApp `DataPullService.GetMatch` |
| `GET stats/players/{player}/matches?start&count&type` | `StatsGetMatchHistory` | 60 s | GrifballWebApp match history pull |
| `GET stats/players/{player}/match-count` | `StatsGetMatchCount` | 60 s | player match counts |
| `GET stats/players/{player}/matches/{matchId}/progression` | `StatsGetPlayerMatchProgression` | immutable | per-match progression |
| `GET skill/matches/{matchId}/players?playerIds=` | `SkillGetMatchPlayerResult` | immutable | per-match CSR/skill |
| `GET skill/playlists/{playlistId}/csr?playerIds=` | `SkillGetPlaylistCsr` | 60 s | current CSR |
| `GET users?xuids=` | `Users` | 300 s | GrifballWebApp XUID to gamertag lookups |
| `GET users/by-gamertag/{gamertag}` | `UserByGamertag` | 300 s | GrifballWebApp gamertag to XUID lookups |
| `GET medals` | `Medals` | 1 h | GrifballWebApp medal metadata |
| `GET gamecms/image?filePath=` | `GameCmsGetImage` | immutable | medal and CMS images (binary) |

Grunt has no service-record endpoint yet. Expose one once it's added to `HaloInfiniteClient`.

## Wire contract

- The body is the serialised `HaloApiResultContainer` (`Result`, `Error.Code`, `Error.Message`, `Error.RetryAfter`), so the
  client gets back exactly what the factory returned. A successful `byte[]` result is sent as raw bytes
  (`application/octet-stream`) instead.
- `X-Halo-Upstream-Status` carries Halo's status code. `0` means Halo gave no response.
- HTTP status: 2xx → 200, other 4xx (e.g. 404) passed through, 401/403 → 502 (the gateway's own Halo credentials failed),
  429 → 503 with `Retry-After`, 5xx or no response → 502. The gateway's own errors don't carry `X-Halo-Upstream-Status`:
  401 (bad API key) and 429 (quota).
- Retries happen in one place per hop. The factory retries Halo calls (401/403 once with fresh tokens; 429/5xx/network with
  exponential backoff and jitter, honouring `Retry-After`; `ClientConfiguration:Retry`). The controller never retries.
  The client retries only its own transport: gateway unreachable, or 429/5xx *without* `X-Halo-Upstream-Status`.
- `byte[]` is still buffered once in memory, because `HaloInfiniteClient` returns `byte[]`. True streaming needs a
  Stream-returning client method, which is a follow-up.

## Using the client

```csharp
services.AddHaloGatewayClient(configuration.GetSection("HaloGateway"));
// HaloGateway: { "BaseAddress": "http://halo-gateway.halo-gateway.svc:8080/", "ApiKey": "<secret>", "Retry": { "MaxAttempts": 3 } }
```

This registers `HaloGatewayClient` as a typed HttpClient and as `IHaloInfiniteClient`. A method the gateway doesn't expose
throws `NotSupportedException` and names the method. Apps that inject `IHaloInfiniteClientFactory` today need to switch to
`IHaloInfiniteClient` for the calls they route through the gateway.

## Running the host

Configuration (`appsettings.json`, environment variables such as `Gateway__AdminKey`):

| Key | Purpose |
|:----|:--------|
| `ClientConfiguration:ClientId` / `ClientSecret` / `RedirectUrl` | Microsoft app (Azure "Mobile and desktop" or web app with secret). `RedirectUrl` = `https://<public gateway host>/oauth/callback`. |
| `ClientConfiguration:Retry:*` | Factory backoff (`MaxAttempts`, `BaseDelay`, `MaxDelay`, `MaxRetryAfter`, `RetryStatusCodes`). |
| `Gateway:ApiKeys[]` | `Name`, `Key` (or `KeySha256`), `Environment`, `Priority` (0 = highest), `PermitsPerMinute`. |
| `Gateway:GlobalQuota` | `PermitsPerMinute` for all keys together. `ReservedFraction` of it is kept for keys with `Priority <= ReservedPriority` (prod wins when budget is tight). |
| `Gateway:AdminKey` | Separate key for `/admin/halo/*` (header `X-Admin-Key`). |
| `Gateway:Oidc` | Browser admin page `/admin`: `Authority`, `ClientId`, `ClientSecret`, `AllowedGroups[]`, `GroupClaim` (default `groups`), `Scopes[]`, `SessionLifetime`. Off (404) until `Authority` and `ClientId` are set. Redirect URI: `https://<public gateway host>/admin/signin-oidc`. |
| `Gateway:Cache` | `Enabled`, `ImmutableTtl`, `MaxCachedBinaryBytes`. |
| `Gateway:Auth` | `TokenPath` (on a volume), `RefreshInterval`, `SignInStateLifetime`, `UseInstanceLock`. |
| `Gateway:DataProtection:KeysPath` | Where the ASP.NET Core Data Protection key ring is persisted. Default: `TokenPath`'s directory plus `data-protection-keys` (the same volume, no chart change needed). |
| `ForwardedHeaders:KnownIPNetworks[]` / `KnownProxies[]` | CIDRs/addresses whose immediate requests may set the client address, scheme and host (X-Forwarded-\*). Unset: the ASP.NET Core default of loopback only, i.e. nothing coming through a real proxy is trusted. |
| `ForwardedHeaders:ForwardedForHeaderName` | Header carrying the client address, e.g. `X-Real-IP` behind Traefik (it has already resolved the real client). Default `X-Forwarded-For`. |
| `ForwardedHeaders:ForwardLimit` | How many proxy entries to walk back through. Default 1. |
| `OTEL_EXPORTER_OTLP_ENDPOINT`, `OTEL_SERVICE_NAME`, ... | Standard OpenTelemetry variables. OTLP export turns on when the endpoint is set (traces, metrics, and the Serilog logs sink). It is the only export: nothing is served for scraping. |

### One-time sign-in (and re-auth)

1. On the Microsoft app registration, add the redirect URI `https://<public gateway host>/oauth/callback`. This one URI
   serves every environment.

From a browser (`Gateway:Oidc` configured): open `https://<gateway>/admin`, sign in with the identity provider (only members
of `AllowedGroups`), press **Sign in with Microsoft**, sign in with the Halo account and approve. The callback sends the browser
back to `/admin`, which shows the status, token expiries, last refresh and last error, with **Refresh now** and **Sign out /
clear tokens** (after a confirmation). The page is server-rendered without scripts; its forms carry antiforgery tokens and
its session is an HttpOnly, Secure, SameSite=Lax cookie scoped to `/admin`. Identity headers set by a proxy are never
trusted. `/admin/halo/*` accepts that session too (unsafe methods then need the antiforgery token) besides the admin key.

With the admin key (automation):

2. `curl -H "X-Admin-Key: $ADMIN" https://<gateway>/admin/halo/signin` returns `{ "url": ... }`. Open it in a browser, sign
   in with the Halo account and approve. Microsoft redirects to `/oauth/callback`, and the gateway redeems the code (only for
   a state it issued, once, within 15 minutes) and stores the token. "Signed in" means done.
3. If the callback can't be reached, paste the redirect URL (or just the code):
   `curl -H "X-Admin-Key: $ADMIN" -H "Content-Type: application/json" -d '{"code":"<url or code>"}' https://<gateway>/admin/halo/code`.
4. `GET /admin/halo/status` shows whether it's authorized, token expiries, the last refresh and the last error.
   `POST /admin/halo/refresh` refreshes now. `DELETE /admin/halo/tokens` clears the token and forces a new sign-in.

A background service refreshes the OAuth token every 30 minutes, which keeps the refresh token rolling (the factory now also
persists tokens it refreshes itself). If Microsoft rejects the refresh token, `halo_auth` turns **Degraded** with the reason,
an Error is logged, and `grunt_gateway_halo_reauth_required` becomes 1. Repeat steps 2 to 4 to fix it.

### Health, metrics, traces

- `/health/live` runs no checks, so a Halo outage or a missing sign-in never restarts the pod. `/health/ready` and `/health`
  run `halo_auth` (Healthy, or Degraded, which still returns 200). Responses contain only the status word.
- Metrics, pushed over OTLP only (no `/metrics`; through an OpenTelemetry Collector into Prometheus they get these names):
  - `grunt_gateway_requests_total{key,environment,action}`
  - `grunt_gateway_quota_rejections_total{key,reason}`
  - `grunt_gateway_quota_remaining{key}`
  - `grunt_gateway_upstream_responses_total{action,status}`
  - `grunt_gateway_upstream_failures_total`
  - `grunt_halo_retries_total{operation,reason,status}`
  - `grunt_gateway_cache_lookups_total{action,result}`
  - `grunt_gateway_halo_reauth_required`
  - `grunt_gateway_halo_token_expiry_seconds{token}`
  - `grunt_gateway_halo_token_refreshes_total{result}`
  - ASP.NET Core, HttpClient and runtime metrics
- Traces cover incoming requests (health probes excluded) and outgoing Halo/Microsoft calls, with
  `grunt.cache`, `grunt.api_key` and `halo.upstream.status` tags.
- Logs go through Serilog: compact JSON on the console (what `kubectl logs` and the cluster's log collector see),
  with trace and span ids carried natively on every line. One summary line per request logs `ClientIp` (after
  `ForwardedHeaders` trust is applied), `RequestHost`, `RequestScheme`, method, path, status, elapsed, the
  matched endpoint and `ConsumerKeyName` (the API key's configured `Name`, never the key itself) once
  authenticated; health probes log at Verbose (dropped by default). The default minimum level is Warning (like
  GrifballWebApp), with `Surprenant.Grunt` and the request summary line raised to Information, so framework noise
  such as the health check service's own "Health check ... completed" line (otherwise printed on every kubelet
  probe) is dropped without an explicit override for it.

### Scale and storage

- Run **one replica**. The file token store is single-writer, and refresh-token rotation must not race. The host holds
  `.gateway.lock` next to the token file and refuses to start if another instance on the same volume holds it. Use a
  `Recreate` rollout.
- The response cache is in-process `IMemoryCache`. Each replica would have its own copy and it's lost on restart. A
  distributed cache (Redis) is the scale-out option.
- Token storage is pluggable: register another `IOAuthTokenStore` (a database row, or a Kubernetes Secret through the API)
  to replace the file store. That also lifts the single-replica limit, once refresh is coordinated (a lease or leader election).
- The ASP.NET Core Data Protection key ring (admin OIDC session cookie, antiforgery tokens, OAuth sign-in state) is
  persisted next to the OAuth token (`Gateway:DataProtection:KeysPath`, default `<TokenPath's directory>/data-protection-keys`)
  instead of the framework default, which the read-only root filesystem can't write to. Keys are unencrypted at rest on the
  PVC; acceptable for this single-tenant internal service today. Add `ProtectKeysWithCertificate` if that needs to change.

### Docker

```bash
docker build -f Grunt/Grunt.Gateway.Host/Dockerfile -t halo-gateway .
docker run -p 8080:8080 -v halo-gateway-data:/data -e Gateway__AdminKey=... halo-gateway
```

The image is the chiseled (distroless, non-root) ASP.NET 10 runtime. It works with a read-only root filesystem and writes only
to `/data`.
