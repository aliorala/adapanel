import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { createRenderer, h, nextTick, ssrContextKey, type App, type Component, type Ref } from "vue";
import SiteBackupView from "./SiteBackupView.vue";
import { backupSite, getBackups, getBackupRunningJobs, type BackupRecord } from "../api/backup";
import { getDatabaseEngines, getDatabaseWorkspace, type DatabaseEngineStatus, type DatabaseWorkspace } from "../api/databases";
import { useSitesStore } from "../stores/sites";
import { useAuthStore } from "../stores/auth";
vi.mock("../router", () => ({ default: { push: vi.fn() } }));
vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key, locale: { value: "en" } }) }));
vi.mock("../api/backup", async original => ({ ...await original<object>(), backupSite: vi.fn(), getBackups: vi.fn(), getBackupRunningJobs: vi.fn() }));
vi.mock("../api/databases", async original => ({ ...await original<object>(), getDatabaseEngines: vi.fn(), getDatabaseWorkspace: vi.fn() }));
vi.mock("../api/myDatabases", () => ({ getMyDatabases: vi.fn(async () => []) }));
let app: App | undefined;
beforeEach(() => { vi.mocked(getBackups).mockResolvedValue([]); vi.mocked(getDatabaseEngines).mockResolvedValue([]); vi.mocked(getBackupRunningJobs).mockResolvedValue({ running: [], interrupted: [], serverTimeUtc: "2026-10-02T10:00:00Z" }); });
afterEach(() => { app?.unmount(); app = undefined; vi.clearAllMocks(); });
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(yes => { resolve = yes; }); return { promise, resolve }; }
type State = { backups: BackupRecord[]; expandedLocation: string; availableDatabases: { engine: string; name: string }[]; databasesLoad: { message: Ref<string> }; encryptionPassword: string; selectedDbKeys: string[]; notice: string; createBackup(): Promise<void> };
function renderMarkup(state: State) {
  const output: unknown[] = [];
  const render = (SiteBackupView as unknown as { ssrRender: (...args: unknown[]) => void }).ssrRender;
  render(state, (part: unknown) => output.push(part), app!._instance, {}, {}, state, {}, {});
  return output.flat(Infinity).join("").replace(/<!--[\s\S]*?-->/g, "");
}
function locationRecord(path = ""): BackupRecord {
  return { id: "location", name: "a.zip", type: "site", source: "a.test", path, format: "zip", createdAtUtc: "2026-10-03T10:00:00Z", size: 10, status: "created", targetType: "local", remoteUri: "https://storage.example.test/archive.zip", encrypted: false, warnings: [], preservedPath: "", chainId: "", chainIndex: 0, backupMode: "full", baseBackupId: "", subscriptionUserId: "", itemCount: 1, localMissing: true, remoteMissing: false, remoteVerifiedUtc: null };
}
async function flush() { for (let i = 0; i < 16; i++) await nextTick(); }
async function mount(role = "Administrator") {
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  const pinia = createPinia(); setActivePinia(pinia); useSitesStore().selectedSiteName = "a.test";
  useAuthStore().session = { isAuthenticated: true, requiresSetupWizard: false,
    user: { id: "fixture", role, userName: "fixture", email: "fixture@example.test", isActive: true, isTwoFactorEnabled: false, preferredLanguage: "en", lastLoginAtUtc: null } };
  app = renderer.createApp({ ...SiteBackupView, render: () => h("section") } as Component).use(pinia); app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: State }).setupState; await flush(); return state;
}
it("reports a failed database engine while preserving databases read from other engines", async () => {
  vi.mocked(getDatabaseEngines).mockResolvedValue([{ engine: "mysql", installed: true }, { engine: "mariadb", installed: true }] as DatabaseEngineStatus[]);
  vi.mocked(getDatabaseWorkspace).mockImplementation(async engine => { if (engine === "mysql") throw new Error("fixture offline"); return { databases: [{ name: "fixture" }] } as DatabaseWorkspace; });
  const state = await mount(); expect(state.availableDatabases).toEqual([{ engine: "mariadb", name: "fixture" }]); expect(state.databasesLoad.message.value).not.toBe("");
});
it("does not duplicate a pending site backup", async () => {
  const state = await mount(), pending = deferred<BackupRecord>(); vi.mocked(backupSite).mockReturnValue(pending.promise);
  const first = state.createBackup(), second = state.createBackup(); pending.resolve({} as BackupRecord); await Promise.all([first, second]); expect(backupSite).toHaveBeenCalledTimes(1);
});
it("clears site-specific database selections and the encryption password on site change", async () => {
  const state = await mount(); state.encryptionPassword = "fixture"; state.selectedDbKeys = ["mysql:a"];
  useSitesStore().selectedSiteName = "b.test"; await flush(); expect(state.encryptionPassword).toBe(""); expect(state.selectedDbKeys).toEqual([]);
});
it("does not clear the new site's password when an old backup completes", async () => {
  const state = await mount(), pending = deferred<BackupRecord>(); vi.mocked(backupSite).mockReturnValueOnce(pending.promise);
  const creating = state.createBackup(); useSitesStore().selectedSiteName = "b.test"; await flush(); state.encryptionPassword = "next-secret";
  pending.resolve({} as BackupRecord); await creating; expect(state.encryptionPassword).toBe("next-secret"); expect(state.notice).toBe("");
});
it.each(["Customer", "Reseller", "Administrator"])("distinguishes a hidden site backup path from an unknown location for %s", async role => {
  vi.mocked(getBackups).mockResolvedValue([locationRecord()]);
  const state = await mount(role);
  state.expandedLocation = "location";
  const html = renderMarkup(state);
  expect(html).toContain(role === "Administrator" ? "panel.backup.location.unknown" : "panel.backup.location.scopedHidden");
  expect(html).not.toContain(role === "Administrator" ? "panel.backup.location.scopedHidden" : "panel.backup.location.unknown");
  expect(html).toContain("panel.backup.missingLocalBadge");
  expect(html).toContain("panel.backup.location.remote");
  expect(html).toContain("storage.example.test/archive.zip");
  expect(html).toContain("panel.backup.download");
});
it("keeps the administrator's site backup path visible and copyable", async () => {
  vi.mocked(getBackups).mockResolvedValue([locationRecord("C:/backups/a.zip")]);
  const state = await mount();
  state.expandedLocation = "location";
  const html = renderMarkup(state);
  expect(html).toContain("C:/backups/a.zip");
  expect(html).toContain("panel.backup.location.copy");
  expect(html).not.toContain("panel.backup.location.scopedHidden");
});
