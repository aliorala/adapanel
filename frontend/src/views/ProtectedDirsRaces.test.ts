import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { createRenderer, h, nextTick, ssrContextKey, type App, type Component } from "vue";
import ProtectedDirsView from "./ProtectedDirsView.vue";
import { getProtectedDirs, protectDir, unprotectDir, type ProtectedDirectory } from "../api/sites";
import { useSitesStore } from "../stores/sites";
const confirm = vi.hoisted(() => vi.fn());
vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key }) }));
vi.mock("../components/ConfirmDialog.vue", () => ({ useConfirm: () => ({ confirm }) }));
vi.mock("../api/sites", async original => ({ ...await original<object>(), getProtectedDirs: vi.fn(), protectDir: vi.fn(), unprotectDir: vi.fn() }));
const item = { relativePath: "private", userName: "owner", isActive: true, isLegacyPlaintext: false } as ProtectedDirectory;
let app: App | undefined;
beforeEach(() => { vi.mocked(getProtectedDirs).mockResolvedValue([item]); confirm.mockImplementation(async options => { await options.action?.(); return true; }); });
afterEach(() => { app?.unmount(); app = undefined; vi.clearAllMocks(); });
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(yes => { resolve = yes; }); return { promise, resolve }; }
type State = { items: ProtectedDirectory[]; loading: boolean; form: { relativePath: string; userName: string; password: string }; notice: string; create(): Promise<void>; remove(item: ProtectedDirectory): Promise<void> };
async function flush() { for (let i = 0; i < 8; i++) await nextTick(); }
async function mount() {
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  const pinia = createPinia(); setActivePinia(pinia); useSitesStore().selectedSiteName = "a.test";
  app = renderer.createApp({ ...ProtectedDirsView, render: () => h("section") } as Component).use(pinia); app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: State }).setupState; await flush(); return state;
}
async function selectSite(name: string) { useSitesStore().selectedSiteName = name; await flush(); }
it("does not remove protection from a different site after confirmation", async () => {
  const state = await mount(); confirm.mockImplementationOnce(async options => { await selectSite("b.test"); await options.action(); return true; });
  await state.remove(item); expect(unprotectDir).not.toHaveBeenCalled();
});
it("clears the previous list and ignores its late result while the next site loads", async () => {
  const first = deferred<ProtectedDirectory[]>(), second = deferred<ProtectedDirectory[]>();
  vi.mocked(getProtectedDirs).mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise);
  const state = await mount(); await selectSite("b.test"); first.resolve([item]); await flush();
  expect(state.items).toEqual([]); expect(state.loading).toBe(true); second.resolve([]); await flush();
});
it("does not carry another site's unsaved password into the selected site", async () => {
  const state = await mount(); state.form.password = "fixture-secret"; state.form.relativePath = "private";
  await selectSite("b.test"); expect(state.form.password).toBe(""); expect(state.form.relativePath).toBe("");
});
it("does not clear the new site's form when an old creation finishes", async () => {
  const state = await mount(), pending = deferred<ProtectedDirectory>(); vi.mocked(protectDir).mockReturnValueOnce(pending.promise);
  Object.assign(state.form, { relativePath: "private", userName: "owner", password: "fixture-secret" });
  const creating = state.create(); await selectSite("b.test"); state.form.relativePath = "new-path";
  pending.resolve(item); await creating; expect(state.form.relativePath).toBe("new-path"); expect(state.notice).toBe("");
});
it("still removes the confirmed directory when the site remains unchanged", async () => {
  const state = await mount(); await state.remove(item); expect(unprotectDir).toHaveBeenCalledWith("a.test", "private");
});
