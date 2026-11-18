# `@playgrifball/grunt-gateway-client`

Generated TypeScript types and Fetch API client for the public endpoints in `Grunt.Gateway.Host`.
The package is generated from the host's OpenAPI document, so endpoint descriptions, parameters,
responses, and authentication stay aligned with the deployed gateway.

Browse every operation and documented response in the
[live Swagger UI](https://grunt.grifball.xyz/swagger/index.html).

The package is published to npmjs and GitHub Packages with each gateway release whose public API changes.
Install it from npmjs without any registry configuration:

```shell
npm install @playgrifball/grunt-gateway-client
```

To use the GitHub Packages copy instead, configure the PlayGrifball scope:

```ini
@playgrifball:registry=https://npm.pkg.github.com
```

The client calls the hosted gateway at `https://grunt.grifball.xyz`. Set your API key on it before making requests:

```ts
import { client, userByGamertag } from '@playgrifball/grunt-gateway-client';

client.setConfig({
  headers: { 'X-Api-Key': process.env.HALO_GATEWAY_API_KEY! },
});

const result = await userByGamertag({
  path: { gamertag: 'Example' },
});
```

## Supported methods

| TypeScript function | HTTP endpoint | Purpose |
|:--------------------|:--------------|:--------|
| `gameCmsGetImage` | `GET /halo/v1/gamecms/image` | Retrieve an image from Halo's game CMS |
| `skillGetMatchPlayerResult` | `GET /halo/v1/skill/matches/{matchId}/players` | Retrieve per-player skill results for a match |
| `skillGetPlaylistCsr` | `GET /halo/v1/skill/playlists/{playlistId}/csr` | Retrieve current playlist CSR for one or more players |
| `statsGetMatchCount` | `GET /halo/v1/stats/players/{player}/match-count` | Retrieve a player's match count |
| `statsGetMatchHistory` | `GET /halo/v1/stats/players/{player}/matches` | Retrieve a page of a player's match history |
| `statsGetMatchStats` | `GET /halo/v1/stats/matches/{matchId}` | Retrieve the stats for one match |
| `statsGetPlayerMatchProgression` | `GET /halo/v1/stats/players/{player}/matches/{matchId}/progression` | Retrieve one player's progression for a match |
| `users` | `GET /halo/v1/users` | Resolve one or more XUIDs to Halo users |
| `userByGamertag` | `GET /halo/v1/users/by-gamertag/{gamertag}` | Resolve a gamertag to a Halo user |
| `medals` | `GET /halo/v1/medals` | Retrieve Halo Infinite medal metadata |

The generated option and result types provide each operation's path, query, response, and error shapes. See the
[live Swagger UI](https://grunt.grifball.xyz/swagger/index.html) for the complete schemas and response descriptions.

## Errors and rate limits

All API keys share the hosted gateway's single upstream Halo account and its budgets. Cache hits do not consume an upstream
call. The current protective budgets are 15 profile calls per minute (shared by `UserByGamertag` and `Users`), 2 match-history
calls per second, and 480 match-stat calls per minute. Halo's actual limits are undocumented and may change.

- When the gateway budget has no room, it returns `429 Too Many Requests` with `Retry-After` without calling Halo.
- When Halo returns `429`, the gateway responds with HTTP `503 Service Unavailable`, preserves `429` in
  `X-Halo-Upstream-Status`, and includes `Retry-After`.
- Other documented consumer errors include `400`, `401`, `404`, and `502`. Inspect the generated result/error types and the
  [Swagger documentation](https://grunt.grifball.xyz/swagger/index.html) for each operation.

See the [complete wire contract and measured rate limits](https://github.com/PlayGrifball/grunt/blob/main/GATEWAY.md#wire-contract)
for details.

Run `../../.github/scripts/generate-gateway-openapi.ps1`, then `npm run check`, to regenerate and
verify the client locally.
