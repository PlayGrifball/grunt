// A screenshot of the chart's dashboard in Grafana (dark theme, full page), for CI's artifacts: what the panels look
// like with the gateway's real metrics. dashboard-stack.sh runs it after dashboard-grafana.mjs, with SCREENSHOT set.
//
//   node charts/halo-gateway/tests/dashboard-screenshot.mjs http://localhost:3000 halo-gateway.png
//
// Runs in the Playwright image, which has the browser but not the package: playwright is resolved from the working
// directory (npm install playwright there first, at the image's version).
import { createRequire } from 'node:module';
import path from 'node:path';

const [url, out] = process.argv.slice(2);
const { chromium } = createRequire(path.join(process.cwd(), 'noop.js'))('playwright');

// The viewport as tall as the dashboard (grid rows of 30 px and 8 px gaps, plus the header): Grafana only loads the
// panels it has on screen.
const { dashboard } = await (await fetch(`${url}/api/dashboards/uid/halo-gateway`)).json();
const rows = Math.max(...dashboard.panels.flatMap(p => [p, ...(p.panels ?? [])]).map(p => p.gridPos.y + p.gridPos.h));
const height = rows * 38 + 300;

const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1600, height } });
// Not "networkidle": Grafana keeps a live connection open, so the network never goes quiet. Waits for the Halo account
// stat's value, which only renders once its query has returned, then a while for the other panels.
await page.goto(`${url}/d/halo-gateway?orgId=1&theme=dark&from=now-15m&to=now`, { waitUntil: 'load' });
await page.getByText(/^(Signed in|Sign in again)$/).first().waitFor({ timeout: 60000 });
await page.waitForTimeout(15000);
await page.screenshot({ path: out, fullPage: true });
await browser.close();
console.log(`${out}: ${dashboard.title}, 1600x${height}`);
