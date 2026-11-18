# `Surprenant.Grunt`

Unofficial .NET client, models, authentication helpers, retry policy, and account-level rate-limit budgets for the Halo
Infinite APIs. This package is part of the [PlayGrifball Grunt](https://github.com/PlayGrifball/grunt) project and is based
on OpenSpartan Grunt by Den Delimarsky.

> Halo's APIs are undocumented and may change without notice. This project is not endorsed by Microsoft or 343 Industries.
> Keep Spartan, clearance, OAuth, and Xbox tokens secret and use your own account at your own risk.

## Install

```shell
dotnet add package Surprenant.Grunt
```

The low-level client accepts an `HttpClient`, Spartan token, optional XUID, and optional clearance token:

```csharp
using Surprenant.Grunt.Core;

using var http = new HttpClient();
var halo = new HaloInfiniteClient(
    http,
    spartanToken: "your-spartan-token",
    xuid: "xuid(your-xuid)",
    clearanceToken: "your-clearance-token");

var match = await halo.StatsGetMatchStats("match-id");
if (match.Result is not null)
{
    Console.WriteLine(match.Result);
}
else
{
    Console.WriteLine($"Halo error {match.Error?.Code}: {match.Error?.Message}");
}
```

`HaloInfiniteClientFactory` provides the complete OAuth/Xbox/Halo sign-in and refresh flow for hosted applications. See
the [gateway operations guide](https://github.com/PlayGrifball/grunt/blob/main/GATEWAY.md) for its configuration and
lifecycle.

## Retries and rate limits

`HaloRequestPolicy` handles transient network failures, `408`, `429`, and `5xx` responses with bounded exponential backoff,
jitter, and `Retry-After` support. `HaloBudgets` can limit groups of operations that share one Halo service quota. Every
request actually sent to Halo—including retries—uses room; cache hits outside this package do not.

Limits measured with one Halo account on 2026-11-15:

| Halo service | Observed behavior |
|:-------------|:------------------|
| Profile (`UserByGamertag`, `Users`) | Roughly 20–30 calls in a minute triggered 4–5 minutes of 429 responses across the service |
| Match history | 2–3 calls per second succeeded; excess calls returned `429` with `Retry-After: 1` |
| Match stats | No 429 observed up to 480 calls per minute; Halo's actual ceiling is unknown |
| Skill | No 429 observed up to 480 calls per minute in the measurement |

These limits are per Halo account, not per IP, and may change. The gateway uses deliberately lower protective budgets. Read
[the full measurements and budget semantics](https://github.com/PlayGrifball/grunt/blob/main/GATEWAY.md#halos-limits)
before configuring production traffic.

## Related clients and documentation

- [Hosted API and Swagger UI](https://grunt.grifball.xyz/swagger/index.html)
- [`Surprenant.Grunt.Gateway.Client`](https://www.nuget.org/packages/Surprenant.Grunt.Gateway.Client) for .NET applications
  that should call the gateway instead of holding Halo credentials
- [`@playgrifball/grunt-gateway-client`](https://www.npmjs.com/package/@playgrifball/grunt-gateway-client) for TypeScript
- [Repository and source](https://github.com/PlayGrifball/grunt)
