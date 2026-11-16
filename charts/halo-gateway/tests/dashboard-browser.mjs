// The chart's dashboard in Grafana's own frontend (Chromium, through Playwright): it opens the dashboard and records
// every query the panels send to /api/ds/query and what came back. So the queries are the ones Grafana makes itself,
// after interpolating $sel (with the escaping its Prometheus datasource applies to a variable's value: Grafana 13 doubles
// backslashes, which is why the chart's selector has none); dashboard-grafana.mjs, through the query API, fills those in itself. dashboard-stack.sh runs it after
// dashboard-grafana.mjs (so the data is there), in the Playwright image.
//
//   node charts/halo-gateway/tests/dashboard-browser.mjs --url http://localhost:3000 --uid <uid> --selector '<$sel>' \
//     [--screenshot halo-gateway.png]
//
// Fails unless every panel with queries sent them; every query Grafana sent carries --selector exactly as the chart
// rendered it, with no variable left; Prometheus answered each without an error; and every panel outside MAY_BE_EMPTY
// got data. With --screenshot, then a full-page screenshot (dark theme), for CI's artifacts.
//
// Runs in the Playwright image, which has the browser but not the package: playwright is resolved from the working
// directory (npm install playwright there first, at the image's version).
import { createRequire } from 'node:module';
import path from 'node:path';
import { MAY_BE_EMPTY } from './dashboard-grafana.mjs';

const args = Object.fromEntries(process.argv.slice(2).reduce((a, v, i, all) => (v.startsWith('--') ? [...a, [v.slice(2), all[i + 1]]] : a), []));
const { url, uid, selector, screenshot } = args;
if (!url || !uid || !selector) throw new Error('needs --url, --uid and --selector');
const { chromium } = createRequire(path.join(process.cwd(), 'noop.js'))('playwright');

const { dashboard } = await (await fetch(`${url}/api/dashboards/uid/${uid}`)).json();
const panels = dashboard.panels.flatMap(p => [p, ...(p.panels ?? [])]).filter(p => p.targets?.length);
// The viewport as tall as the dashboard (grid rows of 30 px and 8 px gaps, plus the header): Grafana only loads the
// panels it has on screen.
const rows = Math.max(...dashboard.panels.flatMap(p => [p, ...(p.panels ?? [])]).map(p => p.gridPos.y + p.gridPos.h));
const height = rows * 38 + 300;

/** What each panel sent and got, by panel id: [{ refId, expr, error, rows }]. */
const seen = new Map();
const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1600, height } });
page.on('response', async response => {
  const request = response.request();
  if (!request.url().includes('/api/ds/query') || request.method() !== 'POST') return;
  const panelId = Number(request.headers()['x-panel-id']);
  let json;
  try { json = await response.json(); } catch { json = {}; }
  for (const query of request.postDataJSON()?.queries ?? []) {
    const result = json.results?.[query.refId];
    const n = (result?.frames ?? []).reduce((sum, f) => sum + (f.data?.values?.[0]?.length ?? 0), 0);
    const error = result?.error ?? (!response.ok() && `${response.status()} ${json.message ?? ''}`);
    seen.set(panelId, [...(seen.get(panelId) ?? []).filter(q => q.refId !== query.refId), { refId: query.refId, expr: query.expr, error, rows: n }]);
  }
});

await page.goto(`${url}/d/${uid}?orgId=1&theme=dark&from=now-15m&to=now`, { waitUntil: 'load' });
// Not "networkidle": Grafana keeps a live connection open, so the network never goes quiet. Waits until every panel
// with queries got its answers, then a while for the panels to draw.
const deadline = Date.now() + 90000;
while (panels.some(p => (seen.get(p.id)?.length ?? 0) < p.targets.length) && Date.now() < deadline) await page.waitForTimeout(1000);
await page.waitForTimeout(5000);
if (screenshot) await page.screenshot({ path: screenshot, fullPage: true });
await browser.close();

const failures = [];
const report = ['| Panel | Query | Rows | Sent |', '| --- | --- | ---: | --- |'];
for (const panel of panels) {
  const queries = seen.get(panel.id) ?? [];
  if (queries.length < panel.targets.length) failures.push(`${panel.title}: Grafana sent ${queries.length} of its ${panel.targets.length} queries`);
  for (const q of queries) {
    report.push(`| ${panel.title} | ${q.refId} | ${q.rows} | \`${q.expr}\` |`);
    // Grafana's own $__ variables ($__rate_interval) are filled in by its backend, after the frontend sends the query
    if (/\$(?!__)\w|\$\{|__[A-Z_]+__/.test(q.expr)) failures.push(`${panel.title} (${q.refId}): a variable left in ${q.expr}`);
    else if (!q.expr.includes(selector)) failures.push(`${panel.title} (${q.refId}): the selector changed on the way: ${q.expr}`);
    if (q.error) failures.push(`${panel.title} (${q.refId}): ${q.error}`);
  }
  if (queries.length && queries.every(q => q.rows === 0) && !MAY_BE_EMPTY.has(panel.title)) failures.push(`${panel.title}: no data`);
}
console.log(report.join('\n'));
if (failures.length) {
  console.log(`\nFAILED:\n  ${failures.join('\n  ')}`);
  process.exit(1);
}
console.log(`\nOK: ${panels.length} panels' queries as Grafana's frontend sent them, $sel = ${selector}${screenshot ? `; ${screenshot}: 1600x${height}` : ''}`);
