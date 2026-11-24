import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { createRenderer, createSSRApp, h, nextTick, ssrContextKey, type App, type Component } from "vue";
import { renderToString } from "@vue/server-renderer";
import SiteAliases from "./SiteAliases.vue";
import { getSiteAliases, addSiteAlias, type SiteAliasWorkspace } from "../api/aliases";
import { getSslWorkspace } from "../api/ssl";
import router from "../router";
vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key }) }));
vi.mock("../router", () => ({ default: { push: vi.fn() } }));
vi.mock("../api/aliases", () => ({ getSiteAliases: vi.fn(), addSiteAlias: vi.fn(), removeSiteAlias: vi.fn() }));
vi.mock("../api/ssl", () => ({ getSslWorkspace: vi.fn() }));
const workspace = (): SiteAliasWorkspace => ({ siteName: "example.com", primaryHostName: "example.com", aliases: [] });
type State = { workspace: SiteAliasWorkspace | null; primaryWwwHost: string; error: string; preparePrimaryWww(): Promise<void>; openSsl(host: string): void };
let app: App | undefined;
async function flush() { for (let i = 0; i < 12; i++) await nextTick(); }
async function mount() {
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  const pinia = createPinia(); setActivePinia(pinia);
  app = renderer.createApp({ ...SiteAliases, render: () => h("section") } as Component, { siteName: "example.com" }).use(pinia); app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: State }).setupState; await flush(); return state;
}
beforeEach(() => {
  vi.mocked(getSiteAliases).mockResolvedValue(workspace());
  vi.mocked(addSiteAlias).mockResolvedValue({ ...workspace(), aliases: [{ hostName: "www.example.com", hasHttps: false, includesWww: false, redirectToPrimary: true }] });
  vi.mocked(getSslWorkspace).mockResolvedValue({ siteName: "example.com", hostNames: ["example.com"], certificates: [], httpsRedirectEnabled: false });
  vi.mocked(router.push).mockResolvedValue(undefined);
});
afterEach(() => { app?.unmount(); app = undefined; vi.restoreAllMocks(); vi.clearAllMocks(); });
it("prepares exactly one missing www alias then opens the matching certificate form", async () => {
  const state = await mount(); expect(state.primaryWwwHost).toBe("www.example.com"); await state.preparePrimaryWww();
  expect(addSiteAlias).toHaveBeenCalledWith("example.com", { hostName: "www.example.com", redirectToPrimary: true, includeWww: false });
  expect(router.push).toHaveBeenCalledWith({ query: { section: "sites", site: "example.com", tool: "ssl", sslHost: "www.example.com" } });
});
it("preserves a www binding created outside the alias manager", async () => {
  vi.mocked(getSslWorkspace).mockResolvedValue({ siteName: "example.com", hostNames: ["example.com", "www.example.com"], certificates: [], httpsRedirectEnabled: false });
  const state = await mount(); await state.preparePrimaryWww(); expect(addSiteAlias).not.toHaveBeenCalled(); expect(router.push).toHaveBeenCalledTimes(1);
});
it("does not repeat writes on a second click while measurement is pending", async () => {
  let finish!: (value: Awaited<ReturnType<typeof getSslWorkspace>>) => void;
  vi.mocked(getSslWorkspace).mockReturnValueOnce(new Promise(resolve => { finish = resolve; }));
  const state = await mount(); const first = state.preparePrimaryWww(); await state.preparePrimaryWww();
  finish({ siteName: "example.com", hostNames: ["example.com"], certificates: [], httpsRedirectEnabled: false }); await first;
  expect(addSiteAlias).toHaveBeenCalledTimes(1); expect(router.push).toHaveBeenCalledTimes(1);
});
it("does not navigate as if setup succeeded when the alias redirect was not applied", async () => {
  vi.mocked(addSiteAlias).mockResolvedValue({ ...workspace(), redirectRuleNoticeKey: "alias.redirect_rule_not_applied" });
  const state = await mount(); await state.preparePrimaryWww(); expect(state.error).toBe("alias.redirect_rule_not_applied"); expect(router.push).not.toHaveBeenCalled();
});
it("stops before a write when the view has been closed", async () => {
  let finish!: (value: Awaited<ReturnType<typeof getSslWorkspace>>) => void;
  vi.mocked(getSslWorkspace).mockReturnValueOnce(new Promise(resolve => { finish = resolve; }));
  const state = await mount(); const first = state.preparePrimaryWww(); app!.unmount(); app = undefined;
  finish({ siteName: "example.com", hostNames: ["example.com"], certificates: [], httpsRedirectEnabled: false }); await first;
  expect(addSiteAlias).not.toHaveBeenCalled(); expect(router.push).not.toHaveBeenCalled();
});
it.each(["www.example.com", "mail.example.com", "192.168.1.1", "site.nip.io", "localhost", "*.example.com"])("does not generate an automatic www target for %s", async host => {
  const state = await mount(); state.workspace = { ...workspace(), primaryHostName: host };
  expect(state.primaryWwwHost).toBe(""); await state.preparePrimaryWww(); expect(addSiteAlias).not.toHaveBeenCalled(); expect(router.push).not.toHaveBeenCalled();
});
it("renders actionable SSL setup for missing HTTPS without claiming www certificate validity", async () => {
  const state = await mount(); state.workspace!.aliases = [{ hostName: "alias.test", includesWww: true, hasHttps: false, redirectToPrimary: true }];
  const html = await renderToString(createSSRApp({ ...SiteAliases, setup: () => state } as Component, { siteName: "example.com" }));
  expect(html).toContain("panel.alias.createHttps"); expect(html).toContain("panel.alias.reviewHttps"); expect(html).toContain("panel.alias.noHttps");
  state.openSsl("www.alias.test"); expect(router.push).toHaveBeenCalledWith({ query: { section: "sites", site: "example.com", tool: "ssl", sslHost: "www.alias.test" } });
});
