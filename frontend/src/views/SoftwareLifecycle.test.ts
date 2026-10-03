import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { createRenderer, h, nextTick, ssrContextKey, type App, type Component } from "vue";
import SoftwareView from "./SoftwareView.vue";
import { useSoftwareStore, type ComponentManifestItem, type InstalledComponent, type ComponentInstallResult } from "../stores/software";
import { useSubscriptionStore } from "../stores/subscription";
import type { OperationProgress } from "../stores/setup";
import { getSoftwareRunningJobs } from "../api/software";
const shared = vi.hoisted(() => ({ epoch: 0, confirm: vi.fn() }));
vi.mock("../api/client", () => ({ apiClient: { get: vi.fn(), post: vi.fn(), delete: vi.fn() }, getSessionEpoch: () => shared.epoch, setReadOnlyMode: vi.fn() }));
vi.mock("../api/software", () => ({ getSoftwareRunningJobs: vi.fn(async () => ({ running: [], interrupted: [], serverTimeUtc: "2026-01-01T00:00:00Z" })) }));
vi.mock("../stores/auth", () => ({ extractApiMessage: (error: Error) => error.message, extractApiDetails: () => "fixture details" }));
vi.mock("../components/ConfirmDialog.vue", () => ({ useConfirm: () => ({ confirm: shared.confirm }) }));
vi.mock("./AppStoreView.vue", () => ({ default: { render: () => null } }));
vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string, args?: object) => key + (args ? JSON.stringify(args) : "") }) }));
type Row = ComponentManifestItem & { installed: InstalledComponent | null };
type State = { activeWorkspace: string; notice: string; noticeError: boolean; noticeDetails: string; manifestRows: Row[]; liveProgress: OperationProgress | null;
  refreshCatalog(): Promise<void>; checkForUpdates(): Promise<void>; installComponent(id: string): Promise<void>; updateComponent(item: Row): Promise<void>; installDefaultStack(): Promise<void>; removeComponent(id: string): Promise<void>; showInstallButton(item: Row): boolean };
let app: App | undefined;
const manifest = (id: string): ComponentManifestItem => ({ id, displayName: id, version: "2.0", type: "zip", downloadUrl: "", sha256: "", installDir: `fixture/${id}`, postInstall: [], dependencies: [], serviceName: "", silentArgs: "", note: "", default: id === "php-8.3" });
const installed = (id: string): InstalledComponent => ({ id: `${id}-record`, componentId: id, displayName: id, version: "1.0", type: "Zip", installDir: `fixture/${id}`, serviceName: "fixture", status: "Installed", lastError: "", generatedPassword: "", isSystemDetected: false, detectionSource: "", availableVersion: "2.0", updateAvailable: true, versionCheckSupported: true, versionCheckFailed: false, installedAtUtc: "2026-01-01T00:00:00Z" });
const result = (id: string, status = "Installed"): ComponentInstallResult => ({ componentId: id, displayName: id, version: "2.0", status, installDir: `fixture/${id}`, serviceName: "", generatedPassword: "", message: status === "Failed" ? "fixture.failed" : "fixture.completed" });
const progress = (): OperationProgress => ({ title: "fixture", running: true, completed: false, failed: false, error: "", steps: [], startedAtUtc: "2026-01-01T00:00:00Z" });
function deferred<T>() { let resolve!: (value: T) => void; let reject!: (error: unknown) => void; const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; }
async function flush() { for (let i = 0; i < 20; i++) await nextTick(); }
async function finish(work: Promise<void>) { await flush(); await vi.advanceTimersByTimeAsync(2000); await work; }
beforeEach(() => { vi.useFakeTimers(); shared.epoch = 0; shared.confirm.mockImplementation(async options => { await options.action(); return true; }); });
afterEach(() => { app?.unmount(); app = undefined; vi.clearAllTimers(); vi.useRealTimers(); vi.restoreAllMocks(); vi.resetAllMocks(); });
async function mount() {
  const pinia = createPinia(); setActivePinia(pinia); const store = useSoftwareStore();
  store.catalog = { manifest: [manifest("php-8.3"), manifest("mysql-8"), manifest("nginx"), manifest("adminer")], defaults: [manifest("php-8.3")], installed: [installed("mysql-8"), installed("nginx"), installed("adminer")] }; store.loaded = true;
  vi.spyOn(store, "fetchCatalog").mockImplementation(async () => { store.loaded = true; return store.catalog!; });
  vi.spyOn(store, "checkForUpdates").mockImplementation(async () => { store.loaded = true; return store.catalog!; });
  vi.spyOn(store, "fetchOperationProgress").mockResolvedValue(progress());
  vi.spyOn(store, "installComponent").mockImplementation(async id => { store.catalog!.installed = [...store.catalog!.installed.filter(item => item.componentId !== id), { ...installed(id), version: "2.0", updateAvailable: false }]; return result(id); });
  vi.spyOn(store, "installDefaultStack").mockImplementation(async () => { store.catalog!.installed.push({ ...installed("php-8.3"), version: "2.0" }); return [result("php-8.3")]; });
  vi.spyOn(store, "removeComponent").mockImplementation(async id => { store.catalog!.installed = store.catalog!.installed.filter(item => item.componentId !== id); });
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  app = renderer.createApp({ ...SoftwareView, render: () => h("section") } as Component).use(pinia); app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: State }).setupState; state.activeWorkspace = "components"; await flush(); return { state, store };
}
for (const blocked of ["unloaded", "readonly", "workspace", "session", "installing", "removing", "defaults", "checking", "loading"] as const) {
  it(`blocks every mutation for ${blocked} context`, async () => {
    const { state, store } = await mount();
    if (blocked === "unloaded") store.loaded = false;
    if (blocked === "readonly") useSubscriptionStore().usage = { isSuspended: true } as never;
    if (blocked === "workspace") state.activeWorkspace = "applications";
    if (blocked === "session") shared.epoch++;
    if (blocked === "installing") store.installingComponentId = "other";
    if (blocked === "removing") store.removingComponentId = "other";
    if (blocked === "defaults") store.installingDefaults = true;
    if (blocked === "checking") store.checkingUpdates = true;
    if (blocked === "loading") store.loading = true;
    await finish(state.installComponent("php-8.3")); await finish(state.updateComponent(state.manifestRows[1])); await finish(state.installDefaultStack()); await state.removeComponent("nginx");
    expect(store.installComponent).not.toHaveBeenCalled(); expect(store.installDefaultStack).not.toHaveBeenCalled(); expect(store.removeComponent).not.toHaveBeenCalled(); expect(shared.confirm).not.toHaveBeenCalled();
  });
}
it("rejects unknown targets and installation of an already installed component", async () => {
  const { state, store } = await mount(); await finish(state.installComponent("missing")); await finish(state.installComponent("nginx")); await state.removeComponent("missing");
  expect(store.installComponent).not.toHaveBeenCalled(); expect(shared.confirm).not.toHaveBeenCalled();
});
it.each(["Installing", "Removing", "Unknown"])("does not offer a blind retry for %s state", async status => {
  const { state, store } = await mount(); store.catalog!.installed[0].status = status; const row = state.manifestRows[1]; expect(state.showInstallButton(row)).toBe(false); await finish(state.installComponent(row.id)); expect(store.installComponent).not.toHaveBeenCalled();
});
it("blocks different writes and refreshes while installation is in flight", async () => {
  const { state, store } = await mount(), pending = deferred<ComponentInstallResult>(); vi.mocked(store.installComponent).mockReturnValueOnce(pending.promise);
  const saving = state.installComponent("php-8.3"); await flush(); const duplicate = state.installComponent("php-8.3"); const defaults = state.installDefaultStack(); await state.removeComponent("nginx"); await state.refreshCatalog(); await state.checkForUpdates();
  expect(store.installComponent).toHaveBeenCalledOnce(); expect(store.installDefaultStack).not.toHaveBeenCalled(); expect(shared.confirm).not.toHaveBeenCalled(); expect(store.fetchCatalog).not.toHaveBeenCalled(); expect(store.checkForUpdates).not.toHaveBeenCalled(); pending.resolve(result("php-8.3")); await finish(Promise.all([saving, duplicate, defaults]).then(() => undefined));
});
for (const change of ["unmount", "workspace", "return", "session", "readonly", "catalog"] as const) {
  it(`invalidates delayed destructive confirmation on ${change}`, async () => {
    const { state, store } = await mount(); shared.confirm.mockImplementationOnce(async options => {
      if (change === "unmount") { app!.unmount(); app = undefined; }
      if (change === "workspace" || change === "return") { state.activeWorkspace = "applications"; if (change === "return") state.activeWorkspace = "components"; }
      if (change === "session") shared.epoch++;
      if (change === "readonly") useSubscriptionStore().usage = { isSuspended: true } as never;
      if (change === "catalog") store.catalog = { ...store.catalog!, installed: store.catalog!.installed.map(item => ({ ...item, version: "new" })) };
      await options.action(); return true;
    }); await state.removeComponent("nginx"); expect(store.removeComponent).not.toHaveBeenCalled();
  });
}
it("does not overlap writes or scans while confirmation is open", async () => {
  const { state, store } = await mount(), pending = deferred<boolean>(); shared.confirm.mockReturnValueOnce(pending.promise); const asking = state.removeComponent("nginx");
  await finish(state.installComponent("php-8.3")); await state.removeComponent("mysql-8"); await state.refreshCatalog(); expect(store.installComponent).not.toHaveBeenCalled(); expect(shared.confirm).toHaveBeenCalledOnce(); expect(store.fetchCatalog).not.toHaveBeenCalled(); pending.resolve(false); await asking;
});
it("does not retarget a risky update when the supplied row changes during confirmation", async () => {
  const { state, store } = await mount(), row = state.manifestRows[1]; shared.confirm.mockImplementationOnce(async options => { row.id = "nginx"; await options.action(); return true; });
  await finish(state.updateComponent(row)); expect(store.installComponent).toHaveBeenCalledExactlyOnceWith("mysql-8", true);
});
it.each(["unmount", "session", "workspace"])("stops progress polling and ignores late notices after %s", async change => {
  const { state, store } = await mount(), pending = deferred<ComponentInstallResult>(); vi.mocked(store.installComponent).mockReturnValueOnce(pending.promise); const saving = state.installComponent("php-8.3"); await flush();
  if (change === "unmount") { app!.unmount(); app = undefined; } else if (change === "session") shared.epoch++; else state.activeWorkspace = "applications";
  const count = vi.mocked(store.fetchOperationProgress).mock.calls.length; await vi.advanceTimersByTimeAsync(4500); expect(store.fetchOperationProgress).toHaveBeenCalledTimes(count);
  pending.resolve(result("php-8.3")); await finish(saving); expect(state.notice).toBe("");
});
it("does not let a disposed operation clear a newer shared progress record", async () => {
  const { state, store } = await mount(), pending = deferred<ComponentInstallResult>(); vi.mocked(store.installComponent).mockReturnValueOnce(pending.promise); const saving = state.installComponent("php-8.3"); await flush(); app!.unmount(); app = undefined;
  const newer = progress(); store.liveProgress = newer; pending.resolve(result("php-8.3")); await finish(saving); expect(store.liveProgress).toEqual(newer);
});
it("does not delay completed work behind a stuck progress request", async () => {
  const { state, store } = await mount(), pending = deferred<OperationProgress>(); vi.mocked(store.fetchOperationProgress).mockReturnValueOnce(pending.promise); let finished = false; const saving = state.installComponent("php-8.3").then(() => { finished = true; }); await flush();
  expect(finished).toBe(true); pending.resolve(progress()); await finish(saving);
});
it("keeps a late scan notice out of a different workspace", async () => {
  const { state, store } = await mount(), pending = deferred<NonNullable<typeof store.catalog>>(); vi.mocked(store.fetchCatalog).mockReturnValueOnce(pending.promise); const reading = state.refreshCatalog(); state.activeWorkspace = "applications"; pending.resolve(store.catalog!); await reading; expect(state.notice).toBe("");
});
it.each(["install", "update", "defaults", "remove"])("reports an unconfirmed %s instead of success when refreshed state disagrees", async action => {
  const { state, store } = await mount(); vi.mocked(store.installComponent).mockImplementation(async id => result(id)); vi.mocked(store.installDefaultStack).mockResolvedValue([result("php-8.3")]); vi.mocked(store.removeComponent).mockResolvedValue(undefined);
  await finish(action === "install" ? state.installComponent("php-8.3") : action === "update" ? state.updateComponent(state.manifestRows[1]) : action === "defaults" ? state.installDefaultStack() : state.removeComponent("nginx"));
  expect(state.notice).toBe("panel.software.resultUnconfirmed"); expect(state.noticeError).toBe(true);
});
it("requires a successful scan before retrying an unconfirmed result", async () => {
  const { state, store } = await mount(); vi.mocked(store.installComponent).mockResolvedValueOnce(result("php-8.3")); await finish(state.installComponent("php-8.3")); await finish(state.installComponent("php-8.3")); expect(store.installComponent).toHaveBeenCalledOnce();
  await state.refreshCatalog(); await finish(state.installComponent("php-8.3")); expect(store.installComponent).toHaveBeenCalledTimes(2); expect(state.noticeError).toBe(false);
});
it("preserves failed-default diagnostics instead of reporting total success", async () => {
  const { state, store } = await mount(); vi.mocked(store.installDefaultStack).mockResolvedValueOnce([result("php-8.3"), result("mysql-8", "Failed")]); await finish(state.installDefaultStack());
  expect(state.notice).toContain('defaultsPartial{"failed":1,"total":2}'); expect(state.noticeDetails).toContain("mysql-8: fixture.failed"); expect(state.noticeError).toBe(true);
});
it.each(["install", "update", "defaults", "remove"])("preserves verified normal %s success", async action => {
  const { state } = await mount(); await finish(action === "install" ? state.installComponent("php-8.3") : action === "update" ? state.updateComponent(state.manifestRows[1]) : action === "defaults" ? state.installDefaultStack() : state.removeComponent("nginx"));
  expect(state.notice).toBe(action === "defaults" ? "software.defaults.completed" : action === "remove" ? "software.remove.completed" : "fixture.completed"); expect(state.noticeError).toBe(false);
});
it("loads an unmeasured catalog when opening the components workspace", async () => {
  const { state, store } = await mount(); store.loaded = false; state.activeWorkspace = "applications"; state.activeWorkspace = "components"; await flush(); expect(store.fetchCatalog).toHaveBeenCalledOnce(); expect(store.loaded).toBe(true);
});
it("prevents mutation while a server-side installation is still running", async () => {
  vi.mocked(getSoftwareRunningJobs).mockResolvedValueOnce({ running: [{ kind: "software", title: "other", startedAtUtc: "2026-01-01T00:00:00Z" }], interrupted: [], serverTimeUtc: "2026-01-01T00:00:00Z" });
  const { state, store } = await mount(); await finish(state.installComponent("php-8.3")); await state.removeComponent("nginx"); expect(store.installComponent).not.toHaveBeenCalled(); expect(shared.confirm).not.toHaveBeenCalled();
});
it.each(["unmount", "session", "workspace"])("ignores stale operation failures on %s", async change => {
  const { state, store } = await mount(), pending = deferred<ComponentInstallResult>(); vi.mocked(store.installComponent).mockReturnValueOnce(pending.promise); const saving = state.installComponent("php-8.3");
  if (change === "unmount") { app!.unmount(); app = undefined; } else if (change === "session") shared.epoch++; else state.activeWorkspace = "applications";
  pending.reject(new Error("old failure")); await finish(saving); expect(state.notice).toBe(""); expect(state.noticeDetails).toBe("");
});
it.each(["update", "remove"])("sends only once when the %s confirmation action is invoked twice", async action => {
  const { state, store } = await mount(); shared.confirm.mockImplementationOnce(async options => { await Promise.all([options.action(), options.action()]); return true; });
  await finish(action === "update" ? state.updateComponent(state.manifestRows[1]) : state.removeComponent("nginx")); expect(action === "update" ? store.installComponent : store.removeComponent).toHaveBeenCalledOnce();
});
it.each(["session", "readonly", "catalog"])("invalidates risky update confirmation after %s changes", async change => {
  const { state, store } = await mount(); shared.confirm.mockImplementationOnce(async options => { if (change === "session") shared.epoch++; else if (change === "readonly") useSubscriptionStore().usage = { isSuspended: true } as never; else store.catalog!.installed[0].availableVersion = "3.0"; await options.action(); return true; });
  await finish(state.updateComponent(state.manifestRows[1])); expect(store.installComponent).not.toHaveBeenCalled();
});
it("can update a verified tool without the service-restart dialog", async () => {
  const { state, store } = await mount(); await finish(state.updateComponent(state.manifestRows[3])); expect(shared.confirm).not.toHaveBeenCalled(); expect(store.installComponent).toHaveBeenCalledExactlyOnceWith("adminer", true); expect(state.noticeError).toBe(false);
});
it("shows an explicit failed install result as an error", async () => {
  const { state, store } = await mount(); vi.mocked(store.installComponent).mockResolvedValueOnce(result("php-8.3", "Failed")); await finish(state.installComponent("php-8.3")); expect(state.notice).toBe("fixture.failed"); expect(state.noticeError).toBe(true);
});
it.each(["empty", "unknown", "unloaded"])("does not report defaults complete for %s evidence", async kind => {
  const { state, store } = await mount(); vi.mocked(store.installDefaultStack).mockImplementationOnce(async () => { if (kind === "unloaded") store.loaded = false; return kind === "empty" ? [] : [result("php-8.3", kind === "unknown" ? "Unknown" : "Installed")]; });
  await finish(state.installDefaultStack()); expect(state.notice).toBe("panel.software.resultUnconfirmed"); expect(state.noticeError).toBe(true);
});
it("continues to block retries after a failed verification scan", async () => {
  const { state, store } = await mount(); vi.mocked(store.installComponent).mockResolvedValueOnce(result("php-8.3")); await finish(state.installComponent("php-8.3")); vi.mocked(store.fetchCatalog).mockRejectedValueOnce(new Error("scan failed")); await state.refreshCatalog(); await finish(state.installComponent("php-8.3")); expect(store.installComponent).toHaveBeenCalledOnce(); expect(state.notice).toBe("scan failed");
});
it("keeps an earlier pending progress response from repopulating a completed operation", async () => {
  const { state, store } = await mount(), pending = deferred<OperationProgress>(); vi.mocked(store.fetchOperationProgress).mockReturnValueOnce(pending.promise); await finish(state.installComponent("php-8.3")); pending.resolve(progress()); await flush(); expect(state.liveProgress).toBeNull();
});
it("also confirms Nginx updates that stop the proxy runtime", async () => {
  const { state, store } = await mount(); shared.confirm.mockResolvedValueOnce(false); await finish(state.updateComponent(state.manifestRows[2])); expect(shared.confirm).toHaveBeenCalledOnce(); expect(store.installComponent).not.toHaveBeenCalled();
});
it("refreshes a completed background installation even while the applications workspace is visible", async () => {
  vi.mocked(getSoftwareRunningJobs).mockResolvedValueOnce({ running: [{ kind: "software", title: "other", startedAtUtc: "2026-01-01T00:00:00Z" }], interrupted: [], serverTimeUtc: "2026-01-01T00:00:00Z" });
  const { state, store } = await mount(); state.activeWorkspace = "applications"; await vi.advanceTimersByTimeAsync(15000); await flush(); expect(store.fetchCatalog).toHaveBeenCalledOnce();
});
