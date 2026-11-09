import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { createRenderer, createSSRApp, h, nextTick, ssrContextKey, type App, type Component } from "vue";
import { renderToString } from "vue/server-renderer";
import DatabasesView from "./DatabasesView.vue";
import { useDatabasesStore } from "../stores/databases";
import { useSoftwareStore, type SoftwareCatalog } from "../stores/software";
import { useSitesStore } from "../stores/sites";
import { useAuthStore } from "../stores/auth";
import * as api from "../api/databases";

const shared = vi.hoisted(() => ({ epoch: 0 }));
vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key }) }));
vi.mock("../api/client", async original => ({ ...await original<object>(), getSessionEpoch: () => shared.epoch }));
vi.mock("../components/ConfirmDialog.vue", () => ({ useConfirm: () => ({ confirm: vi.fn() }) }));
vi.mock("../components/PromptDialog.vue", () => ({ usePrompt: () => ({ prompt: vi.fn() }) }));
vi.mock("../api/databases", async original => ({ ...await original<object>(), getDatabaseDetail: vi.fn(), moveDatabase: vi.fn(), getUserRemoteAccess: vi.fn() }));

function deferred<T>() { let resolve!: (value: T) => void, reject!: (error: unknown) => void;
  const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; }
type Request = ReturnType<typeof deferred<api.DatabaseDetail>> & { engine: string; name: string };
type State = {
  activeModule: string; activeSection: string; databaseSearch: string; detail: api.DatabaseDetail | null; moveTarget: string;
  databasePager: { page: { value: number } }; databaseSize(name: string): string; databaseSite(name: string): string;
  toggleDetail(name: string): Promise<void>; selectEngine(engine: string): void; refreshWorkspace(): Promise<void>;
  doMove(name: string): Promise<void>; attachUser(name: string): Promise<void>; attachUserName: string;
};
let app: App | undefined, requests: Request[];
const detail = (request: Pick<Request, "engine" | "name">, siteName = "site.test", sizeBytes: number | null = 91 * 1024 ** 2): api.DatabaseDetail =>
  ({ ...request, siteName, sizeBytes, tableCount: 38, engineName: request.engine, host: "localhost", port: 3306, users: [] });
const workspace = (engine: string, count = 3): api.DatabaseWorkspace => ({ engine, displayName: engine, host: "localhost", port: 3306,
  componentId: "", serviceName: "", adminUserName: "root", adminPassword: "", supportsHostScopedUsers: true,
  databases: Array.from({ length: count }, (_, n) => ({ engine, name: `db${n}`, owner: "owner", users: [], encoding: "utf8mb4", collation: "utf8mb4_unicode_ci" })),
  users: [{ engine, userName: "db_user", host: "localhost", canLogin: true }] });
async function flush() { for (let n = 0; n < 40; n++) await nextTick(); }
async function mount(count = 3, role = "Administrator", engine = "mariadb") {
  const pinia = createPinia(); setActivePinia(pinia);
  const store = useDatabasesStore(), software = useSoftwareStore(), auth = useAuthStore();
  auth.session = { isAuthenticated: true, requiresSetupWizard: false, user: { id: "actor", userName: "actor", email: "actor@example.test", role,
    preferredLanguage: "en", isActive: true, isTwoFactorEnabled: false, lastLoginAtUtc: null } };
  store.selectedEngine = engine;
  store.engines = ["mariadb", "mysql", "postgresql"].map(key => ({ engine: key, installed: true, ready: true, supportsHardening: true, port: 3306 })) as api.DatabaseEngineStatus[];
  const components = engine === "postgresql" ? ["postgresql-16"] : ["mariadb-11", "mysql-8"];
  software.catalog = { installed: components.map(componentId => ({ componentId, status: "Installed" })), defaults: [], manifest: [] } as unknown as SoftwareCatalog;
  software.loaded = true;
  vi.spyOn(store, "fetchEngines").mockImplementation(async () => store.engines);
  vi.spyOn(store, "fetchWorkspace").mockImplementation(async key => store.workspaces[key] = workspace(key, count));
  vi.spyOn(store, "grantAccess").mockResolvedValue(workspace(engine, count));
  vi.spyOn(software, "fetchCatalog").mockImplementation(async () => software.catalog!);
  vi.spyOn(useSitesStore(), "fetchSites").mockResolvedValue([]);
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  app = renderer.createApp({ ...DatabasesView, render: () => h("section") } as Component).use(pinia); app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: State }).setupState;
  await flush(); return { state, store, auth };
}
const markup = (state: State) => renderToString(createSSRApp({ ...DatabasesView, setup: () => state } as Component));
beforeEach(() => {
  shared.epoch = 0; requests = [];
  vi.mocked(api.getDatabaseDetail).mockImplementation((engine, name) => { const request = { engine, name, ...deferred<api.DatabaseDetail>() }; requests.push(request); return request.promise; });
  vi.mocked(api.moveDatabase).mockResolvedValue({} as never);
});
afterEach(async () => {
  app?.unmount(); app = undefined;
  for (const request of requests) request.resolve(detail(request));
  await flush(); vi.restoreAllMocks(); vi.resetAllMocks();
});

it("loads only two visible measurements, then shares in-flight and cached data with the detail card", async () => {
  const { state } = await mount(); expect(requests.map(r => r.name)).toEqual(["db0", "db1"]);
  expect(state.databaseSize("db0")).toBe("panel.common.loading");
  const opening = state.toggleDetail("db0"); await flush(); expect(requests).toHaveLength(2);
  requests[0]!.resolve(detail(requests[0]!, "linked.test")); await opening; await flush();
  expect(state.databaseSize("db0")).toContain("91"); expect(state.databaseSite("db0")).toBe("linked.test");
  await state.toggleDetail("db0"); await state.toggleDetail("db0"); expect(requests).toHaveLength(3);
  expect(api.getUserRemoteAccess).not.toHaveBeenCalled();
});

it("still publishes the successful row measurement after opening and closing its shared pending detail", async () => {
  const { state } = await mount(1);
  const opening = state.toggleDetail("db0"); await state.toggleDetail("db0");
  requests[0]!.resolve(detail(requests[0]!, "still-visible.test")); await opening; await flush();
  expect(state.detail).toBeNull(); expect(state.databaseSite("db0")).toBe("still-visible.test");
  expect(state.databaseSize("db0")).toContain("91"); expect(requests).toHaveLength(1);
});

it("shows Size and Linked site columns and keeps encoding/collation only in the detail card", async () => {
  const { state } = await mount(1);
  const html = await markup(state), header = html.match(/class="data-row table-head database-row"[^>]*>([\s\S]*?)<\/div>/)?.[1] ?? "";
  expect(header).toContain("panel.database.size"); expect(header).toContain("panel.database.linkedSite");
  expect(header).not.toContain("panel.database.encoding"); expect(header).not.toContain("panel.database.collation");
  expect(html).not.toContain("panel.database.tableCount");
  requests[0]!.resolve(detail(requests[0]!)); await flush();
  await state.toggleDetail("db0"); const card = await markup(state);
  expect(card).toContain("panel.database.encoding"); expect(card).toContain("utf8mb4_unicode_ci");
});

it("keeps a known PostgreSQL site independently of its unsupported size measurement", async () => {
  const { state } = await mount(1, "Administrator", "postgresql");
  requests[0]!.resolve(detail(requests[0]!, "pg-site.test", null)); await flush();
  expect(state.databaseSize("db0")).toBe("panel.database.sizeUnknown"); expect(state.databaseSite("db0")).toBe("pg-site.test");
});

it("keeps a measured size when the server could not read the site mapping", async () => {
  const { state } = await mount(1);
  requests[0]!.resolve({ ...detail(requests[0]!), siteName: null }); await flush();
  expect(state.databaseSize("db0")).toContain("91");
  expect(state.databaseSite("db0")).toBe("panel.database.sizeUnknown");
});

it("distinguishes measured zero and an explicitly unlinked site from missing data or failures", async () => {
  const { state } = await mount(3);
  requests[0]!.resolve(detail(requests[0]!, "", 0)); requests[1]!.reject(new Error("offline")); await flush();
  expect(state.databaseSize("db0")).toMatch(/^0 /); expect(state.databaseSite("db0")).toBe("panel.database.linkedSiteNone");
  expect(state.databaseSize("db1")).toBe("panel.database.sizeUnknown"); expect(state.databaseSite("db1")).toBe("panel.database.sizeUnknown");
  requests[2]!.resolve({ ...detail(requests[2]!), siteName: undefined, sizeBytes: -1 } as unknown as api.DatabaseDetail); await flush();
  expect(state.databaseSize("db2")).toBe("panel.database.sizeUnknown"); expect(state.databaseSite("db2")).toBe("panel.database.sizeUnknown");
});

it("does not start queued rows from an obsolete page or search", async () => {
  const { state } = await mount(30); state.databasePager.page.value = 2; await flush();
  expect(requests).toHaveLength(2);
  requests[0]!.resolve(detail(requests[0]!)); requests[1]!.resolve(detail(requests[1]!)); await flush();
  expect(requests.slice(2).map(r => r.name)).toEqual(["db25", "db26"]);
  state.databaseSearch = "db29"; await flush();
  requests[2]!.resolve(detail(requests[2]!)); requests[3]!.resolve(detail(requests[3]!)); await flush();
  expect(requests.slice(4).map(r => r.name)).toEqual(["db29"]);
});

it.each(["users", "remote", "module", "unmount", "epoch"])("does not continue hidden list reads after %s navigation", async target => {
  const { state } = await mount(5);
  if (target === "module") state.activeModule = "cache";
  else if (target === "unmount") { app!.unmount(); app = undefined; }
  else if (target === "epoch") shared.epoch++;
  else state.activeSection = target;
  await flush(); requests[0]!.resolve(detail(requests[0]!)); requests[1]!.resolve(detail(requests[1]!)); await flush();
  expect(requests).toHaveLength(2); expect(api.getUserRemoteAccess).not.toHaveBeenCalled();
});

it("keeps old engine reads inside the physical cap and never publishes their same-name site", async () => {
  const { state } = await mount(2); state.selectEngine("mysql"); await flush(); expect(requests).toHaveLength(2);
  requests[0]!.resolve(detail(requests[0]!, "old.test")); requests[1]!.resolve(detail(requests[1]!, "old.test")); await flush();
  expect(requests.slice(2).map(r => r.engine)).toEqual(["mysql", "mysql"]);
  expect(state.databaseSite("db0")).toBe("panel.common.loading");
  requests[2]!.resolve(detail(requests[2]!, "new.test")); await flush(); expect(state.databaseSite("db0")).toBe("new.test");
});

it("explicit refresh invalidates cache and in-flight results without exceeding two physical reads", async () => {
  const { state } = await mount(2); await state.refreshWorkspace(); await flush(); expect(requests).toHaveLength(2);
  requests[0]!.resolve(detail(requests[0]!, "old.test")); requests[1]!.resolve(detail(requests[1]!, "old.test")); await flush();
  expect(requests).toHaveLength(4); expect(state.databaseSite("db0")).toBe("panel.common.loading");
  requests[2]!.resolve(detail(requests[2]!, "fresh.test")); await flush(); expect(state.databaseSite("db0")).toBe("fresh.test");
});

it("shows unmeasured rather than endless loading when a refresh cannot reload the workspace", async () => {
  const { state, store } = await mount(1); requests[0]!.resolve(detail(requests[0]!)); await flush();
  vi.mocked(store.fetchWorkspace).mockRejectedValueOnce(new Error("workspace offline"));
  await state.refreshWorkspace(); await flush();
  expect(state.databaseSize("db0")).toBe("panel.database.sizeUnknown");
  expect(state.databaseSite("db0")).toBe("panel.database.sizeUnknown");
  expect(requests).toHaveLength(1);
});

it("refreshes both the shared row and detail after moving a database to another site", async () => {
  const { state } = await mount(1); requests[0]!.resolve(detail(requests[0]!, "old.test")); await flush();
  await state.toggleDetail("db0"); state.moveTarget = "new.test";
  const moving = state.doMove("db0"); await flush(); expect(requests).toHaveLength(2);
  requests[1]!.resolve(detail(requests[1]!, "new.test")); await moving; await flush();
  expect(state.detail?.siteName).toBe("new.test"); expect(state.databaseSite("db0")).toBe("new.test");
  expect(requests).toHaveLength(2);
});

it("does not reuse pre-grant users from the detail cache after an access mutation", async () => {
  const { state } = await mount(1); requests[0]!.resolve(detail(requests[0]!)); await flush(); await state.toggleDetail("db0");
  state.attachUserName = "db_user|localhost"; const writing = state.attachUser("db0"); await flush();
  expect(requests).toHaveLength(2);
  requests[1]!.resolve({ ...detail(requests[1]!), users: [{ userName: "db_user", host: "localhost" }] }); await writing;
  expect(state.detail?.users).toEqual([{ userName: "db_user", host: "localhost" }]);
});

it("does not query privileged summary endpoints for a non-administrator", async () => {
  await mount(3, "Reseller"); expect(api.getDatabaseDetail).not.toHaveBeenCalled(); expect(api.getUserRemoteAccess).not.toHaveBeenCalled();
});
