import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { createRenderer, h, nextTick, ssrContextKey, type App, type Component } from "vue";
import SetupWizardView from "./SetupWizardView.vue";
import { apiClient } from "../api/client";
import { useAuthStore } from "../stores/auth";
import { useSetupStore } from "../stores/setup";
import { useSystemStore } from "../stores/system";
import { useSoftwareStore } from "../stores/software";
import { useSitesStore } from "../stores/sites";

const shared = vi.hoisted(() => ({ epoch: 0, route: { value: { query: { section: "home" } as Record<string, string> } } }));
vi.mock("../api/client", () => ({
  apiClient: { get: vi.fn(), post: vi.fn(), put: vi.fn(), delete: vi.fn() },
  getSessionEpoch: () => shared.epoch, invalidateSessionRequests: () => { shared.epoch++; },
  setReadOnlyMode: vi.fn(), clearCsrfToken: vi.fn(), setSessionExpiredHandler: vi.fn(), readSecureEntryPath: () => "panel"
}));
vi.mock("../router", () => ({ default: { currentRoute: shared.route, push: vi.fn(async () => undefined), replace: vi.fn(async () => undefined) } }));
vi.mock("../i18n", () => ({ setPanelLocale: vi.fn(async () => undefined) }));
vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key, te: () => true }) }));
vi.mock("../composables/navigationGuard", () => ({ allowNavigation: vi.fn(async () => true) }));

type State = {
  mode: string; bootstrapState: string; ftpUserCount: number | null; databaseCount: number | null; sitesCount: number | null; totalBindingCount: number | null;
  banner: string; bannerError: boolean;
  loadDashboardData(): Promise<void>; refreshDashboard(): Promise<void>; logout(): Promise<void>; handleSessionExpired(): void;
  initializeView(): Promise<void>; submitLogin(): Promise<void>;
};
const envelope = (data: unknown) => ({ data: { success: true, data, message: "", code: 0 } });
const ftp = (name: string) => ({ userName: name, siteName: "owned.test", homePath: "D:/fixture/site", createdAtUtc: "2026-10-04T00:00:00Z" });
const owned = [{ engine: "mariadb", name: "tenant_db" }, { engine: "postgresql", name: "TenantDb" }];
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(yes => { resolve = yes; }); return { promise, resolve }; }
async function flush() { for (let i = 0; i < 25; i++) await nextTick(); }
let app: App | undefined;
let ftpRows = [ftp("first"), ftp("second"), ftp("third")];
let ownedRows = owned;

beforeEach(() => {
  vi.useFakeTimers(); shared.epoch = 0; shared.route.value = { query: { section: "home" } };
  ftpRows = [ftp("first"), ftp("second"), ftp("third")]; ownedRows = owned;
  vi.stubGlobal("window", { location: { pathname: "/panel", protocol: "https:", hostname: "panel.example.test", port: "", search: "" },
    matchMedia: () => ({ matches: false }), scrollTo: vi.fn(), setInterval, clearInterval, setTimeout, clearTimeout });
  vi.stubGlobal("document", { hidden: false, title: "", querySelector: () => null,
    documentElement: { dataset: { theme: "dark" }, style: { setProperty: vi.fn(), removeProperty: vi.fn() } } });
  vi.stubGlobal("localStorage", { getItem: () => null, setItem: vi.fn() });
  vi.mocked(apiClient.get).mockImplementation(async url => {
    if (url === "/my-databases") return envelope(ownedRows);
    if (url === "/ftp/users") return envelope(ftpRows);
    if (url === "/scope/sites" || url === "/users" || url === "/audit-logs?take=25" || url === "/php/versions") return envelope([]);
    if (url === "/security/settings") return envelope({ secureEntryPath: "/panel", allowedIpAddresses: [], hasIpRestrictions: false });
    if (url === "/panel-settings") return envelope({ panelName: "Fixture", defaultLanguage: "tr", themeMode: "dark", uiDensity: "comfortable" });
    if (url === "/branding") return envelope({ panelName: "", accentColor: "", logoDataUrl: "", faviconDataUrl: "", logoLinkUrl: "", scope: "server" });
    if (url === "/subscriptions/me") return envelope({ isSuspended: false });
    throw new Error(`Unexpected fixture request: ${url}`);
  });
  vi.mocked(apiClient.post).mockResolvedValue(envelope({}));
});
afterEach(() => { app?.unmount(); app = undefined; vi.clearAllTimers(); vi.useRealTimers(); vi.restoreAllMocks(); vi.resetAllMocks(); vi.unstubAllGlobals(); });

async function mount(role: "Administrator" | "Customer" | "Reseller", allowedSections: string[] | null = null) {
  const pinia = createPinia(); setActivePinia(pinia);
  vi.spyOn(useSetupStore(), "fetchStatus").mockResolvedValue({ isInitialized: false, defaultLanguage: "tr", secureEntryPath: "/panel", preferredHttpsPort: 8443 } as never);
  vi.spyOn(useSystemStore(), "fetchOverview").mockResolvedValue({} as never);
  vi.spyOn(useSoftwareStore(), "fetchCatalog").mockImplementation(async () => {
    const catalog = { installed: [], manifest: [], defaults: [] }; useSoftwareStore().catalog = catalog; useSoftwareStore().loaded = true; return catalog;
  });
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}),
    insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  app = renderer.createApp({ ...SetupWizardView, render: () => h("section") } as Component).use(pinia); app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: State }).setupState;
  await flush();
  signIn(role, allowedSections); state.mode = "dashboard";
  return { state, auth: useAuthStore() };
}
function signIn(role: "Administrator" | "Customer" | "Reseller", allowedSections: string[] | null = null) {
  useAuthStore().session = { isAuthenticated: true, requiresSetupWizard: false, isSubUser: allowedSections !== null, allowedSections,
    user: { id: role + "-actor", userName: role, email: "fixture@example.test", role, preferredLanguage: "tr", isTwoFactorEnabled: false, isActive: true, lastLoginAtUtc: null } };
}

it.each(["Customer", "Reseller"] as const)("loads %s database count from owned databases, never the administrator inventory", async role => {
  const { state } = await mount(role); await state.loadDashboardData();
  expect(apiClient.get).toHaveBeenCalledWith("/my-databases");
  expect(state.databaseCount).toBe(2);
  expect(vi.mocked(apiClient.get).mock.calls.some(([url]) => url.startsWith("/db"))).toBe(false);
});
it("loads a reseller's FTP count from the scoped existing endpoint", async () => {
  const { state } = await mount("Reseller"); await state.loadDashboardData();
  expect(apiClient.get).toHaveBeenCalledWith("/ftp/users"); expect(state.ftpUserCount).toBe(3);
});
it("does not fabricate a zero FTP count for a Customer whose role cannot read that endpoint", async () => {
  const { state } = await mount("Customer"); await state.loadDashboardData();
  expect(apiClient.get).not.toHaveBeenCalledWith("/ftp/users"); expect(state.ftpUserCount).toBeNull();
});
it.each(["logout", "expired"])("clears the previous administrator's local FTP count on %s", async end => {
  const { state } = await mount("Administrator"); await state.loadDashboardData(); expect(state.ftpUserCount).toBe(3);
  if (end === "logout") await state.logout(); else state.handleSessionExpired();
  signIn("Customer"); state.mode = "dashboard"; await state.loadDashboardData();
  expect(state.ftpUserCount).toBeNull();
});
it("does not let a late administrator FTP response publish into the next actor", async () => {
  const { state } = await mount("Administrator"), pending = deferred<ReturnType<typeof envelope>>();
  const fallback = vi.mocked(apiClient.get).getMockImplementation()!;
  vi.mocked(apiClient.get).mockImplementation((url, config) => url === "/ftp/users" ? pending.promise : fallback(url, config));
  const reading = state.loadDashboardData(); await flush();
  expect(apiClient.get).toHaveBeenCalledWith("/ftp/users"); await state.logout(); signIn("Customer"); state.mode = "dashboard";
  pending.resolve(envelope(ftpRows)); await reading;
  expect(state.ftpUserCount).toBeNull();
});
it("a failed FTP refresh invalidates its old total instead of retaining a confirmed number", async () => {
  const { state } = await mount("Administrator"); await state.loadDashboardData(); expect(state.ftpUserCount).toBe(3);
  const fallback = vi.mocked(apiClient.get).getMockImplementation()!;
  vi.mocked(apiClient.get).mockImplementation((url, config) => url === "/ftp/users" ? Promise.reject(new Error("offline")) : fallback(url, config));
  await state.loadDashboardData().catch(() => undefined); expect(state.ftpUserCount).toBeNull();
});
it("a subuser with only database access gets the owned DB count without requesting site or FTP data", async () => {
  const { state } = await mount("Customer", ["database"]); await state.loadDashboardData();
  expect(state.databaseCount).toBe(2); expect(apiClient.get).not.toHaveBeenCalledWith("/scope/sites"); expect(apiClient.get).not.toHaveBeenCalledWith("/ftp/users");
});
it("a subuser without database access gets no DB request or fabricated zero", async () => {
  const { state } = await mount("Customer", ["mail"]); await state.loadDashboardData();
  expect(apiClient.get).not.toHaveBeenCalledWith("/my-databases"); expect(state.databaseCount).toBeNull();
});
it("an empty measured owned DB list is a real zero", async () => {
  ownedRows = []; const { state } = await mount("Customer"); await state.loadDashboardData();
  expect(apiClient.get).toHaveBeenCalledWith("/my-databases"); expect(state.databaseCount).toBe(0);
});
it("does not block independent tenant counts behind an unavailable system overview", async () => {
  const { state } = await mount("Customer"); vi.mocked(useSystemStore().fetchOverview).mockRejectedValueOnce(new Error("overview unavailable"));
  await state.loadDashboardData().catch(() => undefined); expect(state.databaseCount).toBe(2);
});
it("keeps the administrator's measured FTP count and known empty installed DB inventory", async () => {
  const { state } = await mount("Administrator"); await state.loadDashboardData();
  expect(state.ftpUserCount).toBe(3); expect(state.databaseCount).toBe(0); expect(useSitesStore().loaded).toBe(true);
});
it("keeps a newer owned count when an older read finishes last", async () => {
  const { state } = await mount("Customer"), pending = deferred<ReturnType<typeof envelope>>();
  const fallback = vi.mocked(apiClient.get).getMockImplementation()!; let calls = 0;
  vi.mocked(apiClient.get).mockImplementation((url, config) => url === "/my-databases" && ++calls === 1 ? pending.promise : fallback(url, config));
  const older = state.loadDashboardData(); await flush();
  ownedRows = []; await state.loadDashboardData();
  pending.resolve(envelope(owned)); await older;
  expect(state.databaseCount).toBe(0);
});
it.each(["unmount", "actor", "section", "epoch"])("discards a pending owned count after %s changes", async change => {
  const { state, auth } = await mount("Customer"), pending = deferred<ReturnType<typeof envelope>>();
  const fallback = vi.mocked(apiClient.get).getMockImplementation()!;
  vi.mocked(apiClient.get).mockImplementation((url, config) => url === "/my-databases" ? pending.promise : fallback(url, config));
  const reading = state.loadDashboardData(); await flush();
  expect(apiClient.get).toHaveBeenCalledWith("/my-databases");
  if (change === "unmount") { app!.unmount(); app = undefined; }
  else if (change === "actor") auth.session!.user!.id = "another-customer";
  else if (change === "section") { auth.session!.isSubUser = true; auth.session!.allowedSections = ["mail"]; }
  else shared.epoch++;
  pending.resolve(envelope(owned)); await reading;
  expect(state.databaseCount).toBeNull();
});
it("marks site and binding counts unknown after a failed refresh", async () => {
  const { state } = await mount("Customer"); await state.loadDashboardData();
  expect(state.sitesCount).toBe(0); expect(state.totalBindingCount).toBe(0);
  const fallback = vi.mocked(apiClient.get).getMockImplementation()!;
  vi.mocked(apiClient.get).mockImplementation((url, config) => url === "/scope/sites" ? Promise.reject(new Error("sites unavailable")) : fallback(url, config));
  await state.loadDashboardData().catch(() => undefined);
  expect(state.sitesCount).toBeNull(); expect(state.totalBindingCount).toBeNull();
});
it("does not report refresh success when an allowed count failed to load", async () => {
  const { state } = await mount("Reseller");
  const fallback = vi.mocked(apiClient.get).getMockImplementation()!;
  vi.mocked(apiClient.get).mockImplementation((url, config) => url === "/ftp/users" ? Promise.reject(new Error("FTP unavailable")) : fallback(url, config));
  await state.refreshDashboard(); expect(state.bannerError).toBe(true); expect(state.ftpUserCount).toBeNull();
});
it("does not publish a success notice from a refresh that outlives its session", async () => {
  const { state } = await mount("Reseller"), pending = deferred<ReturnType<typeof envelope>>();
  const fallback = vi.mocked(apiClient.get).getMockImplementation()!;
  vi.mocked(apiClient.get).mockImplementation((url, config) => url === "/ftp/users" ? pending.promise : fallback(url, config));
  const reading = state.refreshDashboard(); await flush(); await state.logout(); const notice = state.banner;
  pending.resolve(envelope(ftpRows)); await reading; expect(state.banner).toBe(notice);
});
it("does not call privileged endpoints after the actor changes while subscription status loads", async () => {
  const { state } = await mount("Customer"), pending = deferred<ReturnType<typeof envelope>>();
  const fallback = vi.mocked(apiClient.get).getMockImplementation()!;
  vi.mocked(apiClient.get).mockImplementation((url, config) => url === "/subscriptions/me" ? pending.promise : fallback(url, config));
  const reading = state.loadDashboardData(); await flush(); signIn("Administrator");
  pending.resolve(envelope({ isSuspended: false })); await reading;
  expect(apiClient.get).not.toHaveBeenCalledWith("/ftp/users"); expect(apiClient.get).not.toHaveBeenCalledWith("/security/settings");
});
it("an unavailable admin catalog makes the DB count unknown while independent FTP remains measured", async () => {
  const { state } = await mount("Administrator"); vi.mocked(useSoftwareStore().fetchCatalog).mockRejectedValueOnce(new Error("catalog unavailable"));
  await state.loadDashboardData().catch(() => undefined);
  expect(state.databaseCount).toBeNull(); expect(state.ftpUserCount).toBe(3);
});
it.each([null, {}, [{ engine: "mariadb" }]])("does not turn malformed owned DB data into a measured count (%j)", async malformed => {
  const { state } = await mount("Customer");
  const fallback = vi.mocked(apiClient.get).getMockImplementation()!;
  vi.mocked(apiClient.get).mockImplementation((url, config) => url === "/my-databases" ? Promise.resolve(envelope(malformed)) : fallback(url, config));
  await state.loadDashboardData().catch(() => undefined); expect(state.databaseCount).toBeNull();
});

it.each([
  ["restore", "Administrator", "/ftp/users"], ["login", "Administrator", "/ftp/users"],
  ["restore", "Customer", "/my-databases"], ["login", "Customer", "/my-databases"]
] as const)("keeps %s %s usable and polling when its count fails, then recovers on retry", async (entry, role, failedUrl) => {
  const { state, auth } = await mount(role), session = auth.session;
  vi.mocked(useSetupStore().fetchStatus).mockResolvedValue({ isInitialized: true, defaultLanguage: "tr", secureEntryPath: "/panel", preferredHttpsPort: 8443 } as never);
  const fallback = vi.mocked(apiClient.get).getMockImplementation()!; let failing = true;
  vi.mocked(apiClient.get).mockImplementation((url, config) => url === "/auth/session" ? Promise.resolve(envelope(session)) :
    url === failedUrl && failing ? Promise.reject(new Error("counter unavailable")) : fallback(url, config));
  vi.mocked(apiClient.post).mockResolvedValue(envelope({ isAuthenticated: true, requiresTwoFactor: false }));
  auth.session = null; state.mode = "auth";
  if (entry === "restore") await state.initializeView(); else await state.submitLogin();
  expect(state.mode).toBe("dashboard"); expect(state.bootstrapState).toBe("ready");
  expect(state.bannerError).toBe(true);
  expect(role === "Administrator" ? state.ftpUserCount : state.databaseCount).toBeNull();
  const beforePoll = vi.mocked(useSystemStore().fetchOverview).mock.calls.length;
  await vi.advanceTimersByTimeAsync(15000);
  expect(useSystemStore().fetchOverview).toHaveBeenCalledTimes(beforePoll + 1);
  failing = false; await state.refreshDashboard();
  expect(role === "Administrator" ? state.ftpUserCount : state.databaseCount).toBe(role === "Administrator" ? 3 : 2);
  expect(state.bannerError).toBe(false);
});

it.each(["logout", "unmount"])("does not start restoration polling after %s while counts are pending", async ending => {
  const { state, auth } = await mount("Customer"), session = auth.session, pending = deferred<ReturnType<typeof envelope>>();
  vi.mocked(useSetupStore().fetchStatus).mockResolvedValue({ isInitialized: true, defaultLanguage: "tr", secureEntryPath: "/panel", preferredHttpsPort: 8443 } as never);
  const fallback = vi.mocked(apiClient.get).getMockImplementation()!;
  vi.mocked(apiClient.get).mockImplementation((url, config) => url === "/auth/session" ? Promise.resolve(envelope(session)) :
    url === "/my-databases" ? pending.promise : fallback(url, config));
  const restoring = state.initializeView(); await flush(); expect(apiClient.get).toHaveBeenCalledWith("/my-databases");
  if (ending === "logout") await state.logout(); else { app!.unmount(); app = undefined; }
  pending.resolve(envelope(owned)); await restoring;
  expect(vi.getTimerCount()).toBe(0);
});
