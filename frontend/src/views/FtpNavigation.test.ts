import { afterEach, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { createRenderer, h, nextTick, ssrContextKey, type App, type Component } from "vue";
import { compile } from "@vue/compiler-dom";
import * as Vue from "vue";
import dashboardSource from "./DashboardOverviewView.vue?raw";
import SetupWizardView from "./SetupWizardView.vue";
import FtpView from "./FtpView.vue";
import { useAuthStore } from "../stores/auth";
import { useSitesStore } from "../stores/sites";
import { useSetupStore } from "../stores/setup";
import router from "../router";

const shared = vi.hoisted(() => ({ route: { value: { query: { section: "ftp" } as Record<string, string> } } }));
vi.mock("../router", () => ({ default: { currentRoute: shared.route, push: vi.fn(async () => undefined), replace: vi.fn(async () => undefined) } }));
vi.mock("../i18n", () => ({ setPanelLocale: vi.fn() }));
vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key, te: () => true }) }));
vi.mock("../api/client", () => ({
  apiClient: { get: vi.fn(async () => ({ data: { data: {} } })), post: vi.fn(), put: vi.fn(), delete: vi.fn() },
  getSessionEpoch: () => 0, invalidateSessionRequests: vi.fn(), setReadOnlyMode: vi.fn(), clearCsrfToken: vi.fn(),
  setSessionExpiredHandler: vi.fn(), readSecureEntryPath: () => "panel",
}));
const ftpUsers = vi.hoisted(() => ["one.test", "two.test"].map(siteName => ({ userName: `${siteName}_ftp`, siteName,
  homePath: `D:/sites/${siteName}`, isEnabled: true, existsOnSystem: true, createdAtUtc: "2026-10-28T00:00:00Z" })));
vi.mock("../api/ftp", () => ({
  getFtpUsers: vi.fn(async () => ftpUsers), getFtpStatus: vi.fn(async () => ({ isConfigured: true })),
  ensureFtpServer: vi.fn(async () => ({ isConfigured: true })), getFtpTls: vi.fn(async () => ({ requireSsl: false })),
  createFtpUser: vi.fn(), deleteFtpUser: vi.fn(), setFtpPassword: vi.fn(), setFtpTls: vi.fn(),
}));

const apps: App[] = [];
const settle = async () => { for (let i = 0; i < 30; i++) await nextTick(); };
afterEach(() => { apps.splice(0).forEach(app => app.unmount()); vi.restoreAllMocks(); vi.clearAllMocks(); vi.unstubAllGlobals(); });

function mount(component: Component, role: string) {
  vi.stubGlobal("window", { location: { pathname: "/panel" } });
  vi.stubGlobal("localStorage", { getItem: () => null, setItem: vi.fn() });
  vi.stubGlobal("document", { title: "", querySelector: () => null,
    documentElement: { dataset: { theme: "dark" }, style: { setProperty: vi.fn(), removeProperty: vi.fn() } } });
  const pinia = createPinia(); setActivePinia(pinia);
  useAuthStore(pinia).session = { isAuthenticated: true, requiresSetupWizard: false, user: { id: role, role } as never };
  useSitesStore(pinia).sites = [{ name: "one.test", bindings: [] }] as never;
  // Hold startup at its status read: these tests exercise the actual role and route logic without background dashboard reads.
  vi.spyOn(useSetupStore(pinia), "fetchStatus").mockReturnValue(new Promise(() => {}));
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}),
    insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  const app = renderer.createApp({ ...component, render: () => h("section") } as Component).use(pinia);
  app.provide(ssrContextKey, {}); apps.push(app);
  return (app.mount({}).$ as unknown as { setupState: Record<string, unknown> }).setupState;
}

it.each(["Administrator", "Reseller", "Customer", "Operator"])("restores persisted FTP navigation only for an authorized %s", async role => {
  shared.route.value.query = { section: "ftp" };
  const state = mount(SetupWizardView, role);
  await settle();
  (state.restorePanelLocation as () => void)();
  const allowed = role === "Administrator" || role === "Reseller";
  expect((state.dashboardSections as { id: string }[]).some(section => section.id === "ftp")).toBe(allowed);
  expect(state.activeSection).toBe(allowed ? "ftp" : "home");
  if (!allowed) expect(router.replace).toHaveBeenCalledWith({ query: { section: "home" } });
});

it("the actual dashboard FTP card navigates to FTP and disappears when FTP is not allowed", () => {
  const template = dashboardSource.match(/<button v-if="canReadFtpCount[^>]+>[\s\S]*?<\/button>/)![0];
  const render = new Function("Vue", compile(template, { mode: "function" }).code)(Vue);
  const go = vi.fn();
  const state = { canReadFtpCount: true, can: (id: string) => id === "ftp", ftpUserCount: 2, t: (key: string) => key, go };
  const button = render(state, []);
  button.props.onClick();
  expect(go).toHaveBeenCalledExactlyOnceWith("ftp");
  expect(render({ ...state, can: () => false }, []).type).toBe(Vue.Comment);
});

it.each(["Administrator", "Reseller"])("global FTP shows every API-scoped account for %s without a site filter", async role => {
  const state = mount(FtpView, role);
  await settle();
  expect(state.scopedUsers).toEqual(ftpUsers);
  expect(state.filteredUsers).toEqual(ftpUsers);
  expect((state.form as { siteName: string }).siteName).toBe("");
});
