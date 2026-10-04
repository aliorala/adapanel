import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createPinia } from "pinia";
import { createRenderer, h, nextTick, ssrContextKey, type App, type Component } from "vue";
import { AxiosError, type AxiosResponse, type InternalAxiosRequestConfig } from "axios";
import BackupView from "./BackupView.vue";
import { apiClient, invalidateSessionRequests, setReadOnlyMode } from "../api/client";
import { useAuthStore } from "../stores/auth";
import { useSitesStore } from "../stores/sites";
import type { BackupRecord } from "../api/backup";

const dialogs = vi.hoisted(() => ({ prompt: vi.fn(), confirm: vi.fn() }));
vi.mock("../router", () => ({ default: { push: vi.fn() } }));
vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key, locale: { value: "en" } }) }));
vi.mock("../components/PromptDialog.vue", () => ({ usePrompt: () => ({ prompt: dialogs.prompt }) }));
vi.mock("../components/ConfirmDialog.vue", () => ({ useConfirm: () => ({ confirm: dialogs.confirm }) }));
const record = (): BackupRecord => ({ id: "one", name: "alpha.zip", type: "subscription", source: "alpha.test", path: "", format: "zip", createdAtUtc: "2026-10-04T12:00:00Z", size: 100,
  status: "created", targetType: "local", remoteUri: "", encrypted: false, warnings: [], preservedPath: "", chainId: "", chainIndex: 0, backupMode: "full", baseBackupId: "", subscriptionUserId: "owner", itemCount: 1, localMissing: false, remoteMissing: false, remoteVerifiedUtc: null });
function response(config: InternalAxiosRequestConfig, data: unknown): AxiosResponse { return { config, data: { success: true, data }, headers: {}, status: 200, statusText: "OK" }; }
function zip(config: InternalAxiosRequestConfig): AxiosResponse { return { config, data: new Blob([new Uint8Array([80, 75, 3, 4, 1, 2, 3, 4])], { type: "application/zip" }), headers: { "content-type": "application/zip", "content-disposition": "attachment; filename=alpha-selected.zip" }, status: 200, statusText: "OK" }; }
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(yes => { resolve = yes; }); return { promise, resolve }; }
async function flush() { for (let i = 0; i < 70; i++) await nextTick(); }
type State = { notice: string; noticeError: boolean; selectedItems: string[]; itemsTargetPath: string; canRestoreItems: boolean; canDownloadSelectedItems: boolean; downloadItemsBusy: boolean;
  openBrowser(record: BackupRecord): Promise<void>; closeBrowser(): void; downloadSelectedItems(): Promise<void> };
const originalAdapter = apiClient.defaults.adapter;
let app: App | undefined, calls: InternalAxiosRequestConfig[], click: ReturnType<typeof vi.fn>, createUrl: ReturnType<typeof vi.fn>, link: { href: string; download: string; click: ReturnType<typeof vi.fn>; remove: ReturnType<typeof vi.fn> };
let download: (config: InternalAxiosRequestConfig) => Promise<AxiosResponse>;
beforeEach(() => {
  vi.resetAllMocks(); invalidateSessionRequests(); calls = []; click = vi.fn(); createUrl = vi.fn(() => "blob:fixture"); link = { href: "", download: "", click, remove: vi.fn() };
  vi.stubGlobal("window", { location: { pathname: "/panel" }, setTimeout, clearTimeout, setInterval, clearInterval, URL: { createObjectURL: createUrl, revokeObjectURL: vi.fn() } });
  vi.stubGlobal("document", { createElement: vi.fn(() => link), body: { appendChild: vi.fn() } });
  vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ success: true, data: { requestToken: "fixture-token" } }), { status: 200 })));
  dialogs.prompt.mockResolvedValue("archive-secret"); download = async config => zip(config);
  apiClient.defaults.adapter = async config => {
    calls.push(config); if (config.url === "/backup/download-items") return download(config);
    const data = config.url === "/backup" ? [record()] : config.url?.endsWith("/entries") ? { backupId: "one", prefix: "", entries: [{ name: "a.txt", path: "files/a.txt", isDirectory: false, size: 4 }], truncated: false } : config.url === "/automation" ? { settings: {} } : config.url === "/backup/encryption" ? { enabled: false, passwordUsable: false, algorithm: "AES", updatedAtUtc: "" } : config.url?.includes("running") ? { running: [], interrupted: [], serverTimeUtc: "2026-10-04T12:00:00Z" } : [];
    return response(config, data);
  };
});
afterEach(() => { app?.unmount(); app = undefined; apiClient.defaults.adapter = originalAdapter; invalidateSessionRequests(); vi.unstubAllGlobals(); });
async function mount(role = "Customer", encrypted = false) {
  const pinia = createPinia(), auth = useAuthStore(pinia), sites = useSitesStore(pinia);
  auth.session = { isAuthenticated: true, requiresSetupWizard: false, user: { id: "actor", role, userName: "actor", email: "actor@example.test", preferredLanguage: "en", isActive: true, isTwoFactorEnabled: false, lastLoginAtUtc: null } };
  auth.users = [{ id: "customer", role: "Customer", userName: "customer" }] as never;
  sites.selectedSiteName = "alpha.test"; sites.sites = [{ name: "alpha.test" }, { name: "beta.test" }] as never;
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  app = renderer.createApp({ ...BackupView, render: () => h("section") } as Component).use(pinia); app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: State }).setupState; await flush(); await state.openBrowser({ ...record(), encrypted }); state.selectedItems = ["files/a.txt"]; state.notice = ""; return { state, auth, sites };
}
it.each(["Administrator", "Customer", "Reseller"])("delivers a selected ZIP to %s without a physical target or destructive confirmation", async role => {
  const { state } = await mount(role); expect(state.canDownloadSelectedItems).toBe(true); expect(state.canRestoreItems).toBe(role === "Administrator");
  await state.downloadSelectedItems(); const request = calls.find(config => config.url === "/backup/download-items")!;
  expect(JSON.parse(String(request.data))).toEqual({ backupId: "one", items: ["files/a.txt"], encryptionPassword: "" });
  expect(request.headers["X-CSRF-TOKEN"]).toBe("fixture-token"); expect(click).toHaveBeenCalledOnce(); expect(link.download).toBe("alpha-selected.zip");
  expect(dialogs.confirm).not.toHaveBeenCalled(); expect(state.notice).toBe("panel.backup.selectedDownloadStarted");
});
it("allows this exact read-only POST while the subscription is suspended", async () => {
  const { state } = await mount(); setReadOnlyMode("subscription.suspended_readonly"); await state.downloadSelectedItems(); expect(click).toHaveBeenCalledOnce();
});
it.each([["post", "/backup/restore-items"], ["put", "/backup/download-items"], ["post", "/backup/download-items/other"]] as const)("does not widen the suspended allowlist to %s %s", async (method, url) => {
  setReadOnlyMode("subscription.suspended_readonly"); await expect(apiClient.request({ method, url, data: {} })).rejects.toMatchObject({ response: { status: 403 } }); expect(calls).toEqual([]);
});
it("reserves one encrypted download through the password prompt and snapshots the selected paths", async () => {
  const { state } = await mount("Customer", true), gate = deferred<string>(); dialogs.prompt.mockReturnValueOnce(gate.promise);
  const first = state.downloadSelectedItems(); await state.downloadSelectedItems(); expect(state.downloadItemsBusy).toBe(true); expect(state.canDownloadSelectedItems).toBe(false);
  gate.resolve("archive-secret"); await first;
  expect(dialogs.prompt).toHaveBeenCalledOnce(); expect(calls.filter(config => config.url === "/backup/download-items")).toHaveLength(1);
  expect(JSON.parse(String(calls.find(config => config.url === "/backup/download-items")!.data))).toEqual({ backupId: "one", items: ["files/a.txt"], encryptionPassword: "archive-secret" });
});
it("cancels password entry without sending a download", async () => {
  const { state } = await mount("Customer", true); dialogs.prompt.mockResolvedValueOnce(null); await state.downloadSelectedItems();
  expect(calls.some(config => config.url === "/backup/download-items")).toBe(false); expect(state.downloadItemsBusy).toBe(false); expect(click).not.toHaveBeenCalled();
});
for (const stage of ["prompt", "response"] as const) for (const context of ["session", "site", "close", "unmount"] as const) it(`does not download after ${context} changes during ${stage}`, async () => {
  const { state, auth, sites } = await mount("Customer", stage === "prompt"), gate = deferred<string>(), responseGate = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
  if (stage === "prompt") dialogs.prompt.mockReturnValueOnce(gate.promise); else download = config => (request = config, responseGate.promise);
  const downloading = state.downloadSelectedItems(); await flush();
  if (context === "session") auth.session = { ...auth.session!, user: { ...auth.session!.user! } };
  else if (context === "site") sites.selectedSiteName = "beta.test";
  else if (context === "close") state.closeBrowser();
  else { app!.unmount(); app = undefined; }
  await flush(); if (stage === "prompt") gate.resolve("archive-secret"); else responseGate.resolve(zip(request)); await downloading;
  expect(click).not.toHaveBeenCalled(); expect(createUrl).not.toHaveBeenCalled(); expect(state.notice).toBe("");
  if (stage === "prompt") expect(calls.some(config => config.url === "/backup/download-items")).toBe(false);
});
it.each(["bad-magic", "wrong-mime", "missing-filename", "json-success", "json-error", "http-json-error"])("does not save an unverified selected archive: %s", async kind => {
  const { state } = await mount(); download = async config => {
    const result = zip(config);
    if (kind === "bad-magic") result.data = new Blob(["not a ZIP"], { type: "application/zip" });
    if (kind === "wrong-mime") result.headers["content-type"] = "text/html";
    if (kind === "missing-filename") delete result.headers["content-disposition"];
    if (kind.startsWith("json") || kind === "http-json-error") result.data = new Blob([JSON.stringify({ success: kind === "json-success", message: "backup.password_invalid", data: null })], { type: "application/json" });
    if (kind === "http-json-error") throw new AxiosError("Bad request", "ERR_BAD_REQUEST", config, null, { ...result, status: 400 });
    return result;
  };
  await state.downloadSelectedItems(); expect(click).not.toHaveBeenCalled(); expect(state.noticeError).toBe(true);
  expect(state.notice).toBe(kind === "json-error" || kind === "http-json-error" ? "backup.password_invalid" : "panel.backup.selectedDownloadUnconfirmed");
});
