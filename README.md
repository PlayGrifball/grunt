![Grunt logo](media/grunt-logo.png)

# Grunt

Grunt is an unofficial .NET client for the Halo Infinite APIs and an HTTP gateway that lets applications share one
authenticated Halo account safely. This PlayGrifball fork is based on
[OpenSpartan Grunt](https://github.com/OpenSpartan/grunt) by Den Delimarsky and remains licensed under the MIT License.

> Halo's APIs are undocumented and may change without notice. This project is not endorsed by Microsoft or 343 Industries.
> Use your own account and tokens, keep them secret, and understand that API access carries account risk.

## Documentation

- [Live gateway API and Swagger UI](https://grunt.grifball.xyz/swagger/index.html)
- [Gateway architecture, operations, rate limits, deployment, and administration](GATEWAY.md)
- [Core .NET package](Grunt/Grunt/README.md)
- [Gateway .NET client package](Grunt/Grunt.Gateway.Client/README.md)
- [Generated TypeScript client package](Grunt/Grunt.Gateway.TypeScript/README.md)
- [Interface and source-generation notes](INTERFACE_GENERATION.md)

## Packages

| Package | Registry | Purpose |
|:--------|:---------|:--------|
| `Surprenant.Grunt` | [NuGet](https://www.nuget.org/packages/Surprenant.Grunt) | Direct .NET client, models, authentication, retries, and Halo account budgets |
| `Surprenant.Grunt.Gateway.Client` | [NuGet](https://www.nuget.org/packages/Surprenant.Grunt.Gateway.Client) | Generated `IHaloInfiniteClient` implementation for the hosted gateway |
| `@playgrifball/grunt-gateway-client` | [npm](https://www.npmjs.com/package/@playgrifball/grunt-gateway-client) | Generated TypeScript types and Fetch client for the hosted gateway |

The packages are also mirrored to [PlayGrifball GitHub Packages](https://github.com/orgs/PlayGrifball/packages?repo_name=grunt).

## Projects

| Project | Purpose |
|:--------|:--------|
| [`Grunt`](Grunt/Grunt) | Core Halo Infinite client, models, authentication, retries, and rate-limit budgets |
| [`Grunt.SourceGenerator`](Grunt/Grunt.SourceGenerator) | Generates the client interface, gateway controller, and gateway client |
| [`Grunt.Gateway`](Grunt/Grunt.Gateway) | ASP.NET Core gateway controller, caching, metrics, and error mapping |
| [`Grunt.Gateway.Client`](Grunt/Grunt.Gateway.Client) | .NET client for the gateway |
| [`Grunt.Gateway.TypeScript`](Grunt/Grunt.Gateway.TypeScript) | Generated TypeScript client and checked-in OpenAPI contract |
| [`Grunt.Gateway.Host`](Grunt/Grunt.Gateway.Host) | Deployable gateway host |
| [`Grunt.Librarian`](Grunt/Grunt.Librarian) | Endpoint code-generation utility |
| [`Grunt.Zeta`](Grunt/Grunt.Zeta) | Interactive development and authentication test application |
| [`Tests`](Grunt/Tests) | Unit, contract, OpenAPI, generator, and gateway round-trip tests |

## Gateway quick start

The hosted consumer API uses `X-Api-Key` authentication. Explore its operations, parameters, response bodies, and documented
errors in the [Swagger UI](https://grunt.grifball.xyz/swagger/index.html). Admin, OAuth callback, browser-admin, and health
routes are intentionally excluded from the public OpenAPI document.

For .NET applications:

```shell
dotnet add package Surprenant.Grunt.Gateway.Client
```

```csharp
builder.Services.AddHaloGatewayClient(builder.Configuration.GetSection("HaloGateway"));
```

```json
{
  "HaloGateway": {
    "BaseAddress": "https://grunt.grifball.xyz/",
    "ApiKey": "your-api-key"
  }
}
```

For TypeScript applications:

```shell
npm install @playgrifball/grunt-gateway-client
```

See the [.NET client README](Grunt/Grunt.Gateway.Client/README.md) and
[TypeScript client README](Grunt/Grunt.Gateway.TypeScript/README.md) for complete examples.

## Rate limits

The hosted gateway protects its single upstream Halo account with shared budgets. API keys do not receive separate quotas;
all callers share the same upstream capacity. Cache hits do not consume a Halo call.

Current protective budgets are:

| Halo service | Gateway budget |
|:-------------|:---------------|
| Profile (`UserByGamertag`, `Users`) | 15 calls per minute, shared; a Halo 429 locks the budget for 5 minutes |
| Match history (`StatsGetMatchHistory`) | 2 calls per second, with up to 2 seconds of waiting for room |
| Match stats (`StatsGetMatchStats`) | 480 calls per minute; this is a measured safe ceiling, not Halo's known limit |

When the gateway budget has no room, it returns `429 Too Many Requests` with `Retry-After` without calling Halo. When Halo
itself throttles an upstream request, the gateway returns `503 Service Unavailable`, preserves Halo's `429` in
`X-Halo-Upstream-Status`, and includes `Retry-After`. See [Halo's measured limits](GATEWAY.md#halos-limits) for the evidence
and full behavior.

## Development

Build and test everything:

```shell
dotnet test Grunt/Grunt.slnx --configuration Release
```

Validate the generated TypeScript client:

```powershell
./.github/scripts/generate-gateway-openapi.ps1
Set-Location Grunt/Grunt.Gateway.TypeScript
npm ci
npm run check
```

Changes are welcome through [issues](https://github.com/PlayGrifball/grunt/issues) and
[pull requests](https://github.com/PlayGrifball/grunt/pulls). Please preserve the upstream attribution and review
[CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md).
