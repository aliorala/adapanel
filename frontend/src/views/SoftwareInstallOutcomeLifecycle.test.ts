import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createPinia } from "pinia";
import { AxiosError, type AxiosResponse, type InternalAxiosRequestConfig } from "axios";
import { createRenderer, h, nextTick, ssrContextKey, type App, type Component } from "vue";
import SoftwareView from "./SoftwareView.vue";
import { apiClient, invalidateSessionRequests } from "../api/client";
import { extractApiMessage, useAuthStore } from "../stores/auth";
import { useSoftwareStore, type ComponentInstallResult, type SoftwareCatalog } from "../stores/software";

vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key }) }));
vi.mock("./AppStoreView.vue", () => ({ default: { render: () => null } }));
vi.mock("../components/ConfirmDialog.vue", () => ({ useConfirm: () => ({ confirm: async (options: { action?: () => Promise<void> }) => { await options.action?.(); return true; } }) }));

type State = {
  activeWorkspace: string; notice: string; noticeError: boolean; noticeDetails: string; canWrite: boolean;
  installationResult: { kind: "install" | "update" | "defaults"; results: ComponentInstallResult[]; refreshError: string } | null;
  installComponent(id: string): Promise<void>; installDefaultStack(): Promise<void>; refreshCatalog(): Promise<void>;
};
const component = (id: string) => ({ id, displayName: id, version: "2", type: "zip", downloadUrl: "", sha256: "", installDir: `fixture/${id}`, postInstall: [], dependencies: [], serviceName: id, silentArgs: "", note: "", default: true });
const result = (id = "nginx", status = "Installed"): ComponentInstallResult => ({ componentId: id, displayName: id, version: "2", status, installDir: `fixture/${id}`, serviceName: id, generatedPassword: status === "Installed" ? "fixture-secret-never-in-notice" : "", message: status === "Failed" ? "fixture.install_failed" : "software.install.completed" });
const session = () => ({ isAuthenticated: true, requiresSetupWizard: false, user: { id: "admin-a", userName: "admin", email: "admin@example.test", role: "Administrator", preferredLanguage: "en", isTwoFactorEnabled: false, isActive: true, lastLoginAtUtc: null } });
const reply = (config: InternalAxiosRequestConfig, data: unknown, success = true): AxiosResponse => ({ config, data: { success, data, message: "software.install.completed", code: 200 }, status: 200, statusText: "OK", headers: {} });
const failure = (config: InternalAxiosRequestConfig, message = "fixture.catalog_failed") => new AxiosError(message, "ERR_BAD_RESPONSE", config, undefined, { ...reply(config, null), status: 503, data: { success: false, message } });
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(yes => { resolve = yes; }); return { promise, resolve }; }
let app: App | undefined, calls: InternalAxiosRequestConfig[], installedResults: ComponentInstallResult[], failCatalog: boolean;
let installReply: (config: InternalAxiosRequestConfig) => Promise<AxiosResponse>;
let catalogReply: ((config: InternalAxiosRequestConfig) => Promise<AxiosResponse>) | undefined;
const originalAdapter = apiClient.defaults.adapter;
const posts = () => calls.filter(c => c.method === "post" && c.url?.startsWith("/software/install"));
const reads = () => calls.filter(c => c.method === "get" && c.url === "/software");
const catalog = (): SoftwareCatalog => ({ manifest: [component("nginx"), component("redis")], defaults: [component("nginx"), component("redis")], installed: installedResults.map(item => ({ ...item, id: `${item.componentId}-record`, type: "Zip", lastError: "", isSystemDetected: false, detectionSource: "", availableVersion: "2", updateAvailable: false, versionCheckSupported: true, versionCheckFailed: false, installedAtUtc: "2026-10-04T00:00:00Z" })) });
async function flush() { for (let n = 0; n < 32; n++) await nextTick(); }
async function mount() {
  const pinia = createPinia(), auth = useAuthStore(pinia), store = useSoftwareStore(pinia); auth.session = session();
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  app = renderer.createApp({ ...SoftwareView, render: () => h("section") } as Component).use(pinia); app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: State }).setupState; state.activeWorkspace = "components";
  await vi.waitFor(() => expect(store.loaded).toBe(true)); await flush(); return { state, auth, store };
}
beforeEach(() => {
  invalidateSessionRequests(); calls = []; installedResults = []; failCatalog = false; catalogReply = undefined;
  vi.stubGlobal("window", { location: { pathname: "/panel", hostname: "localhost" } });
  vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ success: true, data: { requestToken: "fixture-only" } }), { status: 200 })));
  installReply = async config => { installedResults = config.url === "/software/install-defaults" ? [result(), result("redis")] : [result(JSON.parse(config.data).componentId)]; return reply(config, config.url === "/software/install-defaults" ? installedResults : installedResults[0]); };
  apiClient.defaults.adapter = async config => {
    calls.push(config);
    if (config.method === "post" && config.url?.startsWith("/software/install")) return installReply(config);
    if (config.url === "/software") { if (catalogReply) return catalogReply(config); if (failCatalog) throw failure(config); return reply(config, catalog()); }
    if (config.url === "/software/running") return reply(config, { running: [], interrupted: [], serverTimeUtc: "2026-10-04T00:00:00Z" });
    if (config.url === "/auth/session") return reply(config, session());
    if (config.url === "/software/operations/progress") return reply(config, { title: "install", running: true, completed: false, failed: false, error: "", steps: [], startedAtUtc: null });
    throw new Error(`Unexpected request ${config.method} ${config.url}`);
  };
});
afterEach(() => { app?.unmount(); app = undefined; apiClient.defaults.adapter = originalAdapter; invalidateSessionRequests(); vi.restoreAllMocks(); vi.unstubAllGlobals(); });

it.each([false, true])("keeps measured %s installation credentials after catalog failure; retry only reads", async defaults => {
  const { state, store } = await mount();
  installReply = async config => { installedResults = defaults ? [result(), result("redis")] : [result()]; failCatalog = true; return reply(config, defaults ? installedResults : installedResults[0]); };
  await (defaults ? state.installDefaultStack() : state.installComponent("nginx"));
  expect(posts()).toHaveLength(1); expect(store.loaded).toBe(false); expect(state.canWrite).toBe(false);
  expect(state.installationResult?.results).toEqual(installedResults);
  expect(state.installationResult?.refreshError).toBe("panel.software.refreshAfterInstallFailed");
  expect(state.notice).toContain("panel.software.refreshAfterInstallFailed"); expect(state.noticeError).toBe(true);
  expect(`${state.notice}\n${state.noticeDetails}`).not.toContain("fixture-secret");
  const readCount = reads().length; failCatalog = false; await state.refreshCatalog();
  expect(reads()).toHaveLength(readCount + 1); expect(posts()).toHaveLength(1); expect(state.canWrite).toBe(true);
  expect(state.installationResult?.refreshError).toBe(""); expect(state.installationResult?.results[0]?.generatedPassword).toBe(result().generatedPassword);
});
it("preserves the completed and failed default rows separately when catalog refresh fails", async () => {
  const { state } = await mount();
  installReply = async config => { failCatalog = true; return reply(config, [result(), result("redis", "Failed")]); };
  await state.installDefaultStack();
  expect(state.installationResult?.results.map(row => row.status)).toEqual(["Installed", "Failed"]);
  expect(state.installationResult?.results[0]?.generatedPassword).toBe(result().generatedPassword);
  expect(state.notice).toContain("panel.software.defaultsPartial"); expect(state.notice).not.toContain("software.defaults.completed");
  expect(state.noticeError).toBe(true);
});
it.each(["Failed", "Pending", "Skipped"])("shared callers cannot mistake %s for a completed installation", async status => {
  const { store } = await mount(); installReply = async config => reply(config, result("nginx", status));
  const error = await store.installComponent("nginx").catch(error => error);
  expect(error).toBeInstanceOf(Error); expect(error.results?.[0]?.status).toBe(status);
  expect(store.installingComponentId).toBe("");
});
it.each([false, true])("typed catalog failure preserves validated %s POST results for other callers", async defaults => {
  const { store } = await mount();
  installReply = async config => { failCatalog = true; return reply(config, defaults ? [result(), result("redis")] : result()); };
  const error = await (defaults ? store.installDefaultStack() : store.installComponent("nginx")).catch(error => error);
  expect(error).toBeInstanceOf(Error); expect(extractApiMessage(error)).toBe("panel.software.refreshAfterInstallFailed");
  expect(error.results?.[0]).toEqual(result()); expect(error.refreshError).toBeInstanceOf(AxiosError);
  expect(posts()).toHaveLength(1); expect(reads()).toHaveLength(2);
});
it.each(["false envelope", "missing arrays"])("treats a %s follow-up catalog as an unconfirmed refresh, preserving the POST result", async kind => {
  const { state, store } = await mount();
  installReply = async config => { installedResults = [result()]; catalogReply = async read => reply(read, kind === "missing arrays" ? {} : catalog(), kind !== "false envelope"); return reply(config, result()); };
  await state.installComponent("nginx");
  expect(store.loaded).toBe(false); expect(state.installationResult?.results).toEqual([result()]);
  expect(state.installationResult?.refreshError).toBe("panel.software.refreshAfterInstallFailed"); expect(state.noticeError).toBe(true);
});
it.each([
  ["false envelope", result(), false], ["wrong component", result("redis"), true], ["unknown status", result("nginx", "Unknown"), true],
  ["missing credentials field", { ...result(), generatedPassword: undefined }, true], ["invalid message", { ...result(), message: null }, true]
])("rejects %s without publishing a result or inferring success from catalog", async (_name, value, success) => {
  const { state } = await mount(); installReply = async config => { installedResults = [result()]; return reply(config, value, success as boolean); };
  await state.installComponent("nginx");
  expect(state.noticeError).toBe(true); expect(state.notice).toBe("panel.software.resultUnconfirmed"); expect(state.installationResult ?? null).toBeNull();
});
it.each([[], [result()], [result(), result()], [result(), result("foreign")]].map(values => [values]))("rejects incomplete/duplicate/foreign default result identities: %j", async values => {
  const { state } = await mount(); installReply = async config => { installedResults = [result(), result("redis")]; return reply(config, values); };
  await state.installDefaultStack();
  expect(state.noticeError).toBe(true); expect(state.notice).toBe("panel.software.resultUnconfirmed"); expect(state.installationResult ?? null).toBeNull();
});
it("preserves an HTTP install error even if reconciliation finds a component", async () => {
  const { state } = await mount(); installReply = async config => { installedResults = [result()]; throw failure(config, "fixture.install_failed"); };
  await state.installComponent("nginx"); expect(state.notice).toBe("fixture.install_failed"); expect(state.noticeError).toBe(true); expect(state.installationResult ?? null).toBeNull();
});
it.each([false, true])("publishes normal %s measured success with an optional empty password", async defaults => {
  const { state } = await mount(); installReply = async config => { installedResults = (defaults ? [result(), result("redis")] : [result()]).map(row => ({ ...row, generatedPassword: "" })); return reply(config, defaults ? installedResults : installedResults[0]); };
  await (defaults ? state.installDefaultStack() : state.installComponent("nginx"));
  expect(state.noticeError).toBe(false); expect(state.installationResult?.results).toEqual(installedResults);
});
it.each(["session", "workspace", "unmount"])("does not publish old credentials after %s departure during follow-up GET", async departure => {
  const { state, auth } = await mount(), pending = deferred<AxiosResponse>(); let read!: InternalAxiosRequestConfig;
  catalogReply = config => { read = config; return pending.promise; };
  const installing = state.installComponent("nginx"); await vi.waitFor(() => expect(read).toBeDefined());
  if (departure === "session") await auth.fetchSession(); else if (departure === "workspace") state.activeWorkspace = "applications"; else { app?.unmount(); app = undefined; }
  pending.resolve(reply(read, catalog())); await installing;
  expect(state.installationResult ?? null).toBeNull(); expect(state.notice).not.toBe("software.install.completed");
});
it("retires an already displayed installation secret after a real session refresh", async () => {
  const { state, auth } = await mount(); await state.installComponent("nginx"); expect(state.installationResult?.results[0]?.generatedPassword).toBe(result().generatedPassword);
  await auth.fetchSession(); expect(state.installationResult ?? null).toBeNull();
});
