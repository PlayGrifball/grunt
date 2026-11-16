// What `helm package` ships (.helmignore), offline: node --test charts/halo-gateway/tests/*.test.mjs
//   - the chart's templates, values, README and the dashboard JSON the template reads
//   - none of its tests, the e2e script, the CI values or the dashboard's generator
// Needs helm (HELM, or helm from PATH) and tar.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { execFileSync } from 'node:child_process';

const chart = path.resolve(import.meta.dirname, '..');
const helm = process.env.HELM || 'helm';

test('the packaged chart has what it needs and nothing for its tests', () => {
  const out = fs.mkdtempSync(path.join(os.tmpdir(), 'chart-package-'));
  execFileSync(helm, ['package', chart, '-d', out], { encoding: 'utf8' });
  const [tgz] = fs.readdirSync(out);
  const files = execFileSync('tar', ['tzf', path.join(out, tgz)], { encoding: 'utf8' }).trim().split('\n')
    .map(f => f.replace(/^halo-gateway\//, ''));
  for (const f of ['Chart.yaml', 'values.yaml', 'README.md', 'templates/deployment.yaml', 'templates/grafana-dashboard.yaml', 'dashboards/halo-gateway.json'])
    assert.ok(files.includes(f), `${f} is not in the package`);
  const extra = files.filter(f => /^(tests|e2e|ci)\//.test(f) || f.endsWith('.mjs') || f.endsWith('.sh'));
  assert.deepEqual(extra, [], 'test files in the package');
});
