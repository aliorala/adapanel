import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { createRenderer, h, nextTick, ssrContextKey, type App, type Component } from "vue";
import type { AxiosResponse, InternalAxiosRequestConfig } from "axios";
import { apiClient, invalidateSessionRequests } from "../api/client";
import { useSystemStore, type SystemOverview, type SystemHistorySample } from "./system";
import MonitoringView from "../views/MonitoringView.vue";
import DashboardOverviewView from "../views/DashboardOverviewView.vue";

vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key }) }));
vi.mock("../api/processes", () => ({ getProcessSnapshot: vi.fn(() => new Promise(() => {})) }));
type MeasuredOverview = SystemOverview & { cpuMeasured: boolean; memoryMeasured: boolean; disksMeasured: boolean; networkMeasured: boolean };
type MeasuredSample = SystemHistorySample & { cpuMeasured: boolean; memoryMeasured: boolean; networkMeasured: boolean };
type State = { cpuValue: number | null; memoryValue: number | null; diskValue: number | null; cpuSparks: string[]; memSparks: string[]; netDownSparks: string[]; hasCpuTrend: boolean };
const overview = (cpuUsagePercent = 10): MeasuredOverview => ({ machineName: "fixture", operatingSystem: "Windows", osArchitecture: "x64", frameworkDescription: ".NET", panelVersion: "0.8.77", currentServerTimeUtc: "2026-10-04T10:00:00Z", bootTimeUtc: "2026-10-04T00:00:00Z", uptime: "10:00:00", cpuUsagePercent, logicalProcessorCount: 4, totalMemoryBytes: 1000, availableMemoryBytes: 800, usedMemoryBytes: 200, memoryUsagePercent: 20, disks: [], networkInterfaces: [], cpuMeasured: true, memoryMeasured: true, disksMeasured: true, networkMeasured: true });
const sample = (cpuPercent = 10, measured = true): MeasuredSample => ({ timestampUtc: "2026-10-04T10:00:00Z", cpuPercent, memoryPercent: cpuPercent, networkDownBytesPerSec: cpuPercent, networkUpBytesPerSec: cpuPercent, cpuMeasured: measured, memoryMeasured: measured, networkMeasured: measured });
function response(config: InternalAxiosRequestConfig, data: unknown): AxiosResponse { return { config, data: { success: true, data }, headers: {}, status: 200, statusText: "OK" }; }
function deferred<T>() { let resolve!: (value: T) => void, reject!: (reason: unknown) => void; const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; }
const originalAdapter = apiClient.defaults.adapter;
let app: App | undefined, calls: InternalAxiosRequestConfig[], read: (config: InternalAxiosRequestConfig) => Promise<AxiosResponse>;
async function flush() { for (let i = 0; i < 35; i++) await nextTick(); }
beforeEach(() => {
  vi.useFakeTimers(); setActivePinia(createPinia()); invalidateSessionRequests(); calls = [];
  vi.stubGlobal("window", globalThis); vi.stubGlobal("location", { pathname: "/panel", hostname: "localhost" }); vi.stubGlobal("document", { hidden: false });
  read = async config => response(config, config.url === "/system/overview" ? overview() : config.url === "/system/history" ? [sample()] : []);
  apiClient.defaults.adapter = async config => { calls.push(config); return read(config); };
});
afterEach(() => { app?.unmount(); app = undefined; apiClient.defaults.adapter = originalAdapter; invalidateSessionRequests(); vi.restoreAllMocks(); vi.unstubAllGlobals(); vi.useRealTimers(); });
const store = () => useSystemStore() as ReturnType<typeof useSystemStore> & { overviewConfirmed: boolean; overviewError: string };
for (const family of ["overview", "history"] as const) {
  const load = () => family === "overview" ? store().fetchOverview() : store().fetchHistory();
  const result = (value: number) => family === "overview" ? overview(value) : [sample(value)];
  const value = () => family === "overview" ? store().overview?.cpuUsagePercent : store().history[0]?.cpuPercent;
  it(`${family}: the newest completed read owns the published measurement`, async () => {
    const pending = deferred<AxiosResponse>(); let first!: InternalAxiosRequestConfig; let count = 0;
    read = config => ++count === 1 ? (first = config, pending.promise) : Promise.resolve(response(config, result(80)));
    const older = load(); await flush(); await load(); pending.resolve(response(first, result(10))); await older; expect(value()).toBe(80);
  });
  it(`${family}: a pre-reset read cannot repopulate the reset store`, async () => {
    const pending = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
    read = config => (request = config, pending.promise); const old = load(); await flush(); store().$reset();
    pending.resolve(response(request, result(10))); await old; expect(value()).toBeUndefined();
  });
  it.each(["false-envelope", "null", "not-finite", "negative"])(`${family}: rejects an invalid measurement %s`, async kind => {
    read = async config => { const invalid = result(kind === "negative" ? -1 : Number.NaN); const res = response(config, kind === "null" ? null : kind === "false-envelope" ? result(0) : invalid); if (kind === "false-envelope") res.data.success = false; return res; };
    await expect(load()).rejects.toThrow(); expect(value()).toBeUndefined();
  });
  it(`${family}: a measured zero remains a valid zero`, async () => { read = async config => response(config, result(0)); await load(); expect(value()).toBe(0); });
}
it("a pre-reset overview finally cannot clear a newer read's loading indicator", async () => {
  const old = deferred<AxiosResponse>(), latest = deferred<AxiosResponse>(); const requests: InternalAxiosRequestConfig[] = [];
  read = config => { requests.push(config); return requests.length === 1 ? old.promise : latest.promise; };
  const first = store().fetchOverview(); await flush(); store().$reset(); const second = store().fetchOverview(); await flush();
  old.resolve(response(requests[0]!, overview())); await first; expect(store().loading).toBe(true);
  latest.resolve(response(requests[1]!, overview(80))); await second; expect(store().loading).toBe(false); expect(store().overviewConfirmed).toBe(true);
});
it("an older failed overview cannot invalidate the latest successful read", async () => {
  const pending = deferred<AxiosResponse>(); let count = 0;
  read = config => ++count === 1 ? pending.promise : Promise.resolve(response(config, overview(80)));
  const old = store().fetchOverview().catch(() => undefined); await flush(); await store().fetchOverview(); pending.reject(new Error("old failure")); await old;
  expect(store().overviewConfirmed).toBe(true); expect(store().overviewError).toBe("");
});
it("a failed current overview preserves the last display but closes its confirmed state", async () => {
  await store().fetchOverview(); read = () => Promise.reject(new Error("offline")); await expect(store().fetchOverview()).rejects.toThrow();
  expect(store().overview?.cpuUsagePercent).toBe(10); expect(store().overviewConfirmed).toBe(false); expect(store().overviewError).not.toBe("");
});
it("a previous-session finally cannot clear the new session loading state", async () => {
  const old = deferred<AxiosResponse>(), latest = deferred<AxiosResponse>(); const requests: InternalAxiosRequestConfig[] = [];
  read = config => { requests.push(config); return requests.length === 1 ? old.promise : latest.promise; };
  const first = store().fetchOverview().catch(() => undefined); await flush(); invalidateSessionRequests(); const second = store().fetchOverview(); await flush();
  old.resolve(response(requests[0]!, overview())); await first; expect(store().loading).toBe(true);
  latest.resolve(response(requests[1]!, overview(80))); await second; expect(store().overview?.cpuUsagePercent).toBe(80);
});
async function mount(view: "monitoring" | "dashboard", propsOverview: MeasuredOverview, confirmed = true, realHistory = false) {
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  const history = store().history;
  if (!realHistory) vi.spyOn(store(), "fetchHistory").mockResolvedValue(history);
  app = renderer.createApp({ ...(view === "monitoring" ? MonitoringView : DashboardOverviewView), render: () => h("section") } as Component, {
    overview: propsOverview, overviewConfirmed: confirmed, loading: false, sitesCount: 0, totalBindingCount: 0, ftpUserCount: 0, databaseCount: 0,
    securityStatusLabel: "", securePath: "", auditItems: [], allowedSections: [], panelUpdate: null
  }); app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: State }).setupState; await flush(); return state;
}
for (const view of ["monitoring", "dashboard"] as const) {
  it(`${view}: unknown numeric placeholders do not become healthy zero metrics`, async () => {
    const state = await mount(view, { ...overview(0), memoryUsagePercent: 0, cpuMeasured: false, memoryMeasured: false });
    expect(state.cpuValue).toBeNull(); expect(state.memoryValue).toBeNull(); expect(state.diskValue).toBeNull();
  });
  it(`${view}: a stale snapshot is not presented as a current health measurement`, async () => {
    const state = await mount(view, overview(), false); expect(state.cpuValue).toBeNull(); expect(state.memoryValue).toBeNull();
  });
  it(`${view}: valid zero metrics are still measured`, async () => {
    const state = await mount(view, { ...overview(0), memoryUsagePercent: 0 }); expect(state.cpuValue).toBe(0); expect(state.memoryValue).toBe(0);
  });
}
it("keeps graph gaps at their original timestamps instead of connecting across unknown history", async () => {
  store().history = [sample(10), sample(20), sample(0, false), sample(30), sample(40)];
  const state = await mount("monitoring", overview()); expect(state.cpuSparks).toHaveLength(2); expect(state.memSparks).toHaveLength(2); expect(state.netDownSparks).toHaveLength(2);
  expect(state.cpuSparks[0]).toBe("0.0,24.4 25.0,21.8"); expect(state.cpuSparks[1]).toBe("75.0,19.2 100.0,16.6");
});
it("does not manufacture a trend from unknown history placeholders", async () => {
  store().history = [sample(0, false), sample(0, false)]; const state = await mount("monitoring", overview()); expect(state.cpuSparks).toEqual([]); expect(state.hasCpuTrend).toBe(false);
});
it("does not publish a history request started by a monitoring screen that has since closed", async () => {
  const pending = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
  read = config => (request = config, pending.promise);
  await mount("monitoring", overview(), true, true); app!.unmount(); app = undefined;
  pending.resolve(response(request, [sample(75)])); await flush(); expect(store().history).toEqual([]);
});
it("keeps older-server measurements unknown when affirmative flags are missing", async () => {
  const old = { ...overview(), cpuMeasured: undefined, memoryMeasured: undefined, disksMeasured: undefined, networkMeasured: undefined };
  read = async config => response(config, old); await store().fetchOverview();
  expect(store().overview?.cpuMeasured).toBe(false); expect(store().overview?.memoryMeasured).toBe(false);
});
it("preserves good disks when a sibling disk could not be measured", async () => {
  const result = overview(); result.disksMeasured = false; result.disks = [
    { name: "C:", label: "", format: "NTFS", measured: true, totalBytes: 100, freeBytes: 20, usedBytes: 80, usagePercent: 80 },
    { name: "D:", label: "", format: "", measured: false, totalBytes: 0, freeBytes: 0, usedBytes: 0, usagePercent: 0 }
  ]; read = async config => response(config, result); await store().fetchOverview();
  expect(store().overview?.disks[0]?.measured).toBe(true); expect(store().overview?.disks[1]?.measured).toBe(false); expect(store().overview?.disksMeasured).toBe(false);
});
