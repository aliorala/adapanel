import { effectScope, type EffectScope } from "vue";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { useRunningTask, useServerRunningTask, type ServerRunningJobs } from "./useRunningTask";
let scope: EffectScope;
beforeEach(() => { vi.useFakeTimers(); scope = effectScope(); });
afterEach(() => { scope.stop(); vi.clearAllTimers(); vi.useRealTimers(); });
const status: ServerRunningJobs = { running: [{ kind: "backup", title: "fixture", startedAtUtc: "2026-10-02T10:00:00Z" }], interrupted: [], serverTimeUtc: "2026-10-02T10:02:00Z" };
function pendingStatus() { let resolve!: (value: ServerRunningJobs) => void; const promise = new Promise<ServerRunningJobs>(yes => { resolve = yes; }); const fetchStatus = vi.fn(() => promise); return { resolve, fetchStatus }; }
function tracker(fetchStatus: () => Promise<ServerRunningJobs>) { return scope.run(() => useServerRunningTask({ fetchStatus, label: job => job.title, interruptedLabel: job => job.title, pollMs: 1000 }))!; }
it("does not recreate polling or elapsed-time timers after disposal during the initial request", async () => {
  const pending = pendingStatus(), task = tracker(pending.fetchStatus); const starting = task.start();
  scope.stop(); pending.resolve(status); await starting;
  expect(vi.getTimerCount()).toBe(0); expect(task.job.value).toBeNull();
});
it("cannot start a local elapsed-time timer after its owner is disposed", () => {
  const task = scope.run(useRunningTask)!; scope.stop(); task.begin("late task"); expect(vi.getTimerCount()).toBe(0);
});
it("manual stop prevents a pending response from restarting polling", async () => {
  const pending = pendingStatus(), task = tracker(pending.fetchStatus); const starting = task.start();
  task.stop(); pending.resolve(status); await starting; await vi.advanceTimersByTimeAsync(4000);
  expect(pending.fetchStatus).toHaveBeenCalledTimes(1); expect(task.job.value).toBeNull();
});
it("coalesces overlapping starts instead of making simultaneous status requests", async () => {
  const pending = pendingStatus(), task = tracker(pending.fetchStatus); const first = task.start(), second = task.start();
  pending.resolve(status); await Promise.all([first, second]); expect(pending.fetchStatus).toHaveBeenCalledTimes(1);
});
it("continues normal polling and clears elapsed time when the server reports completion", async () => {
  const fetchStatus = vi.fn().mockResolvedValueOnce(status).mockResolvedValue({ ...status, running: [] });
  const task = tracker(fetchStatus); await task.start(); expect(task.running.value).toBe(true);
  expect(task.elapsedText.value).toBe("2:00"); await vi.advanceTimersByTimeAsync(1000);
  expect(fetchStatus).toHaveBeenCalledTimes(2); expect(task.running.value).toBe(false); expect(vi.getTimerCount()).toBe(0);
});
it("does not let a stopped read overwrite a restarted tracker", async () => {
  const old = pendingStatus(); const fetchStatus = vi.fn().mockImplementationOnce(old.fetchStatus).mockResolvedValue({ ...status, running: [] });
  const task = tracker(fetchStatus); const first = task.start(); task.stop(); await task.start();
  old.resolve(status); await first; expect(task.job.value).toBeNull(); expect(task.running.value).toBe(false); expect(vi.getTimerCount()).toBe(0);
});
