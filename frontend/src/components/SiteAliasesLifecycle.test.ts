import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { createRenderer, createSSRApp, h, nextTick, ref, ssrContextKey, type App, type Component } from "vue";
import { renderToString } from "vue/server-renderer";
import SiteAliases from "./SiteAliases.vue";
import { addSiteAlias, getSiteAliases, removeSiteAlias, type SiteAliasWorkspace } from "../api/aliases";
const mocks = vi.hoisted(() => ({ confirm: vi.fn(), epoch: 0 }));
vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key }) }));
vi.mock("../router", () => ({ default: { push: vi.fn() } }));
vi.mock("./ConfirmDialog.vue", () => ({ useConfirm: () => ({ confirm: mocks.confirm }) }));
vi.mock("../api/aliases", () => ({ getSiteAliases: vi.fn(), addSiteAlias: vi.fn(), removeSiteAlias: vi.fn() }));
vi.mock("../api/client", async original => ({ ...await original<object>(), getSessionEpoch: () => mocks.epoch }));
type State = { workspace: SiteAliasWorkspace | null; loading: boolean; busy: boolean; error: string;
  form: { hostName: string }; add(): Promise<void>; remove(host: string): Promise<void>; load(): Promise<void> };
const chosen = ref("a.test");
let app: App | undefined;
const workspace = (site = "a.test", notice = ""): SiteAliasWorkspace => ({ siteName: site, primaryHostName: site,
  aliases: [{ hostName: "alias.test", redirectToPrimary: true, includesWww: false, hasHttps: false }], redirectRuleNoticeKey: notice });
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(yes => { resolve = yes; }); return { promise, resolve }; }
async function flush() { for (let i = 0; i < 10; i++) await nextTick(); }
async function mount() {
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  const pinia = createPinia(); setActivePinia(pinia);
  // Keep one component type across parent renders so prop changes test the real lifecycle.
  const component = { ...SiteAliases, render: () => h("section") } as Component;
  app = renderer.createApp({ render: () => h(component, { siteName: chosen.value }) }).use(pinia); app.provide(ssrContextKey, {});
  const root = app.mount({}); await flush();
  return (root.$ as unknown as { subTree: { component: { setupState: State } } }).subTree.component.setupState;
}
beforeEach(() => {
  chosen.value = "a.test"; mocks.epoch = 0;
  vi.mocked(getSiteAliases).mockImplementation(async site => workspace(site));
  vi.mocked(addSiteAlias).mockImplementation(async site => workspace(site));
  vi.mocked(removeSiteAlias).mockImplementation(async site => workspace(site));
  mocks.confirm.mockImplementation(async (options: { action(): Promise<void> }) => { await options.action(); return true; });
});
afterEach(() => { app?.unmount(); app = undefined; vi.clearAllMocks(); });

it("keeps the selected site intact when an earlier load completes late", async () => {
  const pending = deferred<SiteAliasWorkspace>(); vi.mocked(getSiteAliases).mockReturnValueOnce(pending.promise);
  const state = await mount(); chosen.value = "b.test"; await flush();
  expect(state.workspace?.siteName).toBe("b.test");
  pending.resolve(workspace()); await flush(); expect(state.workspace?.siteName).toBe("b.test");
});
it.each(["add", "remove"] as const)("does not carry an already sent %s result into another site", async kind => {
  const state = await mount(), pending = deferred<SiteAliasWorkspace>();
  vi.mocked(kind === "add" ? addSiteAlias : removeSiteAlias).mockReturnValueOnce(pending.promise);
  state.form.hostName = "alias.test";
  const operation = kind === "add" ? state.add() : state.remove("alias.test"); await flush();
  chosen.value = "b.test"; await flush(); state.form.hostName = "new-draft.test";
  pending.resolve(workspace()); await operation;
  expect(state.workspace?.siteName).toBe("b.test"); expect(state.form.hostName).toBe("new-draft.test"); expect(state.error).toBe("");
});
it("does not delete from another site after a confirmation outlives its target", async () => {
  const state = await mount();
  mocks.confirm.mockImplementationOnce(async (options: { action(): Promise<void> }) => {
    chosen.value = "b.test"; await flush(); await options.action(); return true;
  });
  await state.remove("alias.test"); expect(removeSiteAlias).not.toHaveBeenCalled(); expect(state.workspace?.siteName).toBe("b.test");
});
it.each(["epoch", "unmount"])("does not dispatch an accepted delete after %s invalidates its context", async boundary => {
  const state = await mount();
  mocks.confirm.mockImplementationOnce(async (options: { action(): Promise<void> }) => {
    if (boundary === "epoch") mocks.epoch++; else { app!.unmount(); app = undefined; }
    await options.action(); return true;
  });
  await state.remove("alias.test"); expect(removeSiteAlias).not.toHaveBeenCalled();
});
it("reserves a pending confirmation against duplicate delete clicks", async () => {
  const state = await mount(), pending = deferred<boolean>(); mocks.confirm.mockReturnValueOnce(pending.promise);
  const deleting = state.remove("alias.test"); await state.remove("alias.test"); expect(mocks.confirm).toHaveBeenCalledTimes(1);
  pending.resolve(false); await deleting; expect(state.busy).toBe(false);
});
it("shows delete API errors even when the real confirmation contract swallows action failures", async () => {
  const state = await mount();
  vi.mocked(removeSiteAlias).mockRejectedValueOnce({ response: { data: { message: "alias.remove_binding_failed" } } });
  mocks.confirm.mockImplementationOnce(async (options: { action(): Promise<void> }) => {
    try { await options.action(); } catch { /* ConfirmDialog.accept logs and settles true. */ }
    return true;
  });
  await state.remove("alias.test");
  expect(state.error).toBe("alias.remove_binding_failed");
  expect(state.workspace?.aliases[0]?.hostName).toBe("alias.test");
  expect(state.busy).toBe(false);
});
it("blocks keyboard submission while another write is pending", async () => {
  const state = await mount(), pending = deferred<SiteAliasWorkspace>(); state.form.hostName = "alias.test";
  vi.mocked(addSiteAlias).mockReturnValueOnce(pending.promise); const adding = state.add(); await state.add();
  expect(addSiteAlias).toHaveBeenCalledTimes(1); pending.resolve(workspace()); await adding;
});
it.each(["add", "remove"] as const)("shows the measured redirect warning after normal %s while retaining the alias list", async kind => {
  const state = await mount(); const result = workspace("a.test", "alias.redirect_rule_unverified");
  vi.mocked(kind === "add" ? addSiteAlias : removeSiteAlias).mockResolvedValueOnce(result);
  state.form.hostName = "alias.test"; await (kind === "add" ? state.add() : state.remove("alias.test"));
  expect(state.error).toBe("alias.redirect_rule_unverified");
  const html = await renderToString(createSSRApp({ ...SiteAliases, setup: () => ({ ...state, t: (key: string) => key,
    writeGate: () => ({}), primaryWwwHost: "", openSsl: vi.fn() }) } as Component, { siteName: "a.test" }));
  expect(html).toContain('role="alert"'); expect(html).toContain("alias.test"); expect(html).toContain("alias.redirect_rule_unverified");
  expect(html).toContain('status-pill warn');
});
