import { afterEach, expect, it, vi } from "vitest";
import { createRenderer, createSSRApp, h, nextTick, ssrContextKey, type App, type Component } from "vue";
import { renderToString } from "vue/server-renderer";
import { createPinia, setActivePinia } from "pinia";
import SoftwareView from "./SoftwareView.vue";
import { useSoftwareStore, type SoftwareCatalog } from "../stores/software";
import { apiClient } from "../api/client";
import { useAuthStore } from "../stores/auth";

vi.mock("../api/client", () => ({ apiClient: { post: vi.fn(), get: vi.fn() }, getSessionEpoch: () => 0 }));
vi.mock("../api/software", async original => ({ ...await original<object>(), getSoftwareRunningJobs: vi.fn(async () => ({ running: [], interrupted: [] })) }));
vi.mock("../stores/auth", async original => ({ ...await original<object>(), extractApiMessage: () => "failed", extractApiDetails: () => "" }));
vi.mock("../components/ConfirmDialog.vue", () => ({ useConfirm: () => ({ confirm: vi.fn() }) }));
vi.mock("./AppStoreView.vue", () => ({ default: { render: () => null } }));
vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string, args?: object) => key + (args ? JSON.stringify(args) : "") }) }));

let app: App | undefined;
type State = { activeWorkspace: string; checkForUpdates: () => Promise<void>; notice: string; noticeError: boolean };
async function mount(status: string, version = "", updateAvailable = false) {
  const pinia = createPinia(); setActivePinia(pinia);
  useAuthStore().session = { isAuthenticated: true, requiresSetupWizard: false, user: { id: "admin", userName: "admin", email: "admin@example.test", role: "Administrator", preferredLanguage: "en", isTwoFactorEnabled: false, isActive: true, lastLoginAtUtc: null } };
  const store = useSoftwareStore();
  const catalog: SoftwareCatalog = {
    manifest: [{ id: "test", displayName: "Test", version: "2.0.0", type: "zip", installDir: "test",
      downloadUrl: "", sha256: "", postInstall: [], dependencies: [], serviceName: "", silentArgs: "", note: "", default: false }],
    defaults: [], installed: [{ id: "1", componentId: "test", displayName: "Test", version, status, updateAvailable,
      versionCheckSupported: true, versionCheckFailed: false, type: "Zip", installDir: "test", serviceName: "", lastError: "", generatedPassword: "",
      isSystemDetected: false, detectionSource: "", availableVersion: "", installedAtUtc: "2026-01-01T00:00:00Z" }]
  };
  store.catalog = catalog; store.loaded = true;
  vi.mocked(apiClient.post).mockResolvedValue({ data: { data: catalog } });
  const renderer = createRenderer<object, object>({
    createElement: () => ({}), createText: () => ({}), createComment: () => ({}),
    insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {},
    parentNode: () => null, nextSibling: () => null
  });
  app = renderer.createApp({ ...SoftwareView, render: () => h("section") } as Component); app.use(pinia); app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: State }).setupState;
  state.activeWorkspace = "components"; await nextTick();
  // Vitest compiles the SFC template for SSR. Render that real template against the
  // mounted setup state, so version fallback and retry-button regressions are covered.
  return { state, text: () => renderToString(createSSRApp({ ...SoftwareView, setup: () => state } as Component)) };
}
afterEach(() => { app?.unmount(); app = undefined; vi.resetAllMocks(); });

it.each(["failed", "unsupported"])("does not claim everything is current when a version check is %s", async (kind) => {
  const view = await mount("Installed", "1.0.0", true);
  const item = useSoftwareStore().catalog!.installed[0]!;
  item.versionCheckFailed = kind === "failed";
  item.versionCheckSupported = kind !== "unsupported";
  await view.state.checkForUpdates();
  expect(view.state.notice).toContain('updatesIncomplete{"count":1}');
  expect(view.state.notice).toContain('updatesFound{"count":1}');
  expect(view.state.notice).not.toContain("updatesNone");
  expect(view.state.noticeError).toBe(true);
  const html = await view.text();
  expect(html).toContain(kind === "failed" ? "versionCheckFailed" : "versionCheckUnsupported");
  expect(html).toContain("newVersion");
});

it("does not count externally managed software as a failed panel check", async () => {
  const { state } = await mount("Installed", "1.0.0");
  const item = useSoftwareStore().catalog!.installed[0]!;
  item.versionCheckSupported = false; item.isSystemDetected = true;
  await state.checkForUpdates();
  expect(state.noticeError).toBe(false);
});

it("shows unknown installed version and a failure summary instead of the target version or all-current claim", async () => {
  const view = await mount("Failed");
  await view.state.checkForUpdates(); await nextTick();
  expect(view.state.notice).toContain('updatesWithFailures{"failed":1,"count":0}');
  expect(view.state.noticeError).toBe(true);
  const html = await view.text();
  expect(html).toContain("installedVersionUnknown");
  expect(html).not.toContain("2.0.0");
  expect(html).toContain("retryInstall");
});

it("retains the previous version after failure before replacement", async () => {
  const view = await mount("Failed", "1.0.0");
  const html = await view.text();
  expect(html).toContain("1.0.0");
  expect(html).not.toContain("2.0.0");
});

it("still reports no updates for a healthy current installation", async () => {
  const { state } = await mount("Installed", "2.0.0");
  await state.checkForUpdates();
  expect(state.notice).toBe("panel.software.updatesNone");
  expect(state.noticeError).toBe(false);
});

it("still counts updates for healthy older installations", async () => {
  const { state } = await mount("Installed", "1.0.0", true);
  await state.checkForUpdates();
  expect(state.notice).toContain('updatesFound{"count":1}');
  expect(state.noticeError).toBe(false);
});

it("reports both failed attempts and available updates", async () => {
  const { state } = await mount("Failed");
  const store = useSoftwareStore();
  store.catalog!.installed.push({ ...store.catalog!.installed[0], componentId: "other", status: "Installed", updateAvailable: true });
  await state.checkForUpdates();
  expect(state.notice).toContain('updatesWithFailures{"failed":1,"count":1}');
  expect(state.noticeError).toBe(true);
});

it.each([false, true])("refreshes failed install records immediately (default stack: %s)", async (defaults) => {
  await mount("Installed", "1.0.0");
  const store = useSoftwareStore();
  const error = new Error("installation failed");
  vi.mocked(apiClient.post).mockRejectedValue(error);
  const failed = { ...store.catalog!, installed: [{ ...store.catalog!.installed[0], status: "Failed" }] };
  vi.mocked(apiClient.get).mockResolvedValue({ data: { success: true, data: failed } });
  await expect(defaults ? store.installDefaultStack() : store.installComponent("test", true)).rejects.toBe(error);
  expect(store.failedCount).toBe(1);
  expect(store.installingComponentId).toBe("");
  expect(store.installingDefaults).toBe(false);
});

it("keeps the installation error when the subsequent catalog refresh also fails", async () => {
  await mount("Installed", "1.0.0");
  const error = new Error("installation failed");
  vi.mocked(apiClient.post).mockRejectedValue(error);
  vi.mocked(apiClient.get).mockRejectedValue(new Error("network down"));
  await expect(useSoftwareStore().installComponent("test", true)).rejects.toBe(error);
});
