// chart-release-notes.mjs, on a throwaway git repository: node --test .github/scripts/*.test.mjs
//   - the changes are the chart's and the gateway image's commits since the previous <chart>-<version> tag: a pull
//     request's merge as one entry (its title, linked) with its commits under it, a squashed "Subject (#N)" linked;
//     nothing that only touched other paths, nothing from before the previous release, and no other chart's or the NuGet
//     packages' v* tags
//   - a re-run, with this version's tag already on HEAD, still lists the changes since the release before it
//   - the previous release is the highest version merged into HEAD (0.3.10 after 0.3.9)
//   - the first tagged release says so and lists the chart's recent commits
//   - the chart's OCI reference, digest and cosign verify command, and the image it runs: tag, digest, commit, verify
//   - imageCommit: the last commit that changed what the gateway image is built from (IMAGE_PATHS, which are
//     gateway-image.yml's own push paths)
//   - git comes from GIT when it is set
// Needs git (GIT, or git from PATH).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { releaseNotes, imageCommit, IMAGE_PATHS } from './chart-release-notes.mjs';

const repository = 'PlayGrifball/grunt';
const identity = '^https://github\\.com/PlayGrifball/grunt/\\.github/workflows/helm-chart\\.yml@refs/heads/main$';
const digest = 'sha256:' + 'ab'.repeat(32);
const imageDigest = 'sha256:' + 'cd'.repeat(32);
const imageIdentity = '^https://github\\.com/PlayGrifball/grunt/\\.github/workflows/gateway-image\\.yml@refs/heads/main$';

/** A git repository in a temporary directory, with helpers to commit a file and run git. */
function repo() {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'chart-release-notes-'));
  const git = (...args) => execFileSync('git', args, {
    cwd: dir, encoding: 'utf8',
    env: { ...process.env, GIT_AUTHOR_NAME: 't', GIT_AUTHOR_EMAIL: 't@example.invalid', GIT_COMMITTER_NAME: 't', GIT_COMMITTER_EMAIL: 't@example.invalid', GIT_CONFIG_GLOBAL: '/dev/null', GIT_CONFIG_NOSYSTEM: '1' },
  }).trim();
  git('init', '-q', '-b', 'main');
  let n = 0;
  const commit = (file, message) => {
    fs.mkdirSync(path.dirname(path.join(dir, file)), { recursive: true });
    fs.writeFileSync(path.join(dir, file), String(++n));
    git('add', file);
    git('commit', '-q', '-m', message);
    return git('rev-parse', 'HEAD');
  };
  return { dir, git, commit };
}

const notes = (r, version) => releaseNotes({
  cwd: r.dir, chart: 'charts/halo-gateway', name: 'halo-gateway', version, repository,
  chartRepo: 'oci://ghcr.io/playgrifball/charts', digest, identity,
  image: { ref: 'ghcr.io/playgrifball/halo-gateway:sha-1234567', digest: imageDigest, commit: '1234567'.padEnd(40, '0'), identity: imageIdentity },
});

/** A chart released as 0.3.1, then a merged pull request, a squashed one and commits elsewhere. */
function history() {
  const r = repo();
  r.commit('charts/halo-gateway/Chart.yaml', 'Chart: before the previous release');
  r.git('tag', 'halo-gateway-0.3.1');
  r.git('tag', 'v0.1.0');
  const fix = r.commit('Grunt/Program.cs', 'Gateway: a fix');
  r.git('checkout', '-q', '-b', 'dashboard');
  const a = r.commit('charts/halo-gateway/values.yaml', 'Chart: a dashboard value');
  const c = r.commit('Grunt/Other.cs', 'Gateway: on the branch');
  const b = r.commit('charts/halo-gateway/templates/cm.yaml', 'Chart: the dashboard ConfigMap');
  r.git('checkout', '-q', 'main');
  r.git('merge', '-q', '--no-ff', 'dashboard', '-m', 'Merge pull request #17 from PlayGrifball/grafana-dashboard\n\nA Grafana dashboard for the gateway');
  const merge = r.git('rev-parse', 'HEAD');
  const squash = r.commit('charts/halo-gateway/values.yaml', 'Chart: smaller cache (#18)');
  r.git('tag', 'other-chart-9.9.9');
  r.commit('README.md', 'Docs: neither (#19)');
  return { r, a, b, c, fix, merge, squash };
}

test('lists the chart and gateway changes since the previous release', () => {
  const { r, a, b, c, fix, merge, squash } = history();
  const md = notes(r, '0.3.2');
  const pr = n => `https://github.com/${repository}/pull/${n}`;
  const sha = s => `[${s.slice(0, 7)}](https://github.com/${repository}/commit/${s})`;
  assert.ok(md.includes(`- A Grafana dashboard for the gateway ([#17](${pr(17)}), ${sha(merge)})`), md);
  assert.ok(md.includes(`  - Chart: a dashboard value (${sha(a)})`), md);
  assert.ok(md.includes(`  - Gateway: on the branch (${sha(c)})`), md);
  assert.ok(md.includes(`  - Chart: the dashboard ConfigMap (${sha(b)})`), md);
  assert.ok(md.includes(`- Chart: smaller cache ([#18](${pr(18)})) (${sha(squash)})`), md);
  assert.ok(md.includes(`- Gateway: a fix (${sha(fix)})`), md);
  assert.ok(md.indexOf('#18') < md.indexOf('#17') && md.indexOf('#17') < md.indexOf('Gateway: a fix'), 'newest first');
  for (const s of ['neither', 'before the previous release', '#19']) assert.ok(!md.includes(s), `${s} listed:\n${md}`);
  assert.ok(md.includes(`https://github.com/${repository}/compare/halo-gateway-0.3.1...halo-gateway-0.3.2`), md);
  assert.ok(!/first tagged release/i.test(md), md);
});

test('a re-run, with the tag already on HEAD, lists the changes since the release before', () => {
  const { r } = history();
  r.git('tag', 'halo-gateway-0.3.2');
  const md = notes(r, '0.3.2');
  assert.ok(md.includes('A Grafana dashboard for the gateway'), md);
  assert.ok(md.includes('compare/halo-gateway-0.3.1...halo-gateway-0.3.2'), md);
});

test('the previous release is the highest version, not the last in text order', () => {
  const r = repo();
  r.commit('charts/halo-gateway/Chart.yaml', 'Chart: one');
  r.git('tag', 'halo-gateway-0.3.9');
  r.commit('charts/halo-gateway/Chart.yaml', 'Chart: two');
  r.git('tag', 'halo-gateway-0.3.10');
  r.commit('charts/halo-gateway/Chart.yaml', 'Chart: three');
  const md = notes(r, '0.3.11');
  assert.ok(md.includes('compare/halo-gateway-0.3.10...halo-gateway-0.3.11'), md);
  assert.ok(md.includes('Chart: three') && !md.includes('Chart: two'), md);
});

test('the first tagged release says so and lists recent chart and gateway commits', () => {
  const r = repo();
  r.commit('charts/halo-gateway/Chart.yaml', 'Chart: first');
  r.commit('Grunt/Program.cs', 'Gateway: a fix');
  r.commit('README.md', 'Docs: neither');
  r.git('tag', 'v0.1.0');
  r.commit('charts/halo-gateway/values.yaml', 'Chart: second (#3)');
  const md = notes(r, '0.3.51');
  assert.match(md, /first tagged release/i);
  assert.ok(md.includes('Chart: first') && md.includes('Chart: second') && md.includes('Gateway: a fix'), md);
  assert.ok(!md.includes('neither') && !md.includes('/compare/'), md);
});

test('names the OCI reference, the digest and how to verify the signature', () => {
  const { r } = history();
  const md = notes(r, '0.3.2');
  assert.ok(md.includes('oci://ghcr.io/playgrifball/charts/halo-gateway:0.3.2'), md);
  assert.ok(md.includes(digest), md);
  assert.ok(md.includes(`cosign verify ghcr.io/playgrifball/charts/halo-gateway@${digest} --certificate-identity-regexp '${identity}' --certificate-oidc-issuer https://token.actions.githubusercontent.com`), md);
});

test('names the gateway image the chart runs: its tag, digest, commit and how to verify it', () => {
  const { r } = history();
  const md = notes(r, '0.3.2');
  const commit = '1234567'.padEnd(40, '0');
  assert.ok(md.includes('## Image'), md);
  assert.ok(md.includes('`ghcr.io/playgrifball/halo-gateway:sha-1234567`'), md);
  assert.ok(md.includes(`\`${imageDigest}\``), md);
  assert.ok(md.includes(`[1234567](https://github.com/${repository}/commit/${commit})`), md);
  assert.ok(md.includes(`cosign verify ghcr.io/playgrifball/halo-gateway@${imageDigest} --certificate-identity-regexp '${imageIdentity}' --certificate-oidc-issuer https://token.actions.githubusercontent.com`), md);
  assert.ok(md.indexOf('## Image') < md.indexOf('## Chart'), 'the image before the chart');
});

test('imageCommit: the last commit that changed what the gateway image is built from', () => {
  const r = repo();
  r.commit('Grunt/Program.cs', 'Gateway: old');
  const image = r.commit('.dockerignore', 'Image: ignore more');
  r.commit('charts/halo-gateway/values.yaml', 'Chart: only the chart');
  r.commit('README.md', 'Docs');
  assert.equal(imageCommit(r.dir), image);
  const later = r.commit('Grunt/Gateway/Limits.cs', 'Gateway: new');
  assert.equal(imageCommit(r.dir), later);
});

test("IMAGE_PATHS are gateway-image.yml's push paths", () => {
  const workflow = fs.readFileSync(new URL('../workflows/gateway-image.yml', import.meta.url), 'utf8');
  const push = /push:\n\s+branches: \[main\]\n\s+paths: \[(.*)\]/.exec(workflow)?.[1];
  assert.ok(push, 'no push paths in gateway-image.yml');
  const paths = push.split(',').map(p => p.trim().replace(/^"|"$/g, '').replace(/\/\*\*$/, ''));
  assert.deepEqual([...IMAGE_PATHS].sort(), paths.sort());
});

test('runs git from GIT when it is set', () => {
  const { r } = history();
  const saved = process.env.GIT;
  process.env.GIT = path.join(r.dir, 'no-such-git');
  try {
    assert.throws(() => notes(r, '0.3.2'), /ENOENT/);
  } finally {
    if (saved === undefined) delete process.env.GIT; else process.env.GIT = saved;
  }
});
