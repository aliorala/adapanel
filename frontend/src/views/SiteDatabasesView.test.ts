import { afterEach, expect, it, vi } from "vitest";
import { createRenderer, h, nextTick, reactive, ssrContextKey, type App, type Component } from "vue";
import SiteDatabasesView from "./SiteDatabasesView.vue";
import { getSiteDatabases, type SiteDatabases } from "../api/sites";
import { useSitesStore } from "../stores/sites";
import { createDatabase } from "../api/databases";

vi.mock("../api/sites", () => ({ getSiteDatabases: vi.fn(), linkSiteDatabase: vi.fn(), unlinkSiteDatabase: vi.fn() }));
vi.mock("../api/databases", () => ({ createDatabase: vi.fn(), deleteDatabase: vi.fn(), openPhpMyAdmin: vi.fn() }));
vi.mock("../stores/sites", () => ({ useSitesStore: vi.fn() }));
vi.mock("../stores/auth", () => ({ useAuthStore: () => ({ session: { user: { role: "Administrator" } } }), extractApiMessage: () => "failed" }));
vi.mock("../components/ConfirmDialog.vue", () => ({ useConfirm: () => ({ confirm: vi.fn() }) }));
vi.mock("../composables/useSubscriptionGate", () => ({ useSubscriptionGate: () => ({ writeGate: () => ({}) }) }));
vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key }) }));

let app: App | undefined;
type State = { databases: SiteDatabases["linked"]; loading: boolean; notice: string; form: { name: string; password: string }; load: () => Promise<void>; createDb: () => Promise<void> };
function mount() {
  const store = reactive({ selectedSiteName: "first.example" });
  vi.mocked(useSitesStore).mockReturnValue(store as ReturnType<typeof useSitesStore>);
  const renderer = createRenderer<object, object>({
    createElement: () => ({}), createText: () => ({}), createComment: () => ({}),
    insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {},
    parentNode: () => null, nextSibling: () => null
  });
  app = renderer.createApp({ ...SiteDatabasesView, render: () => h("section") } as Component);
  app.provide(ssrContextKey, {});
  const vm = app.mount({});
  return { store, state: (vm.$ as unknown as { setupState: State }).setupState };
}
afterEach(() => { app?.unmount(); app = undefined; vi.resetAllMocks(); });
const data = (name: string): SiteDatabases => ({ linked: [{ engine: "mariadb", name }], candidates: [] });

it("reloads the selected site's databases and ignores the previous site's late response", async () => {
  let oldResponse!: (value: SiteDatabases) => void;
  vi.mocked(getSiteDatabases).mockImplementationOnce(() => new Promise(resolve => { oldResponse = resolve; }));
  vi.mocked(getSiteDatabases).mockResolvedValueOnce(data("second_db"));
  const { store, state } = mount();
  state.form.password = "draft-for-first-site";
  store.selectedSiteName = "second.example";
  await nextTick(); await nextTick();
  expect(getSiteDatabases).toHaveBeenLastCalledWith("second.example");
  expect(state.form.password).toBe("");
  oldResponse(data("first_db")); await nextTick(); await nextTick();
  expect(state.databases.map(db => db.name)).toEqual(["second_db"]);
  expect(state.loading).toBe(false);
});

it("clears old data when the selected site disappears", async () => {
  vi.mocked(getSiteDatabases).mockResolvedValue(data("first_db"));
  const { store, state } = mount();
  await nextTick(); await nextTick();
  store.selectedSiteName = "";
  await nextTick();
  expect(state.databases).toEqual([]);
  expect(state.loading).toBe(false);
});

it("keeps the new site's draft when creation for the previous site completes", async () => {
  vi.mocked(getSiteDatabases).mockResolvedValue(data("db"));
  let finish!: () => void;
  vi.mocked(createDatabase).mockImplementationOnce(() => new Promise(resolve => { finish = () => resolve(undefined as never); }));
  const { store, state } = mount();
  await nextTick();
  const operation = state.createDb();
  await state.createDb();
  expect(createDatabase).toHaveBeenCalledTimes(1);
  store.selectedSiteName = "second.example";
  await nextTick();
  state.form.password = "new-draft";
  finish(); await operation;
  expect(state.form.password).toBe("new-draft");
  expect(state.notice).toBe("");
});
