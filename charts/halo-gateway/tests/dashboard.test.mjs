// The chart's Grafana dashboard (dashboards/halo-gateway.json), offline: node --test charts/halo-gateway/tests/
//   - the JSON is what dashboards/halo-gateway.mjs makes (edit the script, then run it)
//   - it has a uid, unique panel ids, panels inside the 24-column grid
//   - every datasource it names is one of its own variables (a fixed uid would only work in one Grafana)
//   - every grunt_* metric it queries is an instrument the gateway defines (Grunt, Grunt.Gateway, Grunt.Gateway.Host), so
//     a renamed metric fails here, not as an empty panel
//   - every query parses as PromQL (promtool, from PROMTOOL; skipped without it: CI sets it)
// The queries running against real data, through a real Grafana, is dashboard-grafana.mjs (CI's dashboard job).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import generated from '../dashboards/halo-gateway.mjs';

const chart = path.resolve(import.meta.dirname, '..');
const repo = path.resolve(chart, '../..');
const dashboard = JSON.parse(fs.readFileSync(path.join(chart, 'dashboards/halo-gateway.json'), 'utf8'));

/** Every panel, rows' too. */
const panels = d => d.panels.flatMap(p => [p, ...(p.panels ?? [])]);
/** Every query, with its panel. */
const queries = d => panels(d).flatMap(p => (p.targets ?? []).map(t => ({ panel: p.title, target: t, expr: t.expr })));
/** A query as plain PromQL: the selector and Grafana's interval filled in. */
export const promql = expr => expr.replaceAll('$sel', 'k8s_namespace_name="halo-gateway", job=~"(.+/)?halo-gateway"').replaceAll('$__rate_interval', '5m');

test('the JSON is what the generator makes', () => {
  assert.deepEqual(dashboard, JSON.parse(JSON.stringify(generated)), 'run: node charts/halo-gateway/dashboards/halo-gateway.mjs');
});

test('a uid, unique panel ids, panels inside the grid', () => {
  assert.equal(dashboard.uid, 'halo-gateway');
  const ids = panels(dashboard).map(p => p.id);
  assert.equal(new Set(ids).size, ids.length, 'panel ids repeat');
  for (const p of panels(dashboard))
    assert.ok(p.gridPos.x >= 0 && p.gridPos.x + p.gridPos.w <= 24, `${p.title} is outside the 24 columns`);
});

test('every datasource is one of its variables', () => {
  const variables = new Set(dashboard.templating.list.filter(v => v.type === 'datasource').map(v => `\${${v.name}}`));
  const walk = (o, where) => {
    if (Array.isArray(o)) return o.forEach(x => walk(x, where));
    if (!o || typeof o !== 'object') return;
    if (o.datasource) assert.ok(variables.has(o.datasource.uid), `${where}: datasource ${o.datasource.uid} is not a datasource variable`);
    for (const [k, v] of Object.entries(o)) if (k !== 'datasource') walk(v, o.title ?? where);
  };
  walk(dashboard.panels, 'dashboard');
  assert.ok(dashboard.templating.list.some(v => v.name === 'sel' && v.query === '__SELECTOR__'), 'the chart fills $sel in through __SELECTOR__');
});

test('every grunt_* metric it queries is one the gateway defines', () => {
  // Instruments: Create*("grunt.x.y", ...), as Prometheus names them (dots to underscores; a unit or _total added)
  const instruments = new Set();
  const walk = dir => {
    for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
      const f = path.join(dir, e.name);
      if (e.isDirectory() && !['bin', 'obj', 'Tests'].includes(e.name)) walk(f);
      else if (e.name.endsWith('.cs'))
        for (const m of fs.readFileSync(f, 'utf8').matchAll(/Create\w*(?:<[^>]+>)?\(\s*"(grunt\.[a-z_.]+)"/g)) instruments.add(m[1].replaceAll('.', '_'));
    }
  };
  walk(path.join(repo, 'Grunt'));
  assert.ok(instruments.size > 10, `found only ${instruments.size} instruments: the search is broken`);
  const used = new Set(queries(dashboard).flatMap(q => [...q.expr.matchAll(/\b(grunt_[a-z_]+)/g)].map(m => m[1])));
  for (const metric of used) {
    const base = metric.replace(/_(bucket|sum|count)$/, '').replace(/_total$/, '');
    const known = [...instruments].some(i => base === i || base === `${i}_seconds` || base === `${i}_bytes`);
    assert.ok(known, `${metric} is no instrument the gateway defines`);
  }
});

test('every query parses as PromQL', { skip: !process.env.PROMTOOL && 'PROMTOOL not set (CI sets it)' }, () => {
  // As recording rules, one group each, so promtool names the panel of a query that doesn't parse
  const groups = queries(dashboard).map((q, i) => `  - name: "${i} ${q.panel}"\n    rules:\n      - record: q\n        expr: ${JSON.stringify(promql(q.expr))}\n`);
  const file = path.join(fs.mkdtempSync(path.join(os.tmpdir(), 'dashboard-')), 'queries.yml');
  fs.writeFileSync(file, `groups:\n${groups.join('')}`);
  const r = spawnSync(process.env.PROMTOOL, ['check', 'rules', file], { encoding: 'utf8' });
  assert.equal(r.status, 0, r.stdout + r.stderr);
});
