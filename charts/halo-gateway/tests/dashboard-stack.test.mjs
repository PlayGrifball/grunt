// dashboard-stack.sh's own behaviour, with docker and curl stubbed (no containers): node --test charts/halo-gateway/tests/*.test.mjs
//   - a gateway that never answers /health/live fails the run straight away, saying so
//   - its containers and network have names unique to the run, and it removes only those (never someone's `grafana`)
// Needs helm (HELM, or helm from PATH) and bash.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';

const script = path.join(import.meta.dirname, 'dashboard-stack.sh');

/** A directory with stub docker and curl that log their arguments, one call per line, to <dir>/calls. */
function stubs() {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'dashboard-stack-'));
  const log = path.join(dir, 'calls');
  fs.writeFileSync(path.join(dir, 'docker'), `#!/usr/bin/env bash
printf '%s\\n' "docker $*" >> '${log}'
case "$1" in
  port) echo 127.0.0.1:1 ;;
  create|run) echo 0123456789ab ;;
esac
exit 0
`, { mode: 0o755 });
  // Nothing answers: the gateway never comes up
  fs.writeFileSync(path.join(dir, 'curl'), `#!/usr/bin/env bash
printf '%s\\n' "curl $*" >> '${log}'
exit 7
`, { mode: 0o755 });
  return { dir, calls: () => fs.readFileSync(log, 'utf8').trim().split('\n') };
}

function run() {
  const { dir, calls } = stubs();
  const r = spawnSync('bash', [script], {
    encoding: 'utf8', timeout: 60000,
    env: { ...process.env, PATH: `${dir}:${process.env.PATH}`, HEALTH_TIMEOUT: '4', GATEWAY_IMAGE: 'halo-gateway:test' },
  });
  return { ...r, output: r.stdout + r.stderr, calls: calls() };
}

test('fails straight away when the gateway never answers /health/live', () => {
  const r = run();
  assert.ok(!r.error, `the script did not end within 60 s: ${r.error?.message}`);
  assert.notEqual(r.status, 0, r.output);
  assert.match(r.output, /never answered .*\/health\/live/, r.output);
  assert.ok(!r.calls.some(c => c.includes('dashboard-grafana.mjs')), 'went on to the dashboard check');
});

test('names its containers and network uniquely and removes only those', () => {
  const [a, b] = [run(), run()];
  for (const r of [a, b]) {
    const created = r.calls.flatMap(c => [...c.matchAll(/^docker (?:create|run)\b.*?--name (\S+)/g)].map(m => m[1]));
    const networks = r.calls.flatMap(c => [...c.matchAll(/^docker network create (\S+)/g)].map(m => m[1]));
    assert.ok(created.length >= 3, `created ${created}`);
    for (const name of [...created, ...networks]) assert.match(name, /^grunt-dashboard-\S+/, `generic name ${name}`);
    const removed = r.calls.filter(c => /^docker rm\b/.test(c)).flatMap(c => c.split(' ').slice(2).filter(a => !a.startsWith('-')));
    assert.ok(removed.length > 0, 'removed nothing');
    for (const name of removed) assert.ok(created.includes(name), `removed ${name}, which this run did not start`);
    const removedNetworks = r.calls.filter(c => /^docker network rm\b/.test(c)).map(c => c.split(' ')[3]);
    for (const name of removedNetworks) assert.ok(networks.includes(name), `removed network ${name}, which this run did not create`);
  }
  const prefix = r => r.calls.find(c => /--name /.test(c)).match(/--name (\S+)/)[1].split('-').slice(0, -1).join('-');
  assert.notEqual(prefix(a), prefix(b), 'two runs used the same names');
});
