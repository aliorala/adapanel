import { afterEach, expect, it, vi } from "vitest";
import { createRenderer, h, ssrContextKey, type App, type Component } from "vue";
import DbTransferView from "./DbTransferView.vue";
import { transferDatabase, type DatabaseTransferResult } from "../api/databases";
vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key }) }));
vi.mock("../api/databases", async original => ({ ...await original<object>(), transferDatabase: vi.fn() }));
let app: App | undefined;
afterEach(() => { app?.unmount(); app = undefined; vi.clearAllMocks(); });
type State = { form: { sourceHost: string; sourceDatabase: string; sourcePassword: string; targetPassword: string; sourcePort: number; adaptDefiners: boolean }; created: { userName: string; password: string }; run(): Promise<void> };
function mount() {
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  app = renderer.createApp({ ...DbTransferView, render: () => h("section") } as Component); app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: State }).setupState;
  Object.assign(state.form, { sourceHost: "db.example.test", sourceDatabase: "fixture", sourcePassword: "source-secret", targetPassword: "target-secret" }); return state;
}
const result = { targetDatabase: "fixture", userName: "fixture-user", password: "created-secret" } as DatabaseTransferResult;
it.each([false, true])("preserves owners by default and forwards explicit adaptation: %s", async adapt => {
  const state = mount(); expect(state.form.adaptDefiners).toBe(false);
  state.form.adaptDefiners = adapt; vi.mocked(transferDatabase).mockResolvedValue(result); await state.run();
  expect(transferDatabase).toHaveBeenCalledWith(expect.objectContaining({ adaptDefiners: adapt }));
});
function pendingTransfer() { let resolve!: (value: DatabaseTransferResult) => void; const promise = new Promise<DatabaseTransferResult>(yes => { resolve = yes; }); vi.mocked(transferDatabase).mockReturnValue(promise); return { resolve }; }
it("does not start another database transfer while one is running", async () => {
  const state = mount(), pending = pendingTransfer(); const first = state.run(), second = state.run(); pending.resolve(result); await Promise.all([first, second]);
  expect(transferDatabase).toHaveBeenCalledTimes(1);
});
it("clears credentials on unmount and ignores credentials from a late completion", async () => {
  const state = mount(), pending = pendingTransfer(); const running = state.run(); app!.unmount(); app = undefined; pending.resolve(result); await running;
  expect(state.form.sourcePassword).toBe(""); expect(state.form.targetPassword).toBe(""); expect(state.created.password).toBe("");
});
it.each([0, 65536, 1.5, NaN])("does not dispatch an invalid source port %s", async port => {
  const state = mount(); state.form.sourcePort = port; vi.mocked(transferDatabase).mockResolvedValue(result);
  await state.run(); expect(transferDatabase).not.toHaveBeenCalled();
});
it("still exposes newly created credentials after a successful transfer on the active screen", async () => {
  const state = mount(); vi.mocked(transferDatabase).mockResolvedValue(result); await state.run();
  expect(state.created.userName).toBe("fixture-user"); expect(state.created.password).toBe("created-secret");
});
