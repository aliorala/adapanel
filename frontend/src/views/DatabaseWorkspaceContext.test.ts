import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { createRenderer, h, nextTick, ssrContextKey, type App, type Component } from "vue";
import DatabasesView from "./DatabasesView.vue";
import { useDatabasesStore } from "../stores/databases";
import { useSoftwareStore, type SoftwareCatalog } from "../stores/software";
import { useSitesStore } from "../stores/sites";
import { getDatabaseDetail, type DatabaseDetail, type DatabaseWorkspace } from "../api/databases";
const shared = vi.hoisted(() => ({ confirm: vi.fn(), prompt: vi.fn(), epoch: 0 }));
vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key }) }));
vi.mock("../components/ConfirmDialog.vue", () => ({ useConfirm: () => ({ confirm: shared.confirm }) }));
vi.mock("../components/PromptDialog.vue", () => ({ usePrompt: () => ({ prompt: shared.prompt }) }));
vi.mock("../api/client", async original => ({ ...await original<object>(), getSessionEpoch: () => shared.epoch }));
vi.mock("../api/databases", async original => ({ ...await original<object>(), getDatabaseDetail: vi.fn() }));
type State = { notice: string; detail: DatabaseDetail | null; openDetail: string; moveTarget: string; activeModule: string; removeDatabase(name: string): Promise<void>; removeUser(name: string, host: string): Promise<void>; changeUserPassword(name: string, host: string): Promise<void>; toggleDetail(name: string): Promise<void>; selectEngine(engine: string): void };
let app: App | undefined;
function deferred<T>() { let resolve!: (value: T) => void; let reject!: (error: unknown) => void; const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; }
function detail(name: string) { return { engine: "mariadb", name, users: [] } as unknown as DatabaseDetail; }
async function flush() { for (let i = 0; i < 12; i++) await nextTick(); }
beforeEach(() => { shared.epoch = 0; shared.confirm.mockResolvedValue(false); shared.prompt.mockResolvedValue(null); vi.mocked(getDatabaseDetail).mockImplementation(async (_engine, name) => detail(name)); });
afterEach(() => { app?.unmount(); app = undefined; vi.restoreAllMocks(); vi.resetAllMocks(); });
async function mount() {
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  const pinia = createPinia(); setActivePinia(pinia); const store = useDatabasesStore();
  const workspace = { engine: "mariadb", displayName: "MariaDB", databases: [{ name: "site_db", owner: "owner", users: ["site_user"] }], users: [{ userName: "site_user", host: "localhost" }] } as DatabaseWorkspace;
  store.workspaces = { mariadb: workspace, mysql: { ...workspace, engine: "mysql" } };
  vi.spyOn(store, "fetchEngines").mockResolvedValue([]); vi.spyOn(store, "fetchWorkspace").mockResolvedValue(workspace);
  vi.spyOn(store, "deleteDatabase").mockResolvedValue(); vi.spyOn(store, "deleteUser").mockResolvedValue(); vi.spyOn(store, "setUserPassword").mockResolvedValue(workspace);
  useSoftwareStore().catalog = { installed: [{ componentId: "mariadb-11", status: "Installed" }, { componentId: "mysql-8", status: "Installed" }], defaults: [], manifest: [] } as unknown as SoftwareCatalog;
  useSoftwareStore().loaded = true;
  vi.spyOn(useSoftwareStore(), "fetchCatalog").mockImplementation(async () => useSoftwareStore().catalog!);
  vi.spyOn(useSitesStore(), "fetchSites").mockResolvedValue([]);
  app = renderer.createApp({ ...DatabasesView, render: () => h("section") } as Component).use(pinia); app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: State }).setupState; await flush(); return { state, store };
}
for (const kind of ["database", "user"] as const) {
  it(`does not use a ${kind} deletion confirmation after switching away and back`, async () => {
    const { state, store } = await mount(); shared.confirm.mockImplementationOnce(async options => {
      state.selectEngine("mysql"); state.selectEngine("mariadb"); await options.action(); return true;
    });
    if (kind === "database") await state.removeDatabase("site_db"); else await state.removeUser("site_user", "localhost");
    expect(store.deleteDatabase).not.toHaveBeenCalled(); expect(store.deleteUser).not.toHaveBeenCalled();
  });
  it(`ignores a ${kind} deletion confirmation after the screen closes`, async () => {
    const { state, store } = await mount(); shared.confirm.mockImplementationOnce(async options => { app!.unmount(); app = undefined; await options.action(); return true; });
    if (kind === "database") await state.removeDatabase("site_db"); else await state.removeUser("site_user", "localhost");
    expect(store.deleteDatabase).not.toHaveBeenCalled(); expect(store.deleteUser).not.toHaveBeenCalled();
  });
  it(`deletes the confirmed ${kind} normally in its original workspace`, async () => {
    const { state, store } = await mount(); shared.confirm.mockImplementationOnce(async options => { await options.action(); return true; });
    if (kind === "database") { await state.removeDatabase("site_db"); expect(store.deleteDatabase).toHaveBeenCalledExactlyOnceWith("mariadb", "site_db"); }
    else { await state.removeUser("site_user", "localhost"); expect(store.deleteUser).toHaveBeenCalledExactlyOnceWith("mariadb", "site_user", "localhost"); }
  });
}
it("does not change a password on a different engine after the prompt", async () => {
  const { state, store } = await mount(); shared.prompt.mockImplementationOnce(async () => { state.selectEngine("mysql"); return "FixturePassword!"; });
  await state.changeUserPassword("site_user", "localhost"); expect(store.setUserPassword).not.toHaveBeenCalled();
});
it("does not apply a password from an obsolete authenticated session", async () => {
  const { state, store } = await mount(); shared.prompt.mockImplementationOnce(async () => { shared.epoch++; return "FixturePassword!"; });
  await state.changeUserPassword("site_user", "localhost"); expect(store.setUserPassword).not.toHaveBeenCalled();
});
it("changes the confirmed user's password normally", async () => {
  const { state, store } = await mount(); shared.prompt.mockResolvedValueOnce("FixturePassword!"); await state.changeUserPassword("site_user", "localhost");
  expect(store.setUserPassword).toHaveBeenCalledExactlyOnceWith("mariadb", "site_user", "FixturePassword!", "localhost");
});
it("does not open overlapping account or deletion dialogs", async () => {
  const { state } = await mount(), pending = deferred<string | null>(); shared.prompt.mockReturnValueOnce(pending.promise);
  const changing = state.changeUserPassword("site_user", "localhost"); await state.removeDatabase("site_db"); await state.removeUser("site_user", "localhost"); await state.changeUserPassword("site_user", "localhost");
  expect(shared.confirm).not.toHaveBeenCalled(); expect(shared.prompt).toHaveBeenCalledOnce(); pending.resolve(null); await changing;
});
it("does not use a confirmation after navigating to another module", async () => {
  const { state, store } = await mount(); shared.confirm.mockImplementationOnce(async options => { state.activeModule = "cache"; await options.action(); return true; });
  await state.removeDatabase("site_db"); expect(store.deleteDatabase).not.toHaveBeenCalled();
});
it("does not report an old deletion on a newly selected engine", async () => {
  const { state, store } = await mount(), pending = deferred<void>(); vi.mocked(store.deleteDatabase).mockReturnValueOnce(pending.promise);
  shared.confirm.mockImplementationOnce(async options => { await options.action(); return true; }); const operation = state.removeDatabase("site_db");
  state.selectEngine("mysql"); await flush(); pending.resolve(); await operation; expect(state.notice).toBe("");
});
it("keeps the latest database detail when an earlier response arrives last", async () => {
  const { state } = await mount(), first = deferred<DatabaseDetail>(); vi.mocked(getDatabaseDetail).mockReturnValueOnce(first.promise);
  const old = state.toggleDetail("old_db"); await state.toggleDetail("new_db"); first.resolve(detail("old_db")); await old;
  expect(state.openDetail).toBe("new_db"); expect(state.detail?.name).toBe("new_db");
});
it("does not close a current detail when an old database read fails", async () => {
  const { state } = await mount(), first = deferred<DatabaseDetail>(); vi.mocked(getDatabaseDetail).mockReturnValueOnce(first.promise);
  const old = state.toggleDetail("old_db"); await state.toggleDetail("new_db"); first.reject(new Error("old failure")); await old;
  expect(state.openDetail).toBe("new_db"); expect(state.notice).toBe("");
});
it("clears detail when switching database engines", async () => {
  const { state } = await mount(); await state.toggleDetail("site_db"); state.selectEngine("mysql"); await flush(); expect(state.openDetail).toBe(""); expect(state.detail).toBeNull();
});
it("ignores a detail response after its screen is closed", async () => {
  const { state } = await mount(), pending = deferred<DatabaseDetail>(); vi.mocked(getDatabaseDetail).mockReturnValueOnce(pending.promise);
  const read = state.toggleDetail("site_db"); app!.unmount(); app = undefined; pending.resolve(detail("site_db")); await read; expect(state.detail).toBeNull();
});
it("clears the selected destination when opening another database detail", async () => {
  const { state } = await mount(); await state.toggleDetail("first_db"); state.moveTarget = "first-site.test";
  await state.toggleDetail("second_db"); expect(state.moveTarget).toBe("");
});
