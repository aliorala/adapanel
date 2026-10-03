import { afterEach, expect, it, vi } from "vitest";
import { effectScope, type EffectScope } from "vue";
import axios from "axios";
import { invalidateSessionRequests } from "../api/client";
import { useBulkAction } from "./useBulkSelection";

vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key }) }));
let scope: EffectScope;
afterEach(() => { scope?.stop(); invalidateSessionRequests(); });
function setup() { scope = effectScope(); return scope.run(() => useBulkAction())!; }
function deferred() {
  let resolve!: () => void;
  const promise = new Promise<void>(done => { resolve = done; });
  return { promise, resolve };
}

it.each(["session", "unmount"])("does not start queued mutations after %s changes", async change => {
  const bulk = setup(), pending = deferred();
  const worker = vi.fn().mockReturnValue(pending.promise);
  const result = bulk.runBulk(["a", "b", "c"], item => item, worker);
  if (change === "session") invalidateSessionRequests(); else scope.stop();
  pending.resolve();
  const outcome = await result;
  expect(worker.mock.calls).toEqual([["a"]]);
  expect(outcome.done).toBe(1);
  expect(outcome.failures.map(item => item.key)).toEqual(["b", "c"]);
  expect(bulk.busy.value).toBe(false);
});

it("does not include items added after the user started the batch", async () => {
  const bulk = setup(), pending = deferred();
  const items = ["approved"];
  const worker = vi.fn().mockReturnValue(pending.promise);
  const result = bulk.runBulk(items, item => item, worker);
  items.push("not-approved"); pending.resolve();
  expect((await result).total).toBe(1);
  expect(worker.mock.calls).toEqual([["approved"]]);
});

it("prevents concurrent batches without clearing the first batch's busy state", async () => {
  const bulk = setup(), pending = deferred(); const worker = vi.fn().mockReturnValue(pending.promise);
  const first = bulk.runBulk(["a"], item => item, worker);
  const second = await bulk.runBulk(["b"], item => item, vi.fn().mockResolvedValue(undefined));
  expect(second.done).toBe(0); expect(second.failures).toHaveLength(1);
  expect(bulk.busy.value).toBe(true);
  pending.resolve(); expect((await first).done).toBe(1);
});

it("stops after cancellation but still continues after ordinary per-item failures", async () => {
  const bulk = setup();
  const cancelled = vi.fn().mockRejectedValue(new axios.CanceledError());
  const stopped = await bulk.runBulk(["a", "b"], item => item, cancelled);
  expect(cancelled).toHaveBeenCalledOnce(); expect(stopped.failures).toHaveLength(2);
  const failed = vi.fn().mockRejectedValueOnce(new Error("locked")).mockResolvedValueOnce(undefined);
  const completed = await bulk.runBulk(["a", "b"], item => item, failed);
  expect(failed).toHaveBeenCalledTimes(2); expect(completed.done).toBe(1); expect(completed.failures).toHaveLength(1);
});

it("does not send the next item after the caller changes its selected target", async () => {
  const bulk = setup(), pending = deferred(); let current = true;
  const worker = vi.fn().mockReturnValue(pending.promise);
  const result = bulk.runBulk(["a", "b"], item => item, worker, () => current);
  current = false; pending.resolve();
  expect((await result).failures).toEqual([{ key: "b", reason: "panel.bulk.notStarted" }]);
  expect(worker.mock.calls).toEqual([["a"]]);
});
