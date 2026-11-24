import { shallowReactive } from "vue";
import { CanceledError } from "axios";
import type { DatabaseDetail } from "../api/databases";
import { UserFacingError } from "../utils/apiError";

type Entry = { state: "loading" | "ready" | "error"; detail?: DatabaseDetail; expiresAt: number; job?: Job };
type Job = { key: string; engine: string; name: string; version: number; started: boolean; priority: boolean;
  current(): boolean; promise: Promise<DatabaseDetail>; resolve(value: DatabaseDetail): void; reject(error: unknown): void };

export const isMeasuredDatabaseSize = (value: unknown): value is number =>
  typeof value === "number" && Number.isFinite(value) && value >= 0;

/** Component-local cache shared by visible rows and the detail card. Reset never
 * releases a physical slot: obsolete HTTP requests still count until they settle. */
export function useDatabaseDetailReads(fetch: (engine: string, name: string) => Promise<DatabaseDetail>) {
  const entries = shallowReactive(new Map<string, Entry>());
  const pending = new Map<string, Job>();
  let queue: Job[] = [], active = 0, version = 0;
  const keyOf = (engine: string, name: string) => JSON.stringify([engine, name]);
  const cancelled = () => new CanceledError("Database measurement context changed");
  const failed = () => new UserFacingError("panel.database.detailsUnknown");

  function remember(key: string, value: Entry) {
    entries.delete(key); entries.set(key, value);
    while (entries.size > 128) entries.delete(entries.keys().next().value!);
  }
  function read(engine: string, name: string, current: () => boolean, priority = false): Promise<DatabaseDetail> {
    if (!current()) return Promise.reject(cancelled());
    const key = keyOf(engine, name), existing = pending.get(key), cached = entries.get(key);
    if (existing && (existing.version === version || !existing.started)) {
      existing.version = version;
      const previousCurrent = existing.current;
      existing.current = () => previousCurrent() || current();
      if (priority) {
        existing.priority = true;
        if (!existing.started) { queue = [existing, ...queue.filter(job => job !== existing)]; }
      }
      return existing.promise;
    }
    if (cached && cached.expiresAt > Date.now()) {
      remember(key, cached);
      if (cached.state === "ready") return Promise.resolve(cached.detail!);
      if (cached.state === "error" && !priority) return Promise.reject(failed());
    }
    let resolve!: Job["resolve"], reject!: Job["reject"];
    const promise = new Promise<DatabaseDetail>((yes, no) => { resolve = yes; reject = no; });
    const job: Job = { key, engine, name, version, current, priority, started: false, promise, resolve, reject };
    pending.set(key, job);
    remember(key, { state: "loading", expiresAt: 0, job });
    if (priority) queue.unshift(job); else queue.push(job);
    pump();
    return promise;
  }
  function finish(job: Job, result?: DatabaseDetail, error?: unknown) {
    active--;
    if (pending.get(job.key) === job) pending.delete(job.key);
    if (!job.current()) {
      if (entries.get(job.key)?.job === job) entries.delete(job.key);
      job.reject(cancelled());
    } else if (job.version !== version) {
      // A same-context write invalidated this read. Its consumers join a fresh
      // request, after releasing the old physical slot, instead of seeing old data.
      read(job.engine, job.name, job.current, job.priority).then(job.resolve, job.reject);
    } else if (error || !result || result.engine !== job.engine || result.name !== job.name) {
      remember(job.key, { state: "error", expiresAt: Date.now() + 60_000 });
      job.reject(error ?? failed());
    } else {
      remember(job.key, { state: "ready", detail: result, expiresAt: Date.now() + 60_000 });
      job.resolve(result);
    }
    pump();
  }
  function pump() {
    while (active < 2 && queue.length) {
      const job = queue.shift()!;
      if (!job.current()) {
        if (pending.get(job.key) === job) pending.delete(job.key);
        if (entries.get(job.key)?.job === job) entries.delete(job.key);
        job.reject(cancelled()); continue;
      }
      job.version = version; job.started = true; active++;
      // Promise.resolve also catches a synchronous adapter failure.
      Promise.resolve().then(() => fetch(job.engine, job.name))
        .then(result => finish(job, result), error => finish(job, undefined, error));
    }
  }
  function prune() {
    queue = queue.filter(job => {
      if (job.current()) return true;
      if (pending.get(job.key) === job) pending.delete(job.key);
      if (entries.get(job.key)?.job === job) entries.delete(job.key);
      job.reject(cancelled()); return false;
    });
    pump();
  }
  function reset() { version++; entries.clear(); prune(); }

  return { read, reset, prune, entry: (engine: string, name: string) => entries.get(keyOf(engine, name)) };
}
