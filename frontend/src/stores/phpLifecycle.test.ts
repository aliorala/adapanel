import { afterAll, afterEach, beforeAll, beforeEach, expect, it, vi } from "vitest";
import axios, { type AxiosResponse } from "axios";
import type { PhpVersionRecord, SitePhpAssignment } from "../api/php";

let createPinia: typeof import("pinia")["createPinia"], setActivePinia: typeof import("pinia")["setActivePinia"];
let apiClient: typeof import("../api/client")["apiClient"], invalidateSessionRequests: typeof import("../api/client")["invalidateSessionRequests"];
let resetAllStores: typeof import("./resetAll")["resetAllStores"], usePhpStore: typeof import("./php")["usePhpStore"];
let originalAdapter: typeof import("../api/client")["apiClient"]["defaults"]["adapter"];
let interceptor: number | undefined;
const version = (id: string): PhpVersionRecord => ({ componentId: id, displayName: id, version: "8.3", installDir: `fixture/${id}`, executablePath: `fixture/${id}/php.exe`, status: "Installed", isFastCgiRegistered: true, installedAtUtc: "2026-10-04T00:00:00Z" });
const installed = (id: string) => ({ componentId: id, displayName: id, version: "8.3", status: "Installed", installDir: `fixture/${id}`, serviceName: "", generatedPassword: "", message: "php.install.completed" });
const assignment = (id: string): SitePhpAssignment => ({ siteName: `${id}.test`, componentId: id, displayName: id, version: "8.3", executablePath: `fixture/${id}/php.exe`, handlerName: id, isFastCgiRegistered: true,
  handlerOrder: { measured: true, handlerFound: true, precedesCatchAll: true, handlerIndex: 0, catchAllIndex: 1, blockingHandlerName: "", insertedAtFront: true, ordered: true, shadowedByCatchAll: false, missing: false, unverified: false, noticeKey: "" }, handlerOrderNoticeKey: "" });
const response = (data: unknown, message = "php.install.completed") => ({ data: { success: true, data, message } }) as AxiosResponse;
function deferred<T>() { let resolve!: (value: T) => void, reject!: (error: unknown) => void; const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; }
type Store = ReturnType<typeof usePhpStore>;
const actions = [
  { name: "read", method: "get", busy: "loading", run: (s: Store, _id: string) => s.fetchVersions(), result: (id: string) => response([version(id)]) },
  { name: "install", method: "post", busy: "installingComponentId", run: (s: Store, id: string) => s.installVersion(id), result: (id: string) => response(installed(id)) },
  { name: "assign", method: "post", busy: "assigningComponentId", run: (s: Store, id: string) => s.assignVersion(`${id}.test`, id), result: (id: string) => response(assignment(id)) }
] as const;

beforeAll(async () => {
  // Set the browser environment before Pinia is imported, so its actual
  // development action-grouping plugin is available to the final regression.
  vi.stubEnv("NODE_ENV", "development");
  vi.stubGlobal("window", { location: { pathname: "/panel", hostname: "localhost" } });
  ({ createPinia, setActivePinia } = await import("pinia"));
  ({ apiClient, invalidateSessionRequests } = await import("../api/client"));
  ({ resetAllStores } = await import("./resetAll")); ({ usePhpStore } = await import("./php"));
  originalAdapter = apiClient.defaults.adapter;
});
afterAll(() => { vi.unstubAllEnvs(); vi.unstubAllGlobals(); });
beforeEach(() => {
  setActivePinia(createPinia()); invalidateSessionRequests();
  vi.stubGlobal("window", { location: { pathname: "/panel", hostname: "localhost" } });
  vi.spyOn(apiClient, "get").mockResolvedValue(response([version("current")]));
  vi.spyOn(apiClient, "post").mockImplementation(async (url, data) => response(url === "/php/install" ? installed((data as { componentId: string }).componentId) : assignment((data as { componentId: string }).componentId)));
});
afterEach(() => {
  if (interceptor !== undefined) apiClient.interceptors.response.eject(interceptor);
  interceptor = undefined; apiClient.defaults.adapter = originalAdapter;
  invalidateSessionRequests(); vi.restoreAllMocks(); vi.unstubAllGlobals();
});

it("closes the measurement gate during and after a failed refresh while retaining last known rows", async () => {
  const store = usePhpStore(); await store.fetchVersions(); const pending = deferred<AxiosResponse>();
  vi.mocked(apiClient.get).mockReturnValueOnce(pending.promise);
  const read = store.fetchVersions().catch(error => error);
  expect(store.loading).toBe(true); const measuredWhilePending = store.loaded;
  pending.reject(new Error("unavailable")); await read;
  expect(measuredWhilePending).toBe(false); expect(store.loaded).toBe(false); expect(store.loading).toBe(false);
  expect(store.versions).toEqual([version("current")]);
});
it("distinguishes a failed first read from a measured empty inventory and recovers on retry", async () => {
  const store = usePhpStore(); vi.mocked(apiClient.get).mockRejectedValueOnce(new Error("unavailable"));
  await expect(store.fetchVersions()).rejects.toThrow("unavailable"); expect(store.loaded).toBe(false); expect(store.loading).toBe(false);
  vi.mocked(apiClient.get).mockResolvedValueOnce(response([])); await store.fetchVersions(); expect(store.loaded).toBe(true); expect(store.versions).toEqual([]);
});
it("does not overwrite a newer successful list with a late earlier list", async () => {
  const store = usePhpStore(), old = deferred<AxiosResponse>(); vi.mocked(apiClient.get).mockReturnValueOnce(old.promise);
  const first = store.fetchVersions(); await store.fetchVersions(); old.resolve(response([version("old")])); await first;
  expect(store.versions).toEqual([version("current")]); expect(store.loaded).toBe(true);
});
it("does not let an obsolete failure invalidate a newer successful list", async () => {
  const store = usePhpStore(), old = deferred<AxiosResponse>(); vi.mocked(apiClient.get).mockReturnValueOnce(old.promise);
  const first = store.fetchVersions().catch(error => error); await store.fetchVersions(); old.reject(new Error("old failure")); await first;
  expect(store.versions).toEqual([version("current")]); expect(store.loaded).toBe(true);
});
it("an external mutation invalidates a pending PHP read without losing last known rows or stranding loading", async () => {
  const store = usePhpStore(); await store.fetchVersions(); const old = deferred<AxiosResponse>();
  vi.mocked(apiClient.get).mockReturnValueOnce(old.promise); const reading = store.fetchVersions();
  store.invalidateVersions(); old.resolve(response([version("removed") ])); await reading;
  expect(store.loaded).toBe(false); expect(store.loading).toBe(false); expect(store.versions).toEqual([version("current")]);
});
it("a fresh explicit read reopens measurement after invalidation and survives the older response", async () => {
  const store = usePhpStore(), old = deferred<AxiosResponse>(); vi.mocked(apiClient.get).mockReturnValueOnce(old.promise);
  const reading = store.fetchVersions(); store.invalidateVersions();
  await store.fetchVersions(); expect(store.loaded).toBe(true); expect(store.versions).toEqual([version("current")]);
  old.resolve(response([version("removed")])); await reading;
  expect(store.loaded).toBe(true); expect(store.loading).toBe(false); expect(store.versions).toEqual([version("current")]);
});

for (const action of actions) {
  for (const outcome of ["success", "failure"] as const) {
    it(`rejects obsolete-session ${action.name} ${outcome} after the API await without publishing or starting follow-up reads`, async () => {
      const store = usePhpStore(), old = deferred<AxiosResponse>(); vi.mocked(apiClient[action.method]).mockReturnValueOnce(old.promise);
      const pending = action.run(store, "old").catch(error => error); resetAllStores();
      if (outcome === "success") old.resolve(action.result("old")); else old.reject(new Error("old failure"));
      expect(axios.isCancel(await pending)).toBe(true); expect(store.versions).toEqual([]); expect(store.lastAssignment).toBeNull(); expect(store.loaded).toBe(false);
      expect(apiClient.get).toHaveBeenCalledTimes(action.method === "get" ? 1 : 0);
    });
    for (const reset of [false, true]) {
      it(`older ${action.name} ${outcome} cannot clear a ${reset ? "new session" : "newer request"} busy flag`, async () => {
        const store = usePhpStore(), old = deferred<AxiosResponse>(), next = deferred<AxiosResponse>();
        if (action.name === "install") vi.mocked(apiClient.get).mockResolvedValue(response([version("old"), version("next")]));
        vi.mocked(apiClient[action.method]).mockReturnValueOnce(old.promise).mockReturnValueOnce(next.promise);
        const first = action.run(store, "old").catch(error => error); if (reset) resetAllStores();
        const second = action.run(store, "next");
        if (outcome === "success") old.resolve(action.result("old")); else old.reject(new Error("older failure"));
        await first; const busyBeforeLatestCompletes = store[action.busy];
        next.resolve(action.result("next")); await second;
        expect(busyBeforeLatestCompletes).toBe(action.busy === "loading" ? true : "next");
        expect(store[action.busy]).toBe(action.busy === "loading" ? false : "");
      });
    }
  }
}

for (const duringInstall of [false, true]) {
  it(`a read begun ${duringInstall ? "during" : "before"} installation cannot undo its refreshed inventory`, async () => {
    const store = usePhpStore(), old = deferred<AxiosResponse>(), write = deferred<AxiosResponse>();
    vi.mocked(apiClient.get).mockReturnValueOnce(old.promise); vi.mocked(apiClient.post).mockReturnValueOnce(write.promise);
    let read: Promise<PhpVersionRecord[]>, installing: Promise<string>;
    if (duringInstall) { installing = store.installVersion("current"); read = store.fetchVersions(); }
    else { read = store.fetchVersions(); installing = store.installVersion("current"); }
    write.resolve(response(installed("current"))); await installing; old.resolve(response([version("old")])); await read;
    expect(store.versions).toEqual([version("current")]); expect(store.loaded).toBe(true);
  });
}
it("preserves installation acknowledgement when its one follow-up read fails", async () => {
  const store = usePhpStore(); store.versions = [version("old")]; store.loaded = true;
  const failure = new Error("read unavailable"); vi.mocked(apiClient.get).mockRejectedValueOnce(failure);
  const error = await store.installVersion("php-8.3").catch(value => value);
  expect(error).toMatchObject({ installedMessageKey: "php.install.completed", messageKey: "panel.software.refreshAfterInstallFailed", refreshError: failure });
  expect(store.loaded).toBe(false); expect(store.versions).toEqual([version("old")]);
  expect(apiClient.post).toHaveBeenCalledOnce(); expect(apiClient.get).toHaveBeenCalledOnce();
});
it("keeps the original install failure even when the reconciliation read fails", async () => {
  const store = usePhpStore(); store.loaded = true; store.versions = [version("old")];
  const failure = new Error("install unavailable"); vi.mocked(apiClient.post).mockRejectedValueOnce(failure); vi.mocked(apiClient.get).mockRejectedValueOnce(new Error("read unavailable"));
  await expect(store.installVersion("php-8.3")).rejects.toBe(failure);
  expect(store.loaded).toBe(false); expect(apiClient.post).toHaveBeenCalledOnce(); expect(apiClient.get).toHaveBeenCalledOnce();
  expect(store.versions).toEqual([version("old")]);
});
it("does not replace the latest assignment with an older target's response", async () => {
  const store = usePhpStore(), old = deferred<AxiosResponse>(); vi.mocked(apiClient.post).mockReturnValueOnce(old.promise);
  const first = store.assignVersion("old.test", "old"); await store.assignVersion("current.test", "current");
  old.resolve(response(assignment("old"))); await first; expect(store.lastAssignment).toEqual(assignment("current"));
});
it("preserves normal install and assignment return values", async () => {
  const store = usePhpStore(); expect(await store.installVersion("current")).toBe("php.install.completed");
  expect(store.loaded).toBe(true); expect(store.versions).toEqual([version("current")]);
  expect(await store.assignVersion("current.test", "current")).toEqual(assignment("current")); expect(store.lastAssignment).toEqual(assignment("current"));
});
it("catches a reset after the real HTTP interceptor accepted a response", async () => {
  vi.mocked(apiClient.get).mockRestore();
  apiClient.defaults.adapter = async config => ({ config, data: { success: true, data: [version("private old inventory")] }, status: 200, statusText: "OK", headers: {} });
  interceptor = apiClient.interceptors.response.use(result => { resetAllStores(); return result; });
  const store = usePhpStore(), result = await store.fetchVersions().catch(error => error);
  expect(axios.isCancel(result)).toBe(true); expect(store.loaded).toBe(false); expect(store.versions).toEqual([]);
});
it("keeps request ownership separate for different Pinia instances", async () => {
  const first = usePhpStore(), old = deferred<AxiosResponse>(); vi.mocked(apiClient.get).mockReturnValueOnce(old.promise);
  const reading = first.fetchVersions(); const second = usePhpStore(createPinia()); await second.fetchVersions();
  old.resolve(response([version("first")])); await reading;
  expect(first.versions).toEqual([version("first")]); expect(second.versions).toEqual([version("current")]);
});

it("shares ownership across Pinia's real development action proxies", async () => {
  // Browser devtools wrap each action in a new `this` proxy. A WeakMap keyed by
  // that proxy passes ordinary Node tests yet loses all cross-action ownership.
  const vue = await import("vue"), pinia = await import("pinia");
  const php = await import("./php"), client = await import("../api/client");
  const renderer = vue.createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  const instance = pinia.createPinia(), app = renderer.createApp({ render: () => vue.h("section") }).use(instance);
  const probe = pinia.defineStore("php-proxy-probe", { state: () => ({ count: 0 }), actions: { context(): object { return this; } } })(instance);
  const firstContext = probe.context(), secondContext = probe.context();
  expect(firstContext).not.toBe(secondContext); expect(vue.toRaw(firstContext)).toBe(vue.toRaw(secondContext));
  const store = php.usePhpStore(instance), old = deferred<AxiosResponse>();
  vi.spyOn(client.apiClient, "get").mockReturnValueOnce(old.promise).mockResolvedValue(response([version("new")]));
  vi.spyOn(client.apiClient, "post").mockResolvedValue(response(installed("new")));
  try {
    const reading = store.fetchVersions(); await store.installVersion("new");
    old.resolve(response([version("old")])); await reading;
    expect(store.versions).toEqual([version("new")]); expect(store.loaded).toBe(true); expect(store.loading).toBe(false);
  } finally { client.invalidateSessionRequests(); app.unmount(); }
});

for (const [name, rows] of [
  ["missing target", []],
  ["another installed version", [version("php-8.2")]],
  ["failed target", [{ ...version("php-8.3"), status: "Failed" }]],
  ["pending target", [{ ...version("php-8.3"), status: "Pending" }]]
] as const) {
  it(`keeps the install acknowledgement but rejects complete success when the fresh inventory has ${name}`, async () => {
    const store = usePhpStore(); vi.mocked(apiClient.get).mockResolvedValueOnce(response(rows));
    const error = await store.installVersion("php-8.3").catch(value => value);
    expect(error).toMatchObject({ installedMessageKey: "php.install.completed", refreshError: { messageKey: "panel.php.installationUnconfirmed" } });
    expect(store.loaded).toBe(true); expect(store.versions).toEqual(rows); expect(store.installingComponentId).toBe("");
    expect(apiClient.post).toHaveBeenCalledOnce(); expect(apiClient.get).toHaveBeenCalledOnce();
  });
}
it("does not assume runtime version, path or FastCGI assignment from the install acknowledgement", async () => {
  const store = usePhpStore(), measured = { ...version("php-8.3"), version: "8.3.31", installDir: "different/measured", executablePath: "different/measured/php-cgi.exe", isFastCgiRegistered: false };
  vi.mocked(apiClient.get).mockResolvedValueOnce(response([measured]));
  expect(await store.installVersion("php-8.3")).toBe("php.install.completed"); expect(store.versions).toEqual([measured]); expect(store.loaded).toBe(true);
});
it("does not certify an install with a list invalidated while its final scan was waiting", async () => {
  const store = usePhpStore(), scan = deferred<AxiosResponse>(); vi.mocked(apiClient.get).mockReturnValueOnce(scan.promise);
  const installing = store.installVersion("php-8.3").catch(value => value); await vi.waitFor(() => expect(apiClient.get).toHaveBeenCalledOnce());
  store.invalidateVersions(); scan.resolve(response([version("php-8.3")]));
  expect(await installing).toMatchObject({ installedMessageKey: "php.install.completed", refreshError: { messageKey: "panel.php.installationUnconfirmed" } });
  expect(store.loaded).toBe(false); expect(store.versions).toEqual([]);
});
it.each([false, true])("uses the latest committed inventory if another scan supersedes install verification (target present=%s)", async targetPresent => {
  const store = usePhpStore(), scan = deferred<AxiosResponse>();
  const latest = targetPresent ? [version("php-8.3")] : [];
  vi.mocked(apiClient.get).mockReturnValueOnce(scan.promise).mockResolvedValueOnce(response(latest));
  const installing = store.installVersion("php-8.3").catch(value => value); await vi.waitFor(() => expect(apiClient.get).toHaveBeenCalledOnce());
  await store.fetchVersions(); scan.resolve(response(targetPresent ? [] : [version("php-8.3")]));
  const result = await installing;
  if (targetPresent) expect(result).toBe("php.install.completed");
  else expect(result).toMatchObject({ installedMessageKey: "php.install.completed", refreshError: { messageKey: "panel.php.installationUnconfirmed" } });
  expect(store.loaded).toBe(true); expect(store.versions).toEqual(latest); expect(apiClient.post).toHaveBeenCalledOnce();
});
it.each([false, true])("preserves assignment order warnings for the view (measured=%s)", async measured => {
  const store = usePhpStore(), payload = assignment("php-8.3"), noticeKey = measured ? "aspnet.handler_shadowed" : "aspnet.handler_order_unverified";
  payload.handlerOrder = { ...payload.handlerOrder!, measured, precedesCatchAll: false, ordered: false, shadowedByCatchAll: measured, unverified: !measured, noticeKey };
  payload.handlerOrderNoticeKey = noticeKey; vi.mocked(apiClient.post).mockResolvedValueOnce(response(payload));
  const result = await store.assignVersion("php-8.3.test", "php-8.3");
  expect(result).toEqual(payload); expect(store.lastAssignment).toEqual(payload); expect(store.lastAssignment?.handlerOrderNoticeKey).toBe(noticeKey);
});
