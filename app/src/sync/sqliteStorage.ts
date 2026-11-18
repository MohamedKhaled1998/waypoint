import * as SQLite from 'expo-sqlite';
import type { CacheKind, CacheWrite, OutboxItem, Storage } from './storage';

/** The Storage on the phone, in expo-sqlite. Opened lazily; the schema is created on first use. */
export function createSqliteStorage(name = 'waypoint.db'): Storage {
  let opened: Promise<SQLite.SQLiteDatabase> | null = null;
  const db = () =>
    (opened ??= SQLite.openDatabaseAsync(name).then(async (d) => {
      await d.execAsync(`
        CREATE TABLE IF NOT EXISTS cache (kind TEXT NOT NULL, id TEXT NOT NULL, data TEXT NOT NULL, PRIMARY KEY (kind, id));
        CREATE TABLE IF NOT EXISTS outbox (seq INTEGER PRIMARY KEY AUTOINCREMENT, clientId TEXT NOT NULL UNIQUE, kind TEXT NOT NULL, payload TEXT NOT NULL, queuedAt TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
      `);
      return d;
    }));

  return {
    async list<T>(kind: CacheKind) {
      const rows = await (await db()).getAllAsync<{ data: string }>('SELECT data FROM cache WHERE kind = ?', kind);
      return rows.map((r) => JSON.parse(r.data) as T);
    },
    async apply(write: CacheWrite, cursor?: number) {
      await (await db()).withTransactionAsync(async () => {
        const d = await db();
        if (write.replace) await d.runAsync('DELETE FROM cache');
        for (const r of write.removes) await d.runAsync('DELETE FROM cache WHERE kind = ? AND id = ?', r.kind, r.id);
        for (const u of write.upserts)
          await d.runAsync('INSERT OR REPLACE INTO cache (kind, id, data) VALUES (?, ?, ?)', u.kind, u.id, JSON.stringify(u.data));
        if (cursor !== undefined) await d.runAsync("INSERT OR REPLACE INTO meta (key, value) VALUES ('cursor', ?)", String(cursor));
      });
    },
    async cursor() {
      const row = await (await db()).getFirstAsync<{ value: string }>("SELECT value FROM meta WHERE key = 'cursor'");
      return row ? Number(row.value) : 0;
    },
    async enqueue(item: OutboxItem) {
      await (await db()).runAsync(
        'INSERT OR IGNORE INTO outbox (clientId, kind, payload, queuedAt) VALUES (?, ?, ?, ?)',
        item.clientId, item.kind, JSON.stringify(item.payload), item.queuedAt,
      );
    },
    async outbox() {
      const rows = await (await db()).getAllAsync<{ clientId: string; kind: OutboxItem['kind']; payload: string; queuedAt: string }>(
        'SELECT clientId, kind, payload, queuedAt FROM outbox ORDER BY seq',
      );
      return rows.map((r) => ({ ...r, payload: JSON.parse(r.payload) as OutboxItem['payload'] }));
    },
    async dequeue(clientId: string) {
      await (await db()).runAsync('DELETE FROM outbox WHERE clientId = ?', clientId);
    },
    async wipe() {
      await (await db()).execAsync('DELETE FROM cache; DELETE FROM outbox; DELETE FROM meta;');
    },
  };
}
