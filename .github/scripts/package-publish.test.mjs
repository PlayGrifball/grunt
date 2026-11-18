import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';

const publish = readFileSync(new URL('../workflows/publish.yml', import.meta.url), 'utf8');
const release = readFileSync(new URL('../workflows/helm-chart.yml', import.meta.url), 'utf8');

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
