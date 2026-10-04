import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createPinia } from "pinia";
import { createRenderer, h, nextTick, ssrContextKey, type App, type Component } from "vue";
import type { AxiosResponse, InternalAxiosRequestConfig } from "axios";
import BackupView from "./BackupView.vue";
import SiteBackupView from "./SiteBackupView.vue";
import { apiClient, invalidateSessionRequests } from "../api/client";
import { useAuthStore } from "../stores/auth";
import { useSitesStore } from "../stores/sites";
import type { BackupRecord } from "../api/backup";

vi.mock("../router", () => ({ default: { push: vi.fn() } }));
vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key === "panel.backup.resultUnconfirmed" ? "Restore result could not be verified" : key, locale: { value: "en" } }) }));
vi.mock("../components/ConfirmDialog.vue", () => ({ useConfirm: () => ({ confirm: async (options: { action?: () => Promise<void> }) => { await options.action?.(); return true; } }) }));
const record = (id = "one"): BackupRecord => ({ id, name: `${id}.zip`, type: "site", source: "alpha.test", path: "/backups/alpha.zip", format: "zip", createdAtUtc: "2026-10-04T12:00:00Z", size: 100,
  status: "created", targetType: "local", remoteUri: "", encrypted: false, warnings: [], preservedPath: "", chainId: "", chainIndex: 0, backupMode: "full", baseBackupId: "", subscriptionUserId: "", itemCount: 1, localMissing: false, remoteMissing: false, remoteVerifiedUtc: null });
function response(config: InternalAxiosRequestConfig, data: unknown): AxiosResponse { return { config, data, headers: {}, status: 200, statusText: "OK" }; }
function deferred<T>() { let resolve!: (value: T) => void, reject!: (error: unknown) => void; const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; }
async function flush() { for (let i = 0; i < 60; i++) await nextTick(); }
type State = { notice: string; noticeError: boolean; download(backup: BackupRecord): Promise<void>; restore(backup: BackupRecord): Promise<void> };
const originalAdapter = apiClient.defaults.adapter;
let app: App | undefined, calls: InternalAxiosRequestConfig[], click: ReturnType<typeof vi.fn>, revoke: ReturnType<typeof vi.fn>, createUrl: ReturnType<typeof vi.fn>, link: { href: string; download: string; click: ReturnType<typeof vi.fn>; remove: ReturnType<typeof vi.fn> };
let download: (config: InternalAxiosRequestConfig) => Promise<AxiosResponse>;
beforeEach(() => {
  invalidateSessionRequests(); calls = []; click = vi.fn(); revoke = vi.fn(); createUrl = vi.fn(() => "blob:fixture");
  link = { href: "", download: "", click, remove: vi.fn() };
  vi.stubGlobal("window", { location: { pathname: "/panel" }, setTimeout, clearTimeout, setInterval, clearInterval, URL: { createObjectURL: createUrl, revokeObjectURL: revoke } });
  vi.stubGlobal("document", { createElement: vi.fn(() => link), body: { appendChild: vi.fn() } });
  vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ success: true, data: { requestToken: "fixture-token" } }), { status: 200 })));
  download = async config => response(config, new Blob(["fake local archive"], { type: "application/octet-stream" }));
  apiClient.defaults.adapter = async config => {
    calls.push(config); if (config.url?.endsWith("/download")) return download(config);
    const data = config.url === "/backup" ? [record(), record("two")] : config.url === "/automation" ? { settings: {} } : config.url === "/backup/encryption" ? { enabled: false, passwordUsable: false, algorithm: "AES", updatedAtUtc: "" } : config.url?.includes("running") ? { running: [], interrupted: [], serverTimeUtc: "2026-10-04T12:00:00Z" } : [];
    return response(config, { success: true, data });
  };
});
afterEach(() => { app?.unmount(); app = undefined; apiClient.defaults.adapter = originalAdapter; invalidateSessionRequests(); vi.unstubAllGlobals(); });
async function mount(component: typeof BackupView | typeof SiteBackupView) {
  const pinia = createPinia(), auth = useAuthStore(pinia), sites = useSitesStore(pinia);
  auth.session = { isAuthenticated: true, requiresSetupWizard: false, user: { id: "admin-a", role: "Administrator", userName: "admin", email: "admin@example.test", preferredLanguage: "en", isActive: true, isTwoFactorEnabled: false, lastLoginAtUtc: null } };
  sites.selectedSiteName = "alpha.test"; sites.sites = [{ name: "alpha.test" }, { name: "beta.test" }] as never;
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  app = renderer.createApp({ ...component, render: () => h("section") } as Component).use(pinia); app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: State }).setupState; await flush(); state.notice = ""; return { state, auth, sites };
}
for (const [name, component] of [["site", SiteBackupView], ["catalogue", BackupView]] as const) {
  for (const transition of ["actor", "session", "unmount", "epoch"] as const) it(`${name} download never saves a late file after ${transition} changes`, async () => {
    const { state, auth } = await mount(component), gate = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
    download = config => (request = config, gate.promise); const downloading = state.download(record()); await flush();
    if (transition === "actor") auth.session!.user!.id = "admin-b";
    else if (transition === "session") auth.session = { ...auth.session!, user: { ...auth.session!.user! } };
    else if (transition === "epoch") invalidateSessionRequests();
    else { app!.unmount(); app = undefined; }
    gate.resolve(response(request, new Blob(["archive"]))); await downloading;
    expect(createUrl).not.toHaveBeenCalled(); expect(click).not.toHaveBeenCalled(); expect(state.notice).toBe("");
  });
  it(`${name} download reserves one request and preserves the selected filename`, async () => {
    const { state } = await mount(component), gate = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
    download = config => (request = config, gate.promise); const first = state.download(record()); await flush(); const second = state.download(record("two")); await flush();
    gate.resolve(response(request, new Blob(["archive"]))); await Promise.all([first, second]);
    expect(calls.filter(config => config.url?.endsWith("/download"))).toHaveLength(1); expect(click).toHaveBeenCalledOnce(); expect(link.download).toBe("one.zip"); expect(revoke).toHaveBeenCalledWith("blob:fixture");
  });
  it(`${name} ignores a late download error after the view is closed`, async () => {
    const { state } = await mount(component), gate = deferred<AxiosResponse>(); download = () => gate.promise;
    const downloading = state.download(record()); await flush(); app!.unmount(); app = undefined;
    gate.reject(new Error("late transport failure")); await downloading; expect(state.notice).toBe("");
  });
  it(`${name} reports a current download failure and permits a successful retry`, async () => {
    const { state } = await mount(component); download = async () => { throw new Error("download failed"); };
    await state.download(record()); expect(state.noticeError).toBe(true); expect(state.notice).not.toBe(""); expect(click).not.toHaveBeenCalled();
    download = async config => response(config, new Blob(["archive"])); await state.download(record()); expect(click).toHaveBeenCalledOnce();
  });
  it(`${name} translates an invalid restore response instead of showing a raw error key`, async () => {
    const { state } = await mount(component);
    await state.restore(record());
    expect(state.noticeError).toBe(true); expect(state.notice).toBe("Restore result could not be verified");
  });
}
it("does not save a site's late archive after a different site is selected", async () => {
  const { state, sites } = await mount(SiteBackupView), gate = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
  download = config => (request = config, gate.promise); const downloading = state.download(record()); await flush(); sites.selectedSiteName = "beta.test"; await flush();
  gate.resolve(response(request, new Blob(["archive"]))); await downloading; expect(click).not.toHaveBeenCalled(); expect(state.notice).toBe("");
});
