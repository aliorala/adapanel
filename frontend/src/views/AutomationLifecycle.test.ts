import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createRenderer, h, nextTick, ssrContextKey, type App, type Component } from "vue";
import { CanceledError, type AxiosResponse, type InternalAxiosRequestConfig } from "axios";
import AutomationView from "./AutomationView.vue";
import * as automation from "../api/automation";
import * as backup from "../api/backup";
import { apiClient, invalidateSessionRequests } from "../api/client";
import type { BackupSchedulePlan } from "../api/schedule";

vi.mock("../api/automation", () => ({ getAutomation: vi.fn(), runAutomationBackup: vi.fn(), runAutomationSsl: vi.fn(), updateAutomation: vi.fn(), testRemoteBackup: vi.fn() }));
vi.mock("../api/backup", () => ({ getBackupRunningJobs: vi.fn() }));
vi.mock("../stores/auth", () => ({ extractApiMessage: (error: Error) => error.message }));
vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key, locale: { value: "en" } }) }));

type State = {
  status: automation.AutomationStatus | null; form: automation.AutomationSettings; schedulePlan: BackupSchedulePlan;
  loading: boolean; settingsLoaded: boolean; busy: boolean; backupRunning: boolean;
  notice: string; noticeError: boolean; remotePassword: string; testingRemote: boolean;
  remoteTestResult: automation.BackupRemoteTestResult | null;
  refresh(): Promise<void>; save(): Promise<void>; runBackupNow(): Promise<void>; runSslNow(): Promise<void>; testRemote(): Promise<void>;
};
let app: App | undefined;
const originalAdapter = apiClient.defaults.adapter;
const jobs = (running = false) => ({ running: running ? [{ kind: "backup", title: "automation", startedAtUtc: "2026-10-03T00:00:00Z" }] : [], interrupted: [], serverTimeUtc: "2026-10-03T00:01:00Z" });
function status(retention = 14): automation.AutomationStatus {
  return { settings: { backupRetentionDays: retention, backupDatabaseEngines: ["mysql"], backupTargetType: "ftp", backupRemoteUrl: "ftp://a.example.test/backups", backupRemoteUserName: "alice", backupRemoteRegion: "", backupRemotePasswordSet: true, backupScheduleMode: "daily", backupIntervalHours: 24, backupScheduleHour: 3, backupScheduleMinute: 0, backupScheduleDayOfWeek: 1, backupScheduleDayOfMonth: 1 }, lastBackupStatus: "completed", lastBackupDetails: "", lastSslStatus: "completed", lastSslDetails: "" } as automation.AutomationStatus;
}
function deferred<T>() {
  let resolve!: (value: T) => void, reject!: (error: unknown) => void;
  const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
}
async function flush() { for (let i = 0; i < 32; i++) await nextTick(); }
function unmount() { app?.unmount(); app = undefined; }
async function mount() {
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  app = renderer.createApp({ ...AutomationView, render: () => h("section") } as Component);
  app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: State }).setupState;
  await flush();
  return state;
}
beforeEach(() => {
  invalidateSessionRequests();
  vi.mocked(automation.getAutomation).mockResolvedValue(status());
  vi.mocked(backup.getBackupRunningJobs).mockResolvedValue(jobs());
  vi.mocked(automation.updateAutomation).mockResolvedValue(status(21));
  vi.mocked(automation.runAutomationBackup).mockResolvedValue(status());
  vi.mocked(automation.runAutomationSsl).mockResolvedValue(status());
  vi.mocked(automation.testRemoteBackup).mockResolvedValue({ success: true, message: "backup.remote_ok", details: "" });
});
afterEach(() => { unmount(); apiClient.defaults.adapter = originalAdapter; invalidateSessionRequests(); vi.resetAllMocks(); vi.useRealTimers(); vi.unstubAllGlobals(); });

it("does not let an older refresh overwrite a newer refresh", async () => {
  const state = await mount(), old = deferred<automation.AutomationStatus>();
  vi.mocked(automation.getAutomation).mockReturnValueOnce(old.promise).mockResolvedValueOnce(status(30));
  const first = state.refresh(); await flush(); await state.refresh();
  old.resolve(status(7)); await first;
  expect(state.form.backupRetentionDays).toBe(30);
  expect(state.status?.settings.backupRetentionDays).toBe(30);
});

it("keeps the newer refresh loading when an older one finishes", async () => {
  const state = await mount(), old = deferred<automation.AutomationStatus>(), recent = deferred<automation.AutomationStatus>();
  vi.mocked(automation.getAutomation).mockReturnValueOnce(old.promise).mockReturnValueOnce(recent.promise);
  const first = state.refresh(); await flush(); const second = state.refresh(); await flush();
  old.resolve(status(7)); await first; expect(state.loading).toBe(true);
  recent.resolve(status(30)); await second; expect(state.loading).toBe(false);
});

it("ignores an obsolete refresh error after a newer successful read", async () => {
  const state = await mount(), old = deferred<automation.AutomationStatus>();
  vi.mocked(automation.getAutomation).mockReturnValueOnce(old.promise).mockResolvedValueOnce(status(30));
  const first = state.refresh(); await flush(); await state.refresh();
  old.reject(new Error("obsolete read")); await first;
  expect(state.notice).toBe(""); expect(state.form.backupRetentionDays).toBe(30);
});

it.each(["success", "failure"])("invalidates an older GET when saving settings (%s)", async outcome => {
  const state = await mount(), old = deferred<automation.AutomationStatus>();
  vi.mocked(automation.getAutomation).mockReturnValueOnce(old.promise);
  const reading = state.refresh(); await flush();
  state.form.backupRetentionDays = 21; await state.save();
  if (outcome === "success") old.resolve(status(7)); else old.reject(new Error("old GET failed"));
  await reading;
  expect(state.form.backupRetentionDays).toBe(21); expect(state.status?.settings.backupRetentionDays).toBe(21);
  expect(state.notice).toBe("panel.automation.saved"); expect(state.noticeError).toBe(false); expect(state.loading).toBe(false);
});

it("preserves a draft edited while refresh was pending but still updates measured status", async () => {
  const state = await mount(), old = deferred<automation.AutomationStatus>();
  vi.mocked(automation.getAutomation).mockReturnValueOnce(old.promise);
  const reading = state.refresh(); await flush();
  state.form.backupRetentionDays = 99; state.schedulePlan.hour = 22;
  old.resolve(status(7)); await reading;
  expect(state.form.backupRetentionDays).toBe(99); expect(state.schedulePlan.hour).toBe(22);
  expect(state.status?.settings.backupRetentionDays).toBe(7);
});

it("preserves draft and replacement password edited during save while freezing its request", async () => {
  const state = await mount(), pending = deferred<automation.AutomationStatus>();
  vi.mocked(automation.updateAutomation).mockReturnValueOnce(pending.promise);
  state.remotePassword = "submitted-secret";
  const saving = state.save();
  const submitted = vi.mocked(automation.updateAutomation).mock.calls[0]![0];
  state.form.backupRetentionDays = 99; state.form.backupDatabaseEngines.push("postgresql"); state.schedulePlan.hour = 22; state.remotePassword = "next-secret";
  pending.resolve(status(21)); await saving;
  expect(submitted.backupDatabaseEngines).toEqual(["mysql"]);
  expect(submitted.backupRemotePassword).toBe("submitted-secret");
  expect(state.form.backupRetentionDays).toBe(99); expect(state.schedulePlan.hour).toBe(22); expect(state.remotePassword).toBe("next-secret");
  expect(state.status?.settings.backupRetentionDays).toBe(21);
});

it("applies normalized saved settings and clears an unchanged submitted password", async () => {
  const state = await mount(); state.remotePassword = "submitted-secret"; await state.save();
  expect(state.form.backupRetentionDays).toBe(21); expect(state.remotePassword).toBe("");
  expect(state.notice).toBe("panel.automation.saved");
});

it("keeps the same password text when it now belongs to a different remote target draft", async () => {
  const state = await mount(), pending = deferred<automation.AutomationStatus>();
  vi.mocked(automation.updateAutomation).mockReturnValueOnce(pending.promise);
  state.remotePassword = "submitted-secret";
  const saving = state.save();
  state.form.backupRemoteUrl = "ftp://next.example.test";
  state.form.backupRemoteUserName = "next-user";
  pending.resolve(status(21)); await saving;
  expect(state.form.backupRemoteUrl).toBe("ftp://next.example.test");
  expect(state.form.backupRemoteUserName).toBe("next-user");
  expect(state.remotePassword).toBe("submitted-secret");
});

for (const [action, api] of [["runBackupNow", automation.runAutomationBackup], ["runSslNow", automation.runAutomationSsl]] as const) {
  it.each(["before", "during"])(`${action} keeps unsaved settings edited %s execution`, async when => {
    const state = await mount(), pending = deferred<automation.AutomationStatus>(); vi.mocked(api).mockReturnValueOnce(pending.promise);
    if (when === "before") state.form.backupRetentionDays = 99;
    const running = state[action]();
    if (when === "during") state.form.backupRetentionDays = 99;
    pending.resolve(status()); await running;
    expect(state.form.backupRetentionDays).toBe(99); expect(state.status?.lastBackupStatus).toBe("completed");
  });
}

it.each(["save", "runSslNow"] as const)("does not admit duplicate %s or a competing action", async action => {
  const state = await mount(), pending = deferred<automation.AutomationStatus>();
  const api = action === "save" ? automation.updateAutomation : automation.runAutomationSsl;
  vi.mocked(api).mockReturnValueOnce(pending.promise);
  const first = state[action](); await state[action]();
  if (action === "save") await state.runSslNow(); else await state.save();
  expect(api).toHaveBeenCalledOnce();
  expect(action === "save" ? automation.runAutomationSsl : automation.updateAutomation).not.toHaveBeenCalled();
  expect(state.busy).toBe(true);
  pending.resolve(status()); await first; expect(state.busy).toBe(false);
});

it("does not refresh over an outstanding mutation", async () => {
  const state = await mount(), pending = deferred<automation.AutomationStatus>();
  vi.mocked(automation.updateAutomation).mockReturnValueOnce(pending.promise); vi.mocked(automation.getAutomation).mockClear();
  const saving = state.save(); await state.refresh(); expect(automation.getAutomation).not.toHaveBeenCalled();
  pending.resolve(status()); await saving;
});

it.each(["url", "password", "target", "user", "region"])("discards a remote test when its %s changes", async changed => {
  const state = await mount(), pending = deferred<automation.BackupRemoteTestResult>();
  vi.mocked(automation.testRemoteBackup).mockReturnValueOnce(pending.promise);
  const testing = state.testRemote();
  if (changed === "url") state.form.backupRemoteUrl = "ftp://b.example.test/backups";
  else if (changed === "password") state.remotePassword = "different-secret";
  else if (changed === "target") state.form.backupTargetType = "webdav";
  else if (changed === "user") state.form.backupRemoteUserName = "bob";
  else state.form.backupRemoteRegion = "eu-west-1";
  pending.resolve({ success: true, message: "ok", details: "" }); await testing;
  expect(state.remoteTestResult).toBeNull(); expect(state.testingRemote).toBe(false);
});

it("clears a completed remote measurement immediately after changing its target", async () => {
  const state = await mount(); await state.testRemote(); expect(state.remoteTestResult?.success).toBe(true);
  state.form.backupRemoteUrl = "ftp://different.example.test"; await flush();
  expect(state.remoteTestResult).toBeNull();
});

it("does not restore an old remote measurement after the target changes away and back", async () => {
  const state = await mount(), pending = deferred<automation.BackupRemoteTestResult>(); vi.mocked(automation.testRemoteBackup).mockReturnValueOnce(pending.promise);
  const testing = state.testRemote(), original = state.form.backupRemoteUrl;
  state.form.backupRemoteUrl = "ftp://b.example.test"; state.form.backupRemoteUrl = original;
  pending.resolve({ success: true, message: "ok", details: "" }); await testing;
  expect(state.remoteTestResult).toBeNull();
});

it("deduplicates remote tests and allows retry when the first request ends", async () => {
  const state = await mount(), pending = deferred<automation.BackupRemoteTestResult>(); vi.mocked(automation.testRemoteBackup).mockReturnValueOnce(pending.promise);
  const testing = state.testRemote(); await state.testRemote(); expect(automation.testRemoteBackup).toHaveBeenCalledOnce();
  pending.reject(new Error("connection failed")); await testing;
  expect(state.remoteTestResult).toEqual({ success: false, message: "connection failed", details: "" });
  await state.testRemote(); expect(automation.testRemoteBackup).toHaveBeenCalledTimes(2); expect(state.remoteTestResult?.success).toBe(true);
});

it.each(["unmount", "session"])("does not continue the initial read after %s even with real client cancellation", async context => {
  vi.stubGlobal("window", { location: { pathname: "/panel" } });
  const realBackup = await vi.importActual<typeof import("../api/backup")>("../api/backup");
  const realAutomation = await vi.importActual<typeof import("../api/automation")>("../api/automation");
  vi.mocked(backup.getBackupRunningJobs).mockImplementation(realBackup.getBackupRunningJobs);
  vi.mocked(automation.getAutomation).mockImplementation(realAutomation.getAutomation);
  const pending = deferred<AxiosResponse>(); let jobRequest: InternalAxiosRequestConfig | undefined;
  apiClient.defaults.adapter = async config => {
    if (config.url?.includes("/running")) { jobRequest = config; return pending.promise; }
    return { config, status: 200, statusText: "OK", headers: {}, data: { data: status() } };
  };
  const state = await mount(); expect(jobRequest).toBeDefined();
  if (context === "unmount") unmount(); else invalidateSessionRequests();
  pending.resolve({ config: jobRequest!, status: 200, statusText: "OK", headers: {}, data: { data: jobs() } }); await flush();
  expect(automation.getAutomation).not.toHaveBeenCalled(); expect(state.status).toBeNull();
});

it.each(["save", "runBackupNow", "runSslNow"] as const)("ignores late %s success after navigation", async action => {
  const state = await mount(), pending = deferred<automation.AutomationStatus>();
  const api = action === "save" ? automation.updateAutomation : action === "runBackupNow" ? automation.runAutomationBackup : automation.runAutomationSsl;
  vi.mocked(api).mockReturnValueOnce(pending.promise);
  const executing = state[action](); unmount();
  pending.resolve(status(99)); await executing;
  expect(state.status?.settings.backupRetentionDays).toBe(14); expect(state.notice).toBe("");
});

it.each(["save", "runBackupNow", "runSslNow", "testRemote"] as const)("does not publish a cancelled old session result from %s", async action => {
  const state = await mount(), pending = deferred<never>();
  const api = action === "save" ? automation.updateAutomation : action === "runBackupNow" ? automation.runAutomationBackup : action === "runSslNow" ? automation.runAutomationSsl : automation.testRemoteBackup;
  vi.mocked(api).mockReturnValueOnce(pending.promise);
  const executing = state[action](); invalidateSessionRequests(); vi.mocked(backup.getBackupRunningJobs).mockClear();
  pending.reject(new CanceledError("Session changed")); await executing;
  expect(state.notice).toBe(""); expect(state.remoteTestResult).toBeNull(); expect(backup.getBackupRunningJobs).not.toHaveBeenCalled();
});

it("does not start another poll in a changed session while the old view still exists", async () => {
  vi.useFakeTimers(); vi.mocked(backup.getBackupRunningJobs).mockResolvedValue(jobs(true));
  await mount(); vi.mocked(backup.getBackupRunningJobs).mockClear(); invalidateSessionRequests();
  await vi.advanceTimersByTimeAsync(30_000); expect(backup.getBackupRunningJobs).not.toHaveBeenCalled();
});

it("does not dispatch writes before settings have been measured", async () => {
  vi.mocked(automation.getAutomation).mockRejectedValue(new Error("settings unavailable"));
  const state = await mount(); await state.save(); await state.runBackupNow(); await state.runSslNow(); await state.testRemote();
  expect(automation.updateAutomation).not.toHaveBeenCalled(); expect(automation.runAutomationBackup).not.toHaveBeenCalled();
  expect(automation.runAutomationSsl).not.toHaveBeenCalled(); expect(automation.testRemoteBackup).not.toHaveBeenCalled();
});

it("clears the failed refresh notice after a successful retry", async () => {
  vi.mocked(automation.getAutomation).mockRejectedValueOnce(new Error("settings unavailable"));
  const state = await mount(); expect(state.notice).toBe("settings unavailable");
  await state.refresh();
  expect(state.settingsLoaded).toBe(true); expect(state.notice).toBe(""); expect(state.noticeError).toBe(false);
});

it("does not clear a later mutation error when retrying an earlier failed read", async () => {
  const state = await mount();
  vi.mocked(automation.getAutomation).mockRejectedValueOnce(new Error("same diagnostic")); await state.refresh();
  vi.mocked(automation.runAutomationSsl).mockRejectedValueOnce(new Error("same diagnostic")); await state.runSslNow();
  await state.refresh(); expect(state.notice).toBe("same diagnostic"); expect(state.noticeError).toBe(true);
});

it("discards a remote error for an obsolete target and releases its busy state", async () => {
  const state = await mount(), pending = deferred<automation.BackupRemoteTestResult>();
  vi.mocked(automation.testRemoteBackup).mockReturnValueOnce(pending.promise);
  const testing = state.testRemote(); state.form.backupRemoteUrl = "ftp://new.example.test";
  pending.reject(new Error("old target failed")); await testing;
  expect(state.remoteTestResult).toBeNull(); expect(state.testingRemote).toBe(false);
  await state.testRemote(); expect(state.remoteTestResult?.success).toBe(true);
});

it("serializes remote measurement with save and job execution in either direction", async () => {
  const state = await mount(), pending = deferred<automation.BackupRemoteTestResult>();
  vi.mocked(automation.testRemoteBackup).mockReturnValueOnce(pending.promise);
  const testing = state.testRemote(); await state.save(); await state.runSslNow(); await state.runBackupNow();
  expect(automation.updateAutomation).not.toHaveBeenCalled(); expect(automation.runAutomationSsl).not.toHaveBeenCalled(); expect(automation.runAutomationBackup).not.toHaveBeenCalled();
  pending.resolve({ success: true, message: "ok", details: "" }); await testing;
  const savingResult = deferred<automation.AutomationStatus>(); vi.mocked(automation.updateAutomation).mockReturnValueOnce(savingResult.promise);
  const saving = state.save(); await state.testRemote(); expect(automation.testRemoteBackup).toHaveBeenCalledOnce();
  savingResult.resolve(status()); await saving;
  await state.testRemote(); expect(automation.testRemoteBackup).toHaveBeenCalledTimes(2);
});
