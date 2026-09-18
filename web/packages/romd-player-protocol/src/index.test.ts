import { describe, expect, it } from 'vitest';
import {
  isPlayerProtocolVersion,
  PLAYER_EVENT_NAMES,
  PLAYER_PROTOCOL_LIMITS,
  PLAYER_PROTOCOL_VERSION,
  parseParentToPlayerMessage,
  parsePlayerInitMessage,
  parsePlayerToParentMessage,
} from './index';

const validSha256 = 'a'.repeat(64);

function validLoadMessage() {
  return {
    type: 'load',
    core: 'snes9x',
    displayName: 'Super Metroid',
    contentSha256: validSha256,
    relativePath: 'Super Metroid.sfc',
    rom: new Blob(['rom']),
  } as const;
}

function validHelloMessage() {
  return {
    type: 'hello',
    protocol: PLAYER_PROTOCOL_VERSION,
    emulatorJs: {
      version: '4.2.3',
      source: 'cdn',
    },
    cores: ['fceumm', 'snes9x'],
  } as const;
}

class SizedBlob extends Blob {
  readonly #size: number;

  constructor(size: number) {
    super();
    this.#size = size;
  }

  override get size(): number {
    return this.#size;
  }
}

describe('parsePlayerInitMessage', () => {
  it('parses a valid v1 init message', () => {
    expect(
      parsePlayerInitMessage({
        type: 'init',
        protocol: PLAYER_PROTOCOL_VERSION,
      }),
    ).toEqual({ type: 'init', protocol: PLAYER_PROTOCOL_VERSION });
  });

  it.each([
    null,
    [],
    {},
    { type: 'load', protocol: PLAYER_PROTOCOL_VERSION },
    { type: 'init' },
    { type: 'init', protocol: 2 },
    { type: 'init', protocol: '1' },
  ])('rejects malformed or version-mismatched init input: %j', (value) => {
    expect(parsePlayerInitMessage(value)).toBeNull();
  });
});

describe('parseParentToPlayerMessage', () => {
  it('parses every valid parent-to-player union member', () => {
    const load = validLoadMessage();

    expect(parseParentToPlayerMessage(load)).toEqual(load);
    expect(parseParentToPlayerMessage({ type: 'shutdown' })).toEqual({ type: 'shutdown' });
  });

  it.each([
    ['core', ''],
    ['core', 'c'.repeat(PLAYER_PROTOCOL_LIMITS.coreLength + 1)],
    ['core', 123],
    ['displayName', ''],
    ['displayName', 'd'.repeat(PLAYER_PROTOCOL_LIMITS.displayNameLength + 1)],
    ['displayName', null],
    ['relativePath', ''],
    ['relativePath', 'p'.repeat(PLAYER_PROTOCOL_LIMITS.relativePathLength + 1)],
    ['relativePath', {}],
  ])('rejects a malformed %s string', (field, value) => {
    expect(parseParentToPlayerMessage({ ...validLoadMessage(), [field]: value })).toBeNull();
  });

  it.each([
    '',
    'a'.repeat(63),
    'a'.repeat(65),
    'A'.repeat(64),
    'g'.repeat(64),
    123,
    null,
  ])('rejects a malformed SHA-256 hash: %j', (contentSha256) => {
    expect(parseParentToPlayerMessage({ ...validLoadMessage(), contentSha256 })).toBeNull();
  });

  it.each([null, {}, new ArrayBuffer(1), new Uint8Array(1), 'rom'])(
    'rejects a non-Blob ROM: %j',
    (rom) => {
      expect(parseParentToPlayerMessage({ ...validLoadMessage(), rom })).toBeNull();
    },
  );

  it('accepts a ROM at the 64 MiB ceiling and rejects one byte over it', () => {
    expect(
      parseParentToPlayerMessage({
        ...validLoadMessage(),
        rom: new SizedBlob(PLAYER_PROTOCOL_LIMITS.romBytes),
      }),
    ).not.toBeNull();
    expect(
      parseParentToPlayerMessage({
        ...validLoadMessage(),
        rom: new SizedBlob(PLAYER_PROTOCOL_LIMITS.romBytes + 1),
      }),
    ).toBeNull();
  });

  it.each([
    null,
    [],
    {},
    { type: 123 },
    { type: 'unknown' },
    { ...validLoadMessage(), type: 'shutdown-now' },
  ])('rejects malformed or unknown messages: %j', (value) => {
    expect(parseParentToPlayerMessage(value)).toBeNull();
  });
});

describe('parsePlayerToParentMessage', () => {
  it('parses valid hello variants and copies the cores array', () => {
    const cdnHello = validHelloMessage();
    const parsedCdnHello = parsePlayerToParentMessage(cdnHello);

    expect(parsedCdnHello).toEqual(cdnHello);
    expect(parsedCdnHello).not.toBe(cdnHello);
    if (parsedCdnHello?.type === 'hello') {
      expect(parsedCdnHello.cores).not.toBe(cdnHello.cores);
    }

    expect(
      parsePlayerToParentMessage({
        ...validHelloMessage(),
        emulatorJs: { version: '4.2.3', source: 'bundled' },
      }),
    ).toEqual({
      ...validHelloMessage(),
      emulatorJs: { version: '4.2.3', source: 'bundled' },
    });
  });

  it('rejects hello protocol-version mismatches', () => {
    expect(parsePlayerToParentMessage({ ...validHelloMessage(), protocol: 2 })).toBeNull();
    expect(parsePlayerToParentMessage({ ...validHelloMessage(), protocol: '1' })).toBeNull();
  });

  it.each([
    undefined,
    null,
    {},
    { version: '4.2.3' },
    { version: '', source: 'cdn' },
    { version: 'v'.repeat(PLAYER_PROTOCOL_LIMITS.versionLength + 1), source: 'cdn' },
    { version: 423, source: 'cdn' },
    { version: '4.2.3', source: 'latest' },
  ])('rejects malformed EmulatorJS metadata: %j', (emulatorJs) => {
    expect(parsePlayerToParentMessage({ ...validHelloMessage(), emulatorJs })).toBeNull();
  });

  it.each([
    [],
    ['snes9x', 'snes9x'],
    Array.from({ length: PLAYER_PROTOCOL_LIMITS.coreCount + 1 }, (_, index) => `core-${index}`),
    [''],
    ['c'.repeat(PLAYER_PROTOCOL_LIMITS.coreLength + 1)],
    ['snes9x', 123],
    'snes9x',
  ])('rejects an invalid core list: %j', (cores) => {
    expect(parsePlayerToParentMessage({ ...validHelloMessage(), cores })).toBeNull();
  });

  it('accepts the maximum advertised core count', () => {
    const cores = Array.from(
      { length: PLAYER_PROTOCOL_LIMITS.coreCount },
      (_, index) => `core-${index}`,
    );

    expect(parsePlayerToParentMessage({ ...validHelloMessage(), cores })).toMatchObject({ cores });
  });

  it.each(PLAYER_EVENT_NAMES.filter((event) => event !== 'error'))(
    'parses the valid %s lifecycle event',
    (event) => {
      expect(parsePlayerToParentMessage({ type: 'event', event })).toEqual({
        type: 'event',
        event,
      });
    },
  );

  it('parses the valid error event', () => {
    expect(
      parsePlayerToParentMessage({
        type: 'event',
        event: 'error',
        code: 'asset-load-failed',
        message: 'The EmulatorJS loader timed out.',
      }),
    ).toEqual({
      type: 'event',
      event: 'error',
      code: 'asset-load-failed',
      message: 'The EmulatorJS loader timed out.',
    });
  });

  it.each(['loaded', 'start', 'save', 'game-ended', '', 123, null])(
    'rejects an unknown event name: %j',
    (event) => {
      expect(parsePlayerToParentMessage({ type: 'event', event })).toBeNull();
    },
  );

  it.each([
    ['code', ''],
    ['code', 'c'.repeat(PLAYER_PROTOCOL_LIMITS.errorCodeLength + 1)],
    ['code', 123],
    ['message', ''],
    ['message', 'm'.repeat(PLAYER_PROTOCOL_LIMITS.errorMessageLength + 1)],
    ['message', null],
  ])('rejects a malformed error %s string', (field, value) => {
    expect(
      parsePlayerToParentMessage({
        type: 'event',
        event: 'error',
        code: 'failed',
        message: 'Failed.',
        [field]: value,
      }),
    ).toBeNull();
  });

  it.each([null, [], {}, { type: 123 }, { type: 'unknown' }])(
    'rejects malformed or unknown messages: %j',
    (value) => {
      expect(parsePlayerToParentMessage(value)).toBeNull();
    },
  );
});

describe('isPlayerProtocolVersion', () => {
  it('accepts only protocol version 1', () => {
    expect(isPlayerProtocolVersion(PLAYER_PROTOCOL_VERSION)).toBe(true);

    for (const value of [0, 2, '1', null, undefined, {}]) {
      expect(isPlayerProtocolVersion(value)).toBe(false);
    }
  });
});


describe('optional session controls', () => {
  it.each(['pause', 'resume', 'open-controls'])('accepts %s', action => {
    expect(parseParentToPlayerMessage({ type: 'control', action })).toEqual({ type: 'control', action });
  });
  it('rejects unknown commands and malformed capabilities', () => {
    expect(parseParentToPlayerMessage({ type: 'control', action: 'restart' })).toBeNull();
    expect(parsePlayerToParentMessage({ ...validHelloMessage(), features: [123] })).toBeNull();
    expect(parsePlayerToParentMessage({ ...validHelloMessage(), features: ['session-controls'] })).toMatchObject({ features: ['session-controls'] });
    expect(parsePlayerToParentMessage(validHelloMessage())).not.toHaveProperty('features');
  });
});
