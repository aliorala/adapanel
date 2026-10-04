import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createPinia } from "pinia";
import { AxiosError, type AxiosResponse, type InternalAxiosRequestConfig } from "axios";
import { createRenderer, h, nextTick, ssrContextKey, type App, type Component } from "vue";
import SoftwareView from "./SoftwareView.vue";
import { apiClient, invalidateSessionRequests } from "../api/client";
import { extractApiMessage, useAuthStore } from "../stores/auth";
import { useSoftwareStore, type SoftwareCatalog } from "../stores/software";
import type { ConfirmOptions } from "../components/ConfirmDialog.vue";

vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key }) }));
vi.mock("./AppStoreView.vue", () => ({ default: { render: () => null } }));
const { confirm } = vi.hoisted(() => ({ confirm: vi.fn() }));
vi.mock("../components/ConfirmDialog.vue", () => ({ useConfirm: () => ({ confirm }) }));

type State = { activeWorkspace: string; notice: string; noticeError: boolean; noticeDetails: string; canWrite: boolean;
  removeComponent(id: string): Promise<void>; refreshCatalog(): Promise<void> };
const catalog = (present = true): SoftwareCatalog => ({
  manifest: [{ id: "nginx", displayName: "Nginx", version: "2", type: "zip", downloadUrl: "", sha256: "", installDir: "fixture/nginx", postInstall: [], dependencies: [], serviceName: "nginx", silentArgs: "", note: "", default: false }], defaults: [],
  installed: present ? [{ id: "nginx-record", componentId: "nginx", displayName: "Nginx", version: "1", type: "Zip", installDir: "fixture/nginx", serviceName: "nginx", status: "Installed", lastError: "", generatedPassword: "", isSystemDetected: false, detectionSource: "", availableVersion: "2", updateAvailable: true, versionCheckSupported: true, versionCheckFailed: false, installedAtUtc: "2026-10-04T00:00:00Z" }] : []
});
const session = () => ({ isAuthenticated: true, requiresSetupWizard: false, user: { id: "admin-a", userName: "admin", email: "admin@example.test", role: "Administrator", preferredLanguage: "en", isTwoFactorEnabled: false, isActive: true, lastLoginAtUtc: null } });
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(yes => { resolve = yes; }); return { promise, resolve }; }
const reply = (config: InternalAxiosRequestConfig, data: unknown, message = "", success = true): AxiosResponse => ({ config, data: { success, data, message, code: 200 }, status: 200, statusText: "OK", headers: {} });
const readFailure = (config: InternalAxiosRequestConfig) => new AxiosError("fixture read failed", "ERR_BAD_RESPONSE", config, undefined, { ...reply(config, null), status: 503, data: { success: false, message: "fixture.catalog_failed" } });
let app: App | undefined, calls: InternalAxiosRequestConfig[], removed: boolean, failCatalog: boolean;
let removeReply: (config: InternalAxiosRequestConfig) => Promise<AxiosResponse>;
const originalAdapter = apiClient.defaults.adapter;
async function flush() { for (let n = 0; n < 32; n++) await nextTick(); }
const deletes = () => calls.filter(config => config.method === "delete");
const catalogReads = () => calls.filter(config => config.url === "/software" && config.method === "get");
async function mount() {
  const pinia = createPinia(), auth = useAuthStore(pinia), store = useSoftwareStore(pinia); auth.session = session();
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  app = renderer.createApp({ ...SoftwareView, render: () => h("section") } as Component).use(pinia); app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: State }).setupState; state.activeWorkspace = "components";
  await vi.waitFor(() => expect(store.loaded).toBe(true)); await flush(); return { state, auth, store };
}
beforeEach(() => {
  invalidateSessionRequests(); calls = []; removed = false; failCatalog = false;
  confirm.mockReset(); confirm.mockImplementation(async (options: ConfirmOptions) => { await options.action?.(); return true; });
  vi.stubGlobal("window", { location: { pathname: "/panel", hostname: "localhost" } });
  vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ success: true, data: { requestToken: "fixture-only" } }), { status: 200 })));
  removeReply = async config => { removed = true; return reply(config, { componentId: "nginx" }, "software.remove.completed"); };
  apiClient.defaults.adapter = async config => {
    calls.push(config);
    if (config.method === "delete") return removeReply(config);
    if (config.url === "/software") { if (failCatalog) throw readFailure(config); return reply(config, catalog(!removed)); }
    if (config.url === "/auth/session") return reply(config, session());
    if (config.url === "/software/running") return reply(config, { running: [], interrupted: [], serverTimeUtc: "2026-10-04T00:00:00Z" });
    throw new Error(`Unexpected request ${config.method} ${config.url}`);
  };
});
afterEach(() => { app?.unmount(); app = undefined; apiClient.defaults.adapter = originalAdapter; invalidateSessionRequests(); vi.restoreAllMocks(); vi.unstubAllGlobals(); });

it("retains cleanup warnings when the server removes the component record", async () => {
  const { state, store } = await mount();
  removeReply = async config => { removed = true; return reply(config, { componentId: "nginx", warnings: ["fixture/nginx/nginx.exe remains locked", "nginx service could not be stopped"] }, "software.remove.partial"); };
  await state.removeComponent("nginx");
  expect(deletes()).toHaveLength(1); expect(store.catalog?.installed).toEqual([]);
  expect(state.notice).toBe("software.remove.partial"); expect(state.noticeError).toBe(true);
  expect(state.noticeDetails).toContain("nginx.exe remains locked"); expect(state.noticeDetails).toContain("service could not be stopped");
});
it("still confirms a complete removal after the fresh catalog no longer contains the component", async () => {
  const { state } = await mount(); await state.removeComponent("nginx");
  expect(deletes()).toHaveLength(1); expect(state.notice).toBe("software.remove.completed"); expect(state.noticeError).toBe(false);
});
it("keeps partial cleanup as a rejected result for shared callers that do not inspect return values", async () => {
  const { store } = await mount();
  removeReply = async config => { removed = true; return reply(config, { componentId: "nginx", warnings: ["fixture locked service"] }, "software.remove.partial"); };
  const error = await store.removeComponent("nginx").catch(error => error);
  expect(extractApiMessage(error)).toBe("software.remove.partial");
  expect(error.result).toEqual({ componentId: "nginx", warnings: ["fixture locked service"] });
  expect(store.loaded).toBe(true); expect(store.catalog?.installed).toEqual([]); expect(store.removingComponentId).toBe("");
});
it.each([false, true])("preserves the measured removal outcome when follow-up catalog fails (partial=%s), and retries only GET", async partial => {
  const { state, store } = await mount();
  removeReply = async config => { removed = true; failCatalog = true; return reply(config, { componentId: "nginx", ...(partial ? { warnings: ["fixture locked file"] } : {}) }, partial ? "software.remove.partial" : "software.remove.completed"); };
  await state.removeComponent("nginx");
  expect(state.notice).toContain(partial ? "software.remove.partial" : "software.remove.completed");
  expect(state.notice).toContain("panel.software.refreshAfterRemoveFailed"); expect(state.noticeError).toBe(true); expect(store.loaded).toBe(false);
  if (partial) expect(state.noticeDetails).toContain("fixture locked file");
  const reads = catalogReads().length; failCatalog = false; await state.refreshCatalog();
  expect(catalogReads()).toHaveLength(reads + 1); expect(deletes()).toHaveLength(1); expect(store.loaded).toBe(true);
});
it.each([
  ["false envelope", { componentId: "nginx" }, false],
  ["wrong target", { componentId: "mysql-8" }, true],
  ["missing target", {}, true],
  ["invalid warnings", { componentId: "nginx", warnings: "locked" }, true],
  ["invalid warning item", { componentId: "nginx", warnings: [null] }, true]
])("does not infer successful removal from the catalog after a %s response", async (_name, data, success) => {
  const { state } = await mount(); removeReply = async config => { removed = true; return reply(config, data, "software.remove.completed", success as boolean); };
  await state.removeComponent("nginx");
  expect(deletes()).toHaveLength(1); expect(state.noticeError).toBe(true); expect(state.notice).toContain("panel.software.resultUnconfirmed");
});
it("does not announce success when the fresh catalog still contains the component", async () => {
  const { state } = await mount(); removeReply = async config => reply(config, { componentId: "nginx" }, "software.remove.completed");
  await state.removeComponent("nginx"); expect(state.noticeError).toBe(true); expect(state.notice).toBe("panel.software.resultUnconfirmed");
});
it("keeps one reserved removal while its confirmation is open", async () => {
  const { state } = await mount(), accepted = deferred<boolean>(); let options!: ConfirmOptions;
  confirm.mockImplementation(async value => { options = value; if (await accepted.promise) await options.action?.(); return true; });
  const first = state.removeComponent("nginx"); await flush(); await state.removeComponent("nginx");
  expect(confirm).toHaveBeenCalledTimes(1); accepted.resolve(true); await first; expect(deletes()).toHaveLength(1);
});
it("does not use a prior session's confirmation after real session reloading", async () => {
  const { state, auth } = await mount(), accepted = deferred<boolean>();
  confirm.mockImplementation(async (options: ConfirmOptions) => { if (await accepted.promise) await options.action?.(); return true; });
  const removing = state.removeComponent("nginx"); await flush(); await auth.fetchSession(); accepted.resolve(true); await removing;
  expect(deletes()).toHaveLength(0);
  await state.refreshCatalog();
  confirm.mockImplementation(async (options: ConfirmOptions) => { await options.action?.(); return true; });
  await state.removeComponent("nginx"); expect(deletes()).toHaveLength(1); expect(state.notice).toBe("software.remove.completed");
});
it("does not publish an old removal notice after a real session refresh while DELETE is pending", async () => {
  const { state, auth } = await mount(), pending = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
  removeReply = config => { request = config; return pending.promise; };
  const removing = state.removeComponent("nginx"); await vi.waitFor(() => expect(request).toBeDefined());
  await auth.fetchSession(); removed = true; pending.resolve(reply(request, { componentId: "nginx" }, "software.remove.completed")); await removing;
  expect(state.notice).not.toContain("software.remove.completed");
});
