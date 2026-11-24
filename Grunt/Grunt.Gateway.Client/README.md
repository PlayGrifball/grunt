# `Surprenant.Grunt.Gateway.Client`

Generated .NET client for the public [Grunt Halo gateway](https://grunt.grifball.xyz/swagger/index.html). It implements
`IHaloInfiniteClient`, so an application can call the gateway without storing Halo, Xbox, or Microsoft credentials.

The client is generated from the same annotated methods as the server controller. Public endpoint descriptions, parameters,
response types, and errors are documented in the [live Swagger UI](https://grunt.grifball.xyz/swagger/index.html).

## Install

```shell
dotnet add package Surprenant.Grunt.Gateway.Client
```

Register the typed client from configuration. It calls the hosted gateway at `https://grunt.grifball.xyz/` unless
`BaseAddress` is set:

```csharp
using Surprenant.Grunt.Gateway.Client;

builder.Services.AddHaloGatewayClient(builder.Configuration.GetSection("HaloGateway"));
```

```json
{
  "HaloGateway": {
    "ApiKey": "your-api-key",
    "Retry": {
      "MaxAttempts": 3,
      "MaxRetryAfter": "00:00:30"
    }
  }
}
```

Inject `IHaloInfiniteClient` or `HaloGatewayClient` and call the normal Grunt methods:

```csharp
using Surprenant.Grunt.Core;

public sealed class MatchReader(IHaloInfiniteClient halo)
{
    public async Task ReadAsync(Guid matchId)
    {
        var response = await halo.StatsGetMatchStats(matchId);
        // Inspect response.Result or response.Error.
    }
}
```

Methods that are not exposed by the gateway throw `NotSupportedException` and identify the unsupported method.

## Supported methods

| .NET method | HTTP endpoint | Purpose |
|:------------|:--------------|:--------|
| `EconomyArmorCoreCustomization` | `GET /halo/v1/economy/players/{player}/customization/armors/{coreId}` | Retrieve one armor core |
| `EconomyArmorCoresCustomization` | `GET /halo/v1/economy/players/{player}/customization/armors` | Retrieve a player's armor cores and equipped pieces |
| `EconomyGetMultiplePlayersCustomization` | `GET /halo/v1/economy/customization` | Retrieve equipped customization for several players |
| `EconomyPlayerAppearanceCustomization` | `GET /halo/v1/economy/players/{player}/customization/appearance` | Retrieve one player's service tag, emblem, backdrop and poses |
| `EconomyPlayerPublicCustomization` | `GET /halo/v1/economy/players/{player}/customization` | Retrieve one player's public body, appearance and cores |
| `GameCmsGetCustomizationCatalog` | `GET /halo/v1/gamecms/customization-catalog` | Retrieve the item catalog (paths to names and images) |
| `GameCmsGetImage` | `GET /halo/v1/gamecms/image` | Retrieve an image from Halo's game CMS |
| `SkillGetMatchPlayerResult` | `GET /halo/v1/skill/matches/{matchId}/players` | Retrieve per-player skill results for a match |
| `SkillGetPlaylistCsr` | `GET /halo/v1/skill/playlists/{playlistId}/csr` | Retrieve current playlist CSR for one or more players |
| `StatsGetMatchCount` | `GET /halo/v1/stats/players/{player}/match-count` | Retrieve a player's match count |
| `StatsGetMatchHistory` | `GET /halo/v1/stats/players/{player}/matches` | Retrieve a page of a player's match history |
| `StatsGetMatchStats` (`string` and `Guid` overloads) | `GET /halo/v1/stats/matches/{matchId}` | Retrieve the stats for one match |
| `StatsGetPlayerMatchProgression` | `GET /halo/v1/stats/players/{player}/matches/{matchId}/progression` | Retrieve one player's progression for a match |
| `Users` | `GET /halo/v1/users` | Resolve one or more XUIDs to Halo users |
| `UserByGamertag` | `GET /halo/v1/users/by-gamertag/{gamertag}` | Resolve a gamertag to a Halo user |
| `Medals` | `GET /halo/v1/medals` | Retrieve Halo Infinite medal metadata |

Parameters, response schemas, cache behavior, and error responses are documented in the
[live Swagger UI](https://grunt.grifball.xyz/swagger/index.html).

## Errors, retries, and rate limits

All API keys share the hosted gateway's single upstream Halo account and therefore share its budgets. Cache hits do not use
the upstream budget. Current protective budgets are:

| Halo service | Gateway budget |
|:-------------|:---------------|
| Profile (`UserByGamertag`, `Users`) | 15 calls per minute, shared; a Halo 429 locks the budget for 5 minutes |
| Match history (`StatsGetMatchHistory`) | 2 calls per second; a call may wait up to 2 seconds for room |
| Match stats (`StatsGetMatchStats`) | 480 calls per minute; Halo's actual ceiling is unknown |

- A call rejected by the gateway's budget is never sent to Halo. The gateway returns `429 Too Many Requests` with
  `Retry-After`, and `response.Error.NotSent` is `true`.
- A `429` returned by Halo becomes HTTP `503 Service Unavailable`; `X-Halo-Upstream-Status` remains `429`, and the result
  body's error code remains `429`.
- The client retries gateway transport failures and gateway-owned `429`/`5xx` responses, bounded by `Retry.MaxAttempts` and
  `Retry.MaxRetryAfter`. It does not duplicate the gateway's upstream Halo retries.

See [the measured Halo limits and complete wire contract](https://github.com/PlayGrifball/grunt/blob/main/GATEWAY.md#wire-contract)
for details. Rate limits may change because Halo's APIs are undocumented.

## Links

- [Live Swagger UI](https://grunt.grifball.xyz/swagger/index.html)
- [Package source](https://github.com/PlayGrifball/grunt/tree/main/Grunt/Grunt.Gateway.Client)
- [TypeScript gateway client](https://www.npmjs.com/package/@playgrifball/grunt-gateway-client)
