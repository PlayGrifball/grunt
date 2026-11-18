// Calls a running gateway host through the built package. Grunt/Tests/TypeScriptClientEndToEndTests.cs starts the host
// with a fake Halo client answering the values asserted here, runs this file, then checks what the fake was asked.
import assert from 'node:assert/strict';
import test from 'node:test';

import * as gateway from '../dist/index.js';

const baseUrl = process.env.GATEWAY_URL;
const apiKey = process.env.GATEWAY_API_KEY;
assert.ok(baseUrl && apiKey, 'GATEWAY_URL and GATEWAY_API_KEY must be set; run through TypeScriptClientEndToEndTests');

const matchId = '2f5c4b0e-6a77-4a3c-9d0b-0d6a3c1f5e11';
const playlistId = 'edfef3ac-9cbe-4fa2-b949-8f29deafd483';

gateway.client.setConfig({ baseUrl, headers: { 'X-Api-Key': apiKey } });

function assertOk({ data, error, response }) {
  assert.equal(error, undefined, `unexpected ${response.status}: ${JSON.stringify(error)}`);
  assert.equal(response.status, 200);
  return data;
}

test('userByGamertag encodes the gamertag into the path', async () => {
  const data = assertOk(await gateway.userByGamertag({ path: { gamertag: 'Major Nelson#881' } }));

  assert.equal(data.error.code, 200);
  assert.deepEqual(data.result, { xuid: '2533274800000000', gamertag: 'Major Nelson#881', gamerpic: { small: 's.png' } });
});

test('users sends every xuid', async () => {
  const data = assertOk(await gateway.users({ query: { xuids: ['1', '2'] } }));

  assert.deepEqual(data.result.map((user) => user.xuid), ['1', '2']);
});

test('statsGetMatchHistory sends the paging and match type', async () => {
  const data = assertOk(await gateway.statsGetMatchHistory({
    path: { player: 'xuid(123)' },
    query: { start: 25, count: 10, type: 2 }, // MatchType.Custom
  }));

  assert.equal(data.error.code, 200);
  assert.ok(data.result);
});

test('statsGetMatchCount returns the counts', async () => {
  const data = assertOk(await gateway.statsGetMatchCount({ path: { player: 'xuid(123)' } }));

  assert.equal(data.result.matchesPlayedCount, 42);
});

test('statsGetMatchStats reads a match by id', async () => {
  const data = assertOk(await gateway.statsGetMatchStats({ path: { matchId } }));

  assert.equal(data.error.code, 200);
  assert.equal(data.result.matchId, matchId);
  assert.equal(data.result.matchInfo.gameVariantCategory, 25, 'MultiplayerGrifball, as its number');
});

test('statsGetPlayerMatchProgression reads one player in one match', async () => {
  const data = assertOk(await gateway.statsGetPlayerMatchProgression({ path: { player: 'xuid(123)', matchId } }));

  assert.equal(data.error.code, 200);
  assert.ok(data.result);
});

test('skillGetMatchPlayerResult sends every player id', async () => {
  const data = assertOk(await gateway.skillGetMatchPlayerResult({
    path: { matchId },
    query: { playerIds: ['xuid(1)', 'xuid(2)'] },
  }));

  assert.equal(data.error.code, 200);
});

test('skillGetPlaylistCsr sends every player id', async () => {
  const data = assertOk(await gateway.skillGetPlaylistCsr({
    path: { playlistId },
    query: { playerIds: ['xuid(1)', 'xuid(2)'] },
  }));

  assert.equal(data.error.code, 200);
});

test('medals returns the metadata', async () => {
  const data = assertOk(await gateway.medals());

  assert.equal(data.error.code, 200);
  assert.ok(data.result);
});

test('gameCmsGetImage returns the raw image bytes', async () => {
  const data = assertOk(await gateway.gameCmsGetImage({ query: { filePath: 'progression/a b.png' } }));

  // The contract types this as a string, but the client parses application/octet-stream as a Blob.
  assert.ok(data instanceof Blob, `expected a Blob, got ${typeof data}`);
  assert.deepEqual([...new Uint8Array(await data.arrayBuffer())], [137, 80, 78, 71]);
});

test('a missing API key is rejected with 401', async () => {
  const { data, error, response } = await gateway.medals({ headers: { 'X-Api-Key': null } });

  assert.equal(response.status, 401);
  assert.equal(data, undefined);
  assert.ok(error);
});

test("Halo's 404 arrives as the error with the result container", async () => {
  const { data, error, response } = await gateway.userByGamertag({ path: { gamertag: 'nobody' } });

  assert.equal(response.status, 404);
  assert.equal(data, undefined);
  assert.equal(error.error.code, 404);
  assert.equal(error.error.message, 'not found');
});

test("the gateway's own 429 carries Retry-After and notSent", async () => {
  const { error, response } = await gateway.userByGamertag({ path: { gamertag: 'Busy' } });

  assert.equal(response.status, 429);
  assert.equal(response.headers.get('Retry-After'), '42');
  assert.equal(response.headers.get('X-Halo-Upstream-Status'), null);
  assert.equal(error.error.code, 429);
  assert.equal(error.error.notSent, true);
});
