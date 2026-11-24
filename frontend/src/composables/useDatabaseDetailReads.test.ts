import { afterEach, expect, it, vi } from "vitest";
import type { DatabaseDetail } from "../api/databases";
import { isMeasuredDatabaseSize, useDatabaseDetailReads } from "./useDatabaseDetailReads";

function deferred<T>() { let resolve!: (value: T) => void, reject!: (error: unknown) => void;
  const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; }
const detail = (name: string, siteName = "site.test", sizeBytes: number | null = 10, engine = "mariadb") =>
  ({ engine, name, siteName, sizeBytes, users: [], tableCount: 1 } as unknown as DatabaseDetail);
async function flush() { for (let i = 0; i < 12; i++) await Promise.resolve(); }
afterEach(() => vi.restoreAllMocks());

it("shares one in-flight read and a completed cache entry with the detail consumer", async () => {
  const response = deferred<DatabaseDetail>(), fetch = vi.fn((_engine: string, _name: string) => response.promise), reads = useDatabaseDetailReads(fetch);
  const list = reads.read("mariadb", "db", () => true), card = reads.read("mariadb", "db", () => true, true);
  expect(card).toBe(list); await flush(); expect(fetch).toHaveBeenCalledTimes(1);
  response.resolve(detail("db")); expect(await card).toEqual(detail("db"));
  expect(await reads.read("mariadb", "db", () => true, true)).toEqual(detail("db"));
  expect(fetch).toHaveBeenCalledTimes(1);
});

it("preserves the visible row consumer when a shared detail card closes before completion", async () => {
  const response = deferred<DatabaseDetail>(), fetch = vi.fn(() => response.promise), reads = useDatabaseDetailReads(fetch);
  let cardOpen = true;
  const row = reads.read("mariadb", "db", () => true);
  const card = reads.read("mariadb", "db", () => cardOpen, true);
  cardOpen = false; response.resolve(detail("db"));
  await Promise.all([row, card]);
  expect(reads.entry("mariadb", "db")?.detail?.siteName).toBe("site.test"); expect(fetch).toHaveBeenCalledTimes(1);
});

it("keeps the physical two-request limit through resets and drops obsolete queued rows", async () => {
  const responses = Array.from({ length: 4 }, () => deferred<DatabaseDetail>());
  let next = 0;
  const fetch = vi.fn((_engine: string, _name: string) => responses[next++]!.promise), reads = useDatabaseDetailReads(fetch);
  let oldCurrent = true;
  const old = ["one", "two", "queued"].map(name => reads.read("mariadb", name, () => oldCurrent).catch(() => null));
  await flush(); expect(fetch).toHaveBeenCalledTimes(2);
  oldCurrent = false; reads.reset();
  const fresh = ["one", "two"].map(name => reads.read("mysql", name, () => true));
  await flush(); expect(fetch).toHaveBeenCalledTimes(2);
  responses[0]!.resolve(detail("one")); await flush(); expect(fetch).toHaveBeenCalledTimes(3);
  responses[1]!.resolve(detail("two")); await flush(); expect(fetch).toHaveBeenCalledTimes(4);
  expect(fetch.mock.calls.map(call => call[1])).not.toContain("queued");
  responses[2]!.resolve(detail("one", "new.test", 20, "mysql")); responses[3]!.resolve(detail("two", "new.test", 20, "mysql"));
  await Promise.all([...old, ...fresh]);
  expect(reads.entry("mariadb", "one")).toBeUndefined();
  expect(reads.entry("mysql", "one")?.detail?.siteName).toBe("new.test");
});

it("joins a fresh same-context result after mutation instead of letting an old response overwrite it", async () => {
  const old = deferred<DatabaseDetail>(), fresh = deferred<DatabaseDetail>();
  const fetch = vi.fn().mockReturnValueOnce(old.promise).mockReturnValueOnce(fresh.promise), reads = useDatabaseDetailReads(fetch);
  const card = reads.read("mariadb", "db", () => true, true); await flush(); reads.reset();
  const latest = reads.read("mariadb", "db", () => true, true); await flush();
  fresh.resolve(detail("db", "new.test", 50)); await latest;
  old.resolve(detail("db", "old.test", 10)); expect((await card).siteName).toBe("new.test");
  expect(reads.entry("mariadb", "db")?.detail?.sizeBytes).toBe(50); expect(fetch).toHaveBeenCalledTimes(2);
});

it.each([detail("other"), detail("db", "wrong.test", 10, "mysql")])("rejects a mismatched engine/name response", async response => {
  const reads = useDatabaseDetailReads(async () => response);
  await expect(reads.read("mariadb", "db", () => true)).rejects.toThrow();
  expect(reads.entry("mariadb", "db")?.state).toBe("error"); expect(reads.entry("mariadb", "db")?.detail).toBeUndefined();
});

it("keeps a known linked site when size is not supported or cannot be measured", async () => {
  const reads = useDatabaseDetailReads(async () => detail("db", "known.test", null, "postgresql"));
  expect((await reads.read("postgresql", "db", () => true)).siteName).toBe("known.test");
  expect(reads.entry("postgresql", "db")?.state).toBe("ready");
});

it("allows a manual detail retry after failure and re-reads expired measurements", async () => {
  let now = 10; vi.spyOn(Date, "now").mockImplementation(() => now);
  const fetch = vi.fn().mockRejectedValueOnce(new Error("offline")).mockResolvedValue(detail("db"));
  const reads = useDatabaseDetailReads(fetch);
  await expect(reads.read("mariadb", "db", () => true)).rejects.toThrow("offline");
  await expect(reads.read("mariadb", "db", () => true)).rejects.toThrow(); expect(fetch).toHaveBeenCalledTimes(1);
  await reads.read("mariadb", "db", () => true, true); expect(fetch).toHaveBeenCalledTimes(2);
  now += 60_001; await reads.read("mariadb", "db", () => true); expect(fetch).toHaveBeenCalledTimes(3);
});

it("keeps a cached visible row ready when the same visible batch adds a miss to a full cache", async () => {
  const fetch = vi.fn(async (_engine: string, name: string) => detail(name)), reads = useDatabaseDetailReads(fetch);
  for (let n = 0; n < 128; n++) await reads.read("mariadb", `db${n}`, () => true);
  await Promise.all(["db0", "new", "db127"].map(name => reads.read("mariadb", name, () => true)));
  expect(reads.entry("mariadb", "db0")?.state).toBe("ready");
  expect(reads.entry("mariadb", "new")?.state).toBe("ready");
  expect(fetch).toHaveBeenCalledTimes(129);
});

it("rejects pruned queued promises without starting them and releases a failed physical slot", async () => {
  const responses = [deferred<DatabaseDetail>(), deferred<DatabaseDetail>()];
  const fetch = vi.fn().mockReturnValueOnce(responses[0]!.promise).mockReturnValueOnce(responses[1]!.promise).mockResolvedValue(detail("new"));
  const reads = useDatabaseDetailReads(fetch); let visible = true;
  const active = ["one", "two"].map(name => reads.read("mariadb", name, () => true).catch(() => null));
  const queued = reads.read("mariadb", "hidden", () => visible).catch(() => null);
  await flush(); visible = false; reads.prune(); expect(await queued).toBeNull();
  const next = reads.read("mariadb", "new", () => true);
  responses[0]!.reject(new Error("offline")); await next;
  responses[1]!.resolve(detail("two")); await Promise.all(active);
  expect(fetch).toHaveBeenCalledTimes(3); expect(reads.entry("mariadb", "hidden")).toBeUndefined();
});

it("does not interpret missing, negative or non-finite sizes as measured zero", () => {
  for (const invalid of [null, undefined, -1, NaN, Infinity, "0"]) expect(isMeasuredDatabaseSize(invalid)).toBe(false);
  expect(isMeasuredDatabaseSize(0)).toBe(true);
});
