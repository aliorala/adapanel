import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { createRenderer, h, nextTick, reactive, ssrContextKey, type App, type Component } from "vue";
import { routeLocationKey, type RouteLocationNormalizedLoaded } from "vue-router";
import SslView from "./SslView.vue";
import { getSslWorkspace, issueLetsEncrypt, type SslWorkspace } from "../api/ssl";
import { useSitesStore } from "../stores/sites";
vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key }) }));
vi.mock("../api/ssl", async original => ({ ...await original<object>(), getSslWorkspace: vi.fn(), issueLetsEncrypt: vi.fn() }));
const measured = (): SslWorkspace => ({ siteName: "a.test", hostNames: ["a.test", "www.a.test", "alias.test"], certificates: [], httpsRedirectEnabled: false });
let app: App | undefined;
let route: RouteLocationNormalizedLoaded;
type State = { activeTab: string; issueForm: { hostName: string; wildcard: boolean }; refresh(): Promise<boolean> };
async function flush() { for (let i = 0; i < 12; i++) await nextTick(); }
async function mount() {
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  const pinia = createPinia(); setActivePinia(pinia);
  const sites = useSitesStore(); sites.selectedSiteName = "a.test"; vi.spyOn(sites, "fetchSites").mockResolvedValue([]);
  app = renderer.createApp({ ...SslView, render: () => h("section") } as Component).use(pinia);
  app.provide(ssrContextKey, {}); app.provide(routeLocationKey, route);
  const state = (app.mount({}).$ as unknown as { setupState: State }).setupState;
  await flush(); return state;
}
beforeEach(() => {
  route = reactive({ query: { section: "sites", site: "a.test", tool: "ssl", sslHost: "www.a.test" } }) as unknown as RouteLocationNormalizedLoaded;
  vi.mocked(getSslWorkspace).mockResolvedValue(measured());
});
afterEach(() => { app?.unmount(); app = undefined; vi.restoreAllMocks(); vi.clearAllMocks(); });
it("opens Let's Encrypt with the exact bound www hostname without issuing a certificate", async () => {
  const state = await mount();
  expect(state.issueForm.hostName).toBe("www.a.test"); expect(state.issueForm.wildcard).toBe(false);
  expect(state.activeTab).toBe("letsencrypt"); expect(issueLetsEncrypt).not.toHaveBeenCalled();
  state.activeTab = "current"; state.issueForm.hostName = "alias.test";
  await state.refresh(); expect(state.issueForm.hostName).toBe("alias.test"); expect(state.activeTab).toBe("current");
});
it.each(["unbound.test", "*.a.test", "https://www.a.test", ["www.a.test"]])("does not preselect an unmeasured or malformed target %s", async target => {
  route.query.sslHost = target;
  const state = await mount(); expect(state.issueForm.hostName).toBe("a.test"); expect(state.activeTab).toBe("current");
});
it("does not carry a host requested for another site into this site's form", async () => {
  route.query.site = "other.test";
  const state = await mount(); expect(state.issueForm.hostName).toBe("a.test"); expect(state.activeTab).toBe("current");
});
it("does not overwrite a user edit made while the workspace is loading", async () => {
  let finish!: (value: SslWorkspace) => void;
  vi.mocked(getSslWorkspace).mockReturnValueOnce(new Promise(resolve => { finish = resolve; }));
  const state = await mount(); state.issueForm.hostName = "alias.test";
  finish(measured()); await flush(); expect(state.issueForm.hostName).toBe("alias.test"); expect(state.activeTab).toBe("current");
});
