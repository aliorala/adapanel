import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createPinia } from "pinia";
import { createRenderer, h, nextTick, ssrContextKey, type App, type Component, type Ref } from "vue";
import type { AxiosResponse, InternalAxiosRequestConfig } from "axios";
import BackupView from "./BackupView.vue";
import SiteBackupView from "./SiteBackupView.vue";
import FilesView from "./FilesView.vue";
import { apiClient, invalidateSessionRequests } from "../api/client";
import type { BackupRecord } from "../api/backup";
import { useAuthStore } from "../stores/auth";
import { useSitesStore } from "../stores/sites";

const dialogs = vi.hoisted(() => ({ confirm: vi.fn(), prompt: vi.fn() }));
vi.mock("../router", () => ({ default: { currentRoute: { value: { query: {} } }, push: vi.fn() } }));
vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key, locale: { value: "en" } }) }));
vi.mock("../components/ConfirmDialog.vue", () => ({ useConfirm: () => ({ confirm: dialogs.confirm }) }));
vi.mock("../components/PromptDialog.vue", () => ({ usePrompt: () => ({ prompt: dialogs.prompt }) }));
const record = (): BackupRecord => ({ id: "one", name: "alpha.zip", type: "site", source: "alpha.test", path: "/backups/alpha.zip", format: "zip", createdAtUtc: "2026-10-04T12:00:00Z", size: 100,
  status: "restored", targetType: "local", remoteUri: "", encrypted: false, warnings: [], preservedPath: "", chainId: "", chainIndex: 0, backupMode: "full", baseBackupId: "", subscriptionUserId: "", itemCount: 1, localMissing: false, remoteMissing: false, remoteVerifiedUtc: null });
function response(config: InternalAxiosRequestConfig, data: unknown): AxiosResponse { return { config, data: { success: true, data }, headers: {}, status: 200, statusText: "OK" }; }
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(yes => { resolve = yes; }); return { promise, resolve }; }
async function flush() { for (let i = 0; i < 70; i++) await nextTick(); }
type State = { notice: string; noticeError: boolean; fullRestore: { result?: Ref<BackupRecord | null>; refreshError?: Ref<string> }; restore(backup: BackupRecord): Promise<void>; refresh(): Promise<boolean>; refreshBackups(): Promise<boolean> };
const originalAdapter = apiClient.defaults.adapter;
let app: App | undefined, calls: InternalAxiosRequestConfig[], restored: BackupRecord;
let list: (config: InternalAxiosRequestConfig) => Promise<AxiosResponse>, chain: (config: InternalAxiosRequestConfig) => Promise<AxiosResponse>;
beforeEach(() => {
  vi.resetAllMocks(); invalidateSessionRequests(); calls = []; restored = record();
  vi.stubGlobal("window", { location: { pathname: "/panel" }, setTimeout, clearTimeout, setInterval, clearInterval, addEventListener: vi.fn(), removeEventListener: vi.fn() });
  vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ success: true, data: { requestToken: "fixture-token" } }), { status: 200 })));
  dialogs.confirm.mockImplementation(async options => { await options.action?.(); return true; }); dialogs.prompt.mockResolvedValue("");
  list = async config => response(config, [record()]);
  chain = async config => response(config, { chainId: "chain-a", broken: false, missingLinks: [], links: [{ backupId: "one", name: "alpha.zip", chainIndex: 0, backupMode: "full", createdAtUtc: record().createdAtUtc, size: 100, available: true }] });
  apiClient.defaults.adapter = async config => {
    calls.push(config);
    if (config.url === "/backup/restore") return response(config, restored);
    if (config.url === "/backup") return list(config);
    if (config.url?.endsWith("/chain")) return chain(config);
    const data = config.url === "/automation" ? { settings: {} } : config.url === "/backup/encryption" ? { enabled: false, passwordUsable: false, algorithm: "AES", updatedAtUtc: "" } : config.url?.includes("running") ? { running: [], interrupted: [], serverTimeUtc: "2026-10-04T12:00:00Z" } : config.url === "/files" ? { path: "sites/alpha.test", parentPath: "", displayName: "alpha.test", physicalPath: "", isDirectory: true, content: "", entries: [] } : [];
    return response(config, data);
  };
});
afterEach(() => { app?.unmount(); app = undefined; apiClient.defaults.adapter = originalAdapter; invalidateSessionRequests(); vi.unstubAllGlobals(); });
async function mount(component: Component) {
  const pinia = createPinia(), auth = useAuthStore(pinia), sites = useSitesStore(pinia);
  auth.session = { isAuthenticated: true, requiresSetupWizard: false, user: { id: "admin", role: "Administrator", userName: "admin", email: "admin@example.test", preferredLanguage: "en", isActive: true, isTwoFactorEnabled: false, lastLoginAtUtc: null } };
  auth.users = [{ id: "customer", role: "Customer", userName: "customer" }] as never;
  sites.selectedSiteName = "alpha.test"; sites.sites = [{ name: "alpha.test" }, { name: "beta.test" }] as never;
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  app = renderer.createApp({ ...component, render: () => h("section") } as Component).use(pinia); app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: State }).setupState; await flush(); state.notice = ""; return { state, auth, sites };
}
for (const [name, component] of [["catalogue", BackupView], ["site", SiteBackupView], ["files", FilesView]] as const) {
  it(`${name} preserves measured restoration and reports a failed list refresh separately`, async () => {
    const { state } = await mount(component); list = async () => { throw new Error("list unavailable"); };
    await state.restore(record());
    expect(state.fullRestore.result?.value?.id).toBe("one"); expect(state.fullRestore.refreshError?.value).toBe("panel.backup.restoreRefreshFailed");
    expect(state.notice).toBe("panel.backup.restored"); expect(calls.filter(config => config.method === "post")).toHaveLength(1);
    list = async config => response(config, [record()]); await (name === "files" ? state.refreshBackups() : state.refresh());
    expect(state.fullRestore.refreshError?.value).toBe(""); expect(state.fullRestore.result?.value?.id).toBe("one"); expect(state.noticeError).toBe(false);
  });
  it(`${name} publishes restoration while the subsequent list refresh is still pending`, async () => {
    const { state } = await mount(component), gate = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
    list = config => (request = config, gate.promise); const restoring = state.restore(record()); await flush();
    const measured = state.fullRestore.result?.value; gate.resolve(response(request, [record()])); await restoring;
    expect(measured?.id).toBe("one"); expect(state.fullRestore.refreshError?.value).toBe("");
  });
  it(`${name} keeps partial warnings and recovery paths in its measured result`, async () => {
    const { state } = await mount(component); restored = { ...record(), status: "restored_with_errors", preservedPath: "/preserved/alpha", restoredPath: "/restored/alpha", warnings: [{ messageKey: "backup.site_create_failed", target: "alpha.test", detail: "Original restore warning" }] };
    list = async () => { throw new Error("list unavailable"); };
    await state.restore(record()); expect(state.fullRestore.result?.value).toEqual(restored); expect(state.noticeError).toBe(true);
    expect(state.fullRestore.refreshError?.value).toBe("panel.backup.restoreRefreshFailed");
    expect(state.notice).toBe("panel.backup.restoredWithErrors"); expect(state.notice).not.toContain("Original restore warning");
  });
  it(`${name} does not hide warnings behind an otherwise restored status`, async () => {
    const { state } = await mount(component); restored = { ...record(), warnings: [{ messageKey: "backup.site_create_failed", target: "alpha.test", detail: "A component could not be restored" }] };
    await state.restore(record()); expect(state.fullRestore.result?.value?.warnings).toEqual(restored.warnings);
    expect(state.noticeError).toBe(true); expect(state.notice).toContain("panel.backup.restoredWithErrors");
  });
  it(`${name} hides a previous restoration result after the authenticated session changes`, async () => {
    const { state, auth } = await mount(component); await state.restore(record()); expect(state.fullRestore.result?.value?.id).toBe("one");
    auth.session = { ...auth.session!, user: { ...auth.session!.user! } }; await flush(); expect(state.fullRestore.result?.value).toBeNull(); expect(state.fullRestore.refreshError?.value).toBe("");
  });
  it(`${name} stops before password and confirmation on a chain from another snapshot`, async () => {
    const { state } = await mount(component); const normal = chain;
    chain = async config => { const result = await normal(config); result.data.data.chainId = "other-chain"; return result; };
    await state.restore({ ...record(), chainId: "chain-a", encrypted: true });
    expect(dialogs.prompt).not.toHaveBeenCalled(); expect(dialogs.confirm).not.toHaveBeenCalled(); expect(calls.filter(config => config.method === "post")).toHaveLength(0);
  });
}
it.each([["site", SiteBackupView], ["files", FilesView]] as const)("%s result belongs only to its selected site", async (_name, component) => {
  const { state, sites } = await mount(component); await state.restore(record()); expect(state.fullRestore.result?.value?.id).toBe("one");
  sites.selectedSiteName = "beta.test"; await flush(); expect(state.fullRestore.result?.value).toBeNull();
});
