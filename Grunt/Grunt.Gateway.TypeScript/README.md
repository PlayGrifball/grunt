# `@playgrifball/grunt-gateway-client`

Generated TypeScript types and Fetch API client for the public endpoints in `Grunt.Gateway.Host`.
The package is generated from the host's OpenAPI document, so endpoint descriptions, parameters,
responses, and authentication stay aligned with the deployed gateway.

The package is published to GitHub Packages with each gateway release whose public API changes.
Configure the PlayGrifball scope before installing it:

```ini
@playgrifball:registry=https://npm.pkg.github.com
```

```shell
npm install @playgrifball/grunt-gateway-client
```

Set the base URL and API key on the generated client before making requests:

```ts
import { client, userByGamertag } from '@playgrifball/grunt-gateway-client';

client.setConfig({
  baseUrl: 'https://halo-gateway.example.com',
  headers: { 'X-Api-Key': process.env.HALO_GATEWAY_API_KEY! },
});

const result = await userByGamertag({
  path: { gamertag: 'Example' },
});
```

Run `../../.github/scripts/generate-gateway-openapi.ps1`, then `npm run check`, to regenerate and
verify the client locally.
