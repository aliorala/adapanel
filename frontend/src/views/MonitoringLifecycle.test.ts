import { useAuthStore } from "../stores/auth";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { createRenderer, h, nextTick, ssrContextKey, type App, type Component } from "vue";
import MonitoringView from "./MonitoringView.vue";
import { getProcessSnapshot, type ProcessSnapshot } from "../api/processes";
import { useSystemStore, type SystemHistorySample } from "../stores/system";
import { invalidateSessionRequests } from "../api/client";

vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key }) }));
vi.mock("../api/processes", async original => ({ ...await original<object>(), getProcessSnapshot: vi.fn() }));
let app: App | undefined;
beforeEach(() => {
  vi.useFakeTimers(); vi.stubGlobal("window", globalThis); vi.stubGlobal("document", { hidden: false });
  vi.mocked(getProcessSnapshot).mockImplementation(async sort => snapshot(sort));
});
afterEach(() => { app?.unmount(); app = undefined; vi.restoreAllMocks(); vi.resetAllMocks(); vi.unstubAllGlobals(); vi.useRealTimers(); });

function snapshot(sortBy = "cpu"): ProcessSnapshot {
  return { capturedAtUtc: "2026-10-02T10:00:00Z", sampleWindowSeconds: 2, cpuBaselineReady: true, totalCpuPercent: 3, logicalProcessorCount: 4, totalMemoryBytes: 1000, usedMemoryBytes: 100, availableMemoryBytes: 900, memoryUsagePercent: 10, totalProcessCount: 0, sortBy, processes: [] };
}
function deferred<T>() { let resolve!: (value: T) => void, reject!: (error: unknown) => void; const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; }
type State = { snapshot: ProcessSnapshot | null; processSort: "cpu" | "memory"; processPaused: boolean; processError: string; historyError: string; pollProcesses(): Promise<void> };
async function flush() { for (let i = 0; i < 10; i++) await nextTick(); }
async function mount(beforeMount?: () => void) {
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  const pinia = createPinia(); setActivePinia(pinia);
  useAuthStore().session = { isAuthenticated: true, requiresSetupWizard: false, user: { id: "admin", userName: "admin", email: "a@example.test", role: "Administrator", preferredLanguage: "en", isTwoFactorEnabled: false, isActive: true, lastLoginAtUtc: null } };
  vi.spyOn(useSystemStore(), "fetchHistory").mockResolvedValue([]);
  beforeMount?.();
  app = renderer.createApp({ ...MonitoringView, render: () => h("section") } as Component, { overview: { isRestricted: false, disks: [], networkInterfaces: [] }, overviewConfirmed: true, loading: false }).use(pinia);
  app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: State }).setupState;
  await flush(); return state;
}
function unmount() { app!.unmount(); app = undefined; }

it("does not publish a process snapshot after the monitoring screen unmounts", async () => {
  const pending = deferred<ProcessSnapshot>(); vi.mocked(getProcessSnapshot).mockReturnValueOnce(pending.promise);
  const state = await mount(); unmount(); pending.resolve(snapshot()); await flush();
  expect(state.snapshot).toBeNull(); expect(vi.getTimerCount()).toBe(0);
});

it("keeps the displayed snapshot frozen after pausing an in-flight refresh", async () => {
  const state = await mount(), pending = deferred<ProcessSnapshot>();
  vi.mocked(getProcessSnapshot).mockReturnValueOnce(pending.promise);
  const refreshing = state.pollProcesses(); state.processPaused = true; await flush();
  pending.resolve({ ...snapshot(), totalCpuPercent: 99 }); await refreshing;
  expect(state.snapshot?.totalCpuPercent).toBe(3);
});

it("discards the old sort response and immediately reads the latest selected sort", async () => {
  const old = deferred<ProcessSnapshot>(), latest = deferred<ProcessSnapshot>();
  vi.mocked(getProcessSnapshot).mockReturnValueOnce(old.promise).mockReturnValueOnce(latest.promise);
  const state = await mount(); state.processSort = "memory"; await flush();
  old.resolve(snapshot("cpu")); await flush();
  expect(state.snapshot).toBeNull(); expect(getProcessSnapshot).toHaveBeenLastCalledWith("memory");
  latest.resolve(snapshot("memory")); await flush(); expect(state.snapshot?.sortBy).toBe("memory");
});

it("does not show a failed old sort request as an error for the new selection", async () => {
  const pending = deferred<ProcessSnapshot>(); vi.mocked(getProcessSnapshot).mockReturnValueOnce(pending.promise);
  const state = await mount(); state.processSort = "memory"; await flush();
  pending.reject(new Error("old cpu request failed")); await flush();
  expect(state.processError).toBe(""); expect(state.snapshot?.sortBy).toBe("memory");
});

it("refreshes immediately when process monitoring resumes", async () => {
  const state = await mount(); state.processPaused = true; await flush(); state.processPaused = false; await flush();
  expect(getProcessSnapshot).toHaveBeenCalledTimes(2);
});

it("does not pile up history requests when a refresh takes longer than its interval", async () => {
  const pending = deferred<SystemHistorySample[]>();
  await mount(() => vi.mocked(useSystemStore().fetchHistory).mockReturnValue(pending.promise));
  await vi.advanceTimersByTimeAsync(180_000);
  expect(useSystemStore().fetchHistory).toHaveBeenCalledTimes(1);
  pending.resolve([]); await flush();
});

it("continues history polling after a failed refresh without an unhandled rejection", async () => {
  const state = await mount(() => vi.mocked(useSystemStore().fetchHistory).mockRejectedValueOnce(new Error("history offline")));
  expect(state.historyError).toBe("panel.monitor.historyFailed");
  await vi.advanceTimersByTimeAsync(60_000);
  expect(state.historyError).toBe(""); expect(useSystemStore().fetchHistory).toHaveBeenCalledTimes(2);
});

it("still applies normal process updates while preventing overlapping reads", async () => {
  const pending = deferred<ProcessSnapshot>(); vi.mocked(getProcessSnapshot).mockReturnValueOnce(pending.promise);
  const state = await mount(); await vi.advanceTimersByTimeAsync(6000);
  expect(getProcessSnapshot).toHaveBeenCalledTimes(1);
  pending.resolve(snapshot()); await flush();
  expect(state.snapshot?.sortBy).toBe("cpu"); expect(state.processError).toBe("");
});

it("does not show process data or continue server reads after the session changes", async () => {
  const pending = deferred<ProcessSnapshot>(); vi.mocked(getProcessSnapshot).mockReturnValueOnce(pending.promise);
  const state = await mount(); invalidateSessionRequests(); pending.resolve(snapshot()); await flush(); await vi.advanceTimersByTimeAsync(60_000);
  expect(state.snapshot).toBeNull(); expect(getProcessSnapshot).toHaveBeenCalledTimes(1); expect(useSystemStore().fetchHistory).toHaveBeenCalledTimes(1);
});
