import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { createRenderer, h, nextTick, ssrContextKey, type App, type Component } from "vue";
import SiteStatisticsView from "./SiteStatisticsView.vue";
import { getSiteStatistics, type WebStatistics } from "../api/statistics";
import { useAuthStore } from "../stores/auth";
import { useSitesStore } from "../stores/sites";
vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key }) }));
vi.mock("../api/statistics", () => ({ getSiteStatistics: vi.fn() }));
type State = { stats: WebStatistics | null; statsDays: number; notice: string; loading: boolean; load(): Promise<void> };
function result(siteName = "a.test"): WebStatistics { return { siteName, periodDays: 7, generatedAtUtc: "", logPath: "", hasData: true,
  totalRequests: 4, uniqueVisitors: 2, uniqueVisitorsApproximate: false, totalBytes: 10, status2xx: 4, status3xx: 0, status4xx: 0, status5xx: 0,
  topPages: [], topReferrers: [], dailyHits: [] }; }
function deferred<T>() { let resolve!: (value: T) => void, reject!: (error: unknown) => void;
  const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; }
let app: App | undefined;
async function flush() { for (let i = 0; i < 8; i++) await nextTick(); }
async function mount() {
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  const pinia = createPinia(); setActivePinia(pinia); const auth = useAuthStore(pinia);
  auth.session = { isAuthenticated: true, requiresSetupWizard: false, user: { id: "actor", role: "Administrator", userName: "actor", email: "a@example.test", preferredLanguage: "en", isTwoFactorEnabled: false, isActive: true, lastLoginAtUtc: null } };
  const sites = useSitesStore(pinia); sites.selectedSiteName = "a.test";
  app = renderer.createApp({ ...SiteStatisticsView, render: () => h("section") } as Component).use(pinia); app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: State }).setupState; await flush(); return { state, auth, sites };
}
beforeEach(() => vi.mocked(getSiteStatistics).mockResolvedValue(result()));
afterEach(() => { app?.unmount(); app = undefined; vi.resetAllMocks(); });

it.each(["success", "failure"])("ignores an earlier site's late %s", async outcome => {
  const pending = deferred<WebStatistics>(); vi.mocked(getSiteStatistics).mockReturnValueOnce(pending.promise).mockResolvedValueOnce(result("b.test"));
  const { state, sites } = await mount(); sites.selectedSiteName = "b.test"; await flush();
  if (outcome === "success") pending.resolve(result()); else pending.reject(new Error("earlier site failed"));
  await flush(); expect(state.stats?.siteName).toBe("b.test"); expect(state.notice).toBe(""); expect(state.loading).toBe(false);
});
it("clears displayed results and ignores in-flight requests on logout", async () => {
  const { state, auth } = await mount(); const pending = deferred<WebStatistics>(); vi.mocked(getSiteStatistics).mockReturnValueOnce(pending.promise);
  const loading = state.load(); const callsBeforeLogout = vi.mocked(getSiteStatistics).mock.calls.length;
  auth.session = null; await flush(); pending.resolve(result()); await loading;
  expect(state.stats).toBeNull(); expect(state.loading).toBe(false); expect(getSiteStatistics).toHaveBeenCalledTimes(callsBeforeLogout);
});
it("ignores a reply after unmount", async () => {
  const pending = deferred<WebStatistics>(); vi.mocked(getSiteStatistics).mockReturnValueOnce(pending.promise);
  const { state } = await mount(); app!.unmount(); app = undefined; pending.resolve(result()); await flush(); expect(state.stats).toBeNull();
});
it("keeps the newer period when an older period finishes last", async () => {
  const pending = deferred<WebStatistics>(); vi.mocked(getSiteStatistics).mockReturnValueOnce(pending.promise);
  const { state } = await mount(); state.statsDays = 30;
  vi.mocked(getSiteStatistics).mockResolvedValueOnce({ ...result(), periodDays: 30 }); await state.load();
  pending.resolve(result()); await flush(); expect(state.stats?.periodDays).toBe(30);
});
it("ends loading and exposes a current request failure", async () => {
  vi.mocked(getSiteStatistics).mockRejectedValueOnce(new Error("Log unavailable"));
  const { state } = await mount(); expect(state.stats).toBeNull(); expect(state.loading).toBe(false); expect(state.notice).not.toBe("");
});
