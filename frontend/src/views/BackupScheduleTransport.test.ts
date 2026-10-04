import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createPinia } from "pinia";
import { createRenderer, h, nextTick, ssrContextKey, type App, type Component, type Ref } from "vue";
import type { AxiosResponse, InternalAxiosRequestConfig } from "axios";
import BackupView from "./BackupView.vue";
import { apiClient, invalidateSessionRequests } from "../api/client";
import type { SubscriptionBackupSchedule } from "../api/backup";
import { useAuthStore } from "../stores/auth";
import { useSitesStore } from "../stores/sites";

const dialogs = vi.hoisted(() => ({ confirm: vi.fn() }));
vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string, params?: object) => params ? `${key} ${JSON.stringify(params)}` : key, locale: { value: "en" } }) }));
vi.mock("../components/ConfirmDialog.vue", () => ({ useConfirm: () => ({ confirm: dialogs.confirm }) }));
vi.mock("../components/PromptDialog.vue", () => ({ usePrompt: () => ({ prompt: vi.fn() }) }));
const userId = "12345678-1234-5678-90ab-123456789012", otherId = "87654321-4321-8765-90ab-210987654321";
function schedule(id = userId): SubscriptionBackupSchedule { return { userId: id, userName: id === userId ? "Tenant A" : "Tenant B", enabled: true, scheduleMode: "interval", intervalHours: 24, scheduleHour: 3, scheduleMinute: 0, scheduleDayOfWeek: 1, scheduleDayOfMonth: 1, retentionDays: 14, keepCount: 7, incremental: false, fullBackupEveryDays: 7, includeFiles: true, includeDatabases: true, includeMail: true, includeFtp: true, targetType: "local", remoteUrl: "", remoteUserName: "", remoteRegion: "", remotePasswordSet: false, lastRunUtc: "2026-10-04T09:00:00Z", lastStatus: "completed", lastDetails: "", nextRunUtc: null, planId: "", planName: "", maxBackupsPerDay: 0, throttledByQuota: false }; }
function response(config: InternalAxiosRequestConfig, data: unknown, success = true): AxiosResponse { return { config, data: { success, data }, status: 200, statusText: "OK", headers: {} }; }
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(yes => { resolve = yes; }); return { promise, resolve }; }
async function flush() { for (let i = 0; i < 45; i++) await nextTick(); }
type State = { notice: string; noticeError: boolean; busy: boolean; schedules: SubscriptionBackupSchedule[]; schedulesLoaded: boolean; schedulesLoad: { message: Ref<string> }; scheduleQuotaMessage: string; scheduleForm: (SubscriptionBackupSchedule & { plan: { mode: string; intervalHours: number; hour: number; secondHour: number | null }; remotePassword: string }) | null; openScheduleForm(id: string): Promise<void>; loadSchedules(): Promise<boolean>; saveSchedule(): Promise<void>; removeSchedule(row: SubscriptionBackupSchedule): Promise<void>; runScheduleNow(row: SubscriptionBackupSchedule): Promise<void> };
let app: App | undefined, calls: InternalAxiosRequestConfig[], read: (config: InternalAxiosRequestConfig) => Promise<AxiosResponse>, write: (config: InternalAxiosRequestConfig) => Promise<AxiosResponse>, list: (config: InternalAxiosRequestConfig) => Promise<AxiosResponse>;
const original = apiClient.defaults.adapter;
beforeEach(() => {
  vi.resetAllMocks(); invalidateSessionRequests(); calls = [];
  vi.stubGlobal("window", { location: { pathname: "/panel" }, setTimeout, clearTimeout, setInterval, clearInterval, addEventListener: vi.fn(), removeEventListener: vi.fn() });
  vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ success: true, data: { requestToken: "fixture" } }), { status: 200 })));
  dialogs.confirm.mockImplementation(async options => { await options.action?.(); return true; });
  read = async config => response(config, schedule()); list = async config => response(config, [schedule()]); write = async config => response(config, config.method === "delete" ? { userId } : schedule());
  apiClient.defaults.adapter = async config => {
    calls.push(config);
    if (config.url === "/backup/schedules") return list(config);
    if (config.url?.startsWith("/backup/schedules/")) return config.method === "get" ? read(config) : write(config);
    return response(config, config.url === "/automation" ? { settings: {} } : config.url === "/backup/encryption" ? { enabled: false, passwordUsable: false, algorithm: "AES", updatedAtUtc: "" } : config.url?.includes("running") ? { running: [], interrupted: [], serverTimeUtc: "2026-10-04T12:00:00Z" } : []);
  };
});
afterEach(() => { app?.unmount(); app = undefined; apiClient.defaults.adapter = original; invalidateSessionRequests(); vi.unstubAllGlobals(); });
async function mount() {
  const pinia = createPinia(), auth = useAuthStore(pinia), sites = useSitesStore(pinia);
  auth.session = { isAuthenticated: true, requiresSetupWizard: false, user: { id: "admin", role: "Administrator", userName: "admin", email: "admin@example.test", preferredLanguage: "en", isActive: true, isTwoFactorEnabled: false, lastLoginAtUtc: null } };
  auth.users = [{ id: userId, role: "Customer", userName: "Tenant A" }, { id: otherId, role: "Customer", userName: "Tenant B" }] as never;
  sites.sites = [{ name: "alpha.test" }] as never; sites.selectedSiteName = "alpha.test";
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  app = renderer.createApp({ ...BackupView, render: () => h("section") } as Component).use(pinia); app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: State }).setupState; await flush(); state.notice = ""; return { state, auth };
}
it("does not turn the edit button for A into a saved program for B", async () => {
  const { state } = await mount(); read = async config => response(config, schedule(otherId));
  await state.openScheduleForm(userId); await state.saveSchedule();
  expect(calls.filter(config => config.method === "put")).toHaveLength(0); expect(state.scheduleForm).toBeNull(); expect(state.noticeError).toBe(true);
});
it.each(["save", "run", "delete"])("does not report %s success from a failed HTTP envelope", async operation => {
  const { state } = await mount(); await state.openScheduleForm(userId);
  write = async config => response(config, operation === "delete" ? { userId } : schedule(), false);
  if (operation === "save") { state.scheduleForm!.remotePassword = "submitted"; await state.saveSchedule(); } else if (operation === "run") await state.runScheduleNow(schedule()); else await state.removeSchedule(schedule());
  expect(state.noticeError).toBe(true); expect(state.notice).toContain("panel.backup.schedule.resultUnconfirmed"); expect(state.scheduleForm?.userId).toBe(userId);
  if (operation === "save") expect(state.scheduleForm?.remotePassword).toBe("submitted");
});
it.each(["save", "run"])("does not publish another subscription's %s acknowledgement", async operation => {
  const { state } = await mount(); await state.openScheduleForm(userId); write = async config => response(config, schedule(otherId));
  if (operation === "save") await state.saveSchedule(); else await state.runScheduleNow(schedule());
  expect(state.noticeError).toBe(true); expect(state.scheduleForm?.userId).toBe(userId); expect(state.notice).toContain("panel.backup.schedule.resultUnconfirmed");
});
it("keeps measured rows but marks a malformed refresh unconfirmed", async () => {
  const { state } = await mount(); await state.loadSchedules(); list = async config => response(config, [{ ...schedule(), enabled: "false" }]);
  await state.loadSchedules(); expect(state.schedulesLoaded).toBe(false); expect(state.schedules[0]?.enabled).toBe(true); expect(state.schedulesLoad.message.value).not.toBe("");
});
it.each(["interval", "daily"])("reports the actual quota-adjusted %s schedule without inventing another timing mode", async mode => {
  const { state } = await mount(); await state.openScheduleForm(userId); state.scheduleForm!.plan.mode = mode; state.scheduleForm!.plan.secondHour = mode === "daily" ? 15 : null;
  write = async config => response(config, { ...schedule(), scheduleMode: mode, intervalHours: 24, scheduleSecondHour: null, throttledByQuota: true, maxBackupsPerDay: 1 });
  await state.saveSchedule(); expect(state.scheduleForm?.plan.mode).toBe(mode); expect(state.scheduleForm?.plan.secondHour).toBeNull(); expect(state.noticeError).toBe(true);
  expect(state.notice).toContain(mode === "daily" ? "panel.backup.schedule.throttledCalendar" : "panel.backup.schedule.throttled");
  if (mode === "daily") { expect(state.notice).toContain("panel.schedule.summaryDaily"); expect(state.notice).not.toContain('"hours"'); }
});
it("keeps a newer editable draft while a measured save updates the saved schedule list", async () => {
  const { state } = await mount(); await state.openScheduleForm(userId); state.scheduleForm!.retentionDays = 21;
  const gate = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig; write = config => (request = config, gate.promise);
  const saving = state.saveSchedule(); await flush(); state.scheduleForm!.retentionDays = 45; state.scheduleForm!.remotePassword = "next-secret";
  gate.resolve(response(request, { ...schedule(), retentionDays: 21 })); await saving;
  expect(JSON.parse(String(request.data)).retentionDays).toBe(21); expect(state.scheduleForm).toMatchObject({ retentionDays: 45, remotePassword: "next-secret" }); expect(state.notice).toBe("backup.schedule_saved");
});
it.each(["interval", "daily"])("keeps the accepted %s quota warning separate from later draft edits", async mode => {
  const { state } = await mount();
  read = async config => response(config, { ...schedule(), scheduleMode: mode, intervalHours: 24, scheduleHour: 3, scheduleSecondHour: null, throttledByQuota: true, maxBackupsPerDay: 1 });
  await state.openScheduleForm(userId);
  const accepted = state.scheduleQuotaMessage;
  expect(accepted).toContain(mode === "daily" ? "03:00" : '"hours":24');
  state.scheduleForm!.plan.intervalHours = 48; state.scheduleForm!.plan.hour = 6; state.scheduleForm!.plan.secondHour = 18;
  expect(state.scheduleQuotaMessage).toBe(accepted);
  expect(state.scheduleQuotaMessage).not.toContain(mode === "daily" ? "06:00" : '"hours":48');
});
it.each(["completed", "completed_with_errors", "failed"])("preserves a measured %s run and its details", async lastStatus => {
  const { state } = await mount(); write = async config => response(config, { ...schedule(), lastStatus, lastDetails: "Measured run detail" });
  await state.runScheduleNow(schedule()); expect(state.noticeError).toBe(lastStatus !== "completed"); expect(state.notice).toContain("Measured run detail");
});
it("accepts a normal deletion and refreshes the saved programs", async () => {
  const { state } = await mount(); await state.openScheduleForm(userId); list = async config => response(config, []);
  await state.removeSchedule(schedule()); expect(state.scheduleForm).toBeNull(); expect(state.schedules).toEqual([]); expect(state.notice).toBe("backup.schedule_deleted"); expect(state.noticeError).toBe(false);
});
