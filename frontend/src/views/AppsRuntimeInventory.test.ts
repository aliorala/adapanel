import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createPinia } from "pinia";
import { AxiosError, type AxiosResponse, type InternalAxiosRequestConfig } from "axios";
import { createRenderer, h, nextTick, ssrContextKey, type App, type Component } from "vue";
import AppsView from "./AppsView.vue";
import { useAuthStore } from "../stores/auth";
import { useSitesStore } from "../stores/sites";
import { useSoftwareStore, type SoftwareCatalog } from "../stores/software";
import { useAppsStore } from "../stores/apps";
import { apiClient, invalidateSessionRequests } from "../api/client";
import type { AppRuntimeSiteConfiguration, HostNodeAppPayload } from "../api/apps";

vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key }) }));
type State = {
  selectedSiteName: string; activeRuntime: "node" | "python"; startupFile: string; environmentText: string;
  canApply: boolean; runtimeInstalled: boolean; runtimeMeasured: boolean; inventoryLoading: boolean;
  inventoryLoad: { message: { value: string } }; notice: string; noticeError: boolean;
  currentConfiguration: AppRuntimeSiteConfiguration | null;
  refreshConfiguration(): Promise<void>; loadRuntimeInventory(site: string): Promise<boolean>; applyRuntime(): Promise<void>;
};
type Inventory = { componentId: string; isInstalled: boolean }[];
const inventories = (node = true, python = true): Inventory => [{ componentId: "nodejs-lts", isInstalled: node }, { componentId: "python-3", isInstalled: python }];
const configuration = (siteName: string, startupFile = "server.js"): AppRuntimeSiteConfiguration => ({ siteName, startupFile, runtimeType: "node", componentId: "nodejs-lts", workingDirectory: ".", arguments: "", environmentVariables: [], processPath: "", webConfigPath: "", logsDirectory: "", isEnabled: true, hasHttpPlatformHandler: true, managedRuntimeDisabled: true });
const site = (name: string) => ({ id: 1, name, state: "Started", autoStart: true, physicalPath: "", applicationRootPath: "", applicationPoolName: name, bindings: [] });
const catalog = (): SoftwareCatalog => ({ manifest: [], defaults: [], installed: ["nodejs-lts", "python-3"].map(componentId => ({ id: componentId+'-record', componentId, displayName: componentId, status: "Installed", version: componentId==='nodejs-lts' ? "20" : "3", type: 'Zip', installDir: 'fixture/runtime', serviceName: '', lastError: '', generatedPassword: '', isSystemDetected: false, detectionSource: '', availableVersion: '', updateAvailable: false, versionCheckSupported: true, versionCheckFailed: false, installedAtUtc: '2026-10-04T00:00:00Z' })) });
const response = (config: InternalAxiosRequestConfig, data: unknown): AxiosResponse => ({ config, data: { success: true, data }, status: 200, statusText: "OK", headers: {} });
function failure(config: InternalAxiosRequestConfig, code = 503) { return new AxiosError("Inventory unavailable", "ERR_BAD_RESPONSE", config, undefined, { ...response(config, null), status: code, data: { success: false, message: "appruntime.inventory_failed" } }); }
function deferred<T>() { let resolve!: (value: T) => void; let reject!: (reason: unknown) => void; const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; }
let app: App | undefined, calls: InternalAxiosRequestConfig[], role: string;
let inventoryReply: (config: InternalAxiosRequestConfig) => Promise<AxiosResponse>;
const originalAdapter = apiClient.defaults.adapter;
const inventoryCalls = () => calls.filter(call => call.url?.endsWith("/apps/runtimes"));
async function flush() { for (let i = 0; i < 36; i++) await nextTick(); }
function unmount() { app?.unmount(); app = undefined; }
async function mount(options: { emptySites?: boolean; staleCatalog?: boolean } = {}) {
  const pinia = createPinia(), auth = useAuthStore(pinia), sites = useSitesStore(pinia), software = useSoftwareStore(pinia);
  auth.session = { isAuthenticated: true, requiresSetupWizard: false, user: { id: "actor", userName: "actor", email: "actor@example.test", role, preferredLanguage: "en", isTwoFactorEnabled: false, isActive: true, lastLoginAtUtc: null } };
  sites.sites = options.emptySites ? [] : [site("alpha"), site("beta")]; sites.selectedSiteName = options.emptySites ? "" : "alpha"; sites.loaded = true;
  if (options.staleCatalog) { software.catalog = catalog(); software.loaded = true; }
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  app = renderer.createApp({ ...AppsView, render: () => h("section") } as Component).use(pinia); app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: State }).setupState;
  await flush(); return { state, store: useAppsStore(pinia), software };
}
beforeEach(() => {
  invalidateSessionRequests(); calls = []; role = "Reseller";
  vi.stubGlobal("window", { location: { pathname: "/panel", hostname: "localhost" } });
  vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ success: true, data: { requestToken: "fixture-token" } }), { status: 200 })));
  inventoryReply = async config => response(config, inventories());
  apiClient.defaults.adapter = async config => {
    calls.push(config);
    if (config.url === "/software") {
      if (role !== "Administrator") throw failure(config, 403);
      return response(config, catalog());
    }
    if (config.url === "/scope/sites") return response(config, [site("alpha"), site("beta")]);
    if (config.url?.endsWith("/apps/runtimes")) return inventoryReply(config);
    const match = config.url?.match(/^\/sites\/([^/]+)\/(apps|node|python)$/);
    if (match) {
      const target = decodeURIComponent(match[1]!);
      if (config.method === "get") return response(config, configuration(target));
      const input = JSON.parse(String(config.data)) as HostNodeAppPayload;
      return response(config, { ...configuration(target, input.startupFile), runtimeType: match[2], environmentVariables: input.environmentVariables });
    }
    throw new Error(`Unexpected fixture request: ${config.method} ${config.url}`);
  };
});
afterEach(() => { unmount(); apiClient.defaults.adapter = originalAdapter; invalidateSessionRequests(); vi.unstubAllGlobals(); });

it.each(["node", "python"])("lets a reseller publish %s using only the site's scoped runtime measurement", async runtime => {
  const { state } = await mount(); state.activeRuntime = runtime as "node" | "python"; state.startupFile = runtime === "node" ? "reseller.js" : "reseller.py";
  expect(calls.some(call => call.url === "/software")).toBe(false); expect(inventoryCalls()).toHaveLength(1);
  expect(state.runtimeInstalled).toBe(true); expect(state.canApply).toBe(true); await state.applyRuntime();
  expect(calls.filter(call => call.method === "post").map(call => call.url)).toEqual([`/sites/alpha/${runtime}`]);
  expect(state.noticeError).toBe(false);
});
it("loads a reseller's site list even though the full catalog is forbidden", async () => {
  const { state } = await mount({ emptySites: true });
  expect(calls.some(call => call.url === "/software")).toBe(false); expect(calls.some(call => call.url === "/scope/sites")).toBe(true);
  expect(state.selectedSiteName).toBe("alpha"); expect(state.canApply).toBe(true);
});
it("keeps the administrator catalog and publish path", async () => {
  role = "Administrator"; const { state } = await mount();
  expect(calls.filter(call => call.url === "/software")).toHaveLength(1); expect(inventoryCalls()).toHaveLength(0);
  expect(state.canApply).toBe(true); await state.applyRuntime(); expect(calls.some(call => call.method === "post")).toBe(true);
});
it.each([403, 500, 503])("keeps failed scoped inventory unknown and permits a measured retry (%s)", async status => {
  inventoryReply = async config => { throw failure(config, status); }; const { state } = await mount();
  expect(inventoryCalls()).toHaveLength(1); expect(state.runtimeMeasured).toBe(false); expect(state.inventoryLoad.message.value).not.toBe("");
  expect(state.canApply).toBe(false); await state.applyRuntime(); expect(calls.some(call => call.method === "post")).toBe(false);
  inventoryReply = async config => response(config, inventories()); await state.refreshConfiguration();
  expect(state.runtimeMeasured).toBe(true); expect(state.canApply).toBe(true); expect(state.inventoryLoad.message.value).toBe("");
});
it("distinguishes measured not installed from unavailable", async () => {
  inventoryReply = async config => response(config, inventories(false, true)); const { state } = await mount();
  expect(inventoryCalls()).toHaveLength(1); expect(state.runtimeMeasured).toBe(true); expect(state.runtimeInstalled).toBe(false); expect(state.inventoryLoad.message.value).toBe("");
  expect(state.canApply).toBe(false); state.activeRuntime = "python"; expect(state.canApply).toBe(true);
});
it.each([[], [{ componentId: "nodejs-lts", isInstalled: true }], [{ componentId: "nodejs-lts", isInstalled: true }, { componentId: "nodejs-lts", isInstalled: false }], [{ componentId: "nodejs-lts", isInstalled: "false" }, { componentId: "python-3", isInstalled: true }]].map(data => ({ data })))("does not accept an incomplete or malformed successful inventory ($data)", async ({ data }) => {
  inventoryReply = async config => response(config, data); const { state } = await mount();
  expect(inventoryCalls()).toHaveLength(1); expect(state.runtimeMeasured).toBe(false); expect(state.canApply).toBe(false); expect(state.inventoryLoad.message.value).not.toBe("");
});
it("does not reuse an old full catalog for a reseller after scoped inventory fails", async () => {
  inventoryReply = async config => { throw failure(config); }; const { state } = await mount({ staleCatalog: true });
  expect(state.runtimeMeasured).toBe(false); expect(state.canApply).toBe(false); await state.applyRuntime(); expect(calls.some(call => call.method === "post")).toBe(false);
});
it("does not authorize publishing from a failed envelope that still contains inventory rows", async () => {
  inventoryReply = async config => ({ ...response(config, inventories()), data: { success: false, data: inventories(), message: "appruntime.inventory_unavailable" } });
  const { state } = await mount(); expect(state.runtimeMeasured).toBe(false); expect(state.canApply).toBe(false);
  expect(state.inventoryLoad.message.value).not.toBe(""); await state.applyRuntime(); expect(calls.some(call => call.method === "post")).toBe(false);
});
it("closes publishing after a later scoped measurement fails while preserving the draft", async () => {
  const { state } = await mount(); state.startupFile = "unsaved.js"; inventoryReply = async config => { throw failure(config); };
  await state.loadRuntimeInventory("alpha"); expect(state.canApply).toBe(false); expect(state.startupFile).toBe("unsaved.js"); expect(state.runtimeMeasured).toBe(false);
});
it("keeps a draft edited while inventory refresh is pending", async () => {
  const { state } = await mount(), pending = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
  inventoryReply = config => { request = config; return pending.promise; }; const reading = state.refreshConfiguration(); await flush();
  state.startupFile = "next-draft.js"; state.environmentText = "MODE=draft";
  pending.resolve(response(request, inventories())); await reading;
  expect(state.startupFile).toBe("next-draft.js"); expect(state.environmentText).toBe("MODE=draft"); expect(state.canApply).toBe(true);
});
it.each(["success", "failure"])("does not apply A's inventory result to B (%s)", async outcome => {
  const pending = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
  inventoryReply = config => config.url?.includes("/alpha/") ? (request = config, pending.promise) : Promise.resolve(response(config, inventories(false, false)));
  const { state } = await mount(); state.selectedSiteName = "beta"; await flush();
  expect(inventoryCalls()).toHaveLength(2); expect(state.runtimeMeasured).toBe(true); expect(state.runtimeInstalled).toBe(false);
  if (outcome === "success") pending.resolve(response(request, inventories())); else pending.reject(failure(request)); await flush();
  expect(state.selectedSiteName).toBe("beta"); expect(state.runtimeInstalled).toBe(false); expect(state.canApply).toBe(false); expect(state.inventoryLoad.message.value).toBe("");
});
it("keeps the latest same-site inventory and loading state when older reads finish first", async () => {
  const { state } = await mount(), old = deferred<AxiosResponse>(), recent = deferred<AxiosResponse>(); let first!: InternalAxiosRequestConfig, second!: InternalAxiosRequestConfig, index = 0;
  inventoryReply = config => ++index === 1 ? (first = config, old.promise) : (second = config, recent.promise);
  const earlier = state.loadRuntimeInventory("alpha"), latest = state.loadRuntimeInventory("alpha"); await flush();
  old.resolve(response(first, inventories())); await earlier; expect(state.inventoryLoading).toBe(true); expect(state.canApply).toBe(false);
  recent.resolve(response(second, inventories(false, false))); await latest; expect(state.inventoryLoading).toBe(false); expect(state.runtimeInstalled).toBe(false);
});
for (const lifecycle of ["session", "unmount"] as const) {
  it(`${lifecycle} discards an outstanding inventory and does not start another read`, async () => {
    const pending = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
    inventoryReply = config => (request = config, pending.promise); const { state } = await mount(); expect(inventoryCalls()).toHaveLength(1);
    if (lifecycle === "session") invalidateSessionRequests(); else unmount();
    pending.resolve(response(request, inventories())); await flush(); await state.refreshConfiguration();
    expect(inventoryCalls()).toHaveLength(1); expect(state.runtimeMeasured).toBe(false); expect(state.notice).toBe(""); await state.applyRuntime();
    expect(calls.some(call => call.method === "post")).toBe(false);
  });
}
it("encodes the selected site in the scoped inventory request", async () => {
  const { state } = await mount(); state.selectedSiteName = "sub site+#"; await flush();
  expect(inventoryCalls().slice(-1)[0]?.url).toBe("/sites/sub%20site%2B%23/apps/runtimes");
});
