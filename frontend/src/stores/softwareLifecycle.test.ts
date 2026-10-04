import { afterEach, beforeEach, expect, it, vi } from "vitest";
import axios, { type AxiosResponse } from "axios";
import { createPinia, setActivePinia } from "pinia";
import { apiClient, invalidateSessionRequests } from "../api/client";
import { resetAllStores } from "./resetAll";
import { useSoftwareStore, type SoftwareCatalog, type ComponentInstallResult } from "./software";
import type { OperationProgress } from "./setup";

const originalAdapter = apiClient.defaults.adapter;
let extraInterceptor: number | undefined;
const catalog = (id: string): SoftwareCatalog => ({ manifest: [{ id } as SoftwareCatalog["manifest"][number]], defaults: [], installed: [] });
const progress = (title: string): OperationProgress => ({ title, running: true, completed: false, failed: false, error: "", steps: [], startedAtUtc: null });
const installed: ComponentInstallResult = { componentId: "fixture", displayName: "Fixture", version: "1", status: "Installed", installDir: "fixture", serviceName: "fixture", generatedPassword: "", message: "installed" };
const response = (data: unknown) => ({ data: { success: true, data } }) as AxiosResponse;
function deferred<T>() { let resolve!: (value: T) => void; let reject!: (error: unknown) => void; const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; }
type Store = ReturnType<typeof useSoftwareStore>;
const actions = [
  { name: "catalog", method: "get", busy: "loading", run: (s: Store, _id: string) => s.fetchCatalog(), value: catalog("old") },
  { name: "progress", method: "get", busy: null, run: (s: Store, _id: string) => s.fetchOperationProgress(), value: progress("old") },
  { name: "updates", method: "post", busy: "checkingUpdates", run: (s: Store, _id: string) => s.checkForUpdates(), value: catalog("old") },
  { name: "install", method: "post", busy: "installingComponentId", run: (s: Store, id: string) => s.installComponent(id), value: installed },
  { name: "defaults", method: "post", busy: "installingDefaults", run: (s: Store, _id: string) => s.installDefaultStack(), value: [installed] },
  { name: "remove", method: "delete", busy: "removingComponentId", run: (s: Store, id: string) => s.removeComponent(id), value: { componentId: "next", warnings: [] } }
] as const;

beforeEach(() => {
  setActivePinia(createPinia());
  vi.stubGlobal("window", { location: { pathname: "/entry" } });
  invalidateSessionRequests();
  vi.spyOn(apiClient, "get").mockImplementation(async url => response(url!.includes("progress") ? progress("current") : catalog("current")));
  vi.spyOn(apiClient, "post").mockImplementation(async url => response(url.includes("updates/check") ? catalog("checked") : url.includes("install-defaults") ? [installed] : installed));
  vi.spyOn(apiClient, "delete").mockImplementation(async url => response({ componentId: decodeURIComponent(url.split("/").slice(-1)[0]!), warnings: [] }));
});
afterEach(() => {
  if (extraInterceptor !== undefined) apiClient.interceptors.response.eject(extraInterceptor);
  extraInterceptor = undefined;
  apiClient.defaults.adapter = originalAdapter;
  invalidateSessionRequests();
  vi.restoreAllMocks(); vi.unstubAllGlobals();
});

for (const action of actions) {
  it(`does not publish or start follow-up reads from an obsolete ${action.name}`, async () => {
    const store = useSoftwareStore(), old = deferred<AxiosResponse>();
    vi.mocked(apiClient[action.method]).mockReturnValueOnce(old.promise);
    const pending = action.run(store, "old").catch(error => error);
    resetAllStores(); old.resolve(response(action.value));
    expect(axios.isCancel(await pending)).toBe(true);
    expect(store.catalog).toBeNull(); expect(store.liveProgress).toBeNull(); expect(store.loaded).toBe(false);
    expect(apiClient.get).toHaveBeenCalledTimes(action.method === "get" ? 1 : 0);
  });
  if (action.busy) {
    for (const reset of [false, true]) {
      it(`an older ${action.name} cannot clear the ${reset ? "next session's" : "newer request's"} busy state`, async () => {
        const store = useSoftwareStore(), old = deferred<AxiosResponse>(), next = deferred<AxiosResponse>();
        vi.mocked(apiClient[action.method]).mockReturnValueOnce(old.promise).mockReturnValueOnce(next.promise);
        const first = action.run(store, "old").catch(error => error);
        if (reset) resetAllStores();
        const second = action.run(store, "next");
        old.reject(reset ? new axios.CanceledError("Session changed") : new Error("earlier failed")); await first;
        expect(store[action.busy]).toBe(action.busy.endsWith("Id") ? "next" : true);
        next.resolve(response(action.value)); await second;
        expect(store[action.busy]).toBe(action.busy.endsWith("Id") ? "" : false);
      });
    }
  }
}

for (const action of actions.filter(a => a.name === "install" || a.name === "defaults")) {
  it(`a cancelled old ${action.name} does not retry a catalog read in the new session`, async () => {
    const store = useSoftwareStore(), old = deferred<AxiosResponse>();
    vi.mocked(apiClient.post).mockReturnValueOnce(old.promise);
    const pending = action.run(store, "fixture").catch(error => error);
    resetAllStores(); old.reject(new axios.CanceledError("Session changed"));
    expect(axios.isCancel(await pending)).toBe(true); expect(apiClient.get).not.toHaveBeenCalled();
  });
}

it("keeps the most recently requested catalog and its loading owner", async () => {
  const store = useSoftwareStore(), old = deferred<AxiosResponse>(), next = deferred<AxiosResponse>();
  vi.mocked(apiClient.get).mockReturnValueOnce(old.promise).mockReturnValueOnce(next.promise);
  const first = store.fetchCatalog(), second = store.fetchCatalog();
  old.resolve(response(catalog("old"))); await first;
  expect(store.catalog).toBeNull(); expect(store.loading).toBe(true);
  next.resolve(response(catalog("new"))); await second;
  expect(store.catalog).toEqual(catalog("new")); expect(store.loaded).toBe(true); expect(store.loading).toBe(false);
});

it("does not replace a fresh catalog with a late earlier read", async () => {
  const store = useSoftwareStore(), old = deferred<AxiosResponse>(); vi.mocked(apiClient.get).mockReturnValueOnce(old.promise);
  const first = store.fetchCatalog(); await store.fetchCatalog(); old.resolve(response(catalog("old"))); await first;
  expect(store.catalog).toEqual(catalog("current"));
});

it("keeps the latest progress snapshot when polls resolve out of order", async () => {
  const store = useSoftwareStore(), old = deferred<AxiosResponse>(); vi.mocked(apiClient.get).mockReturnValueOnce(old.promise);
  const first = store.fetchOperationProgress(); await store.fetchOperationProgress(); old.resolve(response(progress("old"))); await first;
  expect(store.liveProgress).toEqual(progress("current"));
});

it("does not apply a progress poll that belongs to the previous install context", async () => {
  const store = useSoftwareStore(), old = deferred<AxiosResponse>(), install = deferred<AxiosResponse>();
  vi.mocked(apiClient.get).mockReturnValueOnce(old.promise); vi.mocked(apiClient.post).mockReturnValueOnce(install.promise);
  const poll = store.fetchOperationProgress(); const work = store.installComponent("next");
  old.resolve(response(progress("previous install"))); await poll; expect(store.liveProgress).toBeNull();
  install.resolve(response(installed)); await work;
});

it("an update result invalidates a catalog read that started before it", async () => {
  const store = useSoftwareStore(), old = deferred<AxiosResponse>(); vi.mocked(apiClient.get).mockReturnValueOnce(old.promise);
  const read = store.fetchCatalog(); await store.checkForUpdates(); old.resolve(response(catalog("old"))); await read;
  expect(store.catalog).toEqual(catalog("checked"));
});

it("a completed install invalidates reads started while it was pending", async () => {
  const store = useSoftwareStore(), write = deferred<AxiosResponse>(), read = deferred<AxiosResponse>();
  vi.mocked(apiClient.post).mockReturnValueOnce(write.promise); vi.mocked(apiClient.get).mockReturnValueOnce(read.promise);
  const install = store.installComponent("fixture"), earlier = store.fetchCatalog();
  write.resolve(response(installed)); await install; read.resolve(response(catalog("during install"))); await earlier;
  expect(store.catalog).toEqual(catalog("current"));
});

it("reconciles an update snapshot that overlaps an install instead of undoing the install catalog", async () => {
  const store = useSoftwareStore(), old = deferred<AxiosResponse>(); vi.mocked(apiClient.post).mockReturnValueOnce(old.promise);
  const check = store.checkForUpdates(); await store.installComponent("fixture");
  old.resolve(response(catalog("before install"))); await check;
  expect(store.catalog).toEqual(catalog("current"));
});

for (const action of actions.filter(a => a.name === "install" || a.name === "defaults")) {
  it(`does not retry a successful ${action.name} follow-up read after that read fails`, async () => {
    const error = new Error("catalog unavailable"); vi.mocked(apiClient.get).mockRejectedValue(error);
    await expect(action.run(useSoftwareStore(), "fixture")).rejects.toBe(error);
    expect(apiClient.post).toHaveBeenCalledOnce(); expect(apiClient.get).toHaveBeenCalledOnce();
  });
}

it("preserves a failed install's original error while refreshing its failed record", async () => {
  const error = new Error("install failed"); vi.mocked(apiClient.post).mockRejectedValueOnce(error);
  const store = useSoftwareStore(); await expect(store.installComponent("fixture", true)).rejects.toBe(error);
  expect(store.catalog).toEqual(catalog("current")); expect(store.installingComponentId).toBe("");
  expect(apiClient.post).toHaveBeenCalledWith("/software/install", { componentId: "fixture", force: true });
});

it("preserves normal successful installs and removal payloads", async () => {
  const store = useSoftwareStore(); expect(await store.installComponent("fixture")).toEqual(installed);
  expect(await store.installDefaultStack()).toEqual([installed]); await store.removeComponent("fixture + version");
  expect(apiClient.delete).toHaveBeenCalledExactlyOnceWith("/software/fixture%20%2B%20version");
  expect(store.catalog).toEqual(catalog("current")); expect(store.removingComponentId).toBe("");
});

it("does not commit a response already accepted by the HTTP interceptor just before reset", async () => {
  vi.mocked(apiClient.get).mockRestore();
  apiClient.defaults.adapter = async config => ({ config, status: 200, statusText: "OK", headers: {}, data: { data: catalog("old private catalog") } });
  extraInterceptor = apiClient.interceptors.response.use(result => { resetAllStores(); return result; });
  const store = useSoftwareStore(), result = await store.fetchCatalog().catch(error => error);
  expect(axios.isCancel(result)).toBe(true); expect(store.catalog).toBeNull(); expect(store.loaded).toBe(false);
});

it("keeps request ownership separate for different Pinia instances", async () => {
  const firstStore = useSoftwareStore(), first = deferred<AxiosResponse>(); vi.mocked(apiClient.get).mockReturnValueOnce(first.promise);
  const read = firstStore.fetchCatalog(); setActivePinia(createPinia()); const secondStore = useSoftwareStore(); await secondStore.fetchCatalog();
  first.resolve(response(catalog("first store"))); await read;
  expect(firstStore.catalog).toEqual(catalog("first store")); expect(secondStore.catalog).toEqual(catalog("current"));
});

it("closes the measurement gate during and after an unsuccessful catalog refresh", async () => {
  const store = useSoftwareStore(); await store.fetchCatalog();
  const pending = deferred<AxiosResponse>(); vi.mocked(apiClient.get).mockReturnValueOnce(pending.promise);
  const read = store.fetchCatalog().catch(error => error); expect(store.loaded).toBe(false);
  pending.reject(new Error("unreadable")); await read;
  expect(store.loaded).toBe(false); expect(store.catalog).toEqual(catalog("current"));
});

it("an older failed refresh cannot invalidate a newer measured catalog", async () => {
  const store = useSoftwareStore(), old = deferred<AxiosResponse>(); vi.mocked(apiClient.get).mockReturnValueOnce(old.promise);
  const read = store.fetchCatalog().catch(error => error); await store.fetchCatalog();
  old.reject(new Error("obsolete error")); await read;
  expect(store.loaded).toBe(true); expect(store.catalog).toEqual(catalog("current"));
});

it("a failed update check cannot keep an old catalog marked measured", async () => {
  const store = useSoftwareStore(); await store.fetchCatalog();
  vi.mocked(apiClient.post).mockRejectedValueOnce(new Error("unreadable"));
  await store.checkForUpdates().catch(error => error); expect(store.loaded).toBe(false);
});

it("an older failed update check cannot close a later successful measurement gate", async () => {
  const store = useSoftwareStore(), old = deferred<AxiosResponse>(); vi.mocked(apiClient.post).mockReturnValueOnce(old.promise);
  const check = store.checkForUpdates().catch(error => error); await store.fetchCatalog();
  old.reject(new Error("obsolete check failure")); await check; expect(store.loaded).toBe(true);
});

it("reconciles an update snapshot when a later catalog read already measured another state", async () => {
  const store = useSoftwareStore(), old = deferred<AxiosResponse>(); vi.mocked(apiClient.post).mockReturnValueOnce(old.promise);
  const check = store.checkForUpdates(); await store.fetchCatalog();
  old.resolve(response(catalog("outdated check snapshot"))); await check;
  expect(store.catalog).toEqual(catalog("current")); expect(store.loaded).toBe(true);
});

it("does not revive running progress from a poll started before installation completed", async () => {
  const store = useSoftwareStore(), write = deferred<AxiosResponse>(), oldPoll = deferred<AxiosResponse>();
  vi.mocked(apiClient.post).mockReturnValueOnce(write.promise); vi.mocked(apiClient.get).mockReturnValueOnce(oldPoll.promise);
  const install = store.installComponent("fixture"), poll = store.fetchOperationProgress();
  write.resolve(response(installed)); await install; store.liveProgress = null;
  oldPoll.resolve(response(progress("stale running"))); await poll;
  expect(store.liveProgress).toBeNull();
});
