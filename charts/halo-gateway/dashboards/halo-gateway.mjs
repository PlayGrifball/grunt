// Generates halo-gateway.json, the chart's Grafana dashboard: node charts/halo-gateway/dashboards/halo-gateway.mjs
// Built here rather than hand-edited, so its panels share one shape; tests/dashboard.test.mjs fails if the JSON and
// this script disagree. Its queries select the gateway's series with $sel, a hidden constant the chart fills in
// (templates/grafana-dashboard.yaml, from the alerts' selector).
import { writeFileSync } from 'node:fs';

const ds = { type: 'prometheus', uid: '${datasource}' };
const S = '$sel', R = '$__rate_interval';
const panels = [];
let id = 0, y = 0, x = 0, rowH = 0;

// Lays panels out left to right, wrapping at 24 columns.
const place = (w, h) => { if (x + w > 24) { x = 0; y += rowH; rowH = 0; } const g = { h, w, x, y }; x += w; rowH = Math.max(rowH, h); return g; };
const endRow = () => { y += rowH; x = 0; rowH = 0; };
const targets = list => list.map(([expr, legend], i) => ({ refId: String.fromCodePoint(65 + i), datasource: ds, expr, legendFormat: legend, range: true }));
const row = title => { const p = { id: ++id, type: 'row', title, collapsed: false, gridPos: { h: 1, w: 24, x: 0, y }, panels: [] }; y += 1; return p; };
const ts = (title, description, list, unit = 'short', w = 12, extra = {}) => ({ id: ++id, type: 'timeseries', title, description, datasource: ds, gridPos: place(w, 8), targets: targets(list),
  fieldConfig: { defaults: { unit, custom: { drawStyle: 'line', lineWidth: 1, fillOpacity: 10, showPoints: 'never', spanNulls: true }, ...(extra.defaults || {}) }, overrides: extra.overrides || [] },
  options: { legend: { displayMode: 'table', placement: 'right', calcs: ['max', 'lastNotNull'] }, tooltip: { mode: 'multi', sort: 'desc' } } });
const stat = (title, description, expr, unit, extra = {}) => ({ id: ++id, type: 'stat', title, description, datasource: ds, gridPos: place(6, 4), targets: targets([[expr, '']]),
  fieldConfig: { defaults: { unit, ...(extra.defaults || {}) }, overrides: [] }, options: { reduceOptions: { calcs: ['lastNotNull'], fields: '', values: false }, colorMode: 'background', graphMode: 'none', textMode: 'value' } });
const red429 = [{ matcher: { id: 'byName', options: '429' }, properties: [{ id: 'color', value: { mode: 'fixed', fixedColor: 'red' } }] }];

panels.push(row('Halo'));
panels.push(stat('Halo account', 'Whether the gateway is signed in to Halo (grunt_gateway_halo_reauth_required). Signed out: sign in again on the admin page.',
  `max(grunt_gateway_halo_reauth_required{${S}})`, 'none', { defaults: { mappings: [{ type: 'value', options: { '0': { text: 'Signed in', color: 'green' }, '1': { text: 'Sign in again', color: 'red' } } }], thresholds: { mode: 'absolute', steps: [{ color: 'green', value: null }, { color: 'red', value: 1 }] } } }));
panels.push(stat('Sign-in refresh due in', 'Time left on the Halo account\'s access token, which the gateway refreshes on a schedule (Gateway:Auth:RefreshInterval): below zero means refreshes are failing. (The Spartan token is renewed only when a call needs one, so its own expiry goes negative while the gateway is idle.)',
  `min(grunt_gateway_halo_token_expiry_seconds{${S}, token="access"})`, 's', { defaults: { thresholds: { mode: 'absolute', steps: [{ color: 'red', value: null }, { color: 'orange', value: 60 }, { color: 'green', value: 300 }] } } }));
panels.push(stat('Halo 429s (1 h)', 'Requests Halo answered 429 in the last hour, retries included. The budgets should keep this near 0: if not, a budget lets more through than Halo takes.',
  `sum(increase(grunt_halo_requests_total{${S}, status="429"}[1h])) or vector(0)`, 'short', { defaults: { decimals: 0, thresholds: { mode: 'absolute', steps: [{ color: 'green', value: null }, { color: 'orange', value: 1 }, { color: 'red', value: 20 }] } } }));
panels.push(stat('Turned away by budgets (1 h)', 'Calls the budgets answered 429 without sending them to Halo, in the last hour: expected under load, the callers wait their Retry-After.',
  `sum(increase(grunt_halo_budget_rejections_total{${S}}[1h])) or vector(0)`, 'short', { defaults: { decimals: 0, thresholds: { mode: 'absolute', steps: [{ color: 'green', value: null }, { color: 'orange', value: 1 }] } } }));
endRow();
panels.push(ts('Requests to Halo by operation', 'Every HTTP request sent to Halo, one per attempt (retries included), per minute: what Halo\'s limits count (grunt_halo_requests_total).',
  [[`sum by (operation) (rate(grunt_halo_requests_total{${S}}[${R}])) * 60`, '{{operation}}']], 'reqpm'));
panels.push(ts('Requests to Halo by status', 'The same requests by Halo\'s answer; 0 = no answer. 429 in red.',
  [[`sum by (status) (rate(grunt_halo_requests_total{${S}}[${R}])) * 60`, '{{status}}']], 'reqpm', 12, { overrides: red429 }));
endRow();
panels.push(row('Budgets'));
panels.push(ts('Room left in each budget', 'Calls each budget can still send to Halo in its window now (profile: 15 a minute, matchHistory: 2 a second, matchStats: 480 a minute).',
  [[`max by (budget) (grunt_halo_budget_remaining{${S}})`, '{{budget}}']], 'short', 12, { defaults: { min: 0 } }));
panels.push(ts('Lockout left', 'Time left on a budget\'s lockout after Halo answered 429 (profile: 5 minutes); nothing is sent to Halo meanwhile.',
  [[`max by (budget) (grunt_halo_budget_lockout_seconds{${S}})`, '{{budget}}']], 's', 12, { defaults: { min: 0 } }));
panels.push(ts('Turned away, not sent', 'Calls a budget answered 429 itself, per minute, by reason: window (its limit used up) or lockout (Halo answered 429).',
  [[`sum by (budget, reason) (rate(grunt_halo_budget_rejections_total{${S}}[${R}])) * 60`, '{{budget}} ({{reason}})']], 'reqpm'));
panels.push(ts('Wait for room (p95)', 'How long calls waited for room in their budget before going (up to its MaxWait: matchHistory 2 s).',
  [[`histogram_quantile(0.95, sum by (budget, le) (rate(grunt_halo_budget_wait_seconds_bucket{${S}}[${R}])))`, '{{budget}}']], 's'));
endRow();
panels.push(row('Callers'));
panels.push(ts('Gateway requests by key', 'Requests to the gateway API per minute, by consumer key, cache hits included. No key is limited on its own.',
  [[`sum by (key) (rate(grunt_gateway_requests_total{${S}}[${R}])) * 60`, '{{key}}']], 'reqpm'));
panels.push(ts('Gateway requests by action', 'The same requests by gateway action.',
  [[`sum by (action) (rate(grunt_gateway_requests_total{${S}}[${R}])) * 60`, '{{action}}']], 'reqpm'));
panels.push(ts('Cache hit ratio', 'Share of cacheable requests answered from the gateway\'s cache, by action: those never reach Halo nor count against a budget. 0 while nothing was a hit.',
  [[`(sum by (action) (rate(grunt_gateway_cache_lookups_total{${S}, result="hit"}[${R}])) or sum by (action) (rate(grunt_gateway_cache_lookups_total{${S}}[${R}])) * 0) / sum by (action) (rate(grunt_gateway_cache_lookups_total{${S}}[${R}]))`, '{{action}}']], 'percentunit', 12, { defaults: { min: 0, max: 1 } }));
panels.push(ts('Cache size', 'The response cache: bytes (left) and entries (right).',
  [[`max(grunt_gateway_cache_size_bytes{${S}})`, 'bytes'], [`max(grunt_gateway_cache_entries{${S}})`, 'entries']], 'bytes', 12,
  { overrides: [{ matcher: { id: 'byName', options: 'entries' }, properties: [{ id: 'unit', value: 'short' }, { id: 'custom.axisPlacement', value: 'right' }] }] }));
endRow();
panels.push(row('Health'));
panels.push(ts('Halo answers after retries', 'What each gateway action finally got from Halo, per minute, by status (grunt_gateway_upstream_responses_total); 429 in red.',
  [[`sum by (status) (rate(grunt_gateway_upstream_responses_total{${S}}[${R}])) * 60`, '{{status}}']], 'reqpm', 12, { overrides: red429 }));
panels.push(ts('Retries', 'Halo requests retried, per minute, by operation and reason (status, network, timeout, auth).',
  [[`sum by (operation, reason) (rate(grunt_halo_retries_total{${S}}[${R}])) * 60`, '{{operation}} ({{reason}})']], 'reqpm'));
panels.push(ts('No answer from Halo', 'Gateway actions that got no response from Halo at all, after retries, per minute.',
  [[`sum by (action) (rate(grunt_gateway_upstream_failures_total{${S}}[${R}])) * 60`, '{{action}}']], 'reqpm'));
panels.push(ts('Token refreshes', 'Halo sign-in refreshes per hour, by result.',
  [[`sum by (result) (increase(grunt_gateway_halo_token_refreshes_total{${S}}[1h]))`, '{{result}}']], 'short'));
endRow();

const dashboard = {
  title: 'Halo gateway', uid: 'halo-gateway', tags: ['halo-gateway', 'grunt'], timezone: 'browser', schemaVersion: 39, version: 1, editable: true,
  description: 'The Grunt Halo gateway: Halo requests per endpoint, the budgets that keep them within Halo\'s limits, callers and the cache, and health. Series pushed over OTLP.',
  time: { from: 'now-6h', to: 'now' }, refresh: '1m', graphTooltip: 1,
  templating: { list: [
    { name: 'datasource', label: 'Prometheus', type: 'datasource', query: 'prometheus', current: {}, hide: 0 },
    { name: 'sel', type: 'constant', hide: 2, query: '__SELECTOR__', current: { text: '__SELECTOR__', value: '__SELECTOR__' }, options: [] },
  ] },
  annotations: { list: [] }, links: [], panels,
};
export default dashboard;
if (import.meta.url === `file://${process.argv[1]}`)
  writeFileSync(new URL('./halo-gateway.json', import.meta.url), JSON.stringify(dashboard, null, 2) + '\n');
