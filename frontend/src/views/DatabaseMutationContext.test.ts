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
vi.mock("../api/databases", async original => ({ ...await original<object>(), getDatabaseDetail: vi.fn(), moveDatabase: vi.fn(), checkAndRepairDatabase: vi.fn(), getUserRemoteAccess: vi.fn(), setUserRemoteAccess: vi.fn(), hardenDatabase: vi.fn(), openAdminer: vi.fn(), openPhpMyAdmin: vi.fn() }));

type State = {
  notice: string; detail: api.DatabaseDetail | null; openDetail: string; checkResult: api.DatabaseCheckResult | null;
  activeModule: string; activeSection: string; selectedTables: string[]; attachUserName: string; moveTarget: string;
  attaching: boolean; moving: boolean; checking: boolean; remoteLoading: boolean; remoteSaving: boolean; openingDb: string;
  remoteEditor: { userName: string; mode: api.RemoteAccessMode; origins: string; confirmAnywhere: boolean; applyFirewall: boolean };
  remoteEngines: api.DatabaseRemoteAccessEngine[];
  databaseForm: { name: string; userName: string; password: string; siteName: string };
  userForm: { userName: string; password: string; databaseName: string };
  importForm: { databaseName: string; userName: string; password: string; siteName: string };
  credForm: { adminUserName: string; password: string; port: number };
  showDatabaseForm: boolean; showUserForm: boolean; showImportForm: boolean;
  selectEngine(engine: string): void; openSection(section: string): void;
  toggleDetail(name: string): Promise<void>; openRemoteEditor(user: api.DatabaseUserRemoteAccess): void;
  loadRemoteAccess(): Promise<void>; submitRemoteAccess(): Promise<void>;
  runCheck(name: string): Promise<void>; runRepair(name: string): Promise<void>;
  attachUser(name: string): Promise<void>; doMove(name: string): Promise<void>;
  submitCreateDatabase(): Promise<void>; submitCreateUser(): Promise<void>; submitImport(): Promise<void>; submitRegisterCredentials(): Promise<void>;
  installSelectedEngine(): Promise<void>; hardenSecurity(): Promise<void>; refreshWorkspace(): Promise<void>;
  openInAdminer(name: string): Promise<void>; openInPhpMyAdmin(name: string): Promise<void>;
};
let app: App | undefined;
let tab: { opener: unknown; closed: boolean; close: ReturnType<typeof vi.fn>; location: { replace: ReturnType<typeof vi.fn> } };
function deferred<T>() { let resolve!: (value: T) => void; let reject!: (error: unknown) => void; const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; }
async function flush() { for (let i = 0; i < 12; i++) await nextTick(); }
function workspace(engine = "mariadb") { return { engine, displayName: engine, databases: [{ name: "site_db", owner: "owner", users: ["site_user"] }, { name: "other_db", owner: "owner", users: [] }], users: [{ userName: "site_user", host: "localhost" }] } as api.DatabaseWorkspace; }
function detail(engine = "mariadb", name = "site_db") { return { engine, name, users: [] } as unknown as api.DatabaseDetail; }
function remote(engine = "mariadb"): api.DatabaseRemoteAccessEngine { return { engine, displayName: engine, mechanism: "mysqlHost", serverHost: "127.0.0.1", warningKey: "", supported: true, installed: true, port: 3306, users: [{ engine, userName: "site_user", mode: "closed", allowedOrigins: [], engineHosts: ["localhost"], remoteOpen: false, databases: ["site_db"] }], firewallOpen: false, firewallAllowedAddresses: [] }; }
function report(): api.DatabaseCheckResult { return { engine: "mariadb", database: "site_db", problemCount: 1, tables: [{ table: "broken", status: "problem", canRepair: true, canRebuild: false }] } as api.DatabaseCheckResult; }
beforeEach(() => {
  shared.epoch = 0;
  vi.mocked(api.getDatabaseDetail).mockImplementation(async (engine, name) => detail(engine, name));
  vi.mocked(api.getUserRemoteAccess).mockResolvedValue([remote(), remote("mysql")]);
  vi.mocked(api.setUserRemoteAccess).mockImplementation(async input => remote(input.engine));
  vi.mocked(api.checkAndRepairDatabase).mockResolvedValue(report());
  tab = { opener: {}, closed: false, close: vi.fn(), location: { replace: vi.fn() } };
  vi.stubGlobal("window", { open: vi.fn(() => tab), location: { origin: "https://panel.test", hostname: "panel.test", protocol: "https:" } });
});
afterEach(() => { app?.unmount(); app = undefined; vi.restoreAllMocks(); vi.resetAllMocks(); vi.unstubAllGlobals(); });
async function mount(configure?: (store: ReturnType<typeof useDatabasesStore>) => void) {
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  const pinia = createPinia(); setActivePinia(pinia); const store = useDatabasesStore(), software = useSoftwareStore();
  store.workspaces = { mariadb: workspace(), mysql: workspace("mysql") };
  store.engines = ["mariadb", "mysql"].map(engine => ({ engine, installed: true, ready: true, supportsHardening: true, port: engine === "mysql" ? 3307 : 3306 })) as api.DatabaseEngineStatus[];
  vi.spyOn(store, "fetchEngines").mockImplementation(async () => store.engines);
  vi.spyOn(store, "fetchWorkspace").mockImplementation(async engine => workspace(engine));
  vi.spyOn(store, "createDatabase").mockResolvedValue(workspace()); vi.spyOn(store, "createUser").mockResolvedValue(workspace());
  vi.spyOn(store, "importDatabase").mockResolvedValue(workspace()); vi.spyOn(store, "registerCredentials").mockResolvedValue(store.engines[0]);
  vi.spyOn(store, "grantAccess").mockResolvedValue(workspace());
  software.catalog = { installed: [{ componentId: "mariadb-11", status: "Installed" }, { componentId: "mysql-8", status: "Installed" }], defaults: [], manifest: [] } as unknown as SoftwareCatalog;
  vi.spyOn(software, "fetchCatalog").mockImplementation(async () => software.catalog!); vi.spyOn(software, "installComponent").mockResolvedValue({} as never);
  vi.spyOn(useSitesStore(), "fetchSites").mockResolvedValue([]);
  configure?.(store);
  app = renderer.createApp({ ...DatabasesView, render: () => h("section") } as Component).use(pinia); app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: State }).setupState; await flush(); return { state, store, software };
}

it("does not reuse remote access policy and firewall consent on another engine", async () => {
  const { state } = await mount(); state.openSection("remote"); await flush(); state.openRemoteEditor(remote().users[0]);
  Object.assign(state.remoteEditor, { mode: "anywhere", confirmAnywhere: true, applyFirewall: true });
  state.selectEngine("mysql"); await flush(); await state.submitRemoteAccess();
  expect(api.setUserRemoteAccess).not.toHaveBeenCalled(); expect(state.remoteEditor.confirmAnywhere).toBe(false); expect(state.remoteEditor.applyFirewall).toBe(false);
});
it("does not turn an old engine's check into a repair on the same named database of another engine", async () => {
  const { state } = await mount(), pending = deferred<api.DatabaseCheckResult>(); await state.toggleDetail("site_db");
  vi.mocked(api.checkAndRepairDatabase).mockReturnValueOnce(pending.promise); const read = state.runCheck("site_db");
  state.selectEngine("mysql"); await flush(); await state.toggleDetail("site_db"); pending.resolve(report()); await read;
  if (state.checkResult) { state.selectedTables = ["broken"]; await state.runRepair("site_db"); }
  expect(api.checkAndRepairDatabase).toHaveBeenCalledTimes(1); expect(state.checkResult).toBeNull();
});
it("saves a confirmed remote policy in its original engine", async () => {
  const { state } = await mount(); state.openSection("remote"); await flush(); state.openRemoteEditor(remote().users[0]);
  Object.assign(state.remoteEditor, { mode: "anywhere", confirmAnywhere: true, applyFirewall: true }); await state.submitRemoteAccess();
  expect(api.setUserRemoteAccess).toHaveBeenCalledExactlyOnceWith({ engine: "mariadb", userName: "site_user", mode: "anywhere", confirmAnywhere: true, applyFirewall: true, allowedOrigins: [] });
});
it("repairs a selected problem table from a current check", async () => {
  const { state } = await mount(); await state.toggleDetail("site_db"); await state.runCheck("site_db"); state.selectedTables = ["broken"]; await state.runRepair("site_db");
  expect(api.checkAndRepairDatabase).toHaveBeenLastCalledWith("mariadb", "site_db", ["broken"]);
});

const forms = [
  ["databaseForm", "showDatabaseForm", "submitCreateDatabase", "createDatabase"],
  ["userForm", "showUserForm", "submitCreateUser", "createUser"],
  ["importForm", "showImportForm", "submitImport", "importDatabase"],
  ["credForm", null, "submitRegisterCredentials", "registerCredentials"]
] as const;
function fillForm(state: State, form: typeof forms[number][0], open: typeof forms[number][1]) {
  if (open) state[open] = true;
  Object.assign(state[form], { password: "FixturePassword!" });
  if (form === "databaseForm") Object.assign(state.databaseForm, { name: "new_db", userName: "new_user", siteName: "" });
  if (form === "userForm") Object.assign(state.userForm, { userName: "new_user", databaseName: "site_db" });
  if (form === "importForm") Object.assign(state.importForm, { databaseName: "site_db", userName: "site_user", siteName: "" });
  if (form === "credForm") Object.assign(state.credForm, { adminUserName: "root", port: 3306 });
}
for (const [form, open, submit, method] of forms) {
  it(`blocks repeated ${submit} while its first write is pending`, async () => {
    const { state, store } = await mount(), pending = deferred<api.DatabaseWorkspace>();
    if (form === "credForm") store.engines[0].ready = false;
    fillForm(state, form, open); vi.mocked(store[method]).mockReturnValueOnce(pending.promise as never);
    const first = state[submit](); await state[submit](); expect(store[method]).toHaveBeenCalledTimes(1);
    pending.resolve(workspace()); await first;
  });
  it(`preserves a new engine's ${form} after the old write finishes`, async () => {
    const { state, store } = await mount(), pending = deferred<api.DatabaseWorkspace>();
    if (form === "credForm") store.engines.forEach(engine => { engine.ready = false; });
    fillForm(state, form, open); vi.mocked(store[method]).mockReturnValueOnce(pending.promise as never); const first = state[submit]();
    state.selectEngine("mysql"); await flush(); fillForm(state, form, open); state[form].password = "NewDraftPassword!";
    pending.resolve(workspace()); await first;
    expect(state[form].password).toBe("NewDraftPassword!"); if (open) expect(state[open]).toBe(true); expect(state.notice).toBe("");
  });
  it(`keeps edits made to ${form} while its write is running`, async () => {
    const { state, store } = await mount(), pending = deferred<api.DatabaseWorkspace>();
    if (form === "credForm") store.engines[0].ready = false;
    fillForm(state, form, open); vi.mocked(store[method]).mockReturnValueOnce(pending.promise as never); const first = state[submit]();
    state[form].password = "NewDraftPassword!"; pending.resolve(workspace()); await first;
    expect(state[form].password).toBe("NewDraftPassword!"); if (open) expect(state[open]).toBe(true);
  });
  it(`submits ${form} normally and clears its password`, async () => {
    const { state, store } = await mount(); if (form === "credForm") store.engines[0].ready = false;
    fillForm(state, form, open); await state[submit](); expect(store[method]).toHaveBeenCalledOnce(); expect(state[form].password).toBe("");
  });
}
it("clears all passwords and the open import form when switching engines", async () => {
  const { state } = await mount(); for (const [form, open] of forms) fillForm(state, form, open);
  state.selectEngine("mysql"); await flush(); for (const [form] of forms) expect(state[form].password).toBe(""); expect(state.showImportForm).toBe(false);
});
for (const kind of ["attach", "move"] as const) {
  it(`does not refresh ${kind} against a later selected engine or overwrite its detail`, async () => {
    const { state, store } = await mount(), pending = deferred<api.DatabaseWorkspace>(); await state.toggleDetail("site_db");
    if (kind === "attach") { state.attachUserName = "site_user|localhost"; vi.mocked(store.grantAccess).mockReturnValueOnce(pending.promise); }
    else { state.moveTarget = "target.test"; vi.mocked(api.moveDatabase).mockReturnValueOnce(pending.promise as never); }
    const first = kind === "attach" ? state.attachUser("site_db") : state.doMove("site_db");
    state.selectEngine("mysql"); await flush(); await state.toggleDetail("other_db"); const reads = vi.mocked(api.getDatabaseDetail).mock.calls.length;
    pending.resolve(workspace()); await first; expect(api.getDatabaseDetail).toHaveBeenCalledTimes(reads); expect(state.detail?.name).toBe("other_db"); expect(state.notice).toBe("");
  });
}
it("does not close a different user's remote editor when an old save finishes", async () => {
  const extra = { ...remote().users[0], userName: "second_user" }, pending = deferred<api.DatabaseRemoteAccessEngine>();
  vi.mocked(api.getUserRemoteAccess).mockResolvedValue([{ ...remote(), users: [remote().users[0], extra] }]);
  const { state } = await mount(); state.openSection("remote"); await flush(); state.openRemoteEditor(remote().users[0]);
  vi.mocked(api.setUserRemoteAccess).mockReturnValueOnce(pending.promise); const saving = state.submitRemoteAccess(); state.openRemoteEditor(extra);
  pending.resolve(remote()); await saving; expect(state.remoteEditor.userName).toBe("second_user");
});
it("cannot save stale remote access data after a failed refresh", async () => {
  const { state } = await mount(); state.openSection("remote"); await flush(); state.openRemoteEditor(remote().users[0]);
  vi.mocked(api.getUserRemoteAccess).mockRejectedValueOnce(new Error("read failed")); await state.loadRemoteAccess(); await state.submitRemoteAccess();
  expect(api.setUserRemoteAccess).not.toHaveBeenCalled(); expect(state.remoteEngines).toEqual([]);
});
it("keeps the latest remote read when earlier reads finish last", async () => {
  const { state } = await mount(), first = deferred<api.DatabaseRemoteAccessEngine[]>();
  vi.mocked(api.getUserRemoteAccess).mockReturnValueOnce(first.promise); const old = state.loadRemoteAccess();
  vi.mocked(api.getUserRemoteAccess).mockResolvedValueOnce([{ ...remote(), port: 13306 }]); await state.loadRemoteAccess(); first.resolve([remote()]); await old;
  expect(state.remoteEngines[0].port).toBe(13306);
});
for (const method of ["openInAdminer", "openInPhpMyAdmin"] as const) {
  it(`closes the reserved ${method} tab when its context changes`, async () => {
    const { state } = await mount(), pending = deferred<{ url: string }>();
    vi.mocked(method === "openInAdminer" ? api.openAdminer : api.openPhpMyAdmin).mockReturnValueOnce(pending.promise);
    const old = state[method]("site_db"); expect(window.open).toHaveBeenCalledOnce(); state.selectEngine("mysql"); await flush();
    pending.resolve({ url: "https://db.test/session" }); await old; expect(tab.location.replace).not.toHaveBeenCalled(); expect(tab.close).toHaveBeenCalledOnce();
  });
  it(`preserves normal ${method} navigation`, async () => {
    const { state } = await mount(); vi.mocked(method === "openInAdminer" ? api.openAdminer : api.openPhpMyAdmin).mockResolvedValueOnce({ url: "https://db.test/session" });
    await state[method]("site_db"); expect(tab.location.replace).toHaveBeenCalledWith("https://db.test/session"); expect(tab.opener).toBeNull();
  });
}
it("does not report hardening on another engine", async () => {
  const { state } = await mount(), pending = deferred<Awaited<ReturnType<typeof api.hardenDatabase>>>(); vi.mocked(api.hardenDatabase).mockReturnValueOnce(pending.promise);
  const old = state.hardenSecurity(); state.selectEngine("mysql"); await flush(); pending.resolve({ clean: true, applied: [], failed: [] } as never); await old; expect(state.notice).toBe("");
});
it("does not continue an old refresh against a different engine", async () => {
  const { state, store } = await mount(), pending = deferred<api.DatabaseEngineStatus[]>(); vi.mocked(store.fetchEngines).mockReturnValueOnce(pending.promise);
  const old = state.refreshWorkspace(); state.selectEngine("mysql"); await flush(); const reads = vi.mocked(store.fetchWorkspace).mock.calls.length;
  pending.resolve(store.engines); await old; expect(store.fetchWorkspace).toHaveBeenCalledTimes(reads); expect(state.notice).toBe("");
});
it("does not overwrite a manual engine choice after initial engine loading", async () => {
  const pending = deferred<api.DatabaseEngineStatus[]>(); const { state, store } = await mount(store => { vi.mocked(store.fetchEngines).mockReturnValueOnce(pending.promise); });
  state.selectEngine("mysql"); await flush(); pending.resolve(store.engines); await flush(); expect(store.selectedEngine).toBe("mysql");
});
it("does not publish an installation notice after leaving its context", async () => {
  const { state, software } = await mount(), pending = deferred<Awaited<ReturnType<typeof software.installComponent>>>();
  software.catalog!.installed = []; vi.mocked(software.installComponent).mockReturnValueOnce(pending.promise); const old = state.installSelectedEngine();
  state.activeModule = "cache"; pending.resolve({} as never); await old; expect(state.notice).toBe("");
});

it("keeps new origins typed while a remote save is pending", async () => {
  const { state } = await mount(), pending = deferred<api.DatabaseRemoteAccessEngine>(); state.openSection("remote"); await flush(); state.openRemoteEditor(remote().users[0]);
  Object.assign(state.remoteEditor, { mode: "allowlist", origins: "192.0.2.1" }); vi.mocked(api.setUserRemoteAccess).mockReturnValueOnce(pending.promise);
  const saving = state.submitRemoteAccess(); state.remoteEditor.origins = "192.0.2.2"; pending.resolve(remote()); await saving;
  expect(state.remoteEditor.userName).toBe("site_user"); expect(state.remoteEditor.origins).toBe("192.0.2.2");
});
for (const kind of ["attach", "move"] as const) {
  it(`preserves normal ${kind} and blocks duplicate writes`, async () => {
    const { state, store } = await mount(), pending = deferred<api.DatabaseWorkspace>(); await state.toggleDetail("site_db");
    if (kind === "attach") { state.attachUserName = "site_user|localhost"; vi.mocked(store.grantAccess).mockReturnValueOnce(pending.promise); }
    else { state.moveTarget = "target.test"; vi.mocked(api.moveDatabase).mockReturnValueOnce(pending.promise as never); }
    const first = kind === "attach" ? state.attachUser("site_db") : state.doMove("site_db");
    await (kind === "attach" ? state.attachUser("site_db") : state.doMove("site_db")); pending.resolve(workspace()); await first;
    if (kind === "attach") expect(store.grantAccess).toHaveBeenCalledExactlyOnceWith("mariadb", "site_db", "site_user", "localhost");
    else expect(api.moveDatabase).toHaveBeenCalledExactlyOnceWith("mariadb", "site_db", "target.test");
    expect(state.notice).toBe(kind === "attach" ? "panel.database.attached" : "panel.database.moved"); expect(state.detail?.name).toBe("site_db");
  });
}
it("rejects repairs without a current check or with a table outside its repairable results", async () => {
  const { state } = await mount(); await state.toggleDetail("site_db"); state.selectedTables = ["unknown"]; await state.runRepair("site_db");
  expect(api.checkAndRepairDatabase).not.toHaveBeenCalled(); await state.runCheck("site_db"); state.selectedTables = ["unknown"]; await state.runRepair("site_db"); expect(api.checkAndRepairDatabase).toHaveBeenCalledOnce();
});
it("does not clear a newer engine's check-in-progress flag when the old check completes", async () => {
  const { state } = await mount(), old = deferred<api.DatabaseCheckResult>(), current = deferred<api.DatabaseCheckResult>(); await state.toggleDetail("site_db");
  vi.mocked(api.checkAndRepairDatabase).mockReturnValueOnce(old.promise).mockReturnValueOnce(current.promise); const first = state.runCheck("site_db");
  state.selectEngine("mysql"); await flush(); await state.toggleDetail("site_db"); const second = state.runCheck("site_db");
  old.resolve(report()); await first; expect(state.checking).toBe(true); current.resolve({ ...report(), engine: "mysql" }); await second; expect(state.checking).toBe(false);
});
it.each(["fetchWorkspace", "fetchEngines"] as const)("rejects writes after %s refresh failed even if cached rows remain", async method => {
  const { state, store } = await mount(); vi.mocked(store[method]).mockRejectedValueOnce(new Error("read failed")); await state.refreshWorkspace();
  fillForm(state, "databaseForm", "showDatabaseForm"); await state.submitCreateDatabase(); expect(store.createDatabase).not.toHaveBeenCalled();
});
it("does not lose an error from the post-write detail refresh", async () => {
  const { state, store } = await mount(); await state.toggleDetail("site_db"); state.attachUserName = "site_user|localhost";
  vi.mocked(api.getDatabaseDetail).mockRejectedValueOnce(new Error("refresh failed")); await state.attachUser("site_db");
  expect(store.grantAccess).toHaveBeenCalledOnce(); expect(state.notice).toBe("common.unexpected_error");
});
for (const close of ["unmount", "session"] as const) {
  it(`does not use a remote policy response after ${close}`, async () => {
    const { state } = await mount(), pending = deferred<api.DatabaseRemoteAccessEngine[]>(); vi.mocked(api.getUserRemoteAccess).mockReturnValueOnce(pending.promise);
    const first = state.loadRemoteAccess(); if (close === "unmount") { app!.unmount(); app = undefined; } else shared.epoch++;
    pending.resolve([remote()]); await first; expect(state.remoteEngines).toEqual([]); expect(state.notice).toBe("");
  });
  it(`closes a generated web tab after ${close}`, async () => {
    const { state } = await mount(), pending = deferred<{ url: string }>(); vi.mocked(api.openAdminer).mockReturnValueOnce(pending.promise);
    const first = state.openInAdminer("site_db"); if (close === "unmount") { app!.unmount(); app = undefined; } else shared.epoch++;
    pending.resolve({ url: "https://db.test/session" }); await first; expect(tab.location.replace).not.toHaveBeenCalled(); expect(tab.close).toHaveBeenCalledOnce();
  });
}
it("finishes a normal installation without dispatching it twice", async () => {
  const { state, software, store } = await mount(), pending = deferred<Awaited<ReturnType<typeof software.installComponent>>>(); software.catalog!.installed = [];
  vi.mocked(software.installComponent).mockReturnValueOnce(pending.promise); const first = state.installSelectedEngine(); await state.installSelectedEngine();
  software.catalog!.installed = [{ componentId: "mariadb-11", status: "Installed" }] as never; pending.resolve({} as never); await first;
  expect(software.installComponent).toHaveBeenCalledExactlyOnceWith("mariadb-11"); expect(store.fetchWorkspace).toHaveBeenLastCalledWith("mariadb"); expect(state.notice).toBe("software.install.completed");
});
it("reports normal hardening honestly and blocks duplicate hardening", async () => {
  const { state } = await mount(), pending = deferred<Awaited<ReturnType<typeof api.hardenDatabase>>>(); vi.mocked(api.hardenDatabase).mockReturnValueOnce(pending.promise);
  const first = state.hardenSecurity(); await state.hardenSecurity(); pending.resolve({ clean: false, applied: [], failed: ["fixture"] } as never); await first;
  expect(api.hardenDatabase).toHaveBeenCalledExactlyOnceWith("mariadb"); expect(state.notice).toBe("panel.database.hardenedIncomplete");
});
it("refreshes the original engine normally without duplicate refreshes", async () => {
  const { state, store } = await mount(), pending = deferred<api.DatabaseEngineStatus[]>(); vi.mocked(store.fetchEngines).mockClear().mockReturnValueOnce(pending.promise);
  const first = state.refreshWorkspace(); await state.refreshWorkspace(); pending.resolve(store.engines); await first;
  expect(store.fetchEngines).toHaveBeenCalledOnce(); expect(store.fetchWorkspace).toHaveBeenLastCalledWith("mariadb"); expect(state.notice).toBe("panel.database.refreshed");
});
it("allows only one external tool request and navigates the reserved tab", async () => {
  const { state } = await mount(), pending = deferred<{ url: string }>(); vi.mocked(api.openAdminer).mockReturnValueOnce(pending.promise);
  const first = state.openInAdminer("site_db"); await state.openInPhpMyAdmin("other_db"); expect(window.open).toHaveBeenCalledOnce(); expect(api.openPhpMyAdmin).not.toHaveBeenCalled();
  pending.resolve({ url: "https://db.test/session" }); await first; expect(tab.location.replace).toHaveBeenCalledWith("https://db.test/session");
});
