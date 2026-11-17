// Release notes for a published chart version, the one release for everything grunt deploys: the chart and the gateway
// image it runs. As Markdown on stdout (helm-chart.yml's release job):
//   node .github/scripts/chart-release-notes.mjs --chart charts/halo-gateway --name halo-gateway --version 0.3.51 \
//     --repository PlayGrifball/grunt --chart-repo oci://ghcr.io/playgrifball/charts --digest sha256:... --identity '<regexp>' \
//     --image ghcr.io/playgrifball/halo-gateway:sha-<commit> --image-digest sha256:... --image-commit <sha> --image-identity '<regexp>'
// or, for the commit whose image the chart runs: node .github/scripts/chart-release-notes.mjs --print-image-commit
// The chart's and the gateway's commits since the previous <name>-<version> tag merged into HEAD (the release before this
// one, also on a re-run where this version's tag already exists): a pull request's merge as one entry, its title linked, with the
// branch's commits under it; a squashed "Subject (#N)" linked. The first tagged release lists recent commits. Then the
// image (tag, digest, commit, cosign verify) and the chart (OCI reference, digest, cosign verify). Needs git: GIT, or git
// from PATH.
// Tests: chart-release-notes.test.mjs.
import { execFileSync } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { parseArgs } from 'node:util';

/** How many commits the first tagged release lists. */
const FIRST_RELEASE_COMMITS = 20;

/** What the gateway image is built from: gateway-image.yml's push paths (the tests check they match). */
export const IMAGE_PATHS = ['Grunt', '.dockerignore', '.github/workflows/gateway-image.yml'];

const gitIn = cwd => (...args) => execFileSync(process.env.GIT || 'git', args, { cwd, encoding: 'utf8' }).trim();

/** The last commit on HEAD that changed what the gateway image is built from: the one gateway-image.yml tagged sha-<it>. */
export function imageCommit(cwd) {
  return gitIn(cwd)('log', '-1', '--format=%H', 'HEAD', '--', ...IMAGE_PATHS);
}

export function releaseNotes({ cwd, chart, name, version, repository, chartRepo, digest, identity, image }) {
  const git = gitIn(cwd);
  const url = `https://github.com/${repository}`;
  const tag = `${name}-${version}`;
  const isVersionTag = t => t !== tag && t.startsWith(`${name}-`) && /^\d+\.\d+\.\d+$/.test(t.slice(name.length + 1));
  const previous = git('tag', '--list', `${name}-*`, '--merged', 'HEAD', '--sort=-version:refname').split('\n').find(isVersionTag);

  // Commits as { sha, parents, subject, body }, NUL-separated records
  const log = (...args) => git('log', '--format=%H%x1f%P%x1f%s%x1f%b%x00', ...args, '--', chart, ...IMAGE_PATHS).split('\0')
    .map(r => r.trim()).filter(Boolean)
    .map(r => { const [sha, parents, subject, body] = r.split('\x1f'); return { sha, parents: parents.split(' '), subject, body: body ?? '' }; });
  const link = sha => `[${sha.slice(0, 7)}](${url}/commit/${sha})`;
  const prs = text => text.replaceAll(/\(#(\d+)\)/g, (_, n) => `([#${n}](${url}/pull/${n}))`);
  const entry = c => {
    const merge = /^Merge pull request #(\d+) from \S+/.exec(c.subject);
    if (!merge) return [`- ${prs(c.subject)} (${link(c.sha)})`];
    const title = c.body.split('\n').map(l => l.trim()).find(Boolean) ?? c.subject;
    const branch = c.parents.length > 1 ? log('--no-merges', `${c.parents[0]}..${c.sha}`) : [];
    return [`- ${title} ([#${merge[1]}](${url}/pull/${merge[1]}), ${link(c.sha)})`, ...branch.map(b => `  - ${prs(b.subject)} (${link(b.sha)})`)];
  };

  const lines = [];
  if (previous) {
    const changes = log('--first-parent', `${previous}..HEAD`);
    lines.push(`## Changes since ${previous}`, '');
    lines.push(...(changes.length ? changes.flatMap(entry) : ['No chart or gateway changes (republished).']), '');
    lines.push(`Full diff: ${url}/compare/${previous}...${tag}`, '');
  } else {
    lines.push('## First tagged release', '',
      `The first tagged release: earlier versions have no release. The last ${FIRST_RELEASE_COMMITS} chart and gateway commits:`, '');
    lines.push(...log('--first-parent', '-n', String(FIRST_RELEASE_COMMITS), 'HEAD').flatMap(entry), '');
  }

  lines.push('## Image', '',
    `- \`${image.ref}\``,
    `- Digest: \`${image.digest}\``,
    `- Built from: ${link(image.commit)}`, '',
    'The chart runs it by digest. Signed keylessly with cosign from this repository\'s gateway-image.yml on main. Verify:', '',
    '```sh',
    `cosign verify ${image.ref.replace(/:[^:/]+$/, '')}@${image.digest} --certificate-identity-regexp '${image.identity}' --certificate-oidc-issuer https://token.actions.githubusercontent.com`,
    '```', '');

  const ref = `${chartRepo}/${name}`;
  const chartRef = `${ref.replace(/^oci:\/\//, '')}@${digest}`;
  lines.push('## Chart', '',
    `- OCI reference: \`${ref}:${version}\``,
    `- Digest: \`${digest}\``,
    `- Commit: ${link(git('rev-parse', 'HEAD'))}`, '',
    'Signed keylessly with cosign from this repository\'s helm-chart.yml on main. Verify:', '',
    '```sh',
    `cosign verify ${chartRef} --certificate-identity-regexp '${identity}' --certificate-oidc-issuer https://token.actions.githubusercontent.com`,
    '```');
  return lines.join('\n') + '\n';
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const names = ['chart', 'name', 'version', 'repository', 'chart-repo', 'digest', 'identity', 'image', 'image-digest', 'image-commit', 'image-identity'];
  const { values } = parseArgs({ options: { ...Object.fromEntries(names.map(o => [o, { type: 'string' }])), 'print-image-commit': { type: 'boolean' } } });
  if (values['print-image-commit']) {
    process.stdout.write(imageCommit(process.cwd()) + '\n');
  } else {
    for (const k of names) if (!values[k]) throw new Error(`--${k} is missing`);
    const image = { ref: values.image, digest: values['image-digest'], commit: values['image-commit'], identity: values['image-identity'] };
    process.stdout.write(releaseNotes({ ...values, chartRepo: values['chart-repo'], image, cwd: process.cwd() }));
  }
}
