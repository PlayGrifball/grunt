// The chart's PrometheusRule as a plain Prometheus rules file: helm template with the default values (telemetry and the
// rule turned on, so the default selector), then the spec's groups. alerts.test.mjs tests it with promtool and
// dashboard-stack.sh loads it into Prometheus, so both run the rules as the chart renders them.
//
//   node charts/halo-gateway/tests/prometheus-rules.mjs > rules.yml
//
// Needs helm: HELM, or helm from PATH. SERVICE_NAME: telemetry.serviceName (dashboard-stack.sh sets its gateway's).
import { execFileSync } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const chart = path.resolve(import.meta.dirname, '..');
const helm = process.env.HELM || 'helm';

/** The rules file (groups: ...), from the chart rendered as release halo-gateway in namespace halo-gateway. */
export function renderRules(serviceName = process.env.SERVICE_NAME) {
  const manifest = execFileSync(helm, ['template', 'halo-gateway', chart, '-n', 'halo-gateway',
    '--set', 'oauth.redirectUrl=https://ci.invalid/oauth/callback', '--set', 'telemetry.enabled=true',
    '--set', 'telemetry.endpoint=http://otel-collector:4317', '--set', 'prometheusRule.enabled=true',
    ...(serviceName ? ['--set-string', `telemetry.serviceName=${serviceName}`] : []),
    '--show-only', 'templates/prometheusrule.yaml'], { encoding: 'utf8' });
  // spec is the manifest's last key: everything under it, two spaces less indented
  const spec = manifest.split('\nspec:\n')[1];
  if (!spec?.startsWith('  groups:')) throw new Error(`no spec.groups in the rendered PrometheusRule:\n${manifest}`);
  return spec.split('\n').map(l => l.slice(2)).join('\n');
}

if (process.argv[1] === fileURLToPath(import.meta.url)) process.stdout.write(renderRules());
