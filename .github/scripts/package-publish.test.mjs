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
  assert.match(publish, /npm publish --registry https:\/\/npm\.pkg\.github\.com --access public/);
  assert.match(publish, /npm publish --registry https:\/\/registry\.npmjs\.org --access public/);
  assert.match(publish, /id-token: write/);
  assert.match(publish, /packages: write/);
});

test('the gateway release passes repository secrets to the package publisher', () => {
  assert.match(release, /\r?\n  packages:[\s\S]*?uses: \.\/\.github\/workflows\/publish\.yml[\s\S]*?secrets: inherit/);
});
