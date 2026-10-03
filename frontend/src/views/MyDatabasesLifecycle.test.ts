import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { createRenderer, h, nextTick, ssrContextKey, type App, type Component } from "vue";
import MyDatabasesView from "./MyDatabasesView.vue";
import { getMyDatabases, createMyDatabase, deleteMyDatabase, type OwnedDatabase } from "../api/myDatabases";
import { useSubscriptionStore } from "../stores/subscription";
const shared = vi.hoisted(() => ({ epoch: 0, confirm: vi.fn() }));
vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key }) }));
vi.mock("../components/ConfirmDialog.vue", () => ({ useConfirm: () => ({ confirm: shared.confirm }) }));
vi.mock("../api/client", async original => ({ ...await original<object>(), getSessionEpoch: () => shared.epoch }));
vi.mock("../api/myDatabases", () => ({ getMyDatabases: vi.fn(), createMyDatabase: vi.fn(), deleteMyDatabase: vi.fn() }));
type State = { databases: OwnedDatabase[]; listLoaded: boolean; loading: boolean; busy: boolean; notice: string; form: { engine: string; name: string; userName: string; password: string }; load(): Promise<unknown>; create(): Promise<void>; remove(db: OwnedDatabase): Promise<void> };
let app: App | undefined;
const existing = (): OwnedDatabase => ({ engine: "mariadb", name: "tenant_existing" });
const draft = { engine: "mariadb", name: "next", userName: "reader", password: "FixturePassword!" };
function deferred<T>() { let resolve!: (value: T) => void; let reject!: (error: unknown) => void; const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; }
async function flush() { for (let i = 0; i < 12; i++) await nextTick(); }
beforeEach(() => {
  shared.epoch = 0; shared.confirm.mockImplementation(async options => { await options.action(); return true; });
  vi.mocked(getMyDatabases).mockResolvedValue([existing()]); vi.mocked(createMyDatabase).mockResolvedValue([existing(), { engine: "mariadb", name: "next" }]); vi.mocked(deleteMyDatabase).mockResolvedValue([]);
});
afterEach(() => { app?.unmount(); app = undefined; vi.restoreAllMocks(); vi.resetAllMocks(); });
async function mount() {
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  const pinia = createPinia(); setActivePinia(pinia);
  app = renderer.createApp({ ...MyDatabasesView, render: () => h("section") } as Component).use(pinia); app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: State }).setupState; await flush(); Object.assign(state.form, draft); return state;
}
it("does not submit twice or delete while creation is pending", async () => {
  const state = await mount(), pending = deferred<OwnedDatabase[]>(); vi.mocked(createMyDatabase).mockReturnValueOnce(pending.promise);
  const saving = state.create(); await state.create(); await state.remove(existing());
  expect(createMyDatabase).toHaveBeenCalledOnce(); expect(shared.confirm).not.toHaveBeenCalled(); pending.resolve([existing()]); await saving;
});
it("does not create or delete before the ownership list is measured", async () => {
  const pending = deferred<OwnedDatabase[]>(); vi.mocked(getMyDatabases).mockReturnValueOnce(pending.promise); const state = await mount();
  await state.create(); await state.remove(existing()); expect(createMyDatabase).not.toHaveBeenCalled(); expect(shared.confirm).not.toHaveBeenCalled(); pending.resolve([existing()]); await flush();
});
it("does not mutate after a failed list refresh", async () => {
  const state = await mount(); vi.mocked(getMyDatabases).mockRejectedValueOnce(new Error("unreadable")); await state.load(); await state.create(); await state.remove(existing());
  expect(state.listLoaded).toBe(false); expect(createMyDatabase).not.toHaveBeenCalled(); expect(shared.confirm).not.toHaveBeenCalled();
});
it("keeps the latest list when an older read resolves last", async () => {
  const state = await mount(), pending = deferred<OwnedDatabase[]>(); vi.mocked(getMyDatabases).mockReturnValueOnce(pending.promise); const old = state.load();
  vi.mocked(getMyDatabases).mockResolvedValueOnce([{ engine: "mysql", name: "newest" }]); await state.load(); pending.resolve([]); await old;
  expect(state.databases).toEqual([{ engine: "mysql", name: "newest" }]);
});
it("does not let a superseded read failure erase the current list", async () => {
  const state = await mount(), pending = deferred<OwnedDatabase[]>(); vi.mocked(getMyDatabases).mockReturnValueOnce(pending.promise); const old = state.load(); await state.load(); pending.reject(new Error("old failure")); await old;
  expect(state.listLoaded).toBe(true); expect(state.notice).toBe("");
});
it("does not refresh across an in-flight mutation", async () => {
  const state = await mount(), pending = deferred<OwnedDatabase[]>(); vi.mocked(createMyDatabase).mockReturnValueOnce(pending.promise); const saving = state.create(); await state.load();
  expect(getMyDatabases).toHaveBeenCalledOnce(); pending.resolve([existing()]); await saving;
});
it("preserves the next draft when an earlier creation completes", async () => {
  const state = await mount(), pending = deferred<OwnedDatabase[]>(); vi.mocked(createMyDatabase).mockReturnValueOnce(pending.promise); const saving = state.create();
  Object.assign(state.form, { name: "later", userName: "writer", password: "NewPassword!" }); pending.resolve([existing()]); await saving;
  expect(state.form).toMatchObject({ name: "later", userName: "writer", password: "NewPassword!" }); expect(createMyDatabase).toHaveBeenCalledExactlyOnceWith(draft);
});
it("clears the submitted draft on normal success", async () => {
  const state = await mount(); await state.create(); expect(state.form).toMatchObject({ name: "", userName: "", password: "" }); expect(state.databases).toHaveLength(2); expect(state.notice).toBe("panel.myDb.created");
});
it("does not erase edits changed away and back while a creation is pending", async () => {
  const state = await mount(), pending = deferred<OwnedDatabase[]>(); vi.mocked(createMyDatabase).mockReturnValueOnce(pending.promise); const saving = state.create(); state.form.name = "another"; state.form.name = draft.name;
  pending.resolve([existing()]); await saving; expect(state.form.password).toBe(draft.password); expect(state.form.name).toBe(draft.name);
});
it("clears the secret when choosing a different engine", async () => {
  const state = await mount(); state.form.engine = "mysql"; await flush(); expect(state.form.password).toBe("");
});
for (const invalid of [{ name: "" }, { userName: " " }, { password: "" }, { engine: "postgresql" }]) {
  it(`rejects an invalid creation ${JSON.stringify(invalid)}`, async () => { const state = await mount(); Object.assign(state.form, invalid); await state.create(); expect(createMyDatabase).not.toHaveBeenCalled(); });
}
it("blocks creation and deletion in a readonly subscription", async () => {
  const state = await mount(); useSubscriptionStore().usage = { isSuspended: true } as never; await state.create(); await state.remove(existing()); expect(createMyDatabase).not.toHaveBeenCalled(); expect(shared.confirm).not.toHaveBeenCalled();
});
it("does not open another dialog or create while deletion confirmation is pending", async () => {
  const state = await mount(), pending = deferred<boolean>(); shared.confirm.mockReturnValueOnce(pending.promise); const deleting = state.remove(existing());
  await state.remove(existing()); await state.create(); await state.load(); expect(shared.confirm).toHaveBeenCalledOnce(); expect(createMyDatabase).not.toHaveBeenCalled(); expect(getMyDatabases).toHaveBeenCalledOnce(); pending.resolve(false); await deleting;
});
it("deletes the immutable confirmed target even if its source object changes", async () => {
  const state = await mount(), db = state.databases[0]; shared.confirm.mockImplementationOnce(async options => { db.engine = "mysql"; db.name = "different"; await options.action(); return true; });
  await state.remove(db); expect(deleteMyDatabase).toHaveBeenCalledExactlyOnceWith("mariadb", "tenant_existing");
});
it("never offers deletion for an unmeasured database", async () => {
  const state = await mount(); await state.remove({ engine: "mysql", name: "unowned" }); expect(shared.confirm).not.toHaveBeenCalled();
});
for (const change of ["unmount", "session", "readonly"] as const) {
  it(`invalidates pending deletion on ${change}`, async () => {
    const state = await mount(); shared.confirm.mockImplementationOnce(async options => {
      if (change === "unmount") { app!.unmount(); app = undefined; } else if (change === "session") shared.epoch++; else useSubscriptionStore().usage = { isSuspended: true } as never;
      await options.action(); return true;
    }); await state.remove(existing()); expect(deleteMyDatabase).not.toHaveBeenCalled();
  });
}
it("ignores a creation response belonging to a previous session", async () => {
  const state = await mount(), pending = deferred<OwnedDatabase[]>(); vi.mocked(createMyDatabase).mockReturnValueOnce(pending.promise); const saving = state.create(); shared.epoch++; pending.resolve([]); await saving;
  expect(state.databases).toEqual([existing()]); expect(state.notice).toBe("");
});
it("ignores list results after the screen closes", async () => {
  const state = await mount(), pending = deferred<OwnedDatabase[]>(); vi.mocked(getMyDatabases).mockReturnValueOnce(pending.promise); const loading = state.load(); app!.unmount(); app = undefined; pending.resolve([]); await loading;
  expect(state.databases).toEqual([existing()]); expect(state.form.password).toBe("");
});
it("does not start a new operation in an obsolete session", async () => {
  const state = await mount(); shared.epoch++; await state.create(); await state.remove(existing()); await state.load(); expect(createMyDatabase).not.toHaveBeenCalled(); expect(shared.confirm).not.toHaveBeenCalled(); expect(getMyDatabases).toHaveBeenCalledOnce();
});
it("releases the deletion lock when confirmation is cancelled", async () => {
  const state = await mount(); shared.confirm.mockResolvedValueOnce(false); await state.remove(existing()); await state.create(); expect(createMyDatabase).toHaveBeenCalledOnce();
});
it("deletes normally and reports the returned ownership list", async () => {
  const state = await mount(); await state.remove(existing()); expect(deleteMyDatabase).toHaveBeenCalledExactlyOnceWith("mariadb", "tenant_existing"); expect(state.databases).toEqual([]); expect(state.notice).toBe("panel.myDb.deleted");
});
it("does not claim creation or discard the draft when the returned list lacks the new database", async () => {
  const state = await mount(); vi.mocked(createMyDatabase).mockResolvedValueOnce([existing()]); await state.create();
  expect(state.notice).toBe("panel.myDb.resultUnconfirmed"); expect(state.form).toEqual(draft);
});
it("does not claim deletion when the returned list still contains the database", async () => {
  const state = await mount(); vi.mocked(deleteMyDatabase).mockResolvedValueOnce([existing()]); await state.remove(existing());
  expect(state.notice).toBe("panel.myDb.resultUnconfirmed"); expect(state.databases).toEqual([existing()]);
});
it("recognizes names normalized to lowercase by the ownership store", async () => {
  const state = await mount(); state.form.name = "  Next  "; await state.create();
  expect(createMyDatabase).toHaveBeenCalledExactlyOnceWith({ ...draft, name: "Next" }); expect(state.notice).toBe("panel.myDb.created"); expect(state.form.password).toBe("");
});

for (const engine of ["postgresql", "postgres"]) {
  it(`confirms ${engine} deletion while the differently cased sibling remains`, async () => {
    const target = { engine, name: "Foo" }, sibling = { engine: "postgresql", name: "foo" };
    vi.mocked(getMyDatabases).mockResolvedValueOnce([target, sibling]);
    const state = await mount(); vi.mocked(deleteMyDatabase).mockResolvedValueOnce([sibling]);
    await state.remove(target);
    expect(state.notice).toBe("panel.myDb.deleted"); expect(state.databases).toEqual([sibling]);
  });

  it(`does not confirm ${engine} deletion when its canonical identity remains`, async () => {
    const target = { engine, name: "Foo" }, remaining = { engine: "postgresql", name: "Foo" };
    vi.mocked(getMyDatabases).mockResolvedValueOnce([target]);
    const state = await mount(); vi.mocked(deleteMyDatabase).mockResolvedValueOnce([remaining]);
    await state.remove(target);
    expect(state.notice).toBe("panel.myDb.resultUnconfirmed");
  });
}

it("keeps Windows database case folding when confirming deletion", async () => {
  const target = { engine: "mysql", name: "Foo" };
  vi.mocked(getMyDatabases).mockResolvedValueOnce([target]);
  const state = await mount(); vi.mocked(deleteMyDatabase).mockResolvedValueOnce([{ engine: "mysql", name: "foo" }]);
  await state.remove(target); expect(state.notice).toBe("panel.myDb.resultUnconfirmed");
});
