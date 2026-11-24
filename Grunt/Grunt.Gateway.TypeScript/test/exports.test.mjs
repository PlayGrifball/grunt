import assert from 'node:assert/strict';
import test from 'node:test';

import * as gateway from '../dist/index.js';

test('package exports the configured client and every public gateway operation', () => {
  const expected = [
    'client',
    'economyArmorCoreCustomization',
    'economyArmorCoresCustomization',
    'economyGetMultiplePlayersCustomization',
    'economyPlayerAppearanceCustomization',
    'economyPlayerPublicCustomization',
    'gameCmsGetCustomizationCatalog',
    'gameCmsGetImage',
    'gameCmsGetItem',
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

test('client defaults to the hosted gateway', () => {
  assert.equal(gateway.client.getConfig().baseUrl, 'https://grunt.grifball.xyz');
});
