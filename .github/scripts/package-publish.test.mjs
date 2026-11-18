import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';

const publish = readFileSync(new URL('../workflows/publish.yml', import.meta.url), 'utf8');
const release = readFileSync(new URL('../workflows/helm-chart.yml', import.meta.url), 'utf8');
const rootReadme = readFileSync(new URL('../../README.md', import.meta.url), 'utf8');
const coreReadme = readFileSync(new URL('../../Grunt/Grunt/README.md', import.meta.url), 'utf8');
const gatewayReadme = readFileSync(new URL('../../Grunt/Grunt.Gateway.Client/README.md', import.meta.url), 'utf8');
const typescriptReadme = readFileSync(new URL('../../Grunt/Grunt.Gateway.TypeScript/README.md', import.meta.url), 'utf8');
const coreProject = readFileSync(new URL('../../Grunt/Grunt/Surprenant.Grunt.csproj', import.meta.url), 'utf8');
const gatewayProject = readFileSync(new URL('../../Grunt/Grunt.Gateway.Client/Surprenant.Grunt.Gateway.Client.csproj', import.meta.url), 'utf8');
const openapi = JSON.parse(readFileSync(new URL('../../Grunt/Grunt.Gateway.TypeScript/openapi.json', import.meta.url), 'utf8'));

test('publishes both NuGet clients to nuget.org and GitHub Packages', () => {
  assert.match(publish, /dotnet nuget push[^\n]+https:\/\/api\.nuget\.org\/v3\/index\.json/);
  assert.match(publish, /dotnet nuget push[^\n]+https:\/\/nuget\.pkg\.github\.com\/PlayGrifball\/index\.json/);
});

test('publishes the TypeScript client to GitHub Packages and npmjs', () => {
  assert.match(publish, /npm publish package\/\*\.tgz --registry https:\/\/npm\.pkg\.github\.com --access public/);
  assert.match(publish, /npm publish package\/\*\.tgz --registry https:\/\/registry\.npmjs\.org --access public/);
  assert.match(publish, /npm view "@playgrifball\/grunt-gateway-client@\$VERSION" version --registry https:\/\/npm\.pkg\.github\.com/);
  assert.match(publish, /npm view "@playgrifball\/grunt-gateway-client@\$VERSION" version --registry https:\/\/registry\.npmjs\.org/);
  assert.match(publish, /id-token: write/);
  assert.match(publish, /packages: write/);
});

test('each registry is an independent retryable job', () => {
  for (const job of ['nuget-org', 'nuget-github', 'npm-github', 'npmjs']) {
    assert.match(publish, new RegExp(`\\n  ${job}:\\r?\\n    needs: \\[plan, build\\]`), `${job} is not independent`);
  }
  assert.doesNotMatch(publish, /\n  publish:\r?\n/);
});

test('the gateway release passes repository secrets to the package publisher', () => {
  assert.match(release, /\r?\n  packages:[\s\S]*?uses: \.\/\.github\/workflows\/publish\.yml[\s\S]*?secrets: inherit/);
});

test('the GitHub release waits for packages and records the published package families', () => {
  assert.match(release, /\r?\n  release:\r?\n    needs: \[publish, packages\]/);
  assert.match(release, /NUGET_PUBLISHED: \$\{\{ needs\.packages\.outputs\.nuget \}\}/);
  assert.match(release, /TYPESCRIPT_PUBLISHED: \$\{\{ needs\.packages\.outputs\.typescript \}\}/);
  assert.match(release, /package_flags\+\=\(--nuget\)/);
  assert.match(release, /package_flags\+\=\(--typescript\)/);
});

test('repository and package readmes link Swagger and explain rate limits', () => {
  for (const [name, readme] of Object.entries({ rootReadme, coreReadme, gatewayReadme, typescriptReadme })) {
    assert.match(readme, /https:\/\/grunt\.grifball\.xyz\/swagger\/index\.html/, `${name} has no live Swagger link`);
    assert.match(readme, /rate limits/i, `${name} has no rate-limit documentation`);
  }
  for (const stale of ['github.com/dend/grunt', 'docs.grunt.com', 'docs.gruntapi.com']) {
    assert.ok(!rootReadme.includes(stale), `root README still links ${stale}`);
  }
});

test('each NuGet package embeds its focused local readme', () => {
  for (const [name, project] of Object.entries({ coreProject, gatewayProject })) {
    assert.match(project, /<PackageReadmeFile>README\.md<\/PackageReadmeFile>/, `${name} has no package readme`);
    assert.match(project, /<None Update="README\.md"[^>]*>/, `${name} does not pack its local readme`);
    assert.ok(!project.includes('..\\..\\README.md'), `${name} still packs the repository readme`);
  }
});

test('both gateway client readmes list every public OpenAPI operation and route', () => {
  const operations = Object.entries(openapi.paths).flatMap(([path, methods]) =>
    Object.entries(methods)
      .filter(([method]) => ['get', 'post', 'put', 'patch', 'delete'].includes(method))
      .map(([method, operation]) => ({ id: operation.operationId, route: `${method.toUpperCase()} ${path}` })));

  const rows = readme => {
    const section = readme.split('## Supported methods')[1]?.split(/^## /m)[0] ?? '';
    return [...section.matchAll(/^\| `([^`]+)`(?: \([^|]+\))? \| `([^`]+)` \|/gm)]
      .map(match => ({ id: match[1], route: match[2] }));
  };

  assert.deepEqual(rows(gatewayReadme), operations, 'the .NET supported-method table differs from OpenAPI');
  assert.deepEqual(rows(typescriptReadme), operations.map(operation => ({
    ...operation,
    id: operation.id[0].toLowerCase() + operation.id.slice(1),
  })), 'the TypeScript supported-method table differs from OpenAPI');
});
