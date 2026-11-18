import assert from 'node:assert/strict';
import test from 'node:test';

import * as gateway from '../dist/index.js';

test('package exports the configured client and every public gateway operation', () => {
  const expected = [
    'client',
    'gameCmsGetImage',
    'medals',
    'skillGetMatchPlayerResult',
    'skillGetPlaylistCsr',
    'statsGetMatchCount',
    'statsGetMatchHistory',
    'statsGetMatchStats',
    'statsGetPlayerMatchProgression',
    'userByGamertag',
    'users',
  ];

  assert.deepEqual(Object.keys(gateway).sort(), expected.sort());
});
