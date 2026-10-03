import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createPinia } from "pinia";
import { createRenderer, createSSRApp, h, nextTick, reactive, ref, ssrContextKey, type App, type Component } from "vue";
import { renderToString } from "@vue/server-renderer";
import AppsView from "./AppsView.vue";
import * as api from "../api/apps";
import { invalidateSessionRequests } from "../api/client";
import { useAppsStore } from "../stores/apps";

const fixtures = vi.hoisted(() => ({ sites: null as any, software: null as any, locked: null as any, auth: null as unknown as { session: { user: { role: string } } } }));
vi.mock("../api/apps", () => ({ getSiteAppRuntimeConfiguration: vi.fn(), getSiteAppRuntimeInventory: vi.fn(), hostNodeApp: vi.fn(), hostPythonApp: vi.fn() }));
vi.mock("../stores/sites", () => ({ useSitesStore: () => fixtures.sites }));
vi.mock("../stores/software", () => ({ useSoftwareStore: () => fixtures.software }));
vi.mock("../stores/auth", () => ({ extractApiMessage: (error: Error) => error.message, useAuthStore: () => fixtures.auth }));
vi.mock("../composables/useSubscriptionGate", () => ({ useSubscriptionGate: () => ({ locked: fixtures.locked, writeGate: (disabled = false) => ({ disabled: disabled || fixtures.locked.value }) }) }));
vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key }) }));
type State = {
  selectedSiteName: string; activeRuntime: "node" | "python"; startupFile: string; workingDirectory: string; argumentsValue: string; environmentText: string;
  notice: string; noticeError: boolean; canApply: boolean; busy: boolean; currentConfiguration: api.AppRuntimeSiteConfiguration | null;
  configurationLoading: boolean; configurationLoad: { message: { value: string } };
  loadConfiguration(site: string): Promise<boolean>; refreshConfiguration(): Promise<void>; applyRuntime(): Promise<void>; installRuntime(id?: string): Promise<void>;
};
let app: App | undefined;
function deferred<T>() { let resolve!: (value: T) => void; let reject!: (error: unknown) => void; const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; }
function configuration(siteName = "a", runtimeType = "node", startupFile = "a.js"): api.AppRuntimeSiteConfiguration {
  return { siteName, runtimeType, startupFile, componentId: runtimeType === "python" ? "python-3" : "nodejs-lts", processPath: "", workingDirectory: `${siteName}-folder`, arguments: `--${siteName}`, webConfigPath: "", logsDirectory: "", isEnabled: runtimeType !== "none", hasHttpPlatformHandler: true, managedRuntimeDisabled: true, environmentVariables: [`SITE=${siteName}`, "PORT=%HTTP_PLATFORM_PORT%"] };
}
async function flush() { for (let i = 0; i < 24; i++) await nextTick(); }
function unmount() { app?.unmount(); app = undefined; }
async function mount() {
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  app = renderer.createApp({ ...AppsView, render: () => h("section") } as Component); const pinia = createPinia(); app.use(pinia); app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: State }).setupState; await flush();
  return { state, store: useAppsStore(pinia) };
}
beforeEach(() => {
  invalidateSessionRequests(); fixtures.locked = ref(false); fixtures.auth = reactive({ session: { user: { role: "Administrator" } } });
  fixtures.sites = reactive({ selectedSiteName: "a", sites: [{ name: "a", bindings: [] }, { name: "b", bindings: [] }], loading: false, selectSite(name: string) { this.selectedSiteName = name; }, fetchSites: vi.fn().mockResolvedValue(undefined) });
  fixtures.software = reactive({ catalog: { manifest: [{ id: "nodejs-lts" }, { id: "python-3" }], installed: [{ componentId: "nodejs-lts", status: "Installed" }, { componentId: "python-3", status: "Installed" }] }, loaded: true, loading: false, installingComponentId: "", fetchCatalog: vi.fn().mockResolvedValue(undefined), installComponent: vi.fn().mockResolvedValue({ status: "Installed" }) });
  vi.mocked(api.getSiteAppRuntimeConfiguration).mockImplementation(async site => configuration(site));
  vi.mocked(api.getSiteAppRuntimeInventory).mockResolvedValue([{ componentId: "nodejs-lts", isInstalled: false }, { componentId: "python-3", isInstalled: false }]);
  vi.mocked(api.hostNodeApp).mockImplementation(async (site, input) => ({ ...configuration(site, "node", input.startupFile), workingDirectory: input.workingDirectory, arguments: input.arguments, environmentVariables: [...input.environmentVariables] }));
  vi.mocked(api.hostPythonApp).mockImplementation(async (site, input) => ({ ...configuration(site, "python", input.startupFile), workingDirectory: input.workingDirectory, arguments: input.arguments, environmentVariables: [...input.environmentVariables] }));
});
afterEach(() => { unmount(); invalidateSessionRequests(); vi.resetAllMocks(); });

it.each(["node", "python"])("does not carry %s files or environment from A into an unconfigured B", async runtime => {
  vi.mocked(api.getSiteAppRuntimeConfiguration).mockResolvedValueOnce(configuration("a", runtime, runtime === "node" ? "private-a.js" : "private-a.py"));
  const { state } = await mount(), pending = deferred<api.AppRuntimeSiteConfiguration>();
  vi.mocked(api.getSiteAppRuntimeConfiguration).mockReturnValueOnce(pending.promise); state.selectedSiteName = "b"; await flush();
  pending.resolve(configuration("b", "none", "")); await flush();
  expect(state.environmentText).toBe(""); expect(state.workingDirectory).toBe("."); expect(state.argumentsValue).toBe("");
  expect(state.startupFile).toBe(state.activeRuntime === "node" ? "server.js" : "app.py");
  await state.applyRuntime(); const calls = [...vi.mocked(api.hostNodeApp).mock.calls, ...vi.mocked(api.hostPythonApp).mock.calls];
  expect(calls).toHaveLength(1); expect(calls[0]![0]).toBe("b"); expect(calls[0]![1].environmentVariables).toEqual([]);
});
it("does not reuse A's startup file when B's configured runtime has an empty startup", async () => {
  const { state } = await mount(); vi.mocked(api.getSiteAppRuntimeConfiguration).mockResolvedValueOnce(configuration("b", "node", ""));
  state.selectedSiteName = "b"; await flush(); expect(state.startupFile).not.toBe("a.js");
});
it("retains a draft edited while refresh is pending and updates measured status", async () => {
  const { state } = await mount(), pending = deferred<api.AppRuntimeSiteConfiguration>(); vi.mocked(api.getSiteAppRuntimeConfiguration).mockReturnValueOnce(pending.promise);
  const reading = state.refreshConfiguration(); state.startupFile = "next-draft.js"; state.environmentText = "NEXT=draft";
  pending.resolve(configuration("a", "node", "measured.js")); await reading; await flush();
  expect(state.startupFile).toBe("next-draft.js"); expect(state.environmentText).toBe("NEXT=draft"); expect(state.currentConfiguration?.startupFile).toBe("measured.js");
});
it.each(["node", "python"])("keeps newer %s edits while saving an immutable request", async runtime => {
  const { state } = await mount(), pending = deferred<api.AppRuntimeSiteConfiguration>(); state.activeRuntime = runtime as "node" | "python";
  const action = runtime === "node" ? api.hostNodeApp : api.hostPythonApp; vi.mocked(action).mockReturnValueOnce(pending.promise);
  state.startupFile = "submitted"; state.environmentText = "MODE=submitted"; const saving = state.applyRuntime();
  state.startupFile = "next-draft"; state.environmentText = "MODE=next";
  pending.resolve(configuration("a", runtime, "submitted")); await saving; await flush();
  expect(state.startupFile).toBe("next-draft"); expect(state.environmentText).toBe("MODE=next");
  expect(vi.mocked(action).mock.calls[0]![1].environmentVariables).toEqual(["MODE=submitted"]); expect(state.currentConfiguration?.startupFile).toBe("submitted");
});
it("applies an unchanged successful draft and keeps automatic env out of the editor", async () => {
  const { state } = await mount(); state.startupFile = "saved.js"; await state.applyRuntime(); await flush();
  expect(state.startupFile).toBe("saved.js"); expect(state.environmentText).toBe("SITE=a"); expect(state.notice).toBe("appruntime.node_updated"); expect(state.noticeError).toBe(false);
});
it.each(["success", "failure"])("does not publish A's late mutation notice into B (%s)", async outcome => {
  const { state } = await mount(), pending = deferred<api.AppRuntimeSiteConfiguration>(); vi.mocked(api.hostNodeApp).mockReturnValueOnce(pending.promise);
  const saving = state.applyRuntime(); state.selectedSiteName = "b"; await flush();
  if (outcome === "success") pending.resolve(configuration()); else pending.reject(new Error("A failed"));
  await saving; await flush(); expect(state.startupFile).toBe("a.js"); expect(state.currentConfiguration?.siteName).toBe("b"); expect(state.notice).toBe("");
  expect(vi.mocked(api.hostNodeApp).mock.calls[0]![0]).toBe("a");
});
it("does not replace a selected Python draft with a pending Node result", async () => {
  const { state } = await mount(), pending = deferred<api.AppRuntimeSiteConfiguration>(); vi.mocked(api.hostNodeApp).mockReturnValueOnce(pending.promise);
  const saving = state.applyRuntime(); state.activeRuntime = "python"; state.startupFile = "next.py";
  pending.resolve(configuration()); await saving; await flush(); expect(state.activeRuntime).toBe("python"); expect(state.startupFile).toBe("next.py"); expect(state.notice).toBe("");
});
it("blocks duplicate and cross-runtime apply while one action is pending", async () => {
  const { state } = await mount(), pending = deferred<api.AppRuntimeSiteConfiguration>(); vi.mocked(api.hostNodeApp).mockReturnValueOnce(pending.promise);
  const saving = state.applyRuntime(); await state.applyRuntime(); state.activeRuntime = "python"; await state.applyRuntime();
  expect(api.hostNodeApp).toHaveBeenCalledOnce(); expect(api.hostPythonApp).not.toHaveBeenCalled();
  pending.resolve(configuration()); await saving;
});
it("does not enable writes against cached data after the latest refresh failed, and permits retry", async () => {
  const { state } = await mount(); vi.mocked(api.getSiteAppRuntimeConfiguration).mockRejectedValueOnce(new Error("read failed"));
  await state.refreshConfiguration(); expect(state.canApply).toBe(false); await state.applyRuntime(); expect(api.hostNodeApp).not.toHaveBeenCalled();
  await state.refreshConfiguration(); expect(state.canApply).toBe(true); await state.applyRuntime(); expect(api.hostNodeApp).toHaveBeenCalledOnce();
});
it("does not send writes before the first configuration read or while suspended", async () => {
  const pending = deferred<api.AppRuntimeSiteConfiguration>(); vi.mocked(api.getSiteAppRuntimeConfiguration).mockReturnValueOnce(pending.promise);
  const { state } = await mount(); await state.applyRuntime(); expect(api.hostNodeApp).not.toHaveBeenCalled();
  pending.resolve(configuration()); await flush(); fixtures.locked.value = true; await state.applyRuntime(); await state.installRuntime("nodejs-lts");
  expect(api.hostNodeApp).not.toHaveBeenCalled(); expect(fixtures.software.installComponent).not.toHaveBeenCalled();
});
it.each(["success", "failure"])("ignores an obsolete read after a new selection (%s)", async outcome => {
  const { state } = await mount(), old = deferred<api.AppRuntimeSiteConfiguration>(); vi.mocked(api.getSiteAppRuntimeConfiguration).mockReturnValueOnce(old.promise);
  const reading = state.refreshConfiguration(); state.selectedSiteName = "b"; await flush();
  if (outcome === "success") old.resolve(configuration("a", "python")); else old.reject(new Error("old error"));
  await reading; expect(state.notice).toBe(""); expect(state.currentConfiguration?.siteName).toBe("b");
});
it.each(["success", "failure"])("ignores a read superseded by a successful write (%s)", async outcome => {
  const { state } = await mount(), old = deferred<api.AppRuntimeSiteConfiguration>(); vi.mocked(api.getSiteAppRuntimeConfiguration).mockReturnValueOnce(old.promise);
  const reading = state.refreshConfiguration(); state.startupFile = "saved.js"; await state.applyRuntime();
  if (outcome === "success") old.resolve(configuration("a", "node", "old.js")); else old.reject(new Error("old read failed"));
  await reading; await flush(); expect(state.currentConfiguration?.startupFile).toBe("saved.js"); expect(state.notice).toBe("appruntime.node_updated"); expect(state.noticeError).toBe(false);
});
for (const lifecycle of ["unmount", "session"] as const) {
  const invalidate = () => lifecycle === "unmount" ? unmount() : invalidateSessionRequests();
  it.each(["success", "failure"])(`${lifecycle} ignores a pending save and rejects further actions (%s)`, async outcome => {
    const { state } = await mount(), pending = deferred<api.AppRuntimeSiteConfiguration>(); vi.mocked(api.hostNodeApp).mockReturnValueOnce(pending.promise);
    const saving = state.applyRuntime(); invalidate();
    if (outcome === "success") pending.resolve(configuration("a", "node", "late.js")); else pending.reject(new Error("late error"));
    await saving; await state.applyRuntime(); await state.installRuntime("python-3");
    expect(api.hostNodeApp).toHaveBeenCalledOnce(); expect(fixtures.software.installComponent).not.toHaveBeenCalled(); expect(state.notice).toBe(""); expect(state.startupFile).toBe("a.js");
  });
  it(`${lifecycle} ignores a pending refresh and does not start another GET`, async () => {
    const { state } = await mount(), pending = deferred<api.AppRuntimeSiteConfiguration>(); vi.mocked(api.getSiteAppRuntimeConfiguration).mockReturnValueOnce(pending.promise);
    const reading = state.refreshConfiguration(); invalidate(); pending.resolve(configuration("a", "python", "late.py")); await reading; await state.refreshConfiguration();
    expect(api.getSiteAppRuntimeConfiguration).toHaveBeenCalledTimes(2); expect(state.startupFile).toBe("a.js"); expect(state.notice).toBe("");
  });
  it(`${lifecycle} stops mounted initialization before a follow-up site GET`, async () => {
    const pending = deferred<void>(); fixtures.software.catalog = null; fixtures.software.loaded = false; fixtures.sites.sites = []; fixtures.sites.selectedSiteName = "";
    fixtures.software.fetchCatalog.mockReturnValueOnce(pending.promise); await mount(); invalidate(); pending.resolve(); await flush(); expect(fixtures.sites.fetchSites).not.toHaveBeenCalled();
  });
}
it("suppresses a late install result after the runtime selection changes and prevents duplicates", async () => {
  const { state } = await mount(), pending = deferred<{ status: string }>(); fixtures.software.catalog.installed = []; fixtures.software.installComponent.mockReturnValueOnce(pending.promise);
  const installing = state.installRuntime("nodejs-lts"); await state.installRuntime("nodejs-lts"); state.activeRuntime = "python";
  pending.resolve({ status: "Installed" }); await installing; expect(fixtures.software.installComponent).toHaveBeenCalledOnce(); expect(state.notice).toBe("");
});
it.each(["Customer", "Reseller"])("does not offer an administrator-only installation to %s", async role => {
  fixtures.auth.session.user.role = role; fixtures.software.catalog.installed = [];
  const { state } = await mount(); await state.installRuntime("nodejs-lts"); expect(fixtures.software.installComponent).not.toHaveBeenCalled();
});
it.each(["Failed", "Unknown", "missing", "unmeasured"])("does not report an unconfirmed runtime installation as complete (%s)", async outcome => {
  const { state } = await mount(); fixtures.software.catalog.installed = [];
  fixtures.software.installComponent.mockImplementationOnce(async () => {
    if (outcome === "unmeasured") fixtures.software.loaded = false;
    return { componentId: "nodejs-lts", version: "20", status: outcome === "missing" || outcome === "unmeasured" ? "Installed" : outcome, message: "software.install.failed" };
  });
  await state.installRuntime("nodejs-lts"); expect(state.noticeError).toBe(true); expect(state.notice).not.toBe("software.install.completed");
});
it("reports a measured administrator installation as complete", async () => {
  const { state } = await mount(); fixtures.software.catalog.installed = [];
  fixtures.software.installComponent.mockImplementationOnce(async () => {
    fixtures.software.catalog.installed = [{ componentId: "nodejs-lts", status: "Installed", version: "20" }];
    return { componentId: "nodejs-lts", version: "20", status: "Installed", message: "software.install.completed" };
  });
  await state.installRuntime("nodejs-lts"); expect(state.noticeError).toBe(false); expect(state.notice).toBe("software.install.completed");
});
it("requires a new measurement after a same-site save fails while a different runtime tab is selected", async () => {
  const { state } = await mount(), pending = deferred<api.AppRuntimeSiteConfiguration>(); vi.mocked(api.hostNodeApp).mockReturnValueOnce(pending.promise);
  const saving = state.applyRuntime(); state.activeRuntime = "python";
  pending.reject(new Error("app pool update failed after configuration write")); await saving;
  expect(state.canApply).toBe(false); await state.applyRuntime(); expect(api.hostPythonApp).not.toHaveBeenCalled();
  await state.refreshConfiguration(); expect(state.canApply).toBe(true);
});
it("keeps a newer refresh pending when an older read fails", async () => {
  const { state } = await mount(), old = deferred<api.AppRuntimeSiteConfiguration>(), recent = deferred<api.AppRuntimeSiteConfiguration>();
  vi.mocked(api.getSiteAppRuntimeConfiguration).mockReturnValueOnce(old.promise).mockReturnValueOnce(recent.promise);
  const first = state.refreshConfiguration(), second = state.refreshConfiguration(); old.reject(new Error("old error")); await first;
  expect(state.configurationLoading).toBe(true); expect(state.notice).toBe("");
  recent.resolve(configuration("a", "python", "latest.py")); await second; expect(state.configurationLoading).toBe(false); expect(state.startupFile).toBe("latest.py");
});
it("does not admit a competing save on a different site while an earlier save is pending", async () => {
  const { state } = await mount(), pending = deferred<api.AppRuntimeSiteConfiguration>(); vi.mocked(api.hostNodeApp).mockReturnValueOnce(pending.promise);
  const saving = state.applyRuntime(); state.selectedSiteName = "b"; await flush(); await state.applyRuntime(); expect(api.hostNodeApp).toHaveBeenCalledOnce();
  pending.resolve(configuration()); await saving; expect(state.canApply).toBe(true); await state.applyRuntime(); expect(api.hostNodeApp).toHaveBeenCalledTimes(2);
  expect(vi.mocked(api.hostNodeApp).mock.calls[1]![0]).toBe("b");
});
it("does not accept an A result after selection moved A to B and back", async () => {
  const { state } = await mount(), pending = deferred<api.AppRuntimeSiteConfiguration>(); vi.mocked(api.getSiteAppRuntimeConfiguration).mockReturnValueOnce(pending.promise);
  const reading = state.refreshConfiguration(); state.selectedSiteName = "b"; await flush(); state.selectedSiteName = "a"; await flush();
  state.startupFile = "new-visit.js"; pending.resolve(configuration("a", "python", "previous-visit.py")); await reading;
  expect(state.startupFile).toBe("new-visit.js"); expect(state.notice).toBe("");
});
it("blocks configuration writes when the software catalog measurement failed", async () => {
  const { state } = await mount(); fixtures.software.loaded = false; await state.applyRuntime(); expect(api.hostNodeApp).not.toHaveBeenCalled();
});
it.each(["Administrator", "Reseller", "Customer"])("renders the installation action according to the real %s contract", async role => {
  fixtures.auth.session.user.role = role; fixtures.software.catalog.installed = [];
  const pinia = createPinia(); useAppsStore(pinia).configurations.a = configuration();
  const html = await renderToString(createSSRApp(AppsView).use(pinia));
  expect(html).not.toContain("panel.apps.runtimeReadyHint");
  expect(html).toContain(role === "Administrator" ? "panel.php.install" : "panel.apps.runtimeUnknown");
  if (role !== "Administrator") expect(html).not.toMatch(/<button[^>]*>\s*panel\.php\.install\s*<\/button>/);
});
