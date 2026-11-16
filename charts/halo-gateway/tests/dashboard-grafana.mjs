// The chart's dashboard end to end: every panel's query through a real Grafana's query API (/api/ds/query), against a
// Prometheus the gateway itself pushes its metrics to (dashboard-stack.sh starts them). So it covers the chart's
// rendering (the dashboard's uid and its $sel filled in), Grafana loading the dashboard, every query running, and the
// metrics' real names and labels, not only the file. The query API does not interpolate dashboard variables (Grafana's
// frontend does, before it sends a query), so this fills in $sel itself, with the value as the chart rendered it, and
// sends each panel's minimum interval as the frontend would; dashboard-browser.mjs runs the same panels through
// Grafana's own interpolation and escaping. This one is the wait for data and the alerts' check. With --prometheus and --rules, also the chart's alerts (prometheus-rules.mjs) in that
// Prometheus: every rule in the file loaded, evaluated, health ok and no error, and HaloGatewayMetricsAbsent inactive
// (so its selector matches the series the gateway really pushes).
//
//   node charts/halo-gateway/tests/dashboard-grafana.mjs [--url http://localhost:3000] --uid <uid> [--timeout 180] \
//     [--prometheus http://localhost:9090 --rules rules.yml]
//
// Fails on a query Grafana or Prometheus rejects, and on a panel with no data unless it is in MAY_BE_EMPTY: those need
// what CI can't make (a signed-in Halo account answering, Halo throttling, retries). Prints a table of rows per panel.
import fs from 'node:fs';
import { fileURLToPath } from 'node:url';

const args = Object.fromEntries(process.argv.slice(2).reduce((a, v, i, all) => (v.startsWith('--') ? [...a, [v.slice(2), all[i + 1]]] : a), []));
const url = args.url ?? 'http://localhost:3000';
const timeout = Number(args.timeout ?? 180) * 1000;
const uid = args.uid;
const alertNames = args.rules ? [...fs.readFileSync(args.rules, 'utf8').matchAll(/^ *- alert: (\S+)/gm)].map(m => m[1]) : [];

export const MAY_BE_EMPTY = new Set([
  // Halo itself: nothing is sent without a signed-in account
  'Sign-in refresh due in', 'Halo 429s (1 h)', 'Requests to Halo by operation', 'Requests to Halo by status',
  'Halo answers after retries', 'Retries',
  // Budgets turning calls away, and waits: no call gets as far as Halo in CI
  'Turned away by budgets (1 h)', 'Turned away, not sent', 'Wait for room (p95)',
  'Token refreshes',
]);

async function call(path, body, base = url) {
  const response = await fetch(`${base}${path}`, {
    method: body ? 'POST' : 'GET', headers: { 'Content-Type': 'application/json' }, body: body && JSON.stringify(body),
    signal: AbortSignal.timeout(30000),
  });
  const text = await response.text();
  let json;
  try { json = JSON.parse(text); } catch { json = { message: text }; }
  return { ok: response.ok, status: response.status, json };
}

/** Data points across a /api/ds/query result's frames. */
const rows = result => (result?.frames ?? []).reduce((n, f) => n + (f.data?.values?.[0]?.length ?? 0), 0);

async function check() {
  const failures = [];
  const report = ['| Panel | Query | Rows |', '| --- | --- | ---: |'];
  const health = await call('/api/datasources/uid/prometheus/health');
  if (health.json.status !== 'OK') return { failures: [`datasource prometheus: ${JSON.stringify(health.json)}`], report };
  const loaded = await call(`/api/dashboards/uid/${uid}`);
  if (!loaded.ok) return { failures: [`dashboard ${uid} not loaded: ${loaded.status} ${JSON.stringify(loaded.json)}`], report };
  const dashboard = loaded.json.dashboard;
  const sel = dashboard.templating.list.find(v => v.name === 'sel')?.query;
  if (!sel || sel.includes('__SELECTOR__')) failures.push(`the chart did not fill in $sel: ${sel}`);
  const panels = dashboard.panels.flatMap(p => [p, ...(p.panels ?? [])]).filter(p => p.targets?.length);
  for (const panel of panels) {
    let count = 0;
    for (const target of panel.targets) {
      const query = { ...target, datasource: { type: 'prometheus', uid: 'prometheus' }, expr: target.expr.replaceAll('$sel', sel), maxDataPoints: 100, intervalMs: 15000, ...(panel.interval && { interval: panel.interval }) };
      const { ok, status, json } = await call('/api/ds/query', { queries: [query], from: 'now-15m', to: 'now' });
      const result = json.results?.[target.refId];
      const error = result?.error ?? (!ok && `${status} ${json.message}`);
      const n = rows(result);
      count += n;
      report.push(`| ${panel.title} | ${target.refId} | ${n} |`);
      if (error) failures.push(`${panel.title} (${target.refId}): ${error}`);
    }
    if (count === 0 && !MAY_BE_EMPTY.has(panel.title)) failures.push(`${panel.title}: no data`);
  }
  if (args.prometheus) failures.push(...await checkRules(report));
  return { failures, report, panels: panels.length };
}

/** The chart's alerts in Prometheus: each one loaded, evaluated without error. */
async function checkRules(report) {
  const failures = [];
  const { ok, status, json } = await call('/api/v1/rules', undefined, args.prometheus);
  if (!ok) return [`Prometheus /api/v1/rules: ${status} ${JSON.stringify(json)}`];
  const rules = new Map(json.data.groups.flatMap(g => g.rules).map(r => [r.name, r]));
  if (!alertNames.length) failures.push(`no alerts in ${args.rules}`);
  report.push('', '| Alert | Health | State | Last evaluation |', '| --- | --- | --- | --- |');
  for (const name of alertNames) {
    const rule = rules.get(name);
    if (!rule) { failures.push(`alert ${name}: not loaded in Prometheus`); continue; }
    report.push(`| ${name} | ${rule.health} | ${rule.state} | ${rule.lastEvaluation} |`);
    if (rule.lastEvaluation.startsWith('0001-')) failures.push(`alert ${name}: not evaluated yet`);
    else if (rule.health !== 'ok' || rule.lastError) failures.push(`alert ${name}: health ${rule.health} ${rule.lastError ?? ''}`);
  }
  if (rules.get('HaloGatewayMetricsAbsent')?.state !== 'inactive')
    failures.push('alert HaloGatewayMetricsAbsent is not inactive: its selector misses the series the gateway pushes');
  return failures;
}

async function main() {
  if (!uid) throw new Error('--uid: the dashboard uid');
  const deadline = Date.now() + timeout;
  let outcome;
  while (true) {
    try {
      outcome = await check();
    } catch (error) {
      outcome = { failures: [`Grafana not answering: ${error.message}`], report: [] };
    }
    if (!outcome.failures.length || Date.now() > deadline) break;
    console.log(`waiting on: ${outcome.failures.join('; ')}`);
    await new Promise(r => setTimeout(r, 10000));
}
console.log(outcome.report.join('\n'));
if (outcome.failures.length) {
  console.log(`\nFAILED:\n  ${outcome.failures.join('\n  ')}`);
  process.exit(1);
}
console.log(`\nOK: ${outcome.panels} panels queried through Grafana, every one that can have data has some${alertNames.length ? `; ${alertNames.length} alerts evaluated in Prometheus` : ''}`);
}

if (process.argv[1] === fileURLToPath(import.meta.url)) await main();
