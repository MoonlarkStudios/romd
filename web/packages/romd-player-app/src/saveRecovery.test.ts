import { IDBFactory } from 'fake-indexeddb';
import { prepareBrowserSaves } from './saveRecovery';
import { browserSaveDatabase, browserSaveName } from './saveStorage';

const hash = 'a'.repeat(64);
const saveId = 'b'.repeat(64);
const oldPath = `/data/saves/FCEUmm/${hash}.srm`;
const currentPath = `/data/saves/FCEUmm/${browserSaveName(saveId)}.srm`;

describe('older shared save recovery', () => {
  let factory: IDBFactory;
  let abort: AbortController;
  const createObjectURL = vi.fn(() => 'blob:older-save');
  const revokeObjectURL = vi.fn();

  beforeEach(() => {
    factory = new IDBFactory();
    abort = new AbortController();
    document.body.innerHTML = '';
    vi.stubGlobal('indexedDB', factory);
    class TestURL extends URL {
      static createObjectURL = createObjectURL;
      static revokeObjectURL = revokeObjectURL;
    }
    vi.stubGlobal('URL', TestURL);
    createObjectURL.mockClear();
    revokeObjectURL.mockClear();
  });
  afterEach(() => {
    abort.abort();
    vi.unstubAllGlobals();
  });

  it('waits for an explicit choice, offers a backup download and leaves originals when starting separately', async () => {
    await seed(false);
    const recoveryRequired = vi.fn();
    const pending = prepareBrowserSaves(
      window,
      document,
      hash,
      saveId,
      abort.signal,
      recoveryRequired,
      false,
    );
    await vi.waitFor(() => expect(button('Start with separate saves')).toBeDefined());
    expect(recoveryRequired).toHaveBeenCalledOnce();
    expect(document.querySelector('a')?.download).toBe(`${hash}.srm`);
    expect(createObjectURL).toHaveBeenCalledWith(expect.any(Blob));
    button('Start with separate saves')?.click();
    await pending;
    expect(document.querySelector('section')).toBeNull();
    expect(revokeObjectURL).toHaveBeenCalledWith('blob:older-save');
    expect(await saved(currentPath)).toBeUndefined();
    expect(await saved(oldPath)).toBeDefined();
  });

  it('copies only after consent and keeps the original downloadable on future recovery', async () => {
    await seed(false);
    const pending = prepareBrowserSaves(
      window,
      document,
      hash,
      saveId,
      abort.signal,
      vi.fn(),
      false,
    );
    await vi.waitFor(() => expect(button('Copy older saves into my account')).toBeDefined());
    expect(await saved(currentPath)).toBeUndefined();
    button('Copy older saves into my account')?.click();
    await pending;
    expect(await saved(currentPath)).toBeDefined();
    expect(await saved(oldPath)).toBeDefined();
    await prepareBrowserSaves(window, document, hash, saveId, abort.signal, vi.fn(), false);
    expect(document.querySelector('section')).toBeNull();
    const explicit = prepareBrowserSaves(
      window,
      document,
      hash,
      saveId,
      abort.signal,
      vi.fn(),
      true,
    );
    await vi.waitFor(() => expect(button('Continue with my saves')).toBeDefined());
    expect(button('Copy older saves into my account')?.disabled).toBe(true);
    expect(document.querySelector('a')?.download).toBe(`${hash}.srm`);
    button('Continue with my saves')?.click();
    await explicit;
  });

  it('cancels the prompt without copying when the player is disposed', async () => {
    await seed(false);
    const pending = prepareBrowserSaves(
      window,
      document,
      hash,
      saveId,
      abort.signal,
      vi.fn(),
      false,
    );
    await vi.waitFor(() => expect(document.querySelector('section')).not.toBeNull());
    abort.abort();
    await pending;
    expect(document.querySelector('section')).toBeNull();
    expect(await saved(currentPath)).toBeUndefined();
    expect(await saved(oldPath)).toBeDefined();
  });

  it('keeps backup downloads available and explains a failed copy instead of replacing raced progress', async () => {
    await seed(false);
    const pending = prepareBrowserSaves(
      window,
      document,
      hash,
      saveId,
      abort.signal,
      vi.fn(),
      false,
    );
    await vi.waitFor(() => expect(button('Copy older saves into my account')).toBeDefined());
    const request = factory.open(browserSaveDatabase(saveId), 21);
    request.onupgradeneeded = () => {
      request.result.createObjectStore('FILE_DATA').createIndex('timestamp', 'timestamp');
    };
    const db = await new Promise<IDBDatabase>((resolve) => {
      request.onsuccess = () => resolve(request.result);
    });
    const transaction = db.transaction('FILE_DATA', 'readwrite');
    transaction.objectStore('FILE_DATA').put(
      {
        timestamp: new Date(),
        mode: 33206,
        contents: new Uint8Array([
          99,
        ]),
      },
      currentPath,
    );
    await new Promise<void>((resolve) => {
      transaction.oncomplete = () => resolve();
    });
    db.close();
    button('Copy older saves into my account')?.click();
    await vi.waitFor(() =>
      expect(document.querySelector('[role="status"]')?.textContent).toContain(
        'Copying could not finish',
      ),
    );
    expect(document.querySelector('a')).not.toBeNull();
    expect(
      Array.from(
        (
          (await saved(currentPath)) as {
            contents: Uint8Array;
          }
        ).contents,
      ),
    ).toEqual([
      99,
    ]);
    expect(
      Array.from(
        (
          (await saved(oldPath)) as {
            contents: Uint8Array;
          }
        ).contents,
      ),
    ).toEqual([
      42,
    ]);
    button('Continue with available saves')?.click();
    await pending;
  });

  function button(label: string) {
    return Array.from(document.querySelectorAll('button')).find(
      (button) => button.textContent === label,
    );
  }

  async function seed(current: boolean) {
    const request = factory.open('/data/saves', 21);
    request.onupgradeneeded = () => {
      request.result.createObjectStore('FILE_DATA');
    };
    const db = await new Promise<IDBDatabase>((resolve) => {
      request.onsuccess = () => resolve(request.result);
    });
    const transaction = db.transaction('FILE_DATA', 'readwrite');
    const store = transaction.objectStore('FILE_DATA');
    store.put(
      {
        timestamp: new Date(),
        mode: 33206,
        contents: new Uint8Array([
          42,
        ]),
      },
      oldPath,
    );
    if (current)
      store.put(
        {
          timestamp: new Date(),
          mode: 33206,
          contents: new Uint8Array([
            99,
          ]),
        },
        currentPath,
      );
    await new Promise<void>((resolve) => {
      transaction.oncomplete = () => resolve();
    });
    db.close();
  }

  async function saved(key: string) {
    const name = key === currentPath ? browserSaveDatabase(saveId) : '/data/saves';
    if (!(await factory.databases()).some((database) => database.name === name)) return undefined;
    const request = factory.open(name);
    const db = await new Promise<IDBDatabase>((resolve) => {
      request.onsuccess = () => resolve(request.result);
    });
    try {
      const get = db.transaction('FILE_DATA').objectStore('FILE_DATA').get(key);
      return await new Promise<unknown>((resolve) => {
        get.onsuccess = () => resolve(get.result);
      });
    } finally {
      db.close();
    }
  }
});
