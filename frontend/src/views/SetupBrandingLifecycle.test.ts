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
import { useDatabasesStore } from "../stores/databases";
import type { PanelPreferences, ServerAddressDnsSync } from "../api/panelSettings";

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
  brandingForm: import("../api/panelSettings").Branding; loadBranding(): Promise<boolean>; saveBranding(): Promise<void>;
  mode: string; bootstrapState: string; ftpUserCount: number | null; databaseCount: number | null; sitesCount: number | null; totalBindingCount: number | null;
  mailAccountCount: number | null; dashboardSections: { id: string; badge?: string }[];
  banner: string; bannerError: boolean;
  panelPreferences: PanelPreferences; preferencesBusy: boolean;
  savePanelPreferences(): Promise<void>; retryServerAddressDns(oldAddress: string): Promise<void>; toggleTheme(): Promise<void>;
  loadDashboardData(): Promise<void>; refreshDashboard(): Promise<void>; logout(): Promise<void>; handleSessionExpired(): void;
  initializeView(): Promise<void>; submitLogin(): Promise<void>;
};
const envelope = (data: unknown) => ({ data: { success: true, data, message: "", code: 0 } });
const ftp = (name: string) => ({ userName: name, siteName: "owned.test", homePath: "D:/fixture/site", createdAtUtc: "2026-10-04T00:00:00Z", isEnabled: true, existsOnSystem: true });
const owned = [{ engine: "mariadb", name: "tenant_db" }, { engine: "postgresql", name: "TenantDb" }];
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(yes => { resolve = yes; }); return { promise, resolve }; }
async function flush() { for (let i = 0; i < 25; i++) await nextTick(); }
const icon = { href: "" };
let app: App | undefined;
let ftpRows = [ftp("first"), ftp("second"), ftp("third")];
let ownedRows = owned;

beforeEach(() => {
  vi.useFakeTimers(); shared.epoch = 0; shared.route.value = { query: { section: "home" } };
  ftpRows = [ftp("first"), ftp("second"), ftp("third")]; ownedRows = owned;
  vi.stubGlobal("window", { location: { pathname: "/panel", protocol: "https:", hostname: "panel.example.test", port: "", search: "" },
    matchMedia: () => ({ matches: false }), scrollTo: vi.fn(), setInterval, clearInterval, setTimeout, clearTimeout });
  vi.stubGlobal("document", { hidden: false, title: "", querySelector: () => icon,
    documentElement: { dataset: { theme: "dark" }, style: { setProperty: vi.fn(), removeProperty: vi.fn() } } });
  vi.stubGlobal("localStorage", { getItem: () => null, setItem: vi.fn() });
  vi.mocked(apiClient.get).mockImplementation(async url => {
    if (url === "/mail") return envelope({ installed: true, configured: true, domains: [
      { name: "first.test", accountCount: 2, accountCountMeasured: true },
      { name: "second.test", accountCount: 3, accountCountMeasured: true }
    ], accounts: [] });
    if (url === "/db/engines") return envelope([]);
    if (url === "/my-databases") return envelope(ownedRows);
    if (url === "/ftp/users") return envelope(ftpRows);
    if (url === "/scope/sites" || url === "/users" || url === "/audit-logs?take=25" || url === "/php/versions") return envelope([]);
    if (url === "/security/settings") return envelope({ secureEntryPath: "/panel", allowedIpAddresses: [], hasIpRestrictions: false });
    if (url === "/panel-settings") return envelope({ panelName: "Fixture", defaultLanguage: "tr", themeMode: "dark", uiDensity: "comfortable", serverAddress: "84.51.22.239" });
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

const brand = (name: string) => ({ panelName:name, accentColor:'#112233',logoDataUrl:'',faviconDataUrl:`icon-${name}`,logoLinkUrl:'',scope:'server' });
it('keeps the saved brand in the actual document title, favicon and accent after an old GET resolves',async()=>{
 const {state}=await mount('Administrator');const old=deferred<ReturnType<typeof envelope>>();
 vi.mocked(apiClient.get).mockReturnValueOnce(old.promise as never);
 const pending=state.loadBranding();state.brandingForm=brand('new');
 const original=vi.mocked(apiClient.get).getMockImplementation()!;
 vi.mocked(apiClient.get).mockImplementation((url,config)=>url==='/branding'?Promise.resolve(envelope(brand('new'))) as never:original(url,config));
 vi.mocked(apiClient.put).mockResolvedValue(envelope(brand('new')) as never);
 await state.saveBranding();old.resolve(envelope(brand('old')));await pending;
 expect(document.title).toBe('new');expect(icon.href).toBe('icon-new');expect(document.documentElement.style.setProperty).toHaveBeenLastCalledWith('--brand-accent','#112233');
});
it('does not let a prior branding GET change the document after unmount',async()=>{
 const {state}=await mount('Administrator');const old=deferred<ReturnType<typeof envelope>>();vi.mocked(apiClient.get).mockReturnValueOnce(old.promise as never);
 const pending=state.loadBranding();app?.unmount();app=undefined;const title=document.title,href=icon.href;vi.mocked(document.documentElement.style.setProperty).mockClear();
 old.resolve(envelope(brand('old')));await pending;expect(document.title).toBe(title);expect(icon.href).toBe(href);expect(document.documentElement.style.setProperty).not.toHaveBeenCalled();
});