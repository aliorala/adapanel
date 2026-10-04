import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createPinia } from "pinia";
import type { AxiosResponse, InternalAxiosRequestConfig } from "axios";
import { createRenderer, h, nextTick, ssrContextKey, type App, type Component } from "vue";
import UpdateView from "./UpdateView.vue";
import { apiClient, invalidateSessionRequests } from "../api/client";
import type { UpdateStatus } from "../api/update";
import { useAuthStore } from "../stores/auth";
import { describeUpdateState } from "../utils/updatePresentation";

vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key }) }));
const { confirm } = vi.hoisted(() => ({ confirm: vi.fn() }));
vi.mock("../components/ConfirmDialog.vue", () => ({ useConfirm: () => ({ confirm }) }));
type State = { status: UpdateStatus | null; busy: string; refreshing: boolean; notice: string; noticeError: boolean; installPhase: string; statusConfirmed: boolean;
  refresh(force?: boolean): Promise<void>; downloadUpdate(): Promise<void>; installUpdate(): Promise<void> };
const valid = (extra: Partial<UpdateStatus> = {}): UpdateStatus => ({ currentVersion: "0.8.75", latestVersion: "0.8.76", isUpdateAvailable: true, isCheckFailed: false, lastAttempt: null, downloadUrl: "https://example.test/0.8.76.exe", packageSha256: "a".repeat(64), preparedVersion: "0.8.76", preparedPackageSha256: "a".repeat(64), packagePath: "C:\\mock\\update.exe", isPackageReady: true, isInstallScheduled: false, installScheduledAtUtc: null, notes: "Release notes", githubRepo: "fixture/panel", checkedAtUtc: new Date().toISOString(), ...extra });
function response(config: InternalAxiosRequestConfig, data: unknown): AxiosResponse { return { config, data: { success: true, data }, headers: {}, status: 200, statusText: "OK" }; }
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(yes => { resolve = yes; }); return { promise, resolve }; }
let app: App | undefined, calls: InternalAxiosRequestConfig[], initial: UpdateStatus;
let read: (config: InternalAxiosRequestConfig) => Promise<AxiosResponse>, write: (config: InternalAxiosRequestConfig) => Promise<AxiosResponse>, reload: ReturnType<typeof vi.fn>;
const originalAdapter = apiClient.defaults.adapter;
async function flush() { for (let i = 0; i < 40; i++) await nextTick(); }
function unmount() { app?.unmount(); app = undefined; }
async function mount() {
  const pinia = createPinia(), auth = useAuthStore(pinia);
  auth.session = { isAuthenticated: true, requiresSetupWizard: false, user: { id: "admin", userName: "admin", email: "a@example.test", role: "Administrator", preferredLanguage: "en", isTwoFactorEnabled: false, isActive: true, lastLoginAtUtc: null } };
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  app = renderer.createApp({ ...UpdateView, render: () => h("section") } as Component).use(pinia); app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: State }).setupState; await flush(); return { state, auth };
}
const posts = () => calls.filter(config => config.method === "post");
const gets = () => calls.filter(config => config.method === "get");
beforeEach(() => {
  vi.useFakeTimers(); vi.setSystemTime(new Date("2026-10-04T10:00:00Z")); invalidateSessionRequests(); calls = []; initial = valid(); reload = vi.fn(); confirm.mockReset(); confirm.mockResolvedValue(true);
  vi.stubGlobal("window", { location: { pathname: "/panel", hostname: "localhost", reload }, setInterval: globalThis.setInterval, clearInterval: globalThis.clearInterval, setTimeout: globalThis.setTimeout, clearTimeout: globalThis.clearTimeout });
  vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ success: true, data: { requestToken: "mock-token" } }), { status: 200 })));
  read = async config => response(config, initial);
  write = async config => response(config, valid(config.url === "/update/install" ? { isInstallScheduled: true, installScheduledAtUtc: new Date().toISOString() } : {}));
  apiClient.defaults.adapter = async config => { calls.push(config); return config.method === "get" ? read(config) : write(config); };
});
afterEach(() => { unmount(); vi.clearAllTimers(); vi.useRealTimers(); apiClient.defaults.adapter = originalAdapter; invalidateSessionRequests(); vi.restoreAllMocks(); vi.unstubAllGlobals(); });

it.each(["unmount", "session"])("ignores the initial status read after %s", async change => {
  const pending = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig; read = config => (request = config, pending.promise);
  const { state } = await mount(); if (change === "unmount") unmount(); else invalidateSessionRequests();
  pending.resolve(response(request, valid())); await flush(); expect(state.status).toBeNull(); expect(state.notice).toBe("");
});
it.each(["download", "install"])("does not publish or start polling after %s finishes in a closed view", async action => {
  const { state } = await mount(), pending = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig; write = config => (request = config, pending.promise);
  const running = action === "download" ? state.downloadUpdate() : state.installUpdate(); await flush(); expect(request).toBeDefined(); unmount();
  pending.resolve(response(request, valid({ isInstallScheduled: action === "install" }))); await running; await vi.advanceTimersByTimeAsync(10_000);
  expect(state.notice).toBe(""); expect(gets()).toHaveLength(action === "install" ? 2 : 1); expect(vi.getTimerCount()).toBe(0);
});
it.each(["unmount", "session", "actor"])("does not install after %s changes while confirmation is open", async change => {
  const answer = deferred<boolean>(); confirm.mockReturnValue(answer.promise); const { state, auth } = await mount(); const running = state.installUpdate();
  if (change === "unmount") unmount(); else if (change === "session") invalidateSessionRequests(); else auth.session!.user!.id = "another-admin";
  answer.resolve(true); await running; expect(posts()).toHaveLength(0);
});
it("reserves confirmation against a second install and concurrent download", async () => {
  const answer = deferred<boolean>(); confirm.mockReturnValue(answer.promise); const { state } = await mount(); const running = state.installUpdate();
  const second = state.installUpdate(); await state.downloadUpdate(); expect(confirm).toHaveBeenCalledOnce(); expect(posts()).toHaveLength(0);
  answer.resolve(false); await Promise.all([running, second]); expect(state.busy).toBe("");
});
it.each([{ preparedVersion: "0.8.77" }, { preparedPackageSha256: "b".repeat(64) }, { isPackageReady: false }, { preparedVersion: undefined }])("rechecks the approved package before installing and rejects a changed status %j", async changed => {
  const answer = deferred<boolean>(); confirm.mockReturnValue(answer.promise); const { state } = await mount(); const running = state.installUpdate(); initial = valid(changed);
  answer.resolve(true); await running; expect(posts()).toHaveLength(0); expect(state.noticeError).toBe(true);
});
it("blocks writes while a user-requested status refresh is outstanding", async () => {
  const { state } = await mount(), pending = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig; read = config => (request = config, pending.promise);
  const refreshing = state.refresh(); await flush(); const installing = state.installUpdate(); await state.downloadUpdate();
  expect(confirm).not.toHaveBeenCalled(); expect(posts()).toHaveLength(0); pending.resolve(response(request, valid())); await Promise.all([refreshing, installing]);
});
it.each(["false-envelope", "missing-check-state", "null-data"])("does not accept an unmeasured status response: %s", async kind => {
  read = async config => kind === "false-envelope" ? { ...response(config, valid()), data: { success: false, data: valid() } } : response(config, kind === "null-data" ? null : { ...valid(), isCheckFailed: undefined });
  const { state } = await mount(); expect(state.status).toBeNull(); expect(state.noticeError).toBe(true);
});
it("keeps cached status visible but closes writes after a failed refresh", async () => {
  const { state } = await mount(); read = async () => { throw new Error("offline"); }; await state.refresh(); await state.downloadUpdate(); await state.installUpdate();
  expect(state.status?.currentVersion).toBe("0.8.75"); expect(posts()).toHaveLength(0); expect(state.noticeError).toBe(true);
});
it.each(["not-ready", "different-package", "false-envelope"])("does not report an unconfirmed download as staged: %s", async kind => {
  const { state } = await mount(); write = async config => kind === "false-envelope" ? { ...response(config, valid()), data: { success: false, data: valid() } } : response(config, valid(kind === "not-ready" ? { isPackageReady: false } : { preparedPackageSha256: "b".repeat(64) }));
  await state.downloadUpdate(); expect(state.noticeError).toBe(true); expect(state.notice).not.toBe("panel.update.staged");
});
it("confirms a measured download without requiring installation", async () => { const { state } = await mount(); await state.downloadUpdate(); expect(state.notice).toBe("panel.update.staged"); expect(state.noticeError).toBe(false); });
it("does not start monitoring when installation scheduling was not measured", async () => {
  const { state } = await mount(); write = async config => response(config, valid()); await state.installUpdate(); expect(state.noticeError).toBe(true); expect(vi.getTimerCount()).toBe(0);
});
it("never overlaps slow installation polls", async () => {
  const { state } = await mount(); await state.installUpdate(); const pending = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig; const before = gets().length;
  read = config => (request = config, pending.promise); await vi.advanceTimersByTimeAsync(16_000); expect(gets()).toHaveLength(before + 1);
  pending.resolve(response(request, valid())); await flush();
});
it.each(["unmount", "session", "timeout"])("ignores a late successful poll after %s", async change => {
  const { state } = await mount(); await state.installUpdate(); const pending = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig; read = config => (request = config, pending.promise);
  await vi.advanceTimersByTimeAsync(5000); if (change === "unmount") unmount(); else if (change === "session") invalidateSessionRequests(); else await vi.advanceTimersByTimeAsync(15 * 60_000);
  pending.resolve(response(request, valid({ currentVersion: "0.8.76" }))); await flush(); await vi.advanceTimersByTimeAsync(2000);
  expect(reload).not.toHaveBeenCalled(); expect(state.installPhase).not.toBe("done"); if (change === "timeout") expect(state.installPhase).toBe("timeout");
});
it.each(["0.8.74", "0.8.77", "0.8.75"])("does not claim target 0.8.76 installed when the reported version is %s", async currentVersion => {
  const { state } = await mount(); await state.installUpdate(); read = async config => response(config, valid({ currentVersion, lastAttempt: { phase: "finished", isFinished: true, isFailed: false, error: "", errorKey: "", isDecidedByPanel: false, updatedAtUtc: new Date().toISOString(), installerExitCode: 0 } }));
  await vi.advanceTimersByTimeAsync(5000); await vi.advanceTimersByTimeAsync(2000); expect(state.installPhase).not.toBe("done"); expect(reload).not.toHaveBeenCalled(); expect(state.notice).not.toBe("panel.update.updated");
});
it("verifies the approved version and cancels its delayed page reload on unmount", async () => {
  const { state } = await mount(); await state.installUpdate(); read = async config => response(config, valid({ currentVersion: "0.8.76", isUpdateAvailable: false }));
  await vi.advanceTimersByTimeAsync(5000); expect(state.installPhase).toBe("done"); expect(state.noticeError).toBe(false); unmount(); await vi.advanceTimersByTimeAsync(2000); expect(reload).not.toHaveBeenCalled();
});
it("reloads only after a verified approved version in the active session", async () => {
  const { state } = await mount(); await state.installUpdate(); read = async config => response(config, valid({ currentVersion: "0.8.76", isUpdateAvailable: false }));
  await vi.advanceTimersByTimeAsync(6500); expect(state.installPhase).toBe("done"); expect(reload).toHaveBeenCalledOnce();
});
it("confirms and sends the prepared identity even when the latest release has changed", async () => {
  initial = valid({ latestVersion: "0.8.77", packageSha256: "b".repeat(64), isCheckFailed: true }); const { state } = await mount();
  await state.installUpdate(); expect(JSON.parse(String(posts()[0]!.data))).toEqual({ expectedVersion: "0.8.76", expectedPackageSha256: "a".repeat(64) });
  expect(JSON.stringify(confirm.mock.calls[0])).toContain("0.8.76"); expect(JSON.stringify(confirm.mock.calls[0])).not.toContain("0.8.77");
});
it.each([undefined, ""])("does not offer installation without a measured prepared identity (%s)", async preparedVersion => {
  initial = valid({ preparedVersion }); const { state } = await mount(); await state.installUpdate(); expect(confirm).not.toHaveBeenCalled(); expect(posts()).toHaveLength(0);
});
it("does not claim a same-version reinstall succeeded before the new attempt finishes", async () => {
  initial = valid({ currentVersion: "0.8.76", isUpdateAvailable: false });
  const finished = { phase: "finished", isFinished: true, isFailed: false, error: "", errorKey: "", isDecidedByPanel: false, updatedAtUtc: new Date().toISOString(), installerExitCode: 0 };
  initial.lastAttempt = finished; write = async config => response(config, { ...initial, isInstallScheduled: true });
  const { state } = await mount(); await state.installUpdate(); await vi.advanceTimersByTimeAsync(6500);
  expect(state.installPhase).not.toBe("done"); expect(reload).not.toHaveBeenCalled();
  initial = { ...initial, lastAttempt: { ...finished, updatedAtUtc: new Date().toISOString() } };
  await vi.advanceTimersByTimeAsync(5000); expect(state.installPhase).toBe("done"); expect(reload).toHaveBeenCalledOnce();
});
it("reports a new failed same-version reinstall rather than inferring success from the unchanged version", async () => {
  initial = valid({ currentVersion: "0.8.76", isUpdateAvailable: false }); write = async config => response(config, { ...initial, isInstallScheduled: true });
  const { state } = await mount(); await state.installUpdate(); initial = { ...initial, lastAttempt: { phase: "failed", isFinished: true, isFailed: true, error: "", errorKey: "update.install_failed", isDecidedByPanel: false, updatedAtUtc: new Date().toISOString(), installerExitCode: 1 } };
  await vi.advanceTimersByTimeAsync(6500); expect(state.installPhase).toBe("failed"); expect(state.noticeError).toBe(true); expect(reload).not.toHaveBeenCalled();
});

async function finishObservedInstallation(phase: "failed" | "timeout") {
  const { state } = await mount();
  await state.installUpdate();
  if (phase === "failed") {
    initial = valid({ lastAttempt: { phase: "failed", isFinished: true, isFailed: true,
      error: "", errorKey: "update.install_failed", isDecidedByPanel: false,
      updatedAtUtc: new Date().toISOString(), installerExitCode: 1 } });
    await vi.advanceTimersByTimeAsync(5000);
  } else {
    read = async () => { throw new Error("panel restarting"); };
    await vi.advanceTimersByTimeAsync(15 * 60_000);
  }
  expect(state.installPhase).toBe(phase);
  return state;
}

it.each(["failed", "timeout"] as const)("shows newly verified current status after a %s installation and manual check", async phase => {
  const state = await finishObservedInstallation(phase);
  read = async config => response(config, valid({ currentVersion: "0.8.76", isUpdateAvailable: false,
    isPackageReady: false, preparedVersion: "", preparedPackageSha256: "", packagePath: "" }));
  await state.refresh(true);
  expect(describeUpdateState(state.status, state.statusConfirmed, state.refreshing, state.installPhase)).toMatchObject({ state: "current", tone: "ok" });
  expect(state.installPhase).toBe("");
  expect(state.notice).toBe("");
  expect(reload).not.toHaveBeenCalled();
});

it.each(["failed", "timeout"] as const)("keeps the %s outcome when its manual status check fails", async phase => {
  const state = await finishObservedInstallation(phase);
  read = async () => { throw new Error("still offline"); };
  await state.refresh(true);
  expect(state.installPhase).toBe(phase);
  expect(state.statusConfirmed).toBe(false);
  expect(state.noticeError).toBe(true);
});

it("shows a freshly confirmed scheduled installation instead of the old timeout", async () => {
  const state = await finishObservedInstallation("timeout");
  read = async config => response(config, valid({ isInstallScheduled: true, installScheduledAtUtc: new Date().toISOString() }));
  await state.refresh(true);
  expect(state.status?.isInstallScheduled).toBe(true);
  expect(describeUpdateState(state.status, state.statusConfirmed, state.refreshing, state.installPhase).state).toBe("installing");
  await state.installUpdate();
  expect(posts()).toHaveLength(1);
});

it("does not replace active installation monitoring with a manual check", async () => {
  const { state } = await mount(); await state.installUpdate();
  const reads = gets().length;
  await state.refresh(true);
  expect(gets()).toHaveLength(reads);
  expect(state.installPhase).toBe("scheduled");
  read = async config => response(config, valid({ currentVersion: "0.8.76", isUpdateAvailable: false }));
  await vi.advanceTimersByTimeAsync(5000);
  expect(state.installPhase).toBe("done");
});
