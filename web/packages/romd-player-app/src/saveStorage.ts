// EmulatorJS 4.2.3: IDBFS stores in-game saves in FILE_DATA; browser states
// live separately in EmulatorJS-states. Do not upgrade or delete either DB.
const fileDatabase = '/data/saves';
const stateDatabase = 'EmulatorJS-states';
const fileStore = 'FILE_DATA';
const stateStore = 'states';
const maximumKeys = 100_000;
const maximumRecoveryBytes = 128 * 1024 * 1024;
const maximumRecoveryFiles = 256;

export interface OlderSave {
  database: string;
  destinationDatabase: string;
  store: string;
  key: string;
  destination: string;
  name: string;
  data: Uint8Array<ArrayBuffer>;
  value: unknown;
}

export interface SaveRecovery {
  files: OlderSave[];
  hasCurrentSaves: boolean;
}

export function browserSaveName(saveId: string): string {
  return `romd-v2-${saveId}`;
}

export function browserSaveDatabase(saveId: string): string {
  return `romd-browser-saves-v2-${saveId}`;
}

/** Pinned 4.2.3 adapter: IDBFS opens the mountpoint as its database name.
 * Keep this redirect for the frame's lifetime, including late native flushes.
 * Sharing that database would let one tab's whole-filesystem sync erase another's files.
 */
export function scopeBrowserSaveDatabase(factory: IDBFactory, saveId: string): void {
  const open = factory.open;
  const remove = factory.deleteDatabase;
  const scoped = browserSaveDatabase(saveId);
  factory.open = (name, version) => {
    const target = name === fileDatabase ? scoped : name;
    return version === undefined ? open.call(factory, target) : open.call(factory, target, version);
  };
  factory.deleteDatabase = (name) => remove.call(factory, name === fileDatabase ? scoped : name);
}

export async function inspectOlderSaves(
  factory: IDBFactory,
  contentSha256: string,
  saveId: string,
  skipLegacyIfCurrent = false,
): Promise<SaveRecovery> {
  const currentName = browserSaveName(saveId);
  const result: SaveRecovery = {
    files: [],
    hasCurrentSaves: false,
  };
  let bytes = 0;
  for (const [database, store] of [
    [
      browserSaveDatabase(saveId),
      fileStore,
    ],
    [
      stateDatabase,
      stateStore,
    ],
    [
      fileDatabase,
      fileStore,
    ],
  ]) {
    const db = await openExisting(factory, database);
    if (!db) continue;
    try {
      if (!db.objectStoreNames.contains(store)) {
        throw new Error('The older browser save database has an unsupported format.');
      }
      const transaction = db.transaction(store, 'readonly');
      const completed = transactionComplete(transaction);
      const objectStore = transaction.objectStore(store);
      if (skipLegacyIfCurrent && database === stateDatabase) {
        const current = objectStore.get(`${currentName}.state`);
        current.onsuccess = () => {
          result.hasCurrentSaves = current.result !== undefined;
        };
      }
      let keys = 0;
      let failure: Error | undefined;
      const cursor = objectStore.openKeyCursor();
      cursor.onsuccess = () => {
        if (skipLegacyIfCurrent && result.hasCurrentSaves) return;
        const row = cursor.result;
        if (!row) return;
        try {
          if (++keys > maximumKeys)
            throw new Error('The browser save inventory is too large to recover automatically.');
          if (typeof row.key === 'string') {
            const key = row.key;
            const name = key.split('/').pop() ?? '';
            if (database !== fileDatabase && matchesName(name, currentName))
              result.hasCurrentSaves = true;
            if (database !== browserSaveDatabase(saveId) && matchesName(name, contentSha256)) {
              const request = objectStore.get(key);
              request.onsuccess = () => {
                try {
                  const value: unknown = request.result;
                  if (result.files.length >= maximumRecoveryFiles)
                    throw new Error('There are too many older saves to recover automatically.');
                  const data = saveBytes(
                    database === fileDatabase && isRecord(value) ? value.contents : value,
                  );
                  bytes += data.byteLength;
                  if (bytes > maximumRecoveryBytes)
                    throw new Error('The older saves are too large to recover automatically.');
                  result.files.push({
                    database,
                    destinationDatabase:
                      database === fileDatabase ? browserSaveDatabase(saveId) : database,
                    store,
                    key,
                    destination:
                      key.slice(0, key.length - name.length) +
                      currentName +
                      name.slice(contentSha256.length),
                    name,
                    data,
                    value,
                  });
                } catch (error) {
                  failure = asError(error);
                  transaction.abort();
                }
              };
            }
          }
          row.continue();
        } catch (error) {
          failure = asError(error);
          transaction.abort();
        }
      };
      try {
        await completed;
      } catch (error) {
        throw failure ?? error;
      }
    } finally {
      db.close();
    }
    if (skipLegacyIfCurrent && result.hasCurrentSaves)
      return {
        files: [],
        hasCurrentSaves: true,
      };
  }
  return result;
}

/** Copy only on an explicit choice. add(), rather than put(), prevents lost updates. */
export async function copyOlderSaves(factory: IDBFactory, recovery: SaveRecovery): Promise<void> {
  if (recovery.hasCurrentSaves)
    throw new Error(
      'This account already has progress. Download the older saves to recover them manually.',
    );
  for (const database of new Set(recovery.files.map((file) => file.destinationDatabase))) {
    const files = recovery.files.filter((file) => file.destinationDatabase === database);
    const db =
      files[0].store === fileStore
        ? await openAccountFiles(factory, database)
        : await openExisting(factory, database);
    if (!db) throw new Error('The older saves are no longer available.');
    try {
      const transaction = db.transaction(files[0].store, 'readwrite');
      const completed = transactionComplete(transaction);
      const store = transaction.objectStore(files[0].store);
      const parents = new Set<string>();
      for (const file of files) {
        if (file.store === fileStore) {
          const segments = file.destination.split('/');
          if (
            !file.destination.startsWith('/data/saves/') ||
            segments.slice(3).some((part) => part === '' || part === '.' || part === '..')
          ) {
            transaction.abort();
            await completed;
          }
          for (let index = 4; index < segments.length; index++)
            parents.add(segments.slice(0, index).join('/'));
        }
        store.add(file.value, file.destination);
      }
      for (const parent of parents) {
        const request = store.get(parent);
        request.onsuccess = () => {
          if (request.result === undefined)
            store.add(
              {
                timestamp: new Date(),
                mode: 16895,
              },
              parent,
            );
        };
      }
      await completed;
    } finally {
      db.close();
    }
  }
}

function openAccountFiles(factory: IDBFactory, name: string): Promise<IDBDatabase> {
  return new Promise((resolve, reject) => {
    const request = factory.open(name, 21);
    let blocked = false;
    request.onupgradeneeded = () => {
      if (blocked) {
        request.transaction?.abort();
        return;
      }
      const store = request.result.createObjectStore(fileStore);
      store.createIndex('timestamp', 'timestamp');
    };
    request.onsuccess = () => (blocked ? request.result.close() : resolve(request.result));
    request.onerror = () => reject(request.error);
    request.onblocked = () => {
      blocked = true;
      reject(new Error('Another tab is updating browser saves. Close it and retry.'));
    };
  });
}

function openExisting(factory: IDBFactory, name: string): Promise<IDBDatabase | null> {
  return new Promise((resolve, reject) => {
    const request = factory.open(name);
    let missing = false;
    let blocked = false;
    request.onupgradeneeded = () => {
      missing = true;
      request.transaction?.abort();
    };
    request.onsuccess = () => (blocked ? request.result.close() : resolve(request.result));
    request.onerror = () => (missing ? resolve(null) : reject(request.error));
    request.onblocked = () => {
      blocked = true;
      reject(new Error('Another tab is updating browser saves. Close it and retry.'));
    };
  });
}

function transactionComplete(transaction: IDBTransaction): Promise<void> {
  return new Promise((resolve, reject) => {
    transaction.oncomplete = () => resolve();
    transaction.onabort = () =>
      reject(transaction.error ?? new Error('The save transaction was interrupted.'));
    transaction.onerror = () => {
      /* onabort reports the final transaction outcome. */
    };
  });
}

function matchesName(name: string, identity: string): boolean {
  return name === identity || name.startsWith(`${identity}.`);
}

function saveBytes(value: unknown): Uint8Array<ArrayBuffer> {
  if (
    (value instanceof ArrayBuffer || ArrayBuffer.isView(value)) &&
    value.byteLength > maximumRecoveryBytes
  ) {
    throw new Error('The older saves are too large to recover automatically.');
  }
  if (value instanceof ArrayBuffer) return new Uint8Array(value);
  if (ArrayBuffer.isView(value))
    return new Uint8Array(new Uint8Array(value.buffer, value.byteOffset, value.byteLength));
  throw new Error('An older save has an unsupported format. Its original data was left unchanged.');
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null;
}

function asError(value: unknown): Error {
  return value instanceof Error ? value : new Error('Browser saves could not be inspected.');
}
