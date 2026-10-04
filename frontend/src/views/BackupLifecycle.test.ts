import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { createRenderer, h, nextTick, ssrContextKey, type App, type Component, type Ref } from "vue";
import BackupView from "./BackupView.vue";
import * as api from "../api/backup";
import { getAutomation, updateAutomation, type AutomationSettings, type AutomationStatus } from "../api/automation";
import { getPanelPreferences } from "../api/panelSettings";
import { useAuthStore } from "../stores/auth";
import { useSitesStore } from "../stores/sites";
import { useSubscriptionStore } from "../stores/subscription";
import { UserFacingError } from "../utils/apiError";

const shared = vi.hoisted(() => ({ epoch: 0, confirm: vi.fn(), prompt: vi.fn(), translate: vi.fn() }));
vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: shared.translate, locale: { value: "en" } }) }));
vi.mock("../components/ConfirmDialog.vue", () => ({ useConfirm: () => ({ confirm: shared.confirm }) }));
vi.mock("../components/PromptDialog.vue", () => ({ usePrompt: () => ({ prompt: shared.prompt }) }));
vi.mock("../api/client", async original => ({ ...await original<object>(), getSessionEpoch: () => shared.epoch }));
vi.mock("../api/backup", async original => {
  const actual = await original<Record<string, unknown>>();
  return Object.fromEntries(Object.entries(actual).map(([key, value]) => [key, typeof value === "function" ? vi.fn() : value]));
});
vi.mock("../api/automation", () => ({ getAutomation: vi.fn(), updateAutomation: vi.fn() }));
vi.mock("../api/panelSettings", () => ({ getPanelPreferences: vi.fn() }));

type State = {
  expandedLocation: string;
  backups: api.BackupRecord[]; listConfirmed: boolean; loading: boolean; busy: boolean; notice: string; noticeError: boolean;
  listLoad: { message: Ref<string> }; chainLoad: { message: Ref<string> }; chain: api.BackupChain | null;
  chainRecord: api.BackupRecord | null; chainLoading: boolean; verification: api.BackupVerificationResult | null;
  browseEntries: api.BackupEntry[]; browseRecord: api.BackupRecord | null; browseLoading: boolean;
  backupKind: string; targetForm: api.BackupTargetPayload; selectedItems: string[];
  databaseEngine: string; databaseName: string; restoreItemsBusy: boolean;
  activeView: string; schedules: api.SubscriptionBackupSchedule[]; schedulesLoading: boolean; schedulesLoaded: boolean;
  schedulesLoad: { message: Ref<string> }; scheduleForm: { userId: string; remotePassword: string; retentionDays: number; plan: { intervalHours: number } } | null;
  autoForm: AutomationSettings | null; autoStatus: AutomationStatus | null; autoLoad: { message: Ref<string> };
  encryption: api.BackupEncryptionSettings | null; encryptionForm: { password: string; confirm: string; acknowledged: boolean };
  loadSchedules(): Promise<unknown>; openScheduleForm(id: string): Promise<void>; saveSchedule(): Promise<void>;
  closeScheduleForm(): void; retryFailedLoads(): Promise<void>;
  removeSchedule(schedule: api.SubscriptionBackupSchedule): Promise<void>; runScheduleNow(schedule: api.SubscriptionBackupSchedule): Promise<void>;
  loadAutoBackup(): Promise<unknown>; saveAutoBackup(): Promise<void>; loadEncryption(): Promise<void>; saveEncryption(): Promise<void>; disableEncryption(): Promise<void>;
  chainDeleteTargets(record: api.BackupRecord): api.BackupRecord[];
  refresh(): Promise<unknown>; openChain(record: api.BackupRecord): Promise<void>; closeChain(): void;
  removeBackup(record: api.BackupRecord): Promise<void>; removeChain(record: api.BackupRecord): Promise<void>;
  restore(record: api.BackupRecord): Promise<void>; createSelectedBackup(): Promise<void>;
  verify(): Promise<void>; openBrowser(record: api.BackupRecord): Promise<void>; closeBrowser(): void;
  restoreSelectedItems(): Promise<void>;
  createDatabaseToConfiguredFtp(): Promise<void>; retryRemote(record: api.BackupRecord): Promise<void>;
};
let app: App | undefined;
function renderMarkup(state: State) {
  const output: unknown[] = [];
  const render = (BackupView as unknown as { ssrRender: (...args: unknown[]) => void }).ssrRender;
  render(state, (part: unknown) => output.push(part), app!._instance, {}, {}, state, {}, {});
  return output.flat(Infinity).join("").replace(/<!--[\s\S]*?-->/g, "");
}
function record(id = "first"): api.BackupRecord {
  return { id, name: id, type: "site", source: "site.test", path: `/backups/${id}.zip`, format: "zip", createdAtUtc: "2026-10-03T10:00:00Z", size: 10, status: "created", targetType: "local", remoteUri: "", encrypted: false, warnings: [], preservedPath: "", chainId: "", chainIndex: 0, backupMode: "full", baseBackupId: "", subscriptionUserId: "", itemCount: 1, localMissing: false, remoteMissing: false, remoteVerifiedUtc: null };
}
function chain(id = "first"): api.BackupChain { return { chainId: id, links: [], broken: false, missingLinks: [] }; }
function schedule(userId = "tenant"): api.SubscriptionBackupSchedule {
  return { userId, userName: userId, enabled: true, scheduleMode: "interval", intervalHours: 24, scheduleHour: 2, scheduleMinute: 0, scheduleDayOfWeek: 0, scheduleDayOfMonth: 1, retentionDays: 14, keepCount: 7, incremental: false, fullBackupEveryDays: 7, includeFiles: true, includeDatabases: true, includeMail: true, includeFtp: true, targetType: "local", remoteUrl: "", remoteUserName: "", remoteRegion: "", remotePasswordSet: false, lastRunUtc: null, lastStatus: "completed", lastDetails: "", nextRunUtc: null, planId: "", planName: "", maxBackupsPerDay: 0, throttledByQuota: false };
}
function automation(retention = 14): AutomationStatus { return { settings: { backupRetentionDays: retention, autoBackupEnabled: true, backupScheduleMode: "interval", backupIntervalHours: 24, backupScheduleHour: 2, backupScheduleMinute: 0, backupScheduleDayOfWeek: 0, backupScheduleDayOfMonth: 1, backupDatabaseEngines: ["postgresql"], backupTargetType: "local", backupRemoteUrl: "" } } as AutomationStatus; }
function deferred<T>() { let resolve!: (value: T) => void, reject!: (error: unknown) => void; const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; }
async function flush() { for (let i = 0; i < 32; i++) await nextTick(); }
function unmount() { app!.unmount(); app = undefined; }
beforeEach(() => {
  shared.epoch = 0;
  shared.translate.mockImplementation((key: string) => key);
  shared.confirm.mockImplementation(async options => { await options.action?.(); return true; });
  shared.prompt.mockResolvedValue("");
  vi.mocked(api.getBackups).mockResolvedValue([record()]);
  vi.mocked(api.getBackupRunningJobs).mockResolvedValue({ running: [], interrupted: [], serverTimeUtc: "2026-10-03T10:00:00Z" });
  vi.mocked(api.getBackupChain).mockResolvedValue(chain());
  vi.mocked(api.getBackupEncryption).mockResolvedValue({ enabled: false, passwordUsable: false, updatedAtUtc: "", algorithm: "" });
  vi.mocked(getAutomation).mockResolvedValue(automation());
  vi.mocked(updateAutomation).mockResolvedValue(automation());
  vi.mocked(api.getBackupSchedules).mockResolvedValue([schedule()]);
  vi.mocked(api.getBackupSchedule).mockImplementation(async id => schedule(id));
  vi.mocked(api.updateBackupSchedule).mockImplementation(async id => schedule(id));
  vi.mocked(api.runBackupScheduleNow).mockImplementation(async id => schedule(id));
  vi.mocked(api.setBackupEncryption).mockResolvedValue({ enabled: true, passwordUsable: true, updatedAtUtc: "2026-10-03T12:00:00Z", algorithm: "AES" });
  vi.mocked(api.backupPanel).mockResolvedValue(record("panel"));
  vi.mocked(getPanelPreferences).mockResolvedValue({ defaultBackupRoot: "" } as never);
  vi.mocked(api.backupSite).mockResolvedValue(record("created"));
  vi.mocked(api.backupMail).mockResolvedValue(record("mail"));
  vi.mocked(api.backupDatabase).mockResolvedValue(record("db"));
  vi.mocked(api.retryRemoteUpload).mockResolvedValue({ ...record(), status: "created_remote_synced", remoteUri: "ftp://backup/archive.zip" });
  vi.mocked(api.restoreBackupItems).mockResolvedValue({ restoredCount: 1, targetRoot: "/restored" });
  vi.mocked(api.restoreBackup).mockResolvedValue({ ...record(), status: "restored" });
  vi.mocked(api.deleteBackupChain).mockResolvedValue({ deletedIds: ["first"], deletedNames: ["first"] });
  vi.mocked(api.verifyBackups).mockResolvedValue({ checked: 1, issues: [], orphans: [], orphanScanSkipped: [] } as never);
  vi.mocked(api.getBackupEntries).mockResolvedValue({ prefix: "", entries: [], truncated: false } as never);
});
afterEach(() => { app?.unmount(); app = undefined; vi.restoreAllMocks(); vi.resetAllMocks(); vi.unstubAllGlobals(); });
async function mount() {
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  const pinia = createPinia(); setActivePinia(pinia);
  useSitesStore().sites = [{ name: "site.test" }] as never; useSitesStore().selectedSiteName = "site.test";
  useAuthStore().session = { isAuthenticated: true, user: { id: "admin", role: "Administrator" } } as never;
  useAuthStore().users = [{ id: "tenant", role: "Customer", userName: "tenant" }] as never;
  app = renderer.createApp({ ...BackupView, render: () => h("section") } as Component).use(pinia); app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: State }).setupState; await flush(); return state;
}

it("keeps the list count unconfirmed until the first list request succeeds", async () => {
  const pending = deferred<api.BackupRecord[]>(); vi.mocked(api.getBackups).mockReturnValueOnce(pending.promise);
  const state = await mount(); expect(state.listConfirmed).toBe(false);
  expect(renderMarkup(state)).toMatch(/class="status-pill"[^>]*>…<\/span>/); expect(renderMarkup(state)).not.toContain("0 panel.backup.records");
  pending.resolve([]); await flush(); expect(state.listConfirmed).toBe(true); expect(state.backups).toEqual([]);
  expect(renderMarkup(state)).toContain("0 panel.backup.records");
});
it("marks a failed refresh count unconfirmed while preserving rows and supports retry", async () => {
  const state = await mount(); expect(state.listConfirmed).toBe(true);
  vi.mocked(api.getBackups).mockRejectedValueOnce(new Error("list unavailable")); await state.refresh();
  expect(state.listConfirmed).toBe(false); expect(state.backups).toEqual([record()]);
  expect(renderMarkup(state)).toMatch(/class="status-pill"[^>]*>—<\/span>/); expect(renderMarkup(state)).not.toContain("1 panel.backup.records");
  await state.refresh(); expect(state.listConfirmed).toBe(true);
  expect(renderMarkup(state)).toContain("1 panel.backup.records");
});

it.each(["Customer", "Reseller", "Administrator"])("explains a projected location accurately for %s without hiding backup health", async role => {
  const state = await mount(); useAuthStore().session!.user!.role = role;
  state.backups = [{ ...record(), path: "", localMissing: true, remoteUri: "https://storage.example.test/archive.zip", warnings: [{ messageKey: "backup.database_dump_empty", target: "postgresql/Foo", detail: "" }] }];
  state.expandedLocation = "first";
  const html = renderMarkup(state);
  expect(html).toContain(role === "Administrator" ? "panel.backup.location.unknown" : "panel.backup.location.scopedHidden");
  expect(html).not.toContain(role === "Administrator" ? "panel.backup.location.scopedHidden" : "panel.backup.location.unknown");
  expect(html).toContain("panel.backup.missingLocalBadge");
  expect(html).toContain("panel.backup.remoteTarget");
  expect(html).toContain("storage.example.test/archive.zip");
  expect(html).toContain("panel.backup.warningsBadge");
});
it("keeps the administrator's local archive location and copy control", async () => {
  const state = await mount(); state.expandedLocation = "first";
  const html = renderMarkup(state);
  expect(html).toContain("/backups/first.zip");
  expect(html).toContain("panel.backup.location.copy");
  expect(html).not.toContain("panel.backup.location.scopedHidden");
});
it.each([
  ["site", "owner-id", true],
  ["site", "", true],
  ["subscription", "owner-id", false],
  ["subscription", "", false]
])("renders remote retry by backup type %s with owner stamp %j", async (type, subscriptionUserId, visible) => {
  const state = await mount();
  state.backups = [{ ...record(), type: String(type), subscriptionUserId: String(subscriptionUserId), status: "created_remote_failed", targetType: "ftp", contentHash: "a".repeat(64) }];
  const html = renderMarkup(state);
  if (visible) expect(html).toContain("panel.backup.retryRemote");
  else expect(html).not.toContain("panel.backup.retryRemote");
});

it("keeps the newest list when an older response finishes last", async () => {
  const state = await mount(), pending = deferred<api.BackupRecord[]>(); vi.mocked(api.getBackups).mockReturnValueOnce(pending.promise);
  const first = state.refresh(); vi.mocked(api.getBackups).mockResolvedValueOnce([record("new")]); await state.refresh();
  pending.resolve([record("old")]); await first; expect(state.backups.map(x => x.id)).toEqual(["new"]);
});
it("does not let an older read clear the newest read's loading state", async () => {
  const state = await mount(), first = deferred<api.BackupRecord[]>(), second = deferred<api.BackupRecord[]>();
  vi.mocked(api.getBackups).mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise);
  const a = state.refresh(), b = state.refresh(); first.resolve([]); await a; expect(state.loading).toBe(true); second.resolve([record()]); await b;
});
it("ignores obsolete list errors", async () => {
  const state = await mount(), pending = deferred<api.BackupRecord[]>(); vi.mocked(api.getBackups).mockReturnValueOnce(pending.promise);
  const first = state.refresh(); await state.refresh(); pending.reject(new Error("old read failed")); await first;
  expect(state.notice).toBe(""); expect(state.listLoad.message.value).toBe("");
});
it.each(["unmount", "session"])("stops initial loading and follow-up GETs after %s", async context => {
  const pending = deferred<api.BackupRecord[]>(); vi.mocked(api.getBackups).mockReturnValueOnce(pending.promise);
  const state = await mount(); if (context === "unmount") unmount(); else shared.epoch++;
  pending.resolve([record()]); await flush(); expect(state.backups).toEqual([]);
  expect(getAutomation).not.toHaveBeenCalled(); expect(api.getBackupRunningJobs).not.toHaveBeenCalled();
});
it("preserves the newer chain when an old chain lookup finishes last", async () => {
  const state = await mount(), pending = deferred<api.BackupChain>(); vi.mocked(api.getBackupChain).mockReturnValueOnce(pending.promise).mockResolvedValueOnce(chain("new"));
  const first = state.openChain(record()); await state.openChain(record("new")); pending.resolve(chain("old")); await first;
  expect(state.chainRecord?.id).toBe("new"); expect(state.chain?.chainId).toBe("new");
});
it.each(["close", "browse", "unmount", "session"])("discards chain reads invalidated by %s", async context => {
  const state = await mount(), pending = deferred<api.BackupChain>(); vi.mocked(api.getBackupChain).mockReturnValueOnce(pending.promise);
  const reading = state.openChain(record()); if (context === "close") state.closeChain(); else if (context === "browse") await state.openBrowser(record("browse")); else if (context === "unmount") unmount(); else shared.epoch++;
  pending.resolve(chain()); await reading; expect(state.chain).toBeNull();
});
it.each(["removeBackup", "removeChain"] as const)("does not duplicate %s while confirmation is open", async operation => {
  const state = await mount(), pending = deferred<boolean>(); shared.confirm.mockReturnValueOnce(pending.promise);
  const first = state[operation](record()); await state[operation](record()); expect(shared.confirm).toHaveBeenCalledOnce(); pending.resolve(false); await first;
});
it.each(["unmount", "session", "readonly"])("does not dispatch a confirmed delete after %s", async context => {
  const state = await mount(); shared.confirm.mockImplementationOnce(async options => {
    if (context === "unmount") unmount(); else if (context === "session") shared.epoch++; else useSubscriptionStore().usage = { isSuspended: true } as never;
    await options.action(); return true;
  }); await state.removeBackup(record()); expect(api.deleteBackup).not.toHaveBeenCalled();
});
it("uses the confirmed immutable delete identity", async () => {
  const state = await mount(), target = record(); shared.confirm.mockImplementationOnce(async options => { target.id = "different"; await options.action(); return true; });
  await state.removeBackup(target); expect(api.deleteBackup).toHaveBeenCalledExactlyOnceWith("first");
});
it.each(["unmount", "session"])("does not refresh or report a late deletion after %s", async context => {
  const state = await mount(), pending = deferred<void>(); vi.mocked(api.deleteBackup).mockReturnValueOnce(pending.promise); vi.mocked(api.getBackups).mockClear();
  const deleting = state.removeBackup(record()); if (context === "unmount") unmount(); else shared.epoch++;
  pending.resolve(); await deleting; expect(api.getBackups).not.toHaveBeenCalled(); expect(state.notice).toBe("");
});
it("does not open a chain-delete confirmation from an obsolete deletion failure", async () => {
  const state = await mount(), pending = deferred<void>(); vi.mocked(api.deleteBackup).mockReturnValueOnce(pending.promise);
  const deleting = state.removeBackup(record()); unmount(); pending.reject(new UserFacingError("backup.chain_has_dependents")); await deleting; expect(shared.confirm).toHaveBeenCalledOnce();
});
it.each(["removeBackup", "removeChain", "createSelectedBackup", "restore"] as const)("keeps a list refresh failure visible after %s", async operation => {
  const state = await mount(); vi.mocked(api.getBackups).mockRejectedValueOnce(new UserFacingError("backup.list_unavailable"));
  if (operation === "createSelectedBackup") await state[operation](); else await state[operation](record());
  expect(state.noticeError).toBe(true); expect(state.notice).toContain("backup.list_unavailable");
});
it.each(["unmount", "session"])("does not begin restore after an obsolete chain lookup on %s", async context => {
  const state = await mount(), pending = deferred<api.BackupChain>(); vi.mocked(api.getBackupChain).mockReturnValueOnce(pending.promise);
  const restoring = state.restore({ ...record(), chainId: "chain" }); if (context === "unmount") unmount(); else shared.epoch++;
  pending.resolve(chain()); await restoring; expect(api.getBackupChain).toHaveBeenCalledOnce(); expect(shared.confirm).not.toHaveBeenCalled(); expect(api.restoreBackup).not.toHaveBeenCalled();
});
it("does not send two create requests while the first is pending", async () => {
  const state = await mount(), pending = deferred<api.BackupRecord>(); vi.mocked(api.backupSite).mockReturnValueOnce(pending.promise);
  const creating = state.createSelectedBackup(); await state.createSelectedBackup(); expect(api.backupSite).toHaveBeenCalledOnce(); pending.resolve(record("created")); await creating;
});
it.each(["unmount", "session"])("does not publish or refresh a create result after %s", async context => {
  const state = await mount(), pending = deferred<api.BackupRecord>(); vi.mocked(api.backupSite).mockReturnValueOnce(pending.promise); vi.mocked(api.getBackups).mockClear();
  const creating = state.createSelectedBackup(); if (context === "unmount") unmount(); else shared.epoch++;
  pending.resolve(record("created")); await creating; expect(api.getBackups).not.toHaveBeenCalled(); expect(state.notice).toBe("");
});
it("snapshots the target approved for a mail backup", async () => {
  const state = await mount(); state.backupKind = "mail"; state.targetForm.remoteUrl = "approved";
  shared.confirm.mockImplementationOnce(async () => { state.targetForm.remoteUrl = "changed"; return true; });
  await state.createSelectedBackup(); expect(api.backupMail).toHaveBeenCalledExactlyOnceWith(expect.objectContaining({ remoteUrl: "approved" }));
});
it("preserves the next backup password when an older backup completes", async () => {
  const state = await mount(), pending = deferred<api.BackupRecord>(); state.targetForm.encryptionPassword = "submitted"; vi.mocked(api.backupSite).mockReturnValueOnce(pending.promise);
  const creating = state.createSelectedBackup(); state.targetForm.encryptionPassword = "next"; pending.resolve(record("created")); await creating;
  expect(state.targetForm.encryptionPassword).toBe("next");
});
it("does not apply browser entries from a previous session", async () => {
  const state = await mount(), pending = deferred<Awaited<ReturnType<typeof api.getBackupEntries>>>(); vi.mocked(api.getBackupEntries).mockReturnValueOnce(pending.promise);
  const browsing = state.openBrowser(record()); shared.epoch++; pending.resolve({ prefix: "", entries: [{ path: "private.txt" }], truncated: false } as never); await browsing; expect(state.browseEntries).toEqual([]);
});
it("does not republish a verification result or refresh after unmount", async () => {
  const state = await mount(), pending = deferred<api.BackupVerificationResult>(); vi.mocked(api.verifyBackups).mockReturnValueOnce(pending.promise); vi.mocked(api.getBackups).mockClear();
  const verifying = state.verify(); unmount(); pending.resolve({ checked: 9 } as never); await verifying;
  expect(state.verification).toBeNull(); expect(api.getBackups).not.toHaveBeenCalled();
});
it("keeps normal creation, deletion, chain and restoration working", async () => {
  const state = await mount(); await state.createSelectedBackup(); expect(state.notice).toBe("panel.backup.created");
  await state.openChain(record()); expect(state.chain?.chainId).toBe("first");
  await state.restore(record()); expect(state.notice).toBe("panel.backup.restored");
  await state.removeBackup(record()); expect(state.notice).toBe("panel.backup.deletedNotice");
  await state.removeChain(record()); expect(state.notice).toBe("panel.backup.chain.deletedNotice"); expect(state.noticeError).toBe(false);
});

it("still creates a database backup using the confirmed configured FTP target", async () => {
  const state = await mount(); state.databaseEngine = "postgresql"; state.databaseName = "Foo";
  vi.mocked(getAutomation).mockResolvedValueOnce({ settings: { backupTargetType: "ftp", backupRemoteUrl: "ftp://approved" } } as never);
  await state.createDatabaseToConfiguredFtp();
  expect(api.backupDatabase).toHaveBeenCalledExactlyOnceWith("postgresql", "Foo", expect.objectContaining({ useConfiguredFtpTarget: true, remoteUrl: "ftp://approved" }));
});
it.each(["unmount", "session"])("does not open a configured FTP confirmation after %s", async context => {
  const state = await mount(), pending = deferred<Awaited<ReturnType<typeof getAutomation>>>(); vi.mocked(getAutomation).mockReturnValueOnce(pending.promise);
  const creating = state.createDatabaseToConfiguredFtp(); if (context === "unmount") unmount(); else shared.epoch++;
  pending.resolve({ settings: { backupTargetType: "ftp", backupRemoteUrl: "ftp://approved" } } as never); await creating;
  expect(shared.confirm).not.toHaveBeenCalled(); expect(api.backupDatabase).not.toHaveBeenCalled();
});
it("snapshots the database before the configured target read", async () => {
  const state = await mount(), pending = deferred<Awaited<ReturnType<typeof getAutomation>>>(); state.databaseEngine = "postgresql"; state.databaseName = "Foo";
  vi.mocked(getAutomation).mockReturnValueOnce(pending.promise); const creating = state.createDatabaseToConfiguredFtp();
  state.databaseName = "other"; pending.resolve({ settings: { backupTargetType: "ftp", backupRemoteUrl: "ftp://approved" } } as never); await creating;
  expect(api.backupDatabase).toHaveBeenCalledWith("postgresql", "Foo", expect.anything());
});
it("does not retry remote upload using stale target settings after their read fails", async () => {
  vi.mocked(getAutomation).mockResolvedValueOnce({ settings: { backupTargetType: "ftp", backupRemoteUrl: "ftp://old" } } as never);
  const state = await mount(); vi.mocked(getAutomation).mockRejectedValueOnce(new UserFacingError("backup.target_unavailable")); await state.retryRemote(record());
  expect(shared.confirm).not.toHaveBeenCalled(); expect(api.retryRemoteUpload).not.toHaveBeenCalled(); expect(state.noticeError).toBe(true);
});
it.each(["unmount", "session"])("does not dispatch remote retry after an invalidated confirmation on %s", async context => {
  const state = await mount(); vi.mocked(getAutomation).mockResolvedValueOnce({ settings: { backupTargetType: "ftp", backupRemoteUrl: "ftp://approved" } } as never);
  shared.confirm.mockImplementationOnce(async options => { if (context === "unmount") unmount(); else shared.epoch++; await options.action(); return true; });
  await state.retryRemote(record()); expect(api.retryRemoteUpload).not.toHaveBeenCalled();
});
it("keeps the initial browse request invalidated when closing its panel", async () => {
  const state = await mount(), pending = deferred<Awaited<ReturnType<typeof api.getBackupEntries>>>(); vi.mocked(api.getBackupEntries).mockReturnValueOnce(pending.promise);
  const browsing = state.openBrowser(record()); state.closeBrowser(); pending.resolve({ prefix: "", entries: [{ path: "stale.txt" }], truncated: false } as never); await browsing;
  expect(state.browseEntries).toEqual([]); expect(state.browseRecord).toBeNull();
});
it("holds a granular restore lock while its confirmation is open", async () => {
  const state = await mount(); await state.openBrowser(record()); state.selectedItems = ["files/a.txt"];
  const pending = deferred<boolean>(); shared.confirm.mockReturnValueOnce(pending.promise);
  const restoring = state.restoreSelectedItems(); await state.restoreSelectedItems(); await state.createSelectedBackup();
  expect(shared.confirm).toHaveBeenCalledOnce(); expect(api.backupSite).not.toHaveBeenCalled(); pending.resolve(false); await restoring; expect(state.busy).toBe(false);
});
it("releases a cancelled encrypted item password request without dispatching a restore", async () => {
  const state = await mount(); await state.openBrowser({ ...record(), encrypted: true }); state.selectedItems = ["files/a.txt"];
  shared.prompt.mockResolvedValueOnce(null); await state.restoreSelectedItems(); expect(state.busy).toBe(false); expect(api.restoreBackupItems).not.toHaveBeenCalled();
});
it("restores only the immutable selected item snapshot", async () => {
  const state = await mount(); await state.openBrowser(record()); state.selectedItems = ["files/a.txt"];
  shared.confirm.mockImplementationOnce(async options => { state.selectedItems = ["files/b.txt"]; await options.action(); return true; });
  await state.restoreSelectedItems(); expect(api.restoreBackupItems).toHaveBeenCalledExactlyOnceWith("first", ["files/a.txt"], "", ""); expect(state.notice).toBe("panel.backup.itemsRestoredAll");
});
it.each([
  ["/restored/site", "panel.backup.itemsRestoredAll"],
  ["", "panel.backup.itemsRestoredScoped"],
  ["   ", "panel.backup.itemsRestoredScoped"]
])("reports granular restoration without an empty target label for %j", async (targetRoot, messageKey) => {
  const state = await mount(); await state.openBrowser(record()); state.selectedItems = ["files/a.txt", "files/b.txt"];
  vi.mocked(api.restoreBackupItems).mockResolvedValueOnce({ restoredCount: 2, targetRoot });
  await state.restoreSelectedItems();
  expect(api.restoreBackupItems).toHaveBeenCalledExactlyOnceWith("first", ["files/a.txt", "files/b.txt"], "", "");
  expect(state.notice).toBe(messageKey); expect(state.noticeError).toBe(false);
  expect(shared.translate).toHaveBeenCalledWith(messageKey, { items: 2, count: 2, target: targetRoot });
});
it("retains the newly edited granular selection when an earlier restore finishes", async () => {
  const state = await mount(); await state.openBrowser(record()); state.selectedItems = ["files/a.txt"];
  const pending = deferred<Awaited<ReturnType<typeof api.restoreBackupItems>>>(); vi.mocked(api.restoreBackupItems).mockReturnValueOnce(pending.promise);
  const restoring = state.restoreSelectedItems(); state.selectedItems = ["files/b.txt"]; pending.resolve({ restoredCount: 1, targetRoot: "/restored" }); await restoring;
  expect(state.selectedItems).toEqual(["files/b.txt"]);
});
it("does not hide a current chain error with an older successful read", async () => {
  const state = await mount(), pending = deferred<api.BackupChain>(); vi.mocked(api.getBackupChain).mockReturnValueOnce(pending.promise).mockRejectedValueOnce(new UserFacingError("backup.chain_unavailable"));
  const reading = state.openChain(record()); await state.openChain(record("new")); pending.resolve(chain()); await reading;
  expect(state.chain).toBeNull(); expect(state.chainLoad.message.value).toBe("backup.chain_unavailable");
});
it("still requests the dependent-chain second confirmation before deleting the chain", async () => {
  const state = await mount(); vi.mocked(api.deleteBackup).mockRejectedValueOnce(new UserFacingError("backup.chain_has_dependents"));
  await state.removeBackup(record()); expect(shared.confirm).toHaveBeenCalledTimes(2); expect(api.deleteBackupChain).toHaveBeenCalledExactlyOnceWith("first", ["first"]);
});
it.each([false, true])("renders one retryable list error after mutation=%s while preserving the table", async mutation => {
  const state = await mount(); vi.mocked(api.getBackups).mockRejectedValueOnce(new UserFacingError("backup.list_unavailable"));
  if (mutation) await state.removeBackup(record()); else await state.refresh(); await flush();
  const html = renderMarkup(state);
  expect(html.match(/role="alert"/g)).toHaveLength(1);
  expect(html.match(/backup\.list_unavailable/g)).toHaveLength(1);
  expect(html).toContain("panel.common.retry");
  expect(html).toContain("first");
  expect(html).not.toContain("panel.backup.empty");
});
it("clears an obsolete plain list error after a successful retry", async () => {
  const state = await mount(); vi.mocked(api.getBackups).mockRejectedValueOnce(new UserFacingError("backup.list_unavailable"));
  await state.refresh(); await state.refresh(); expect(state.listLoad.message.value).toBe(""); expect(state.notice).toBe(""); expect(state.noticeError).toBe(false);
});

// Round 15: the remaining subscription schedule and administrator settings flows.
it("keeps the latest schedules and loading owner when reads overlap", async () => {
  const state = await mount(), first = deferred<api.SubscriptionBackupSchedule[]>(), second = deferred<api.SubscriptionBackupSchedule[]>();
  vi.mocked(api.getBackupSchedules).mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise);
  const a = state.loadSchedules(), b = state.loadSchedules(); first.resolve([schedule("old")]); await a; expect(state.schedulesLoading).toBe(true);
  second.resolve([schedule("new")]); await b; expect(state.schedules[0]?.userId).toBe("new");
});
it("does not let an obsolete schedule failure erase the latest measured list", async () => {
  const state = await mount(), pending = deferred<api.SubscriptionBackupSchedule[]>(); vi.mocked(api.getBackupSchedules).mockReturnValueOnce(pending.promise);
  const reading = state.loadSchedules(); await state.loadSchedules(); pending.reject(new UserFacingError("backup.schedule_read_failed")); await reading;
  expect(state.schedules).toEqual([schedule()]); expect(state.schedulesLoad.message.value).toBe(""); expect(state.schedulesLoaded).toBe(true);
});
it("preserves stale schedule rows but marks a failed refresh unmeasured", async () => {
  const state = await mount(); await state.loadSchedules(); vi.mocked(api.getBackupSchedules).mockRejectedValueOnce(new UserFacingError("backup.schedule_read_failed"));
  await state.loadSchedules(); expect(state.schedules).toEqual([schedule()]); expect(state.schedulesLoaded).toBe(false); expect(state.schedulesLoad.message.value).not.toBe("");
});
it.each(["unmount", "session"])("ignores schedule list/form results after %s", async context => {
  const state = await mount(), rows = deferred<api.SubscriptionBackupSchedule[]>(), form = deferred<api.SubscriptionBackupSchedule>();
  vi.mocked(api.getBackupSchedules).mockReturnValueOnce(rows.promise); vi.mocked(api.getBackupSchedule).mockReturnValueOnce(form.promise);
  const a = state.loadSchedules(), b = state.openScheduleForm("tenant"); if (context === "unmount") unmount(); else shared.epoch++;
  rows.resolve([schedule()]); form.resolve(schedule()); await Promise.all([a,b]); expect(state.schedules).toEqual([]); expect(state.scheduleForm).toBeNull();
});
it("keeps the newly requested schedule form when an older read completes last", async () => {
  const state = await mount(), pending = deferred<api.SubscriptionBackupSchedule>(); vi.mocked(api.getBackupSchedule).mockReturnValueOnce(pending.promise);
  const reading = state.openScheduleForm("old"); await state.openScheduleForm("new"); pending.resolve(schedule("old")); await reading; expect(state.scheduleForm?.userId).toBe("new");
});
it("does not reopen a closed schedule form when saving completes", async () => {
  const state = await mount(); await state.openScheduleForm("tenant"); const pending = deferred<api.SubscriptionBackupSchedule>(); vi.mocked(api.updateBackupSchedule).mockReturnValueOnce(pending.promise);
  const saving = state.saveSchedule(); state.scheduleForm = null; pending.resolve(schedule()); await saving; expect(state.scheduleForm).toBeNull();
});
it("preserves edits made to a schedule while saving its previous draft", async () => {
  const state = await mount(); await state.openScheduleForm("tenant"); state.scheduleForm!.remotePassword = "submitted";
  const pending = deferred<api.SubscriptionBackupSchedule>(); vi.mocked(api.updateBackupSchedule).mockReturnValueOnce(pending.promise);
  const saving = state.saveSchedule(); state.scheduleForm!.remotePassword = "next-secret"; state.scheduleForm!.retentionDays = 30;
  pending.resolve(schedule()); await saving; expect(state.scheduleForm).toMatchObject({ remotePassword: "next-secret", retentionDays: 30 });
  expect(api.updateBackupSchedule).toHaveBeenCalledWith("tenant", expect.objectContaining({ remotePassword: "submitted", retentionDays: 14 }));
});
it.each(["saveSchedule", "removeSchedule", "runScheduleNow"] as const)("does not duplicate %s while it is pending", async operation => {
  const state = await mount(); await state.openScheduleForm("tenant"); const pending = deferred<api.SubscriptionBackupSchedule>();
  if (operation === "saveSchedule") vi.mocked(api.updateBackupSchedule).mockReturnValueOnce(pending.promise); else shared.confirm.mockReturnValueOnce(pending.promise as never);
  const first = operation === "saveSchedule" ? state.saveSchedule() : state[operation](schedule());
  if (operation === "saveSchedule") { await state.saveSchedule(); expect(api.updateBackupSchedule).toHaveBeenCalledOnce(); } else { await state[operation](schedule()); expect(shared.confirm).toHaveBeenCalledOnce(); }
  pending.resolve(operation === "saveSchedule" ? schedule() : false as never); await first;
});
it.each(["saveSchedule", "removeSchedule", "runScheduleNow"] as const)("keeps list refresh errors visible after %s", async operation => {
  const state = await mount(); await state.openScheduleForm("tenant"); vi.mocked(api.getBackupSchedules).mockRejectedValueOnce(new UserFacingError("backup.schedule_read_failed"));
  if (operation === "saveSchedule") await state.saveSchedule(); else await state[operation](schedule());
  expect(state.noticeError).toBe(true); expect(state.notice).toContain("backup.schedule_read_failed");
});
it.each(["removeSchedule", "runScheduleNow"] as const)("captures the confirmed subscription target for %s", async operation => {
  const state = await mount(), target = schedule(); shared.confirm.mockImplementationOnce(async options => { target.userId = "different"; await options.action?.(); return true; });
  await state[operation](target); const request = operation === "removeSchedule" ? api.deleteBackupSchedule : api.runBackupScheduleNow;
  expect(request).toHaveBeenCalledExactlyOnceWith("tenant");
});
it.each(["removeSchedule", "runScheduleNow"] as const)("invalidates %s confirmation after its session changes", async operation => {
  const state = await mount(); shared.confirm.mockImplementationOnce(async options => { shared.epoch++; await options.action?.(); return true; });
  await state[operation](schedule()); expect(api.deleteBackupSchedule).not.toHaveBeenCalled(); expect(api.runBackupScheduleNow).not.toHaveBeenCalled();
});
it.each(["saveSchedule", "removeSchedule", "runScheduleNow"] as const)("does not refresh or publish a late %s response", async operation => {
  const state = await mount(); await state.openScheduleForm("tenant"); const pending = deferred<api.SubscriptionBackupSchedule>();
  if (operation === "saveSchedule") vi.mocked(api.updateBackupSchedule).mockReturnValueOnce(pending.promise);
  else if (operation === "removeSchedule") vi.mocked(api.deleteBackupSchedule).mockReturnValueOnce(pending.promise as never);
  else vi.mocked(api.runBackupScheduleNow).mockReturnValueOnce(pending.promise);
  vi.mocked(api.getBackups).mockClear(); const running = operation === "saveSchedule" ? state.saveSchedule() : state[operation](schedule()); await flush(); unmount();
  pending.resolve(schedule()); await running; expect(api.getBackupSchedules).not.toHaveBeenCalled(); expect(api.getBackups).not.toHaveBeenCalled(); expect(state.notice).toBe("");
});
it.each(["", "running"])("does not report an unconfirmed schedule status '%s' as successful", async lastStatus => {
  const state = await mount(); vi.mocked(api.runBackupScheduleNow).mockResolvedValueOnce({ ...schedule(), lastStatus }); await state.runScheduleNow(schedule());
  expect(state.noticeError).toBe(true); expect(state.notice).toContain("panel.backup.schedule.resultUnconfirmed");
});
it("retains partial-run and quota-normalized schedule results", async () => {
  const state = await mount(); await state.openScheduleForm("tenant");
  vi.mocked(api.updateBackupSchedule).mockResolvedValueOnce({ ...schedule(), intervalHours: 48, throttledByQuota: true }); await state.saveSchedule();
  expect(state.scheduleForm!.plan.intervalHours).toBe(48); expect(state.noticeError).toBe(true); expect(state.notice).toContain("panel.backup.schedule.throttled");
  vi.mocked(api.runBackupScheduleNow).mockResolvedValueOnce({ ...schedule(), lastStatus: "completed_with_errors", lastDetails: "one database missing" }); await state.runScheduleNow(schedule());
  expect(state.noticeError).toBe(true); expect(state.notice).toContain("one database missing");
});
it("keeps current automation settings when an older settings read completes last", async () => {
  const state = await mount(), pending = deferred<AutomationStatus>(); vi.mocked(getAutomation).mockReturnValueOnce(pending.promise).mockResolvedValueOnce(automation(21));
  const reading = state.loadAutoBackup(); await state.loadAutoBackup(); pending.resolve(automation(1)); await reading; expect(state.autoForm!.backupRetentionDays).toBe(21);
});
it("does not save automation from a stale form after the last read failed", async () => {
  const state = await mount(); vi.mocked(getAutomation).mockRejectedValueOnce(new UserFacingError("backup.settings_read_failed")); await state.loadAutoBackup(); await state.saveAutoBackup();
  expect(updateAutomation).not.toHaveBeenCalled(); expect(state.autoForm).toBeNull();
});
it("does not duplicate automation saves or clear the first save's operation lock", async () => {
  const state = await mount(), pending = deferred<AutomationStatus>(); vi.mocked(updateAutomation).mockReturnValueOnce(pending.promise);
  const saving = state.saveAutoBackup(); await state.saveAutoBackup(); expect(state.busy).toBe(true); expect(updateAutomation).toHaveBeenCalledOnce(); pending.resolve(automation()); await saving;
});
it("preserves a newer automation draft and snapshots nested request fields", async () => {
  const state = await mount(), pending = deferred<AutomationStatus>(); vi.mocked(updateAutomation).mockReturnValueOnce(pending.promise);
  const saving = state.saveAutoBackup(); state.autoForm!.backupRetentionDays = 30; state.autoForm!.backupDatabaseEngines.push("mysql"); pending.resolve(automation()); await saving;
  expect(state.autoForm!.backupRetentionDays).toBe(30); expect(updateAutomation).toHaveBeenCalledWith(expect.objectContaining({ backupRetentionDays: 14, backupDatabaseEngines: ["postgresql"] }));
});
it.each(["saveAutoBackup", "saveEncryption"] as const)("ignores a %s response after the session changes", async operation => {
  const state = await mount(), pending = deferred<never>(); Object.assign(state.encryptionForm, { password: "secret", confirm: "secret", acknowledged: true });
  if (operation === "saveAutoBackup") vi.mocked(updateAutomation).mockReturnValueOnce(pending.promise); else vi.mocked(api.setBackupEncryption).mockReturnValueOnce(pending.promise);
  const saving = state[operation](); shared.epoch++; pending.resolve(operation === "saveAutoBackup" ? automation(99) as never : { enabled: true, passwordUsable: true } as never); await saving;
  expect(state.notice).toBe(""); expect(state.autoForm!.backupRetentionDays).toBe(14); expect(state.encryption?.enabled).toBe(false);
});
it("does not apply a pre-save settings GET over the saved result", async () => {
  const state = await mount(), pending = deferred<AutomationStatus>(); vi.mocked(getAutomation).mockReturnValueOnce(pending.promise);
  const reading = state.loadAutoBackup(); vi.mocked(updateAutomation).mockResolvedValueOnce(automation(99)); await state.saveAutoBackup(); pending.resolve(automation(1)); await reading;
  expect(state.autoForm!.backupRetentionDays).toBe(99);
});
it("ignores superseded encryption reads", async () => {
  const state = await mount(), pending = deferred<api.BackupEncryptionSettings>(); vi.mocked(api.getBackupEncryption).mockReturnValueOnce(pending.promise).mockResolvedValueOnce({ enabled: true, passwordUsable: true, updatedAtUtc: "", algorithm: "AES" });
  const reading = state.loadEncryption(); await state.loadEncryption(); pending.resolve({ enabled: false, passwordUsable: false, updatedAtUtc: "", algorithm: "" }); await reading; expect(state.encryption?.enabled).toBe(true);
});
it("does not duplicate encryption saves and preserves the next secret draft", async () => {
  const state = await mount(), pending = deferred<api.BackupEncryptionSettings>(); Object.assign(state.encryptionForm, { password: "secret", confirm: "secret", acknowledged: true });
  vi.mocked(api.setBackupEncryption).mockReturnValueOnce(pending.promise); const saving = state.saveEncryption(); await state.saveEncryption();
  Object.assign(state.encryptionForm, { password: "next", confirm: "next" }); pending.resolve({ enabled: true, passwordUsable: true, updatedAtUtc: "", algorithm: "AES" }); await saving;
  expect(api.setBackupEncryption).toHaveBeenCalledOnce(); expect(state.encryptionForm.password).toBe("next");
});
it("does not disable encryption from an obsolete confirmation", async () => {
  const state = await mount(); shared.confirm.mockImplementationOnce(async options => { unmount(); await options.action(); return true; });
  await state.disableEncryption(); expect(api.setBackupEncryption).not.toHaveBeenCalled();
});
it("holds the encryption disable confirmation lock across other settings writes", async () => {
  const state = await mount(), pending = deferred<boolean>(); shared.confirm.mockReturnValueOnce(pending.promise);
  const disabling = state.disableEncryption(); await state.disableEncryption(); await state.saveAutoBackup();
  expect(shared.confirm).toHaveBeenCalledOnce(); expect(updateAutomation).not.toHaveBeenCalled(); pending.resolve(false); await disabling;
});
it.each(["Customer", "Reseller"])("shows only supported creation options to %s", async role => {
  const state = await mount(); useAuthStore().session!.user!.role = role; state.activeView = "create"; const html = renderMarkup(state);
  expect(html).toContain("panel.backup.siteTitle"); expect(html).toContain("panel.backup.subscription.title");
  expect(html).not.toContain("panel.backup.dbTitle"); expect(html).not.toContain("panel.backup.mailTitle"); expect(html).not.toContain("panel.backup.panelTitle");
});
it.each(["database", "mail", "panel"])("blocks the admin-only %s creation handler for scoped users", async kind => {
  const state = await mount(); useAuthStore().session!.user!.role = "Customer"; state.backupKind = kind; await state.createSelectedBackup();
  expect(api.backupDatabase).not.toHaveBeenCalled(); expect(api.backupMail).not.toHaveBeenCalled(); expect(api.backupPanel).not.toHaveBeenCalled(); expect(shared.confirm).not.toHaveBeenCalled();
});
it("rechecks admin authority after confirming server mail backup", async () => {
  const state = await mount(); state.backupKind = "mail"; shared.confirm.mockImplementationOnce(async () => { useAuthStore().session!.user!.role = "Customer"; return true; });
  await state.createSelectedBackup(); expect(api.backupMail).not.toHaveBeenCalled();
});
it("keeps administrator database/mail/panel creation options and tenant granular restore", async () => {
  const state = await mount(); state.activeView = "create"; const html = renderMarkup(state);
  for (const key of ["panel.backup.dbTitle", "panel.backup.mailTitle", "panel.backup.panelTitle"]) expect(html).toContain(key);
  for (const kind of ["database", "mail", "panel"]) { state.backupKind = kind; await state.createSelectedBackup(); }
  expect(api.backupDatabase).toHaveBeenCalledOnce(); expect(api.backupMail).toHaveBeenCalledOnce(); expect(api.backupPanel).toHaveBeenCalledOnce();
  useAuthStore().session!.user!.role = "Customer"; await state.openBrowser(record()); state.selectedItems = ["files/a.txt"]; await state.restoreSelectedItems(); expect(api.restoreBackupItems).toHaveBeenCalledOnce();
});
it("limits chain-delete impact to its case-insensitive site source and kind", async () => {
  const state = await mount(), base = { ...record(), chainId: "shared" };
  state.backups = [base, { ...record("same"), source: "SITE.TEST", chainId: "shared", chainIndex: 1 }, { ...record("different"), source: "other.test", chainId: "shared", chainIndex: 1 }, { ...record("other-kind"), type: "subscription", chainId: "shared", chainIndex: 1 }];
  expect(state.chainDeleteTargets(base).map(item => item.id)).toEqual(["first", "same"]);
});
it("preserves PostgreSQL case while matching canonical engine aliases in chain impact", async () => {
  const state = await mount(), base = { ...record(), type: "database", source: "postgresql:Foo", chainId: "shared" };
  state.backups = [base, ...["postgres:Foo", "postgresql:foo", "mysql:Foo", "postgresql:Other"].map((source, index) => ({ ...record(String(index)), type: "database", source, chainId: "shared", chainIndex: 1 }))];
  expect(state.chainDeleteTargets(base).map(item => item.id)).toEqual(["first", "0"]);
});
it("keeps SQL Server aliases and name casing equivalent in chain impact", async () => {
  const state = await mount(), base = { ...record(), type: "database", source: "sqlserver:DB", chainId: "shared" };
  state.backups = [base, { ...record("same"), type: "database", source: "mssql:db", chainId: "shared", chainIndex: 1 }, { ...record("other"), type: "database", source: "mysql:db", chainId: "shared", chainIndex: 1 }];
  expect(state.chainDeleteTargets(base).map(item => item.id)).toEqual(["first", "same"]);
});
it("does not reopen a closed form or release a later operation from its old form read", async () => {
  const state = await mount(); await state.openScheduleForm("tenant");
  const reading = deferred<api.SubscriptionBackupSchedule>(), saving = deferred<AutomationStatus>();
  vi.mocked(api.getBackupSchedule).mockReturnValueOnce(reading.promise); const old = state.openScheduleForm("other");
  state.closeScheduleForm(); vi.mocked(updateAutomation).mockReturnValueOnce(saving.promise); const current = state.saveAutoBackup();
  reading.resolve(schedule("other")); await old; expect(state.scheduleForm).toBeNull(); expect(state.busy).toBe(true);
  saving.resolve(automation()); await current; expect(state.busy).toBe(false);
});
it("preserves an edited-away-and-back schedule draft when a save finishes", async () => {
  const state = await mount(); await state.openScheduleForm("tenant"); state.scheduleForm!.remotePassword = "submitted";
  const pending = deferred<api.SubscriptionBackupSchedule>(); vi.mocked(api.updateBackupSchedule).mockReturnValueOnce(pending.promise);
  const saving = state.saveSchedule(); state.scheduleForm!.retentionDays = 99; state.scheduleForm!.retentionDays = 14;
  pending.resolve(schedule()); await saving; expect(state.scheduleForm!.remotePassword).toBe("submitted");
});
it.each(["removeSchedule", "disableEncryption"] as const)("surfaces %s failures even when ConfirmDialog swallows callback rejections", async operation => {
  const state = await mount(); shared.confirm.mockImplementationOnce(async options => { try { await options.action(); } catch { /* Real ConfirmDialog settles instead of rejecting. */ } return true; });
  const failure = new UserFacingError("backup.write_failed");
  if (operation === "removeSchedule") { vi.mocked(api.deleteBackupSchedule).mockRejectedValueOnce(failure); await state.removeSchedule(schedule()); }
  else { vi.mocked(api.setBackupEncryption).mockRejectedValueOnce(failure); await state.disableEncryption(); }
  expect(state.noticeError).toBe(true); expect(state.notice).toBe("backup.write_failed"); expect(state.busy).toBe(false);
});
it.each(["removeSchedule", "runScheduleNow"] as const)("does not dispatch %s after suspension during confirmation", async operation => {
  const state = await mount(); shared.confirm.mockImplementationOnce(async options => { useSubscriptionStore().usage = { isSuspended: true } as never; await options.action?.(); return true; });
  await state[operation](schedule()); expect(api.deleteBackupSchedule).not.toHaveBeenCalled(); expect(api.runBackupScheduleNow).not.toHaveBeenCalled();
});
it("blocks administrator-only settings and remote handlers for a tenant", async () => {
  const state = await mount(); Object.assign(state.encryptionForm, { password: "secret", confirm: "secret", acknowledged: true }); useAuthStore().session!.user!.role = "Customer";
  await state.saveAutoBackup(); await state.saveEncryption(); await state.disableEncryption(); await state.createDatabaseToConfiguredFtp(); await state.retryRemote(record());
  expect(updateAutomation).not.toHaveBeenCalled(); expect(api.setBackupEncryption).not.toHaveBeenCalled(); expect(api.retryRemoteUpload).not.toHaveBeenCalled(); expect(api.backupDatabase).not.toHaveBeenCalled(); expect(shared.confirm).not.toHaveBeenCalled();
});
it("preserves the newer settings error when an earlier read succeeds", async () => {
  const state = await mount(), pending = deferred<AutomationStatus>(); vi.mocked(getAutomation).mockReturnValueOnce(pending.promise).mockRejectedValueOnce(new UserFacingError("backup.settings_read_failed"));
  const old = state.loadAutoBackup(); await state.loadAutoBackup(); pending.resolve(automation()); await old;
  expect(state.autoForm).toBeNull(); expect(state.autoLoad.message.value).toBe("backup.settings_read_failed");
});
it("keeps normal schedule saves, completed runs, deletion and encryption settings working", async () => {
  const state = await mount(); await state.openScheduleForm("tenant"); state.scheduleForm!.remotePassword = "submitted";
  await state.saveSchedule(); expect(state.scheduleForm!.remotePassword).toBe(""); expect(state.notice).toBe("backup.schedule_saved");
  await state.runScheduleNow(schedule()); expect(state.notice).toBe("backup.schedule_run"); expect(state.noticeError).toBe(false);
  await state.removeSchedule(schedule()); expect(state.scheduleForm).toBeNull(); expect(state.notice).toBe("backup.schedule_deleted");
  Object.assign(state.encryptionForm, { password: "secret", confirm: "secret", acknowledged: true }); await state.saveEncryption(); expect(state.encryptionForm.password).toBe(""); expect(state.encryption?.enabled).toBe(true);
  vi.mocked(api.setBackupEncryption).mockResolvedValueOnce({ enabled: false, passwordUsable: false, updatedAtUtc: "", algorithm: "AES" }); await state.disableEncryption(); expect(state.encryption?.enabled).toBe(false); expect(state.noticeError).toBe(false);
  await state.saveAutoBackup(); expect(state.notice).toBe("panel.backup.autoSaved");
});
it("renders one schedule refresh failure and makes retry restore the confirmed outcome", async () => {
  const state = await mount(); state.activeView = "schedules"; await state.loadSchedules();
  vi.mocked(api.getBackupSchedules).mockRejectedValueOnce(new UserFacingError("backup.schedule_read_failed")); await state.runScheduleNow(schedule());
  const html = renderMarkup(state); expect(html.match(/role="alert"/g)).toHaveLength(1); expect(html.match(/backup\.schedule_read_failed/g)).toHaveLength(1); expect(html).toContain("panel.common.retry");
  await state.retryFailedLoads(); expect(state.notice).toBe("backup.schedule_run"); expect(state.noticeError).toBe(false);
});
it("retains a partial result's warning after its schedule list retry succeeds", async () => {
  const state = await mount(); vi.mocked(api.runBackupScheduleNow).mockResolvedValueOnce({ ...schedule(), lastStatus: "completed_with_errors", lastDetails: "partial archive" });
  vi.mocked(api.getBackupSchedules).mockRejectedValueOnce(new UserFacingError("backup.schedule_read_failed")); await state.runScheduleNow(schedule());
  await state.retryFailedLoads(); expect(state.notice).toContain("partial archive"); expect(state.notice).not.toContain("backup.schedule_read_failed"); expect(state.noticeError).toBe(true);
});
it("keeps an automation draft edited while its reload was pending", async () => {
  const state = await mount(), pending = deferred<AutomationStatus>(); vi.mocked(getAutomation).mockReturnValueOnce(pending.promise);
  const loading = state.loadAutoBackup(); state.autoForm!.backupRetentionDays = 35; pending.resolve(automation(21)); await loading;
  expect(state.autoForm!.backupRetentionDays).toBe(35); expect(state.autoStatus!.settings.backupRetentionDays).toBe(21);
});

function branchLink(id: string, index: number, parent = "", overrides: Partial<api.BackupRecord> = {}): api.BackupRecord {
  return { ...record(id), chainId: "branch", chainIndex: index, baseBackupId: parent,
    backupMode: index === 0 ? "full" : "incremental", createdAtUtc: `2026-10-03T10:0${index}:00Z`, ...overrides };
}
function branchedRecords() {
  return [branchLink("root", 0), branchLink("a1", 1, "root"), branchLink("b1", 1, "root", { createdAtUtc: "2026-10-03T10:01:30Z" }),
    branchLink("a2", 2, "a1"), branchLink("b2", 2, "b1", { createdAtUtc: "2026-10-03T10:02:30Z" }), branchLink("a3", 3, "a2")];
}

it("limits a branched delete to the selected node and its transitive actual descendants", async () => {
  const state = await mount(); state.backups = branchedRecords();
  expect(state.chainDeleteTargets(state.backups[1]).map(item => item.id)).toEqual(["a1", "a2", "a3"]);
});
it("does not call a later sibling or its higher-index descendant dependent on a terminal node", async () => {
  const state = await mount(); state.backups = branchedRecords();
  expect(state.chainDeleteTargets(state.backups[4]).map(item => item.id)).toEqual(["b2"]);
});
it("includes both explicit branches when their common parent is selected", async () => {
  const state = await mount(); state.backups = branchedRecords();
  expect(state.chainDeleteTargets(state.backups[0]).map(item => item.id)).toEqual(["root", "a1", "b1", "a2", "b2", "a3"]);
});
it("matches a chain identifier case-insensitively while keeping backup parent IDs exact", async () => {
  const state = await mount(); state.backups = [branchLink("root", 0), branchLink("next", 1, "root", { chainId: "BRANCH" })];
  expect(state.chainDeleteTargets(state.backups[0]).map(item => item.id)).toEqual(["root", "next"]);
});
it("preserves a unique linear legacy chain that has no stored parent descriptors", async () => {
  const state = await mount(); state.backups = [branchLink("root", 0), branchLink("next", 1), branchLink("last", 2)];
  expect(state.chainDeleteTargets(state.backups[1]).map(item => item.id)).toEqual(["next", "last"]);
});

const ambiguousChainCases: Array<[string, () => api.BackupRecord[]]> = [
  ["ambiguous legacy parent", () => [branchLink("root", 0), branchLink("a1", 1, "root"), branchLink("b1", 1, "root"), branchLink("legacy", 2)]],
  ["missing legacy predecessor", () => [branchLink("root", 0), branchLink("legacy", 2)]],
  ["duplicate backup ID", () => [branchLink("root", 0), branchLink("same", 1, "root"), branchLink("same", 1, "root")]],
  ["cycle", () => [branchLink("root", 0, "last"), branchLink("last", 1, "root")]],
  ["parent from another source", () => [branchLink("root", 0), branchLink("foreign", 1, "root", { source: "other.test" }), branchLink("next", 2, "foreign")]],
  ["parent ID with wrong case", () => [branchLink("root", 0), branchLink("next", 1, "ROOT")]],
  ["negative index", () => [branchLink("root", 0), branchLink("next", -1, "root")]],
  ["missing explicit parent", () => [branchLink("root", 0), branchLink("next", 1, "missing")]],
  ["nonconsecutive explicit parent", () => [branchLink("root", 0), branchLink("next", 2, "root")]],
  ["two full roots", () => [branchLink("root", 0), branchLink("second-root", 0), branchLink("next", 1, "root")]],
  ["scoped node ID duplicated in another source", () => [branchLink("root", 0), branchLink("next", 1, "root"), branchLink("next", 0, "", { source: "other.test" })]],
  ["incremental root", () => [branchLink("root", 0, "", { backupMode: "incremental" }), branchLink("next", 1, "root")]],
  ["fractional index", () => [branchLink("root", 0), branchLink("next", 0.5, "root")]],
  ["out-of-range index", () => [branchLink("root", 0), branchLink("next", 2147483648, "root")]]
];
it.each(ambiguousChainCases)("does not request destructive confirmation for an unresolved %s", async (_name, rows) => {
  const state = await mount(); state.backups = rows(); await state.removeChain(state.backups[0]);
  expect(shared.confirm).not.toHaveBeenCalled(); expect(api.deleteBackupChain).not.toHaveBeenCalled();
  expect(state.notice).toBe("backup.chain_ambiguous"); expect(state.noticeError).toBe(true);
});
it("shows why an ambiguous chain cannot be deleted without inventing a zero-backup count", async () => {
  const state = await mount(); state.backups = ambiguousChainCases[0][1](); state.chainRecord = state.backups[0]; state.chain = chain();
  const html = renderMarkup(state), button = html.match(/<button[^>]*>\s*panel\.backup\.chain\.delete[\s\S]*?<\/button>/)?.[0];
  expect(button).toBeDefined(); expect(button).toContain("disabled"); expect(button).not.toContain("(0)"); expect(html).toContain("backup.chain_ambiguous");
});
it("confirms only the chosen branch and sends exactly those IDs to the deletion API", async () => {
  const state = await mount(); state.backups = branchedRecords(); await state.removeChain(state.backups[1]);
  expect(shared.confirm).toHaveBeenCalledOnce(); expect(shared.confirm.mock.calls[0][0]).toMatchObject({ impact: ["a1", "a2", "a3"], requireText: "a1" });
  expect(api.deleteBackupChain).toHaveBeenCalledExactlyOnceWith("a1", ["a1", "a2", "a3"]);
});
it("does not expand approved deletion IDs if the catalogue changes during the confirmation", async () => {
  const state = await mount(); state.backups = branchedRecords();
  shared.confirm.mockImplementationOnce(async options => { state.backups.push(branchLink("a4", 4, "a3")); await options.action(); return true; });
  await state.removeChain(state.backups[1]); expect(api.deleteBackupChain).toHaveBeenCalledExactlyOnceWith("a1", ["a1", "a2", "a3"]);
});
it("refreshes a changed server target set and requires a new user decision instead of retrying DELETE", async () => {
  const state = await mount(); state.backups = branchedRecords(); vi.mocked(api.getBackups).mockClear();
  vi.mocked(api.deleteBackupChain).mockRejectedValueOnce(new UserFacingError("backup.chain_changed"));
  await state.removeChain(state.backups[1]); expect(api.deleteBackupChain).toHaveBeenCalledOnce(); expect(shared.confirm).toHaveBeenCalledOnce();
  expect(api.getBackups).toHaveBeenCalledOnce(); expect(state.notice).toBe("backup.chain_changed"); expect(state.noticeError).toBe(true);
});
it("keeps both a changed-target rejection and its failed refresh visible", async () => {
  const state = await mount(); state.backups = branchedRecords();
  vi.mocked(api.deleteBackupChain).mockRejectedValueOnce(new UserFacingError("backup.chain_changed"));
  vi.mocked(api.getBackups).mockRejectedValueOnce(new UserFacingError("backup.list_unavailable"));
  await state.removeChain(state.backups[1]); expect(state.notice).toContain("backup.chain_changed");
  expect(state.notice).toContain("backup.list_unavailable"); expect(state.noticeError).toBe(true);
});
it.each(["session", "unmount"])("does not refresh changed deletion targets after %s", async context => {
  const state = await mount(), pending = deferred<api.BackupDeleteResult>(); state.backups = branchedRecords();
  vi.mocked(api.deleteBackupChain).mockReturnValueOnce(pending.promise); vi.mocked(api.getBackups).mockClear();
  const deleting = state.removeChain(state.backups[1]); if (context === "session") shared.epoch++; else unmount();
  pending.reject(new UserFacingError("backup.chain_changed")); await deleting;
  expect(api.getBackups).not.toHaveBeenCalled(); expect(state.notice).toBe("");
});
it("does not block a valid branch because unrelated source records have duplicate IDs", async () => {
  const state = await mount(); state.backups = [...branchedRecords(), branchLink("unrelated", 0, "", { source: "other.test" }), branchLink("unrelated", 0, "", { source: "another.test" })];
  expect(state.chainDeleteTargets(state.backups[1]).map(item => item.id)).toEqual(["a1", "a2", "a3"]);
});
it("preserves server-side ambiguity rejection without a misleading deletion success", async () => {
  const state = await mount(); state.backups = branchedRecords();
  vi.mocked(api.deleteBackupChain).mockRejectedValueOnce(new UserFacingError("backup.chain_ambiguous"));
  await state.removeChain(state.backups[1]); expect(state.notice).toBe("backup.chain_ambiguous"); expect(state.noticeError).toBe(true);
  expect(api.deleteBackupChain).toHaveBeenCalledOnce();
});
it.each([{ deletedIds: ["a3"] }, { deletedIds: [] as string[] }])("reports a partial chain deletion with the names still present ($deletedIds deleted)", async ({ deletedIds }) => {
  const state = await mount(); state.backups = branchedRecords();
  vi.mocked(api.deleteBackupChain).mockResolvedValueOnce({ deletedIds, deletedNames: deletedIds });
  await state.removeChain(state.backups[1]);
  expect(state.noticeError).toBe(true); expect(state.notice).toBe("panel.backup.chain.partialDeleted");
  expect(shared.translate).toHaveBeenCalledWith("panel.backup.chain.partialDeleted", {
    count: deletedIds.length, names: ["a1", "a2", "a3"].filter(id => !deletedIds.includes(id)).join(", ")
  });
});
it("reports normal chain success when the entire approved set was deleted in leaf-first order", async () => {
  const state = await mount(); state.backups = branchedRecords();
  vi.mocked(api.deleteBackupChain).mockResolvedValueOnce({ deletedIds: ["a3", "a2", "a1"], deletedNames: ["a3", "a2", "a1"] });
  await state.removeChain(state.backups[1]); expect(state.noticeError).toBe(false); expect(state.notice).toBe("panel.backup.chain.deletedNotice");
});
it("does not hide partial deletion behind a failed list refresh", async () => {
  const state = await mount(); state.backups = branchedRecords();
  vi.mocked(api.deleteBackupChain).mockResolvedValueOnce({ deletedIds: ["a3"], deletedNames: ["a3"] });
  vi.mocked(api.getBackups).mockRejectedValueOnce(new UserFacingError("backup.list_unavailable"));
  await state.removeChain(state.backups[1]); expect(state.noticeError).toBe(true);
  expect(state.notice).toContain("panel.backup.chain.partialDeleted"); expect(state.notice).toContain("backup.list_unavailable");
});
it("reports the approved remaining names even when the catalogue changes before deletion returns", async () => {
  const state = await mount(), pending = deferred<api.BackupDeleteResult>(); state.backups = branchedRecords();
  vi.mocked(api.deleteBackupChain).mockReturnValueOnce(pending.promise); const deleting = state.removeChain(state.backups[1]);
  state.backups[1].name = "changed label"; pending.resolve({ deletedIds: ["a3"], deletedNames: ["a3"] }); await deleting;
  expect(shared.translate).toHaveBeenCalledWith("panel.backup.chain.partialDeleted", { count: 1, names: "a1, a2" });
});
