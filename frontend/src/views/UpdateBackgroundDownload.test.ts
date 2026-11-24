import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createPinia } from "pinia";
import type { AxiosResponse, InternalAxiosRequestConfig } from "axios";
import { createRenderer, h, nextTick, ssrContextKey, type App, type Component } from "vue";
import UpdateView from "./UpdateView.vue";
import { apiClient, invalidateSessionRequests } from "../api/client";
import type { UpdateDownload, UpdateStatus } from "../api/update";
import { useAuthStore } from "../stores/auth";

// Canlıda yaşanan hata: indirme POST isteğinin içinde yürüyordu; sayfa yenilenince sessizce yarıda
// kalıyor ve ekran hiçbir hata göstermiyordu. İndirme artık sunucuda arka plan işi; ekran durumu okur.
vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string, values?: Record<string, unknown>) => values ? `${key}:${JSON.stringify(values)}` : key }) }));
const { confirm } = vi.hoisted(() => ({ confirm: vi.fn() }));
vi.mock("../components/ConfirmDialog.vue", () => ({ useConfirm: () => ({ confirm }) }));
type State = { status: UpdateStatus | null; busy: string; notice: string; noticeError: boolean; downloadRunning: boolean; canInstall: boolean; canDownload: boolean;
  downloadUpdate(): Promise<void> };
const download = (state: UpdateDownload["state"], extra: Partial<UpdateDownload> = {}): UpdateDownload =>
  ({ state, percent: null, errorKey: "", targetVersion: "0.8.76", startedAtUtc: null, updatedAtUtc: null, ...extra });
const valid = (extra: Partial<UpdateStatus> = {}): UpdateStatus => ({ currentVersion: "0.8.75", latestVersion: "0.8.76", isUpdateAvailable: true, isCheckFailed: false, lastAttempt: null,
  downloadUrl: "https://example.test/0.8.76.exe", packageSha256: "a".repeat(64), preparedVersion: "", preparedPackageSha256: "", packagePath: "", isPackageReady: false,
  isInstallScheduled: false, installScheduledAtUtc: null, notes: "Release notes", githubRepo: "fixture/panel", checkedAtUtc: new Date().toISOString(), ...extra });
const readyPackage = { preparedVersion: "0.8.76", preparedPackageSha256: "a".repeat(64), isPackageReady: true, packagePath: "C:\\mock\\update.exe" };
function response(config: InternalAxiosRequestConfig, data: unknown): AxiosResponse { return { config, data: { success: true, data }, headers: {}, status: 200, statusText: "OK" }; }
let app: App | undefined, calls: InternalAxiosRequestConfig[], read: () => UpdateStatus, write: () => UpdateStatus;
const originalAdapter = apiClient.defaults.adapter;
async function flush() { for (let i = 0; i < 40; i++) await nextTick(); }
async function mount() {
  const pinia = createPinia(), auth = useAuthStore(pinia);
  auth.session = { isAuthenticated: true, requiresSetupWizard: false, user: { id: "admin", userName: "admin", email: "a@example.test", role: "Administrator", preferredLanguage: "en", isTwoFactorEnabled: false, isActive: true, lastLoginAtUtc: null } };
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  app = renderer.createApp({ ...UpdateView, render: () => h("section") } as Component).use(pinia); app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: State }).setupState; await flush(); return state;
}
function close() { app?.unmount(); app = undefined; }
const gets = () => calls.filter(config => config.method === "get");
beforeEach(() => {
  vi.useFakeTimers(); vi.setSystemTime(new Date("2026-10-04T10:00:00Z")); invalidateSessionRequests(); calls = []; confirm.mockReset(); confirm.mockResolvedValue(true);
  vi.stubGlobal("window", { location: { pathname: "/panel", hostname: "localhost", reload: vi.fn() }, setInterval: globalThis.setInterval, clearInterval: globalThis.clearInterval, setTimeout: globalThis.setTimeout, clearTimeout: globalThis.clearTimeout });
  vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ success: true, data: { requestToken: "mock-token" } }), { status: 200 })));
  read = () => valid(); write = () => valid();
  apiClient.defaults.adapter = async config => { calls.push(config); return response(config, config.method === "get" ? read() : write()); };
});
afterEach(() => { app?.unmount(); app = undefined; vi.clearAllTimers(); vi.useRealTimers(); apiClient.defaults.adapter = originalAdapter; invalidateSessionRequests(); vi.restoreAllMocks(); vi.unstubAllGlobals(); });

it("shows an in-progress download after a page reload and keeps polling until it is ready", async () => {
  let current = valid({ download: download("downloading", { percent: 40 }) });
  read = () => current;
  const state = await mount();
  expect(state.downloadRunning).toBe(true);
  expect(state.canDownload).toBe(false);
  expect(gets()).toHaveLength(1);

  await vi.advanceTimersByTimeAsync(2000);
  expect(gets()).toHaveLength(2);
  expect(state.downloadRunning).toBe(true);

  current = valid({ ...readyPackage, download: download("ready", { percent: 100 }) });
  await vi.advanceTimersByTimeAsync(2000);
  expect(state.downloadRunning).toBe(false);
  expect(state.notice).toBe("panel.update.staged");
  expect(state.noticeError).toBe(false);
  expect(state.canInstall).toBe(true);
  await vi.advanceTimersByTimeAsync(10_000);
  expect(gets()).toHaveLength(3);
});

it("shows a translated error when the download failed or was interrupted by a restart", async () => {
  read = () => valid({ download: download("failed", { errorKey: "update.download_interrupted" }) });
  const state = await mount();
  expect(state.noticeError).toBe(true);
  expect(state.notice).toBe("update.download_interrupted");
  expect(state.downloadRunning).toBe(false);
  expect(state.canDownload).toBe(true);
  await vi.advanceTimersByTimeAsync(10_000);
  expect(gets()).toHaveLength(1);
});

it("does not allow installing while the download runs, but keeps a valid staged package installable after a failed retry", async () => {
  read = () => valid({ ...readyPackage, download: download("downloading") });
  const running = await mount();
  expect(running.canInstall).toBe(false);
  close();
  read = () => valid({ ...readyPackage, download: download("failed", { errorKey: "update.checksum_mismatch" }) });
  const failed = await mount();
  expect(failed.canInstall).toBe(true);
});

it("announces a persisted failure only once per failure, not on every page open", async () => {
  const store = new Map<string, string>();
  const storage = { getItem: (key: string) => store.get(key) ?? null, setItem: (key: string, value: string) => { store.set(key, value); } };
  vi.stubGlobal("window", { location: { pathname: "/panel", hostname: "localhost", reload: vi.fn() }, localStorage: storage, setTimeout: globalThis.setTimeout, clearTimeout: globalThis.clearTimeout });
  read = () => valid({ download: download("failed", { errorKey: "update.download_interrupted", updatedAtUtc: "2026-10-04T09:00:00Z" }) });
  const first = await mount();
  expect(first.notice).toBe("update.download_interrupted");
  close();
  const second = await mount();
  expect(second.notice).toBe("");
  close();
  read = () => valid({ download: download("failed", { errorKey: "update.download_interrupted", updatedAtUtc: "2026-10-04T09:30:00Z" }) });
  expect((await mount()).notice).toBe("update.download_interrupted");
});

it("retries the status poll with growing delays after a network error, then stops and shows the error", async () => {
  let fail = false;
  read = () => { if (fail) throw new Error("network"); return valid({ download: download("downloading", { percent: 10 }) }); };
  const state = await mount();
  fail = true;
  await vi.advanceTimersByTimeAsync(2000);   // 1. deneme başarısız -> 4 sn sonra
  expect(gets()).toHaveLength(2);
  await vi.advanceTimersByTimeAsync(4000);   // 2. -> 8 sn
  expect(gets()).toHaveLength(3);
  expect(state.noticeError).toBe(false);
  await vi.advanceTimersByTimeAsync(8000);   // 3. -> 16 sn
  expect(gets()).toHaveLength(4);
  await vi.advanceTimersByTimeAsync(16000);  // 4. başarısız: dur ve göster
  expect(gets()).toHaveLength(5);
  expect(state.noticeError).toBe(true);
  await vi.advanceTimersByTimeAsync(60000);
  expect(gets()).toHaveLength(5);
});

it("starts the download on the server, returns immediately and surfaces a later failure", async () => {
  let current = valid();
  read = () => current;
  write = () => valid({ download: download("downloading", { percent: 0 }) });
  const state = await mount();
  await state.downloadUpdate();
  expect(calls.filter(config => config.method === "post")).toHaveLength(1);
  expect(state.busy).toBe("");
  expect(state.downloadRunning).toBe(true);

  current = valid({ download: download("failed", { errorKey: "update.checksum_mismatch" }) });
  await vi.advanceTimersByTimeAsync(2000);
  expect(state.downloadRunning).toBe(false);
  expect(state.noticeError).toBe(true);
  expect(state.notice).toBe("update.checksum_mismatch");
});
