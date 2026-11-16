// The chart's alerts (templates/prometheusrule.yaml), offline: node --test charts/halo-gateway/tests/*.test.mjs
//   - every alert has a severity, a summary and a description (and a runbook_url, if it has one, is a URL)
//   - alerts.promtool.yml has, for every alert, a case where it fires and one where it doesn't
//   - the rules as the chart renders them pass promtool check rules and promtool test rules alerts.promtool.yml:
//     thresholds, `for:`, `or vector(0)` and absent() behave on made-up series (promtool, from PROMTOOL; skipped
//     without it: CI sets it)
// The rules evaluating in a real Prometheus on the gateway's own metrics is dashboard-stack.sh (CI's dashboard job).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { renderRules } from './prometheus-rules.mjs';

const rules = renderRules();
const unitTests = fs.readFileSync(path.join(import.meta.dirname, 'alerts.promtool.yml'), 'utf8');
/** Each alert's text in the rules file, by name. */
const alerts = new Map(rules.split(/^ *- alert: /m).slice(1).map(a => [a.split('\n')[0].trim(), a]));

test('the chart has its alerts', () => {
  assert.deepEqual([...alerts.keys()].sort(),
    ['HaloGatewayMetricsAbsent', 'HaloGatewayReauthRequired', 'HaloGatewayUpstreamErrors', 'HaloGatewayUpstreamThrottled']);
});

test('every alert has a severity, a summary and a description', () => {
  for (const [name, text] of alerts) {
    assert.match(text, /^\s+severity: (critical|warning|info)$/m, `${name}: no severity`);
    assert.match(text, /^\s+summary: \S/m, `${name}: no summary`);
    assert.match(text, /^\s+description: \S/m, `${name}: no description`);
    for (const m of text.matchAll(/^\s+runbook_url: (.*)$/gm)) assert.match(m[1], /^https:\/\//, `${name}: runbook_url ${m[1]}`);
  }
});

test('every alert has a case where it fires and one where it does not', () => {
  // Each `- eval_time:` entry: its alertname, and whether it expects alerts
  const cases = unitTests.split(/^ *- eval_time: /m).slice(1)
    .map(c => ({ alert: c.match(/alertname: (\S+)/)?.[1], fires: !/exp_alerts: \[\]/.test(c) }));
  for (const name of alerts.keys()) {
    assert.ok(cases.some(c => c.alert === name && c.fires), `${name}: no case where it fires`);
    assert.ok(cases.some(c => c.alert === name && !c.fires), `${name}: no case where it stays quiet`);
  }
});

test('the alerts behave (promtool test rules)', { skip: !process.env.PROMTOOL && 'PROMTOOL not set (CI sets it)' }, () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'alerts-'));
  fs.writeFileSync(path.join(dir, 'rules.yml'), rules);
  fs.writeFileSync(path.join(dir, 'alerts.promtool.yml'), unitTests);
  for (const args of [['check', 'rules', 'rules.yml'], ['test', 'rules', 'alerts.promtool.yml']]) {
    const r = spawnSync(process.env.PROMTOOL, args, { cwd: dir, encoding: 'utf8' });
    assert.equal(r.status, 0, `promtool ${args.join(' ')}\n${r.stdout}${r.stderr}`);
  }
});
