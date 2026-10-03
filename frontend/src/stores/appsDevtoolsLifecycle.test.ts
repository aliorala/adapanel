import { afterAll, beforeAll, expect, it, vi } from "vitest";
import type { AxiosResponse } from "axios";
import type { AppRuntimeSiteConfiguration } from "../api/apps";

// Use Pinia's actual development action-grouping plugin. NODE_ENV=test normally
// bypasses it, hiding the distinct `this` Proxy that each browser action receives.
let vue: typeof import("vue"), pinia: typeof import("pinia"), apps: typeof import("./apps"), client: typeof import("../api/client");
beforeAll(async () => {
  vi.stubEnv("NODE_ENV", "development");
  vi.stubGlobal("window", { location: { hostname: "localhost", pathname: "/panel" } });
  vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ success: true, data: { requestToken: "fixture-token" } }), { status: 200 })));
  vue = await import("vue"); pinia = await import("pinia");
  apps = await import("./apps"); client = await import("../api/client");
});
afterAll(() => { client?.invalidateSessionRequests(); vi.unstubAllEnvs(); vi.unstubAllGlobals(); });
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(yes => { resolve = yes; }); return { promise, resolve }; }
function installedPinia() {
  const instance = pinia.createPinia();
  const renderer = vue.createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  renderer.createApp({ render: () => vue.h("section") }).use(instance);
  return instance;
}
function configuration(file: string, runtimeType: string): AppRuntimeSiteConfiguration {
  return { siteName: "alpha", runtimeType, startupFile: file, componentId: runtimeType === "node" ? "nodejs-lts" : "python-3", processPath: "", workingDirectory: ".", arguments: "", webConfigPath: "", logsDirectory: "", isEnabled: true, hasHttpPlatformHandler: true, managedRuntimeDisabled: true, environmentVariables: [] };
}

it("really exercises Pinia's distinct development action contexts", () => {
  const useProbe = pinia.defineStore("action-context-probe", { state: () => ({ count: 0 }), actions: { context(): object { return this; } } });
  const store = useProbe(installedPinia()), first = store.context(), second = store.context();
  expect((store as unknown as { _isOptionsAPI: boolean })._isOptionsAPI).toBe(true);
  expect(first).not.toBe(second);
  expect(vue.toRaw(first)).toBe(vue.toRaw(second));
});

it.each(["node", "python"])("development Pinia keeps a %s POST result after an older real API GET returns", async runtime => {
  const store = apps.useAppsStore(installedPinia()), old = deferred<AxiosResponse>();
  const adapter = client.apiClient.defaults.adapter;
  let readConfig: AxiosResponse["config"] | undefined;
  client.apiClient.defaults.adapter = async config => {
    if (config.method === "get") { readConfig = config; return old.promise; }
    return { config, data: { success: true, data: configuration("verified-new", runtime) }, headers: {}, status: 200, statusText: "OK" };
  };
  try {
    const reading = store.fetchConfiguration("alpha").catch(error => error);
    await vi.waitFor(() => expect(readConfig).toBeDefined());
    const payload = { startupFile: "verified-new", workingDirectory: ".", arguments: "", componentId: runtime === "node" ? "nodejs-lts" : "python-3", environmentVariables: [] };
    await (runtime === "node" ? store.configureNode("alpha", payload) : store.configurePython("alpha", payload));
    expect(store.getConfiguration("alpha")?.startupFile).toBe("verified-new");
    old.resolve({ config: readConfig!, data: { success: true, data: configuration("previous-old", "node") }, headers: {}, status: 200, statusText: "OK" });
    await reading;
    expect(store.getConfiguration("alpha")?.startupFile).toBe("verified-new");
  } finally { client.apiClient.defaults.adapter = adapter; }
});

const runtimeInventory = (installed: boolean) => [{ componentId: "nodejs-lts", isInstalled: installed }, { componentId: "python-3", isInstalled: installed }];
it.each(["success", "failure"])("development Pinia preserves a newer scoped inventory after an older %s", async outcome => {
  const store = apps.useAppsStore(installedPinia()), old = deferred<AxiosResponse>();
  const adapter = client.apiClient.defaults.adapter; let oldConfig: AxiosResponse["config"] | undefined, count = 0;
  client.apiClient.defaults.adapter = async config => {
    if (++count === 1) { oldConfig = config; return old.promise; }
    return { config, data: { success: true, data: runtimeInventory(false) }, headers: {}, status: 200, statusText: "OK" };
  };
  try {
    const first = store.fetchRuntimeInventory("alpha").catch(error => error); await vi.waitFor(() => expect(oldConfig).toBeDefined());
    await store.fetchRuntimeInventory("alpha");
    old.resolve({ config: oldConfig!, data: outcome === "success" ? { success: true, data: runtimeInventory(true) } : { success: true, data: [] }, headers: {}, status: 200, statusText: "OK" });
    await first;
    expect(store.runtimeInventories.alpha).toEqual(runtimeInventory(false)); expect(store.runtimeInventoryLoaded.alpha).toBe(true);
  } finally { client.apiClient.defaults.adapter = adapter; }
});
it("development Pinia invalidates inventory measured before a configuration write", async () => {
  const store = apps.useAppsStore(installedPinia()), old = deferred<AxiosResponse>();
  const adapter = client.apiClient.defaults.adapter; let oldConfig: AxiosResponse["config"] | undefined;
  client.apiClient.defaults.adapter = async config => {
    if (config.method === "get") { oldConfig = config; return old.promise; }
    return { config, data: { success: true, data: configuration("saved.js", "node") }, headers: {}, status: 200, statusText: "OK" };
  };
  try {
    const reading = store.fetchRuntimeInventory("alpha").catch(error => error); await vi.waitFor(() => expect(oldConfig).toBeDefined());
    await store.configureNode("alpha", { startupFile: "saved.js", componentId: "nodejs-lts", workingDirectory: ".", arguments: "", environmentVariables: [] });
    old.resolve({ config: oldConfig!, data: { success: true, data: runtimeInventory(true) }, headers: {}, status: 200, statusText: "OK" }); await reading;
    expect(store.runtimeInventoryLoaded.alpha).toBe(false); expect(store.runtimeInventories.alpha).toBeUndefined();
  } finally { client.apiClient.defaults.adapter = adapter; }
});
it("development Pinia does not put an earlier session's inventory into a reset store", async () => {
  const store = apps.useAppsStore(installedPinia()), old = deferred<AxiosResponse>();
  const adapter = client.apiClient.defaults.adapter; let oldConfig: AxiosResponse["config"] | undefined, count = 0;
  client.apiClient.defaults.adapter = async config => {
    if (++count === 1) { oldConfig = config; return old.promise; }
    return { config, data: { success: true, data: runtimeInventory(false) }, headers: {}, status: 200, statusText: "OK" };
  };
  try {
    const first = store.fetchRuntimeInventory("alpha").catch(error => error); await vi.waitFor(() => expect(oldConfig).toBeDefined());
    client.invalidateSessionRequests(); store.$reset(); await store.fetchRuntimeInventory("alpha");
    old.resolve({ config: oldConfig!, data: { success: true, data: runtimeInventory(true) }, headers: {}, status: 200, statusText: "OK" }); await first;
    expect(store.runtimeInventories.alpha).toEqual(runtimeInventory(false)); expect(store.runtimeInventoryLoaded.alpha).toBe(true);
  } finally { client.apiClient.defaults.adapter = adapter; }
});
