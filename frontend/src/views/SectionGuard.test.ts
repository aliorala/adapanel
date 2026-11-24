import { afterEach, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { createRenderer, h, nextTick, ssrContextKey, type App, type Component } from "vue";
import SetupWizardView from "./SetupWizardView.vue";
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


it.each(["Customer", "Reseller"])("shows a not-authorised screen (not a blank page) when %s opens ?section=monitor", async role => {
  shared.route.value.query = { section: "monitor" };
  const state = mount(SetupWizardView, role);
  await settle();
  (state.restorePanelLocation as () => void)();
  expect((state.dashboardSections as { id: string }[]).some(section => section.id === "monitor")).toBe(false);
  expect(state.deniedSection).toBe("monitor");
  expect(state.activeSection).toBe("home");
  // Adres Ana Sayfa'ya çevrilir (varolan sözleşme) ama açıklama ekranı kalır.
  expect(router.replace).toHaveBeenCalledWith({ query: { section: "home" } });
  shared.route.value.query = { section: "home" };
  (state.restorePanelLocation as () => void)();
  expect(state.deniedSection).toBe("monitor");
});

it("does not show the denied screen to the administrator who may open the section, nor for an unknown section", async () => {
  shared.route.value.query = { section: "monitor" };
  const admin = mount(SetupWizardView, "Administrator");
  await settle();
  (admin.restorePanelLocation as () => void)();
  expect(admin.deniedSection).toBe("");
  expect(admin.activeSection).toBe("monitor");

  shared.route.value.query = { section: "no-such-section" };
  const customer = mount(SetupWizardView, "Customer");
  await settle();
  (customer.restorePanelLocation as () => void)();
  expect(customer.deniedSection).toBe("");
  expect(customer.activeSection).toBe("home");
});
