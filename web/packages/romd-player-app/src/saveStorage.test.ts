import { IDBFactory } from 'fake-indexeddb';
import {
  browserSaveDatabase,
  browserSaveName,
  copyOlderSaves,
  inspectOlderSaves,
  scopeBrowserSaveDatabase,
} from './saveStorage';

const hash = 'a'.repeat(64);
const saveId = 'b'.repeat(64);
const oldPath = `/data/saves/FCEUmm/${hash}.srm`;
const accountDatabase = browserSaveDatabase(saveId);
const newPath = `/data/saves/FCEUmm/${browserSaveName(saveId)}.srm`;
const payload = {
  timestamp: new Date('2026-01-01'),
  mode: 33206,
  contents: new Uint8Array([
    4,
    8,
    15,
  ]),
};

describe('pinned EmulatorJS save recovery', () => {
  let factory: IDBFactory;
  beforeEach(() => {
    factory = new IDBFactory();
  });

  it('redirects the native mount database for the frame lifetime without exposing or deleting shared saves', async () => {
    await seed('/data/saves', 21, 'FILE_DATA', [
      [
        oldPath,
        payload,
      ],
    ]);
    const shared = await open('/data/saves');
    scopeBrowserSaveDatabase(factory, saveId);
    await seed('/data/saves', 21, 'FILE_DATA', [
      [
        newPath,
        payload,
      ],
    ]);
    expect((await factory.databases()).map((database) => database.name)).toContain(accountDatabase);
    const readShared = () =>
      new Promise<unknown>((resolve) => {
        const request = shared.transaction('FILE_DATA').objectStore('FILE_DATA').get(oldPath);
        request.onsuccess = () => resolve(request.result);
      });
    expect(JSON.stringify(await readShared())).toBe(JSON.stringify(payload));
    await new Promise<void>((resolve, reject) => {
      const request = factory.deleteDatabase('/data/saves');
      request.onsuccess = () => resolve();
      request.onerror = () => reject(request.error);
    });
    expect(JSON.stringify(await readShared())).toBe(JSON.stringify(payload));
    shared.close();
  });

  it('does not create empty legacy databases while inspecting a new browser', async () => {
    expect(await inspectOlderSaves(factory, hash, saveId)).toEqual({
      files: [],
      hasCurrentSaves: false,
    });
    expect(await factory.databases()).toEqual([]);
  });

  it('keeps legacy progress unassigned until explicit recovery, then copies files and states without deleting originals', async () => {
    await seed('/data/saves', 21, 'FILE_DATA', [
      [
        oldPath,
        payload,
      ],
      [
        '/data/saves/FCEUmm',
        {
          mode: 16895,
        },
      ],
      [
        `/data/saves/FCEUmm/${hash}0.srm`,
        payload,
      ],
    ]);
    await seed('EmulatorJS-states', 1, 'states', [
      [
        `${hash}.state`,
        new Uint8Array([
          16,
          23,
          42,
        ]),
      ],
    ]);
    const recovery = await inspectOlderSaves(factory, hash, saveId);
    expect(recovery.files).toHaveLength(2);
    expect(recovery.hasCurrentSaves).toBe(false);
    expect(await read(accountDatabase, 'FILE_DATA', newPath)).toBeUndefined();
    await copyOlderSaves(factory, recovery);
    expect(JSON.stringify(await read('/data/saves', 'FILE_DATA', oldPath))).toBe(
      JSON.stringify(payload),
    );
    expect(JSON.stringify(await read(accountDatabase, 'FILE_DATA', newPath))).toBe(
      JSON.stringify(payload),
    );
    expect(JSON.stringify(await read('EmulatorJS-states', 'states', `${hash}.state`))).toBe(
      JSON.stringify(
        new Uint8Array([
          16,
          23,
          42,
        ]),
      ),
    );
    expect(
      JSON.stringify(await read('EmulatorJS-states', 'states', `${browserSaveName(saveId)}.state`)),
    ).toBe(
      JSON.stringify(
        new Uint8Array([
          16,
          23,
          42,
        ]),
      ),
    );
    expect((await inspectOlderSaves(factory, hash, 'c'.repeat(64))).hasCurrentSaves).toBe(false);
  });

  it('protects existing account progress and still makes older files available for download', async () => {
    await seed('/data/saves', 21, 'FILE_DATA', [
      [
        oldPath,
        payload,
      ],
    ]);
    await seed(accountDatabase, 21, 'FILE_DATA', [
      [
        newPath,
        {
          ...payload,
          contents: new Uint8Array([
            99,
          ]),
        },
      ],
    ]);
    const recovery = await inspectOlderSaves(factory, hash, saveId);
    expect(recovery.files[0].data).toEqual(payload.contents);
    expect(recovery.hasCurrentSaves).toBe(true);
    await expect(copyOlderSaves(factory, recovery)).rejects.toThrow(/already has progress/);
    expect(JSON.stringify((await read(accountDatabase, 'FILE_DATA', newPath))?.contents)).toBe(
      JSON.stringify(
        new Uint8Array([
          99,
        ]),
      ),
    );
  });

  it('aborts the whole database copy if another tab writes the destination after inspection', async () => {
    const rtc = oldPath.replace('.srm', '.rtc');
    await seed('/data/saves', 21, 'FILE_DATA', [
      [
        oldPath,
        payload,
      ],
      [
        rtc,
        payload,
      ],
    ]);
    const recovery = await inspectOlderSaves(factory, hash, saveId);
    const changed = {
      ...payload,
      contents: new Uint8Array([
        99,
      ]),
    };
    await seed(accountDatabase, 21, 'FILE_DATA', [
      [
        newPath,
        changed,
      ],
    ]);
    await expect(copyOlderSaves(factory, recovery)).rejects.toBeDefined();
    expect(JSON.stringify(await read(accountDatabase, 'FILE_DATA', newPath))).toBe(
      JSON.stringify(changed),
    );
    expect(
      await read(accountDatabase, 'FILE_DATA', newPath.replace('.srm', '.rtc')),
    ).toBeUndefined();
    expect(JSON.stringify(await read('/data/saves', 'FILE_DATA', oldPath))).toBe(
      JSON.stringify(payload),
    );
    expect(JSON.stringify(await read('/data/saves', 'FILE_DATA', rtc))).toBe(
      JSON.stringify(payload),
    );
  });

  it('refuses unfamiliar storage instead of starting with apparently missing progress', async () => {
    await seed('/data/saves', 21, 'FILE_DATA', [
      [
        oldPath,
        {
          contents: 'unrecognized',
        },
      ],
    ]);
    await expect(inspectOlderSaves(factory, hash, saveId)).rejects.toThrow(/unsupported format/);
    expect(await read('/data/saves', 'FILE_DATA', oldPath)).toEqual({
      contents: 'unrecognized',
    });
  });

  it('can launch existing account states without touching malformed unowned file data', async () => {
    await seed('/data/saves', 21, 'FILE_DATA', [
      [
        oldPath,
        {
          contents: 'unrecognized',
        },
      ],
    ]);
    await seed('EmulatorJS-states', 1, 'states', [
      [
        `${browserSaveName(saveId)}.state`,
        new Uint8Array([
          99,
        ]),
      ],
    ]);
    expect(await inspectOlderSaves(factory, hash, saveId, true)).toEqual({
      files: [],
      hasCurrentSaves: true,
    });
    expect(await read('/data/saves', 'FILE_DATA', oldPath)).toEqual({
      contents: 'unrecognized',
    });
  });

  it('does not mistake a different account, game, or a directory for this game’s shared save', async () => {
    await seed('/data/saves', 21, 'FILE_DATA', [
      [
        `/data/saves/FCEUmm/${'d'.repeat(64)}.srm`,
        payload,
      ],
      [
        `/data/saves/FCEUmm/${browserSaveName('c'.repeat(64))}.srm`,
        payload,
      ],
    ]);
    expect(await inspectOlderSaves(factory, hash, saveId)).toEqual({
      files: [],
      hasCurrentSaves: false,
    });
  });

  async function seed(
    name: string,
    version: number,
    store: string,
    rows: [
      string,
      unknown,
    ][],
  ) {
    const db = await new Promise<IDBDatabase>((resolve, reject) => {
      const request = factory.open(name, version);
      request.onupgradeneeded = () => {
        request.result.createObjectStore(store);
      };
      request.onsuccess = () => resolve(request.result);
      request.onerror = () => reject(request.error);
    });
    await new Promise<void>((resolve, reject) => {
      const transaction = db.transaction(store, 'readwrite');
      transaction.oncomplete = () => resolve();
      transaction.onabort = () => reject(transaction.error);
      for (const [key, value] of rows) transaction.objectStore(store).put(value, key);
    });
    db.close();
  }

  async function open(name: string) {
    return new Promise<IDBDatabase>((resolve, reject) => {
      const request = factory.open(name);
      request.onsuccess = () => resolve(request.result);
      request.onerror = () => reject(request.error);
    });
  }

  async function read(name: string, store: string, key: string) {
    if (!(await factory.databases()).some((database) => database.name === name)) return undefined;
    const db = await open(name);
    try {
      return await new Promise<Record<string, unknown> & Uint8Array>((resolve, reject) => {
        const request = db.transaction(store).objectStore(store).get(key);
        request.onsuccess = () => resolve(request.result);
        request.onerror = () => reject(request.error);
      });
    } finally {
      db.close();
    }
  }
});
