/**
 * A small key-value store for the phone's offline data. IndexedDB in the browser; a Map in tests. Values may be anything the structured
 * clone algorithm accepts (including Blobs, so photos wait here until they are sent).
 */
export interface KeyValueStore {
  get<T>(key: string): Promise<T | undefined>;
  put(key: string, value: unknown): Promise<void>;
  delete(key: string): Promise<void>;
  /** Keys that start with the prefix, in key order. */
  keys(prefix: string): Promise<string[]>;
}

// Blobs are immutable, so they are shared; everything else is copied, as IndexedDB would.
const copy = (value: unknown) => (typeof Blob !== 'undefined' && value instanceof Blob ? value : structuredClone(value));

export class MemoryStore implements KeyValueStore {
  private readonly data = new Map<string, unknown>();

  async get<T>(key: string): Promise<T | undefined> {
    return this.data.has(key) ? copy(this.data.get(key)) as T : undefined;
  }

  async put(key: string, value: unknown): Promise<void> {
    this.data.set(key, copy(value));
  }

  async delete(key: string): Promise<void> {
    this.data.delete(key);
  }

  async keys(prefix: string): Promise<string[]> {
    return [...this.data.keys()].filter((k) => k.startsWith(prefix)).sort();
  }
}

const DB_NAME = 'fms-offline';
const STORE = 'kv';

export class IdbStore implements KeyValueStore {
  private db: Promise<IDBDatabase> | null = null;

  private open(): Promise<IDBDatabase> {
    this.db ??= new Promise((resolve, reject) => {
      const request = indexedDB.open(DB_NAME, 1);
      request.onupgradeneeded = () => request.result.createObjectStore(STORE);
      request.onsuccess = () => resolve(request.result);
      request.onerror = () => reject(request.error);
    });
    return this.db;
  }

  private async run<T>(mode: IDBTransactionMode, action: (store: IDBObjectStore) => IDBRequest<T>): Promise<T> {
    const db = await this.open();
    return new Promise<T>((resolve, reject) => {
      const request = action(db.transaction(STORE, mode).objectStore(STORE));
      request.onsuccess = () => resolve(request.result);
      request.onerror = () => reject(request.error);
    });
  }

  get<T>(key: string): Promise<T | undefined> {
    return this.run('readonly', (s) => s.get(key)) as Promise<T | undefined>;
  }

  async put(key: string, value: unknown): Promise<void> {
    await this.run('readwrite', (s) => s.put(value, key));
  }

  async delete(key: string): Promise<void> {
    await this.run('readwrite', (s) => s.delete(key));
  }

  async keys(prefix: string): Promise<string[]> {
    const all = (await this.run('readonly', (s) => s.getAllKeys())) as IDBValidKey[];
    return all.map(String).filter((k) => k.startsWith(prefix)).sort();
  }
}
