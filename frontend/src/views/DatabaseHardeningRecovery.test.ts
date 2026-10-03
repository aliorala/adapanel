import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { createRenderer, h, nextTick, ssrContextKey, type App, type Component } from "vue";
import DatabasesView from "./DatabasesView.vue";
import { useDatabasesStore } from "../stores/databases";
import { useSoftwareStore, type SoftwareCatalog } from "../stores/software";
import { useSitesStore } from "../stores/sites";
import * as api from "../api/databases";

const shared = vi.hoisted(() => ({ epoch: 0 }));
vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key }) }));
vi.mock("../components/ConfirmDialog.vue", () => ({ useConfirm: () => ({ confirm: vi.fn() }) }));
vi.mock("../components/PromptDialog.vue", () => ({ usePrompt: () => ({ prompt: vi.fn() }) }));
vi.mock("../api/client", async original => ({ ...await original<object>(), getSessionEpoch: () => shared.epoch }));
vi.mock("../api/databases", async original => ({ ...await original<object>(), hardenDatabase: vi.fn() }));

type EngineStatus = api.DatabaseEngineStatus & { adminPasswordPending?: boolean };
type State = {
  notice: string; noticeError: boolean; workspaceReady: boolean; hardening: boolean; needsCredentials: boolean;
  showDatabaseForm: boolean; showUserForm: boolean; showImportForm: boolean;
  databaseForm: { name: string }; userForm: { userName: string; password: string };
  importForm: { databaseName: string; userName: string; password: string };
  credForm: { password: string };
  hardenSecurity(): Promise<void>; refreshWorkspace(): Promise<void>; selectEngine(engine: string): void;
  submitCreateDatabase(): Promise<void>; submitCreateUser(): Promise<void>; submitImport(): Promise<void>;
  submitRegisterCredentials(): Promise<void>;
};
let app: App | undefined;
function status(overrides: Partial<EngineStatus> = {}): EngineStatus {
  return { engine: "mariadb", displayName: "MariaDB", installed: true, ready: false, adminPasswordPending: true,
    supportsHardening: true, systemDetected: false, host: "127.0.0.1", port: 3306, adminUserName: "root", ...overrides };
}
function workspace(engine = "mariadb"): api.DatabaseWorkspace {
  return { engine, displayName: engine, componentId: "mariadb-11", serviceName: "db", host: "127.0.0.1", port: 3306,
    adminUserName: "root", adminPassword: "", supportsHostScopedUsers: true, databases: [], users: [] };
}
function report(): api.DatabaseHardeningReport { return { clean: true, applied: [], failed: [], unverified: [], measured: true }; }
function deferred<T>() { let resolve!: (value: T) => void, reject!: (error: unknown) => void; const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; }
async function flush() { for (let i = 0; i < 24; i++) await nextTick(); }
function markup(state: State) {
  const output: unknown[] = [];
  (DatabasesView as unknown as { ssrRender: (...args: unknown[]) => void }).ssrRender(state, (part: unknown) => output.push(part), app!._instance, {}, {}, state, {}, {});
  return output.flat(Infinity).join("").replace(/<!--[\s\S]*?-->/g, "");
}
function hardeningButton(state: State) { return markup(state).match(/<button[^>]*>\s*panel\.database\.harden(?:ing)?\s*<\/button>/)?.[0]; }
beforeEach(() => { shared.epoch = 0; vi.mocked(api.hardenDatabase).mockResolvedValue(report()); });
afterEach(() => { app?.unmount(); app = undefined; vi.restoreAllMocks(); vi.resetAllMocks(); });
async function mount(engine = status(), configure?: (store: ReturnType<typeof useDatabasesStore>) => void) {
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  const pinia = createPinia(); setActivePinia(pinia);
  const store = useDatabasesStore(); store.engines = [engine, status({ engine: "mysql", ready: true, adminPasswordPending: false })];
  vi.spyOn(store, "fetchEngines").mockImplementation(async () => store.engines);
  vi.spyOn(store, "fetchWorkspace").mockImplementation(async key => { const result = workspace(key); store.workspaces[key] = result; return result; });
  vi.spyOn(store, "createDatabase").mockResolvedValue(workspace()); vi.spyOn(store, "createUser").mockResolvedValue(workspace());
  vi.spyOn(store, "importDatabase").mockResolvedValue(workspace()); vi.spyOn(store, "registerCredentials").mockResolvedValue(engine);
  useSoftwareStore().catalog = { installed: [{ componentId: "mariadb-11", status: "Installed" }, { componentId: "mysql-8", status: "Installed" }], defaults: [], manifest: [] } as unknown as SoftwareCatalog;
  vi.spyOn(useSitesStore(), "fetchSites").mockResolvedValue([]); configure?.(store);
  app = renderer.createApp({ ...DatabasesView, render: () => h("section") } as Component).use(pinia); app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: State }).setupState; await flush(); return { state, store };
}

it("offers a measured pending-password recovery without first opening the blocked workspace", async () => {
  const { state, store } = await mount();
  expect(state.needsCredentials).toBe(true); expect(state.workspaceReady).toBe(false); expect(store.fetchWorkspace).not.toHaveBeenCalled();
  expect(hardeningButton(state)).toBeDefined(); expect(hardeningButton(state)).not.toContain("disabled");
  await state.hardenSecurity(); expect(api.hardenDatabase).toHaveBeenCalledExactlyOnceWith("mariadb");
  expect(state.noticeError).toBe(true); expect(state.notice).toBe("database.admin_password_unverified");
});

it.each([true, false])("explains the credential form according to the measured pending flag %s", async pending => {
  const { state } = await mount(status({ adminPasswordPending: pending }));
  const html = markup(state);
  expect(html.includes("database.admin_password_unverified")).toBe(pending);
  expect(html.includes("panel.database.manageExistingHint")).toBe(!pending);
});

it.each([
  ["ordinary missing credentials", { adminPasswordPending: false }],
  ["older server without pending measurement", { adminPasswordPending: undefined }],
  ["uninstalled engine", { installed: false }],
  ["unsupported hardening", { supportsHardening: false }]
] as const)("does not offer recovery for %s", async (_name, overrides) => {
  const { state } = await mount(status(overrides)); await state.hardenSecurity();
  expect(api.hardenDatabase).not.toHaveBeenCalled(); const button = hardeningButton(state); if (button) expect(button).toContain("disabled");
});

it("does not trust cached pending metadata when the initial engine measurement failed", async () => {
  const { state } = await mount(status(), store => vi.mocked(store.fetchEngines).mockRejectedValueOnce(new Error("engine measurement failed")));
  await state.hardenSecurity(); expect(api.hardenDatabase).not.toHaveBeenCalled(); expect(hardeningButton(state)).toContain("disabled");
});

it("opens ordinary work only after recovery and a fresh ready workspace measurement", async () => {
  const { state, store } = await mount();
  vi.mocked(api.hardenDatabase).mockImplementationOnce(async () => { store.engines[0] = status({ ready: true, adminPasswordPending: false }); return report(); });
  await state.hardenSecurity();
  expect(store.fetchEngines).toHaveBeenCalledTimes(2); expect(store.fetchWorkspace).toHaveBeenCalledExactlyOnceWith("mariadb");
  expect(state.workspaceReady).toBe(true); expect(state.needsCredentials).toBe(false); expect(state.noticeError).toBe(false);
});

it("keeps ordinary database, user and import writes blocked while recovery is pending", async () => {
  const { state, store } = await mount(); state.showDatabaseForm = state.showUserForm = state.showImportForm = true;
  state.databaseForm.name = "new_db"; Object.assign(state.userForm, { userName: "user", password: "pass" });
  Object.assign(state.importForm, { databaseName: "new_db", userName: "user", password: "pass" });
  await state.submitCreateDatabase(); await state.submitCreateUser(); await state.submitImport();
  expect(store.createDatabase).not.toHaveBeenCalled(); expect(store.createUser).not.toHaveBeenCalled(); expect(store.importDatabase).not.toHaveBeenCalled();
});

it("holds the recovery lock through status and workspace remeasurement", async () => {
  const { state, store } = await mount(), pending = deferred<api.DatabaseEngineStatus[]>();
  vi.mocked(store.fetchEngines).mockReturnValueOnce(pending.promise); const first = state.hardenSecurity(); await flush();
  await state.hardenSecurity(); expect(api.hardenDatabase).toHaveBeenCalledOnce(); expect(state.hardening).toBe(true);
  store.engines[0] = status({ ready: true, adminPasswordPending: false }); pending.resolve(store.engines); await first; expect(state.hardening).toBe(false);
});

it("does not register a competing password during recovery", async () => {
  const { state, store } = await mount(), pending = deferred<api.DatabaseHardeningReport>();
  vi.mocked(api.hardenDatabase).mockReturnValueOnce(pending.promise); const first = state.hardenSecurity(); state.credForm.password = "operator password";
  await state.submitRegisterCredentials(); expect(store.registerCredentials).not.toHaveBeenCalled(); pending.resolve(report()); await first;
});

it("keeps a pending recovery available after a failed attempt without claiming ordinary readiness", async () => {
  const { state, store } = await mount(); vi.mocked(api.hardenDatabase).mockRejectedValueOnce(new Error("database.admin_password_unverified"));
  await state.hardenSecurity(); expect(store.fetchEngines).toHaveBeenCalledTimes(2);
  expect(state.workspaceReady).toBe(false); expect(state.noticeError).toBe(true); expect(hardeningButton(state)).not.toContain("disabled");
});

it("discovers pending password metadata after a previously ready hardening attempt failed", async () => {
  const { state, store } = await mount(status({ ready: true, adminPasswordPending: false }));
  vi.mocked(api.hardenDatabase).mockImplementationOnce(async () => { store.engines[0] = status(); throw new Error("database.admin_password_unverified"); });
  await state.hardenSecurity(); expect(store.fetchEngines).toHaveBeenCalledTimes(2); expect(state.workspaceReady).toBe(false);
  expect(hardeningButton(state)).not.toContain("disabled"); expect(state.noticeError).toBe(true);
});

it.each(["engine", "session", "unmount"])("does not read or publish an old recovery result after %s changes", async kind => {
  const { state, store } = await mount(), pending = deferred<api.DatabaseHardeningReport>();
  vi.mocked(api.hardenDatabase).mockReturnValueOnce(pending.promise); const first = state.hardenSecurity();
  expect(api.hardenDatabase).toHaveBeenCalledOnce();
  if (kind === "engine") { state.selectEngine("mysql"); await flush(); } else if (kind === "session") shared.epoch++; else { app!.unmount(); app = undefined; }
  const reads = vi.mocked(store.fetchEngines).mock.calls.length; pending.resolve(report()); await first;
  expect(store.fetchEngines).toHaveBeenCalledTimes(reads); expect(state.notice).toBe("");
});

it("does not report a ready workspace after the recovery status refresh failed", async () => {
  const { state, store } = await mount(); vi.mocked(store.fetchEngines).mockRejectedValueOnce(new Error("engine measurement failed"));
  await state.hardenSecurity(); expect(api.hardenDatabase).toHaveBeenCalledOnce(); expect(state.workspaceReady).toBe(false);
  expect(state.noticeError).toBe(true); expect(hardeningButton(state)).toContain("disabled");
});

it("preserves normal hardening and remeasures the ready workspace", async () => {
  const { state, store } = await mount(status({ ready: true, adminPasswordPending: false }));
  await state.hardenSecurity(); expect(api.hardenDatabase).toHaveBeenCalledExactlyOnceWith("mariadb");
  expect(store.fetchWorkspace).toHaveBeenCalledTimes(2); expect(state.workspaceReady).toBe(true); expect(state.noticeError).toBe(false);
});

it("closes a previously ready workspace for the duration of password hardening", async () => {
  const { state, store } = await mount(status({ ready: true, adminPasswordPending: false })), pending = deferred<api.DatabaseHardeningReport>();
  vi.mocked(api.hardenDatabase).mockReturnValueOnce(pending.promise); const first = state.hardenSecurity();
  expect(state.workspaceReady).toBe(false); state.showDatabaseForm = true; state.databaseForm.name = "new_db";
  await state.submitCreateDatabase(); expect(store.createDatabase).not.toHaveBeenCalled();
  pending.resolve(report()); await first; expect(state.workspaceReady).toBe(true);
});

it("allows recovery again after a successful explicit retry of a failed status refresh", async () => {
  const { state, store } = await mount(); vi.mocked(store.fetchEngines).mockRejectedValueOnce(new Error("engine measurement failed"));
  await state.refreshWorkspace(); expect(hardeningButton(state)).toContain("disabled");
  await state.refreshWorkspace(); expect(hardeningButton(state)).not.toContain("disabled");
  await state.hardenSecurity(); expect(api.hardenDatabase).toHaveBeenCalledOnce();
});

it.each(["not ready", "missing", "uninstalled"])("does not claim recovery when the final engine status is %s", async finalStatus => {
  const { state, store } = await mount();
  vi.mocked(api.hardenDatabase).mockImplementationOnce(async () => {
    store.engines = finalStatus === "missing" ? [] : [status({ adminPasswordPending: false, installed: finalStatus !== "uninstalled", ready: finalStatus === "uninstalled" })];
    return report();
  });
  await state.hardenSecurity();
  expect(state.workspaceReady).toBe(false); expect(store.fetchWorkspace).not.toHaveBeenCalled();
  expect(state.noticeError).toBe(true); expect(state.notice).toBe("panel.database.hardenedUnverified");
});
