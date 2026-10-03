import { afterAll, afterEach, beforeAll, expect, it, vi } from "vitest";
import type { AxiosResponse, InternalAxiosRequestConfig } from "axios";
import type { ReverseProxyConfiguration } from "../api/proxy";
let vue: typeof import("vue"), pinia: typeof import("pinia"), proxy: typeof import("./proxy"), client: typeof import("../api/client");
let originalAdapter: typeof import("../api/client").apiClient.defaults.adapter;
beforeAll(async () => {
  vi.stubEnv("NODE_ENV", "development"); vi.stubGlobal("window", { location: { hostname: "localhost", pathname: "/panel" } });
  vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ success: true, data: { requestToken: "fixture-token" } }), { status: 200 })));
  vue = await import("vue"); pinia = await import("pinia"); proxy = await import("./proxy"); client = await import("../api/client"); originalAdapter = client.apiClient.defaults.adapter;
});
afterEach(() => { client.apiClient.defaults.adapter = originalAdapter; client.invalidateSessionRequests(); });
afterAll(() => { vi.unstubAllEnvs(); vi.unstubAllGlobals(); });
function installedPinia() {
  const instance = pinia.createPinia(); const renderer = vue.createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  renderer.createApp({ render: () => vue.h("section") }).use(instance); return instance;
}
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(yes => { resolve = yes; }); return { promise, resolve }; }
function configuration(url: string): ReverseProxyConfiguration { return { siteName: "a.test", destinationUrl: url, mountPath: "/", isProxyEnabled: !!url, isArrEnabled: true, hasHtmlLinkRewrite: false, preserveHostHeader: true, reverseRewriteHostInResponseHeaders: false, rewriteRules: [], webConfigPath: "" }; }
function response(config: InternalAxiosRequestConfig, url: string): AxiosResponse { return { config, data: { success: true, data: configuration(url) }, headers: {}, status: 200, statusText: "OK" }; }
const payload = { destinationUrl: "http://127.0.0.1:4000", mountPath: "/", enableHtmlLinkRewrite: false };
it("uses the actual distinct Pinia development action proxies", () => {
  const useProbe = pinia.defineStore("proxy-context-probe", { state: () => ({}), actions: { context(): object { return this; } } }); const store = useProbe(installedPinia());
  const first = store.context(), second = store.context(); expect(first).not.toBe(second); expect(vue.toRaw(first)).toBe(vue.toRaw(second));
});
it.each(["save", "remove"])("does not let an earlier GET replace a %s result in development Pinia", async action => {
  const store = proxy.useProxyStore(installedPinia()), pending = deferred<AxiosResponse>(); let read!: InternalAxiosRequestConfig;
  client.apiClient.defaults.adapter = async config => config.method === "get" ? (read = config, pending.promise) : response(config, action === "save" ? payload.destinationUrl : "");
  const reading = store.fetchConfiguration("a.test").catch(error => error); await vi.waitFor(() => expect(read).toBeDefined());
  await (action === "save" ? store.saveProxy("a.test", payload) : store.removeProxy("a.test")); pending.resolve(response(read, "http://127.0.0.1:3000")); await reading;
  expect(store.getConfiguration("a.test")?.destinationUrl).toBe(action === "save" ? payload.destinationUrl : "");
});
it("keeps the latest GET when older same-site reads return last", async () => {
  const store = proxy.useProxyStore(installedPinia()), pending = deferred<AxiosResponse>(); let read!: InternalAxiosRequestConfig, count = 0;
  client.apiClient.defaults.adapter = async config => ++count === 1 ? (read = config, pending.promise) : response(config, payload.destinationUrl);
  const first = store.fetchConfiguration("a.test").catch(error => error); await vi.waitFor(() => expect(read).toBeDefined()); await store.fetchConfiguration("a.test");
  pending.resolve(response(read, "http://127.0.0.1:3000")); await first; expect(store.getConfiguration("a.test")?.destinationUrl).toBe(payload.destinationUrl);
});
it("serializes proxy and rewrite writes through a shared action boundary", async () => {
  const store = proxy.useProxyStore(installedPinia()), pending = deferred<AxiosResponse>(); let first!: InternalAxiosRequestConfig, calls = 0;
  client.apiClient.defaults.adapter = async config => { calls++; first ??= config; return pending.promise; };
  const saving = store.saveProxy("a.test", payload).catch(error => error); await vi.waitFor(() => expect(first).toBeDefined());
  const rewriting = store.saveRewriteRule("a.test", { name: "r", matchPattern: "old", rewriteUrl: "new", stopProcessing: true, appendQueryString: true, ignoreCase: true }).catch(error => error);
  for (let i = 0; i < 10; i++) await vue.nextTick(); pending.resolve(response(first, payload.destinationUrl)); await Promise.all([saving, rewriting]); expect(calls).toBe(1);
});
it("does not resurrect configuration or clear a newer write after store reset", async () => {
  const store = proxy.useProxyStore(installedPinia()), old = deferred<AxiosResponse>(), fresh = deferred<AxiosResponse>(); let first!: InternalAxiosRequestConfig, second!: InternalAxiosRequestConfig;
  client.apiClient.defaults.adapter = config => !first ? (first = config, old.promise) : (second = config, fresh.promise);
  const savingOld = store.saveProxy("a.test", payload).catch(error => error); await vi.waitFor(() => expect(first).toBeDefined()); store.$reset();
  const savingNew = store.saveProxy("a.test", payload).catch(error => error); await vi.waitFor(() => expect(second).toBeDefined());
  old.resolve(response(first, "http://127.0.0.1:3000")); await savingOld; expect(store.getConfiguration("a.test")).toBeNull(); expect(store.savingSiteName).toBe("a.test");
  fresh.resolve(response(second, payload.destinationUrl)); await savingNew; expect(store.getConfiguration("a.test")?.destinationUrl).toBe(payload.destinationUrl); expect(store.measured["a.test"]).toBe(true);
});
it("does not let a pre-reset GET publish or clear a newer GET's loading state", async () => {
  const store = proxy.useProxyStore(installedPinia()), old = deferred<AxiosResponse>(), fresh = deferred<AxiosResponse>(); let first!: InternalAxiosRequestConfig, second!: InternalAxiosRequestConfig;
  client.apiClient.defaults.adapter = config => !first ? (first = config, old.promise) : (second = config, fresh.promise);
  const readingOld = store.fetchConfiguration("a.test").catch(error => error); await vi.waitFor(() => expect(first).toBeDefined()); store.$reset();
  const readingNew = store.fetchConfiguration("a.test").catch(error => error); await vi.waitFor(() => expect(second).toBeDefined());
  old.resolve(response(first, "http://127.0.0.1:3000")); await readingOld;
  expect(store.getConfiguration("a.test")).toBeNull(); expect(store.loadingSiteName).toBe("a.test"); expect(store.measured["a.test"]).not.toBe(true);
  fresh.resolve(response(second, payload.destinationUrl)); await readingNew; expect(store.measured["a.test"]).toBe(true); expect(store.loadingSiteName).toBe("");
});
it.each([true, false])("preserves newer measured state after an older response with success=%s", async success => {
  const store = proxy.useProxyStore(installedPinia()), old = deferred<AxiosResponse>(); let first!: InternalAxiosRequestConfig, count = 0;
  client.apiClient.defaults.adapter = async config => ++count === 1 ? (first = config, old.promise) : response(config, payload.destinationUrl);
  const readingOld = store.fetchConfiguration("a.test").catch(error => error); await vi.waitFor(() => expect(first).toBeDefined()); await store.fetchConfiguration("a.test");
  old.resolve({ ...response(first, "http://127.0.0.1:3000"), data: { success, data: configuration("http://127.0.0.1:3000") } }); await readingOld;
  expect(store.measured["a.test"]).toBe(true); expect(store.getConfiguration("a.test")?.destinationUrl).toBe(payload.destinationUrl);
});
