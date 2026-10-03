import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { createRenderer, h, nextTick, ssrContextKey, type App, type Component } from "vue";
import SiteDatabasesView from "./SiteDatabasesView.vue";
import { getSiteDatabases, linkSiteDatabase, unlinkSiteDatabase, type SiteDatabaseRef, type SiteDatabases } from "../api/sites";
import { createDatabase, deleteDatabase, openPhpMyAdmin, openAdminer, type DatabaseWorkspace } from "../api/databases";
import { useSitesStore } from "../stores/sites";
import { useAuthStore, type CurrentUser } from "../stores/auth";
import { invalidateSessionRequests } from "../api/client";
import type { ConfirmOptions } from "../components/ConfirmDialog.vue";

const confirm = vi.hoisted(() => vi.fn());
vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key }) }));
vi.mock("../components/ConfirmDialog.vue", () => ({ useConfirm: () => ({ confirm }) }));
vi.mock("../api/sites", async original => ({ ...await original<object>(), getSiteDatabases: vi.fn(), linkSiteDatabase: vi.fn(), unlinkSiteDatabase: vi.fn() }));
vi.mock("../api/databases", async original => ({ ...await original<object>(), createDatabase: vi.fn(), deleteDatabase: vi.fn(), openPhpMyAdmin: vi.fn(), openAdminer: vi.fn() }));

function workspace(site = "a.test"): SiteDatabases { return { linked: [{ engine: "mariadb", name: site.replace(/\./g, "_") }], candidates: [{ engine: "mariadb", name: "existing" }, { engine: "postgresql", name: "other" }] }; }
let app: App | undefined;
let tab: { opener: unknown; closed: boolean; close: ReturnType<typeof vi.fn>; location: { replace: ReturnType<typeof vi.fn> } };
beforeEach(() => {
  vi.mocked(getSiteDatabases).mockImplementation(async site => workspace(site));
  confirm.mockImplementation(async (options: ConfirmOptions) => { await options.action?.(); return true; });
  tab = { opener: {}, closed: false, close: vi.fn(), location: { replace: vi.fn() } };
  vi.stubGlobal("window", { open: vi.fn(() => tab), location: { origin: "https://panel.test", hostname: "panel.test", protocol: "https:" } });
});
afterEach(() => { app?.unmount(); app = undefined; vi.restoreAllMocks(); vi.resetAllMocks(); vi.unstubAllGlobals(); });
function deferred<T>() { let resolve!: (value: T) => void, reject!: (error: unknown) => void; const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; }
type State = {
  databases: SiteDatabaseRef[]; candidates: SiteDatabaseRef[]; selectedCandidate: string; loaded: boolean; loading: boolean;
  creating: boolean; linking: boolean; opening: boolean; deletingKey: string; unlinkingKey: string; showForm: boolean;
  form: { name: string; userName: string; password: string }; notice: string;
  load(): Promise<void>; createDb(): Promise<void>; linkDb(): Promise<void>; unlinkDb(db: SiteDatabaseRef): Promise<void>;
  deleteDb(db: SiteDatabaseRef): Promise<void>; openDatabaseTool(db: SiteDatabaseRef): Promise<void>;
};
async function flush() { for (let i = 0; i < 12; i++) await nextTick(); }
async function mount(role = "Administrator") {
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  const pinia = createPinia(); setActivePinia(pinia); useSitesStore().selectedSiteName = "a.test";
  useAuthStore().session = { isAuthenticated: true, requiresSetupWizard: false, user: { role } as CurrentUser };
  app = renderer.createApp({ ...SiteDatabasesView, render: () => h("section") } as Component).use(pinia); app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: State }).setupState; await flush(); return state;
}
async function selectSite(site: string) { useSitesStore().selectedSiteName = site; await flush(); }
function fill(state: State) { state.showForm = true; Object.assign(state.form, { name: "new_db", userName: "new_user", password: "FixturePassword123!" }); }

it("does not create with defaults after the database workspace failed to load", async () => {
  vi.mocked(getSiteDatabases).mockRejectedValueOnce(new Error("read failed")); const state = await mount(); fill(state); await state.createDb();
  expect(createDatabase).not.toHaveBeenCalled(); expect(state.loaded).toBe(false);
});
it("does not link a stale candidate after refresh failed", async () => {
  const state = await mount(); state.selectedCandidate = "mariadb:existing"; vi.mocked(getSiteDatabases).mockRejectedValueOnce(new Error("read failed"));
  await state.load(); await state.linkDb(); expect(linkSiteDatabase).not.toHaveBeenCalled();
});
it("does not send another deletion prompt while confirmation is pending", async () => {
  const state = await mount(), pending = deferred<boolean>(); confirm.mockReturnValueOnce(pending.promise);
  const deleting = state.deleteDb(state.databases[0]); await state.deleteDb(state.databases[0]); expect(confirm).toHaveBeenCalledOnce(); pending.resolve(false); await deleting;
});
it("does not reuse a deletion confirmation after leaving and returning to the same site", async () => {
  const state = await mount(); confirm.mockImplementationOnce(async (options: ConfirmOptions) => { await selectSite("b.test"); await selectSite("a.test"); await options.action?.(); return true; });
  await state.deleteDb(state.databases[0]); expect(deleteDatabase).not.toHaveBeenCalled();
});
it("deletes exactly the engine and database named in the accepted confirmation", async () => {
  const state = await mount(), target = state.databases[0]; confirm.mockImplementationOnce(async (options: ConfirmOptions) => {
    target.engine = "postgresql"; target.name = "different"; await options.action?.(); return true;
  });
  await state.deleteDb(target); expect(deleteDatabase).toHaveBeenCalledWith("mariadb", "a_test");
});
it("does not dispatch a queued deletion after authentication changes", async () => {
  const state = await mount(); confirm.mockImplementationOnce(async (options: ConfirmOptions) => { invalidateSessionRequests(); await options.action?.(); return true; });
  await state.deleteDb(state.databases[0]); expect(deleteDatabase).not.toHaveBeenCalled();
});
it("keeps a new form after an old creation completes on a later visit to the same site", async () => {
  const state = await mount(), pending = deferred<DatabaseWorkspace>(); fill(state); vi.mocked(createDatabase).mockReturnValueOnce(pending.promise);
  const creating = state.createDb(); await selectSite("b.test"); await selectSite("a.test"); fill(state); state.form.password = "NewDraftPassword";
  pending.resolve({} as DatabaseWorkspace); await creating; expect(state.form.password).toBe("NewDraftPassword"); expect(state.showForm).toBe(true);
});
it("preserves a reopened creation form after an earlier submission finishes", async () => {
  const state = await mount(), pending = deferred<DatabaseWorkspace>(); fill(state); vi.mocked(createDatabase).mockReturnValueOnce(pending.promise);
  const creating = state.createDb(); state.showForm = false; await flush(); state.showForm = true; state.form.password = "NewDraftPassword";
  pending.resolve({} as DatabaseWorkspace); await creating; expect(state.form.password).toBe("NewDraftPassword"); expect(state.showForm).toBe(true);
});
it("starts a new site's write independently and does not unlock it on an old completion", async () => {
  const state = await mount(), old = deferred<DatabaseWorkspace>(), current = deferred<DatabaseWorkspace>(); fill(state);
  vi.mocked(createDatabase).mockReturnValueOnce(old.promise).mockReturnValueOnce(current.promise); const first = state.createDb();
  await selectSite("b.test"); fill(state); const second = state.createDb(); expect(createDatabase).toHaveBeenCalledTimes(2);
  old.resolve({} as DatabaseWorkspace); await first; expect(state.creating).toBe(true); current.resolve({} as DatabaseWorkspace); await second; expect(state.creating).toBe(false);
});
it("does not publish an old list after authentication changes", async () => {
  const pending = deferred<SiteDatabases>(); vi.mocked(getSiteDatabases).mockReturnValueOnce(pending.promise); const state = await mount();
  invalidateSessionRequests(); pending.resolve(workspace()); await flush(); expect(state.databases).toEqual([]); expect(state.loaded).toBe(false);
});
it("does not navigate a reserved phpMyAdmin tab after its site scope changes", async () => {
  const state = await mount(), pending = deferred<{ url: string }>(); vi.mocked(openPhpMyAdmin).mockReturnValueOnce(pending.promise);
  const opening = state.openDatabaseTool(state.databases[0]); await selectSite("b.test"); pending.resolve({ url: "https://db.test/session" }); await opening;
  expect(tab.location.replace).not.toHaveBeenCalled(); expect(tab.close).toHaveBeenCalledOnce(); expect(state.notice).toBe("");
});
it("keeps link success visible after the refreshed database list arrives", async () => {
  const state = await mount(); state.selectedCandidate = "postgresql:other"; await state.linkDb();
  expect(linkSiteDatabase).toHaveBeenCalledWith("a.test", "postgresql", "other"); expect(state.notice).toBe("panel.siteDb.linked");
});
it("keeps unlink success visible after the refreshed database list arrives", async () => {
  const state = await mount(); await state.unlinkDb(state.databases[0]);
  expect(unlinkSiteDatabase).toHaveBeenCalledWith("a.test", "mariadb", "a_test"); expect(state.notice).toBe("panel.siteDb.unlinked");
});
it("preserves successful creation and clears the submitted password", async () => {
  const state = await mount(); fill(state); await state.createDb();
  expect(createDatabase).toHaveBeenCalledWith({ engine: "mariadb", name: "new_db", userName: "new_user", password: "FixturePassword123!", siteName: "a.test" });
  expect(state.form.password).toBe(""); expect(state.showForm).toBe(false); expect(state.notice).toBe("panel.siteDb.created");
});
it("preserves deletion confirmation and its final success message", async () => {
  const state = await mount(); await state.deleteDb(state.databases[0]);
  expect(confirm.mock.calls[0][0]).toMatchObject({ requireText: "a_test" }); expect(deleteDatabase).toHaveBeenCalledWith("mariadb", "a_test"); expect(state.notice).toBe("panel.siteDb.deleted");
});
it("keeps the existing synchronous tab reservation and normal navigation", async () => {
  const state = await mount(), pending = deferred<{ url: string }>(); vi.mocked(openPhpMyAdmin).mockReturnValueOnce(pending.promise);
  const opening = state.openDatabaseTool(state.databases[0]); expect(window.open).toHaveBeenCalledWith("", "_blank"); expect(tab.opener).toBeNull();
  pending.resolve({ url: "https://db.test/session" }); await opening; expect(tab.location.replace).toHaveBeenCalledWith("https://db.test/session"); expect(tab.close).not.toHaveBeenCalled();
});
it("opens PostgreSQL with Adminer instead of directing it to a MariaDB phpMyAdmin login", async () => {
  vi.mocked(getSiteDatabases).mockResolvedValueOnce({ linked: [{ engine: "postgresql", name: "pg_site" }], candidates: [] });
  vi.mocked(openAdminer).mockResolvedValueOnce({ url: "https://db.test/adminer" }); const state = await mount(); await state.openDatabaseTool(state.databases[0]);
  expect(openPhpMyAdmin).not.toHaveBeenCalled(); expect(openAdminer).toHaveBeenCalledWith("postgresql", "pg_site");
  expect(tab.location.replace).toHaveBeenCalledWith("https://db.test/adminer");
});
it("does not cover a post-write list failure with a success notice", async () => {
  const state = await mount(); state.selectedCandidate = "mariadb:existing"; vi.mocked(getSiteDatabases).mockRejectedValueOnce(new Error("refresh failed"));
  await state.linkDb(); expect(state.notice).toBe("common.unexpected_error"); expect(state.loaded).toBe(false); expect(state.linking).toBe(false);
});
it("rejects duplicate creation and overlapping writes while creation is pending", async () => {
  const state = await mount(), pending = deferred<DatabaseWorkspace>(); fill(state); state.selectedCandidate = "mariadb:existing";
  vi.mocked(createDatabase).mockReturnValueOnce(pending.promise); const creating = state.createDb(); await state.createDb(); await state.linkDb(); await state.deleteDb(state.databases[0]);
  expect(createDatabase).toHaveBeenCalledOnce(); expect(linkSiteDatabase).not.toHaveBeenCalled(); expect(confirm).not.toHaveBeenCalled();
  pending.resolve({} as DatabaseWorkspace); await creating;
});
it("does not refresh or report a stale link after leaving and returning to the same site", async () => {
  const state = await mount(), pending = deferred<void>(); state.selectedCandidate = "mariadb:existing"; vi.mocked(linkSiteDatabase).mockReturnValueOnce(pending.promise as never);
  const linking = state.linkDb(); await selectSite("b.test"); await selectSite("a.test"); state.selectedCandidate = "postgresql:other";
  const reads = vi.mocked(getSiteDatabases).mock.calls.length; pending.resolve(); await linking;
  expect(getSiteDatabases).toHaveBeenCalledTimes(reads); expect(state.selectedCandidate).toBe("postgresql:other"); expect(state.notice).toBe("");
});
it("keeps newer lists when earlier reads finish last and ignores completion after unmount", async () => {
  const state = await mount(), first = deferred<SiteDatabases>(), second = deferred<SiteDatabases>();
  vi.mocked(getSiteDatabases).mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise); const a = state.load(), b = state.load();
  second.resolve({ linked: [{ engine: "mariadb", name: "latest" }], candidates: [] }); await b; first.resolve(workspace()); await a;
  expect(state.databases[0]?.name).toBe("latest");
  const closed = deferred<SiteDatabases>(); vi.mocked(getSiteDatabases).mockReturnValueOnce(closed.promise); const reading = state.load();
  app!.unmount(); app = undefined; closed.resolve(workspace()); await reading; expect(state.loaded).toBe(false); expect(state.databases).toEqual([]);
});
it("keeps a reseller's unlink action but blocks admin-only creation and deletion", async () => {
  const state = await mount("Reseller"); const db = state.databases[0]; fill(state); await state.createDb(); await state.deleteDb(db); await state.unlinkDb(db);
  expect(createDatabase).not.toHaveBeenCalled(); expect(confirm).not.toHaveBeenCalled(); expect(unlinkSiteDatabase).toHaveBeenCalledWith("a.test", "mariadb", "a_test");
});
it.each([
  ["Reseller", "mariadb"], ["Customer", "mariadb"],
  ["Reseller", "postgresql"], ["Customer", "postgresql"]
])("does not open an administrator database tool for %s on %s", async (role, engine) => {
  vi.mocked(getSiteDatabases).mockResolvedValueOnce({ linked: [{ engine, name: "site_db" }], candidates: [] });
  const state = await mount(role);
  await state.openDatabaseTool(state.databases[0]);
  expect(window.open).not.toHaveBeenCalled();
  expect(openPhpMyAdmin).not.toHaveBeenCalled();
  expect(openAdminer).not.toHaveBeenCalled();
  expect(state.opening).toBe(false);
});
it("closes a reserved database tab if the screen closes during URL generation", async () => {
  const state = await mount(), pending = deferred<{ url: string }>(); vi.mocked(openPhpMyAdmin).mockReturnValueOnce(pending.promise);
  const opening = state.openDatabaseTool(state.databases[0]); app!.unmount(); app = undefined; pending.resolve({ url: "https://db.test/session" }); await opening;
  expect(tab.close).toHaveBeenCalledOnce(); expect(tab.location.replace).not.toHaveBeenCalled();
});
it("releases deletion state when the confirmation is cancelled", async () => {
  const state = await mount(); confirm.mockResolvedValueOnce(false); await state.deleteDb(state.databases[0]);
  expect(deleteDatabase).not.toHaveBeenCalled(); expect(state.deletingKey).toBe(""); await state.load(); expect(state.loaded).toBe(true);
});
