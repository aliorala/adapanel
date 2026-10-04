import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createPinia } from "pinia";
import { createI18n } from "vue-i18n";
import { createRenderer, createSSRApp, h, nextTick, ssrContextKey, type App, type Component } from "vue";
import { renderToString } from "@vue/server-renderer";
import { type AxiosResponse, type InternalAxiosRequestConfig } from "axios";
import SoftwareView from "./SoftwareView.vue";
import { apiClient, invalidateSessionRequests } from "../api/client";
import { useAuthStore } from "../stores/auth";
import { SoftwareInstallationRefreshError, useSoftwareStore, type SoftwareCatalog, type ComponentManifestItem, type InstalledComponent } from "../stores/software";
import { safeMessageCompiler } from "../i18n";
import en from "../i18n/en.json";

vi.mock("./AppStoreView.vue", () => ({ default: { render: () => null } }));
vi.mock("../components/ConfirmDialog.vue", () => ({ useConfirm: () => ({ confirm: vi.fn() }) }));

type State = { activeWorkspace: string; search: string; canWrite: boolean; notice: string; noticeError: boolean;
  refreshCatalog(): Promise<void>; checkForUpdates(): Promise<void>; installDefaultStack(): Promise<void> };
const manifest = (id = "nginx"): ComponentManifestItem => ({ id, displayName: "Catalog runtime", version: "1", type: "zip", downloadUrl: "", sha256: "", installDir: "", postInstall: [], dependencies: [], serviceName: "", silentArgs: "", note: "", default: false });
const installed = (extra: Partial<InstalledComponent> = {}): InstalledComponent => ({ id: "row-nginx", componentId: "nginx", displayName: "Catalog runtime", version: "1", type: "Zip", installDir: "", serviceName: "", status: "Installed", lastError: "", generatedPassword: "", isSystemDetected: false, detectionSource: "", availableVersion: "", updateAvailable: false, versionCheckSupported: true, versionCheckFailed: false, installedAtUtc: "0001-01-01T00:00:00", ...extra });
const catalog = (): SoftwareCatalog => ({ manifest: [manifest()], defaults: [], installed: [installed()] });
const envelope = (data: unknown, success = true) => ({ success, data, code: 200, message: "" });
const response = (config: InternalAxiosRequestConfig, data: unknown): AxiosResponse => ({ config, status: 200, statusText: "OK", headers: {}, data });
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(yes => { resolve = yes; }); return { promise, resolve }; }
const i18n = () => createI18n({ legacy: false, locale: "en", fallbackLocale: false, messageCompiler: safeMessageCompiler, messages: { en } });
let app: App | undefined, read: (config: InternalAxiosRequestConfig) => Promise<AxiosResponse>, check: (config: InternalAxiosRequestConfig) => Promise<AxiosResponse>;
let calls: InternalAxiosRequestConfig[];
const originalAdapter = apiClient.defaults.adapter;
async function flush() { for (let n = 0; n < 40; n++) await nextTick(); }
async function mount() {
  const pinia = createPinia(), store = useSoftwareStore(pinia), auth = useAuthStore(pinia);
  auth.session = { isAuthenticated: true, requiresSetupWizard: false, user: { id: "admin", userName: "admin", email: "admin@example.test", role: "Administrator", preferredLanguage: "en", isActive: true, isTwoFactorEnabled: false, lastLoginAtUtc: null } };
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  app = renderer.createApp({ ...SoftwareView, render: () => h("section") } as Component).use(pinia).use(i18n()); app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: State }).setupState;
  state.activeWorkspace = "components"; await flush();
  return { state, store, html: () => renderToString(createSSRApp({ ...SoftwareView, setup: () => state } as Component).use(i18n())) };
}
beforeEach(() => {
  invalidateSessionRequests(); calls = [];
  vi.stubGlobal("window", { location: { pathname: "/panel", hostname: "localhost" } });
  vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify(envelope({ requestToken: "fixture-only" })), { status: 200 })));
  read = async config => response(config, envelope(catalog())); check = read;
  apiClient.defaults.adapter = async config => {
    calls.push(config);
    if (config.url === "/software") return read(config);
    if (config.url === "/software/updates/check") return check(config);
    if (config.url === "/software/running") return response(config, envelope({ running: [], interrupted: [], serverTimeUtc: "2026-10-04T00:00:00Z" }));
    if (config.url === "/software/install") return response(config, envelope({ componentId: "nginx", displayName: "Catalog runtime", version: "1", status: "Installed", installDir: "", serviceName: "", generatedPassword: "DEMO-ONLY", message: "software.install.completed" }));
    throw new Error(`Unexpected ${config.method} ${config.url}`);
  };
});
afterEach(() => { app?.unmount(); app = undefined; apiClient.defaults.adapter = originalAdapter; invalidateSessionRequests(); vi.restoreAllMocks(); vi.unstubAllGlobals(); });

const malformedRows: [string, () => unknown][] = [
  ["null installed row", () => ({ ...catalog(), installed: [null] })],
  ["manifest row without identity", () => ({ ...catalog(), manifest: [{ ...manifest(), id: undefined }] })],
  ["numeric searchable name", () => ({ ...catalog(), manifest: [{ ...manifest(), displayName: 7 }] })],
  ["installed row without status", () => ({ ...catalog(), installed: [{ ...installed(), status: null }] })],
  ["conflicting installed identity", () => ({ ...catalog(), installed: [installed(), installed({ id: "other-row", status: "Failed" })] })],
  ["null default row", () => ({ ...catalog(), defaults: [null] })]
];
it.each(malformedRows)("keeps the last renderable catalog after GET returns a %s", async (_name, value) => {
  const view = await mount(), previous = view.store.catalog;
  read = async config => response(config, envelope(value())); view.state.search = "catalog";
  await view.state.refreshCatalog();
  await expect(view.html()).resolves.toContain("Catalog runtime");
  expect(view.store.catalog).toBe(previous); expect(view.store.loaded).toBe(false); expect(view.state.canWrite).toBe(false);
  expect(view.state.noticeError).toBe(true); expect(view.state.notice).not.toBe(en["panel.software.refreshed"]);
  const before = calls.length; await view.state.installDefaultStack(); expect(calls).toHaveLength(before);
  read = async config => response(config, envelope(catalog())); await view.state.refreshCatalog();
  expect(view.store.loaded).toBe(true); expect(view.state.canWrite).toBe(true);
});
it.each(["false envelope", "missing arrays", "string support flag", "string detected flag", "null failure flag", "string update flag"])("does not announce no updates after a %s update-check response", async kind => {
  const view = await mount(), previous = view.store.catalog;
  const value = catalog();
  if (kind === "string support flag") (value.installed[0] as unknown as Record<string, unknown>).versionCheckSupported = "false";
  if (kind === "string detected flag") (value.installed[0] as unknown as Record<string, unknown>).isSystemDetected = "false";
  if (kind === "null failure flag") (value.installed[0] as unknown as Record<string, unknown>).versionCheckFailed = null;
  if (kind === "string update flag") (value.installed[0] as unknown as Record<string, unknown>).updateAvailable = "false";
  check = async config => response(config, envelope(kind === "missing arrays" ? {} : value, kind !== "false envelope"));
  await view.state.checkForUpdates();
  expect(view.state.noticeError).toBe(true); expect(view.state.notice).not.toBe(en["panel.software.updatesNone"]);
  expect(view.store.catalog).toBe(previous); expect(view.store.loaded).toBe(false); expect(view.state.canWrite).toBe(false);
  await expect(view.html()).resolves.toContain("Catalog runtime");
});
it("applies the same nested boundary to update checks as to ordinary reads", async () => {
  const view = await mount(), previous = view.store.catalog;
  check = async config => response(config, envelope({ ...catalog(), installed: [null] }));
  await view.state.checkForUpdates();
  await expect(view.html()).resolves.toContain("Catalog runtime"); expect(view.store.catalog).toBe(previous); expect(view.store.loaded).toBe(false);
});
it("preserves a verified install and its password when its follow-up catalog has a malformed nested row", async () => {
  const { store } = await mount(), previous = store.catalog;
  read = async config => response(config, envelope({ ...catalog(), installed: [{ ...installed(), status: null }] }));
  const error = await store.installComponent("nginx").catch(error => error);
  expect(error).toBeInstanceOf(SoftwareInstallationRefreshError); expect(error.results[0].generatedPassword).toBe("DEMO-ONLY");
  expect(store.catalog).toBe(previous); expect(store.loaded).toBe(false);
  expect(calls.filter(config => config.url === "/software/install")).toHaveLength(1);
});
it.each(["GET", "check"])("an older malformed %s cannot erase a newer measured catalog or close its gate", async kind => {
  const { store } = await mount(), delayed = deferred<AxiosResponse>(); let oldConfig!: InternalAxiosRequestConfig;
  const hold = (config: InternalAxiosRequestConfig) => { oldConfig = config; return delayed.promise; };
  if (kind === "GET") read = hold; else check = hold;
  const old = (kind === "GET" ? store.fetchCatalog() : store.checkForUpdates()).catch(error => error);
  await vi.waitFor(() => expect(oldConfig).toBeDefined());
  read = async config => response(config, envelope({ ...catalog(), installed: [installed({ version: "2" })] }));
  await store.fetchCatalog(); const latest = store.catalog;
  delayed.resolve(response(oldConfig, envelope({ ...catalog(), installed: [null] }))); await old;
  expect(store.catalog).toEqual(latest); expect(store.loaded).toBe(true); expect(store.loading).toBe(false); expect(store.checkingUpdates).toBe(false);
});
it.each(["GET", "check"])("never publishes a validated %s response accepted just before session invalidation", async kind => {
  const { store } = await mount(); let interceptor: number | undefined;
  try {
    interceptor = apiClient.interceptors.response.use(result => { if (result.config.url === (kind === "GET" ? "/software" : "/software/updates/check")) invalidateSessionRequests(); return result; });
    const result = await (kind === "GET" ? store.fetchCatalog() : store.checkForUpdates()).catch(error => error);
    expect(result).toHaveProperty("code", "ERR_CANCELED"); expect(store.loaded).toBe(false);
  } finally { if (interceptor !== undefined) apiClient.interceptors.response.eject(interceptor); }
});
it.each(["GET", "check"])("accepts a genuinely empty %s catalog", async kind => {
  const view = await mount(); const empty = async (config: InternalAxiosRequestConfig) => response(config, envelope({ manifest: [], defaults: [], installed: [] }));
  if (kind === "GET") { read = empty; await view.state.refreshCatalog(); } else { check = empty; await view.state.checkForUpdates(); }
  expect(view.store.loaded).toBe(true); expect(view.state.noticeError).toBe(false);
  await expect(view.html()).resolves.toContain(en["panel.software.noItems"]);
});
it.each([
  { version: "1", availableVersion: "" },
  { version: "", availableVersion: "2" },
  { version: "1", availableVersion: "  " }
])("does not call a supported component current without both version measurements: %j", async versions => {
  const view = await mount();
  check = async config => response(config, envelope({ ...catalog(), installed: [installed(versions)] }));
  await view.state.checkForUpdates();
  expect(view.store.loaded).toBe(true); expect(view.store.incompleteCheckCount).toBe(1);
  expect(view.state.notice).not.toBe(en["panel.software.updatesNone"]); expect(view.state.noticeError).toBe(true);
  expect(view.state.notice).toContain("1");
});
it("preserves a measured no-update result with nonempty installed and available versions", async () => {
  const view = await mount(); check = async config => response(config, envelope({ ...catalog(), installed: [installed({ availableVersion: "1" })] }));
  await view.state.checkForUpdates(); expect(view.store.loaded).toBe(true); expect(view.state.notice).toBe(en["panel.software.updatesNone"]); expect(view.state.noticeError).toBe(false);
});
it("normalizes optional null manifest text while preserving usable component identity and empty arrays", async () => {
  const value = { ...catalog(), manifest: [{ ...manifest(), version: null, sha256: null, serviceName: null, silentArgs: null, note: null, processName: null }] };
  read = async config => response(config, envelope(value));
  const view = await mount(); view.state.search = "catalog";
  expect(view.store.loaded).toBe(true); expect(view.store.catalog?.manifest[0].id).toBe("nginx");
  await expect(view.html()).resolves.toContain("Catalog runtime");
});
it.each([
  { status: "Failed", version: "", lastError: "fixture failure", updateAvailable: false },
  { status: "Pending", version: "", updateAvailable: false },
  { version: "vendor-custom-build", availableVersion: "newer-build", versionCheckSupported: false, updateAvailable: true },
  { availableVersion: "2", versionCheckFailed: true, updateAvailable: true },
  { version: "", versionCheckSupported: false, isSystemDetected: true, detectionSource: "Windows Feature" }
])("preserves legitimate backend empty/unknown/default metadata %j", async extra => {
  const value = { ...catalog(), installed: [installed(extra)] }; read = async config => response(config, envelope(value));
  const view = await mount(); expect(view.store.loaded).toBe(true); expect(view.store.catalog).toEqual(value);
  await expect(view.html()).resolves.toContain("Catalog runtime");
});
