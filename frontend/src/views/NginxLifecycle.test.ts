import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { createRenderer, h, nextTick, ssrContextKey, type App, type Component } from "vue";
import NginxView from "./NginxView.vue";
import { configureNginx, getNginxStatus, startNginx, stopNginx, reloadNginx, testNginxConfiguration, type NginxStatus, type NginxConfiguration } from "../api/nginx";
import { SoftwareInstallationIncompleteError, SoftwareInstallationRefreshError, SoftwareRemovalIncompleteError, useSoftwareStore, type SoftwareCatalog } from "../stores/software";
const shared = vi.hoisted(() => ({ confirm: vi.fn(), epoch: 0 }));
vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key }) }));
vi.mock("../components/ConfirmDialog.vue", () => ({ useConfirm: () => ({ confirm: shared.confirm }) }));
vi.mock("../api/client", async original => ({ ...await original<object>(), getSessionEpoch: () => shared.epoch }));
vi.mock("../api/nginx", () => ({ configureNginx: vi.fn(), getNginxStatus: vi.fn(), startNginx: vi.fn(), stopNginx: vi.fn(), reloadNginx: vi.fn(), testNginxConfiguration: vi.fn() }));
const measured = (extra: Partial<NginxStatus> = {}): NginxStatus => ({ isInstalled: true, isRunning: true, isStartupTaskRegistered: true, status: "Running", version: "fixture", installDir: "fixture", configPath: "fixture/conf/nginx.conf", listenAddress: "127.0.0.1", listenPort: 8088, serverName: "example.test", upstreamUrl: "http://127.0.0.1:80", cacheEnabled: true, cacheSizeMb: 128, lastConfigTest: "valid", accessUrl: "http://127.0.0.1:8088", ...extra });
const catalog: SoftwareCatalog = { manifest: [], defaults: [], installed: [] };
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(yes => { resolve = yes; }); return { promise, resolve }; }
type State = { status: NginxStatus; form: NginxConfiguration; busy: boolean; notice: string; statusLoaded: boolean; loading: boolean; refresh(): Promise<boolean>; save(): Promise<void>; install(): Promise<void>; remove(): Promise<void>; changeState(action: "start" | "stop" | "reload"): Promise<void>; testConfiguration(): Promise<void> };
let app: App | undefined;
beforeEach(() => { shared.epoch = 0; shared.confirm.mockResolvedValue(true); vi.mocked(getNginxStatus).mockResolvedValue(measured()); vi.mocked(configureNginx).mockResolvedValue(measured()); vi.mocked(startNginx).mockResolvedValue(measured()); vi.mocked(stopNginx).mockResolvedValue(measured({ isRunning: false })); vi.mocked(reloadNginx).mockResolvedValue(measured()); vi.mocked(testNginxConfiguration).mockResolvedValue(measured()); });
afterEach(() => { app?.unmount(); app = undefined; vi.restoreAllMocks(); vi.resetAllMocks(); });
async function flush() { for (let i = 0; i < 12; i++) await nextTick(); }
async function mount(pendingCatalog?: Promise<SoftwareCatalog>) {
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  const pinia = createPinia(); setActivePinia(pinia); const software = useSoftwareStore();
  software.catalog = pendingCatalog ? null : catalog;
  vi.spyOn(software, "fetchCatalog").mockReturnValue(pendingCatalog ?? Promise.resolve(catalog));
  vi.spyOn(software, "installComponent").mockResolvedValue({ componentId: "nginx", displayName: "Nginx", version: "fixture", status: "Installed", installDir: "fixture", serviceName: "", generatedPassword: "", message: "" });
  vi.spyOn(software, "removeComponent").mockResolvedValue({ componentId: "nginx", warnings: [] });
  app = renderer.createApp({ ...NginxView, render: () => h("section") } as Component).use(pinia); app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: State }).setupState; await flush(); return { state, software };
}
it("preserves partial removal instead of announcing nginx was removed", async () => {
  const { state, software } = await mount();
  shared.confirm.mockImplementation(async options => { await options.action?.(); return true; });
  vi.mocked(software.removeComponent).mockRejectedValueOnce(new SoftwareRemovalIncompleteError({ componentId: "nginx", warnings: ["locked runtime"] }));
  await state.remove();
  expect(software.removeComponent).toHaveBeenCalledExactlyOnceWith("nginx"); expect(state.notice).toBe("software.remove.partial");
});
it.each([false, true])("does not convert a shared-store install outcome into nginx success (refresh=%s)", async refreshFailed => {
  vi.mocked(getNginxStatus).mockResolvedValueOnce(measured({ isInstalled: false }));
  const { state, software } = await mount();
  const results = [{ componentId: "nginx", displayName: "Nginx", version: "1", status: refreshFailed ? "Installed" : "Failed", installDir: "fixture", serviceName: "", generatedPassword: "", message: "software.install_failed" }];
  vi.mocked(software.installComponent).mockRejectedValueOnce(refreshFailed
    ? new SoftwareInstallationRefreshError(results, new Error("fixture catalog failure")) : new SoftwareInstallationIncompleteError(results));
  await state.install();
  expect(software.installComponent).toHaveBeenCalledExactlyOnceWith("nginx");
  expect(state.notice).toBe(refreshFailed ? "panel.software.refreshAfterInstallFailed" : "software.install_failed");
  expect(getNginxStatus).toHaveBeenCalledOnce();
});
it("does not save defaults while the server configuration is unread", async () => {
  const pending = deferred<NginxStatus>(); vi.mocked(getNginxStatus).mockReturnValueOnce(pending.promise); const { state } = await mount(); await state.save(); expect(configureNginx).not.toHaveBeenCalled(); pending.resolve(measured()); await flush();
});
it("does not save after configuration refresh fails", async () => {
  const { state } = await mount(); vi.mocked(getNginxStatus).mockRejectedValueOnce(new Error("fixture unavailable")); await state.refresh(); await state.save(); expect(configureNginx).not.toHaveBeenCalled(); expect(state.statusLoaded).toBe(false);
});
it("preserves unsaved configuration when testing the installed configuration", async () => {
  const { state } = await mount(); state.form.upstreamUrl = "http://127.0.0.1:9080"; await state.testConfiguration(); expect(state.form.upstreamUrl).toBe("http://127.0.0.1:9080"); expect(state.notice).toBe("panel.nginx.testPassed");
});
it("preserves edits made while a save is pending", async () => {
  const { state } = await mount(), pending = deferred<NginxStatus>(); vi.mocked(configureNginx).mockReturnValueOnce(pending.promise); state.form.listenPort = 9080;
  const write = state.save(); state.form.listenPort = 9081; pending.resolve(measured({ listenPort: 9080 })); await write;
  expect(configureNginx).toHaveBeenCalledWith(expect.objectContaining({ listenPort: 9080 })); expect(state.status.listenPort).toBe(9080); expect(state.form.listenPort).toBe(9081);
});
it("does not send a second save or service action while saving", async () => {
  const { state } = await mount(), pending = deferred<NginxStatus>(); vi.mocked(configureNginx).mockReturnValue(pending.promise);
  const first = state.save(), second = state.save(); await state.changeState("stop"); pending.resolve(measured()); await Promise.all([first, second]); expect(configureNginx).toHaveBeenCalledOnce(); expect(stopNginx).not.toHaveBeenCalled();
});
it("ignores an older status after a newer refresh", async () => {
  const { state } = await mount(), pending = deferred<NginxStatus>(); vi.mocked(getNginxStatus).mockReturnValueOnce(pending.promise).mockResolvedValueOnce(measured({ listenPort: 9090 }));
  const first = state.refresh(); await state.refresh(); pending.resolve(measured({ listenPort: 8080 })); await first; expect(state.status.listenPort).toBe(9090);
});
it("keeps the read failure visible after installing", async () => {
  vi.mocked(getNginxStatus).mockResolvedValueOnce(measured({ isInstalled: false })); const { state } = await mount(); vi.mocked(getNginxStatus).mockRejectedValueOnce(new Error("fixture unreadable")); await state.install(); expect(state.notice).toBe("common.unexpected_error");
});
it("does not remove Nginx from a confirmation whose screen closed", async () => {
  const { state, software } = await mount(); await state.remove(); app!.unmount(); app = undefined; await shared.confirm.mock.calls[0][0].action(); expect(software.removeComponent).not.toHaveBeenCalled();
});
it("does not refresh a closed screen after installation finishes", async () => {
  vi.mocked(getNginxStatus).mockResolvedValueOnce(measured({ isInstalled: false })); const { state, software } = await mount(), pending = deferred<Awaited<ReturnType<typeof software.installComponent>>>(); vi.mocked(software.installComponent).mockReturnValueOnce(pending.promise);
  const operation = state.install(); app!.unmount(); app = undefined; pending.resolve({} as Awaited<ReturnType<typeof software.installComponent>>); await operation; expect(getNginxStatus).toHaveBeenCalledOnce();
});
it("loads status independently of a slow software catalog", async () => {
  const pending = deferred<SoftwareCatalog>(); await mount(pending.promise); expect(getNginxStatus).toHaveBeenCalledOnce(); pending.resolve(catalog); await flush();
});
it("does not apply status after the session changes", async () => {
  const { state } = await mount(), pending = deferred<NginxStatus>(); vi.mocked(getNginxStatus).mockReturnValueOnce(pending.promise); const read = state.refresh(); shared.epoch++; pending.resolve(measured({ listenPort: 9090 })); await read; expect(state.status.listenPort).toBe(8088);
});
it("does not write from an obsolete session", async () => {
  const { state } = await mount(); shared.epoch++; await state.save(); await state.changeState("start"); expect(configureNginx).not.toHaveBeenCalled(); expect(startNginx).not.toHaveBeenCalled();
});
it("applies a normal successful configuration save", async () => {
  const { state } = await mount(); state.form.listenPort = 9080; vi.mocked(configureNginx).mockResolvedValueOnce(measured({ listenPort: 9080 })); await state.save(); expect(state.form.listenPort).toBe(9080); expect(state.status.listenPort).toBe(9080); expect(state.notice).toBe("panel.nginx.saved");
});
it("removes Nginx after confirmation in the current screen", async () => {
  const { state, software } = await mount(); await state.remove(); vi.mocked(getNginxStatus).mockResolvedValueOnce(measured({ isInstalled: false })); await shared.confirm.mock.calls[0][0].action(); expect(software.removeComponent).toHaveBeenCalledExactlyOnceWith("nginx"); expect(state.status.isInstalled).toBe(false); expect(state.notice).toBe("panel.nginx.removed");
});
it("does not report a removed component when the measured installation remains", async () => {
  const { state } = await mount(); await state.remove(); await shared.confirm.mock.calls[0][0].action(); expect(state.notice).toBe("nginx.remove_failed");
});
it("does not report running when start returns a stopped measurement", async () => {
  const { state } = await mount(); vi.mocked(startNginx).mockResolvedValueOnce(measured({ isRunning: false })); await state.changeState("start"); expect(state.notice).toBe("nginx.start_failed");
});
it("does not report stopped when stop returns a running measurement", async () => {
  const { state } = await mount(); vi.mocked(stopNginx).mockResolvedValueOnce(measured()); await state.changeState("stop"); expect(state.notice).toBe("nginx.stop_failed");
});
it("reports the missing startup task after installation", async () => {
  vi.mocked(getNginxStatus).mockResolvedValueOnce(measured({ isInstalled: false })); const { state } = await mount(); vi.mocked(getNginxStatus).mockResolvedValueOnce(measured({ isStartupTaskRegistered: false })); await state.install(); expect(state.notice).toBe("nginx.startup_task_failed");
});
it("reports a confirmed installation after status and startup task are measured", async () => {
  vi.mocked(getNginxStatus).mockResolvedValueOnce(measured({ isInstalled: false })); const { state } = await mount(); await state.install(); expect(state.notice).toBe("panel.nginx.installed");
});
