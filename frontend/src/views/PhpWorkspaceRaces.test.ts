import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { createRenderer, h, nextTick, ssrContextKey, type App, type Component } from "vue";
import PhpView from "./PhpView.vue";
import { getSitePhpSettings, updateSitePhpSettings, type SitePhpSettings } from "../api/php";
import { useSitesStore } from "../stores/sites";
import { usePhpStore } from "../stores/php";
import { useAuthStore } from "../stores/auth";
import { SoftwareRemovalIncompleteError, useSoftwareStore } from "../stores/software";

vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key }) }));
vi.mock("../api/php", async original => ({ ...await original<object>(), getSitePhpSettings: vi.fn(), updateSitePhpSettings: vi.fn() }));
vi.mock("../components/ConfirmDialog.vue", () => ({ useConfirm: () => ({ confirm: async (options: { action?: () => Promise<unknown> }) => { await options.action?.(); return true; } }) }));
let app: App | undefined;
beforeEach(() => { vi.mocked(getSitePhpSettings).mockImplementation(async site => settings(site)); });
afterEach(() => { app?.unmount(); app = undefined; vi.restoreAllMocks(); vi.clearAllMocks(); });
function settings(siteName: string, memoryLimit = "256M"): SitePhpSettings { return { siteName, memoryLimit, maxExecutionTime: 120, uploadMaxFilesize: "64M", postMaxSize: "64M", displayErrors: false, openBasedirEnabled: true, configured: true }; }
function deferred<T>() { let resolve!: (value: T) => void, reject!: (error: unknown) => void; const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; }
type State = { settings: SitePhpSettings; settingsLoaded: boolean; settingsError: string; savingSettings: boolean; notice: string; noticeError: boolean; saveSettings(): Promise<void>; removeVersion(id: string): Promise<void> };
async function flush() { for (let i = 0; i < 8; i++) await nextTick(); }
async function mount() {
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  const pinia = createPinia(); setActivePinia(pinia);
  useAuthStore().session = { isAuthenticated: true, requiresSetupWizard: false, user: { id: 'admin', userName: 'admin', email: 'admin@example.test', role: 'Administrator', preferredLanguage: 'en', isActive: true, isTwoFactorEnabled: false, lastLoginAtUtc: null } };
  useSitesStore().selectedSiteName = "a.test";
  vi.spyOn(useSitesStore(), "fetchSites").mockResolvedValue([]);
  vi.spyOn(usePhpStore(), "fetchVersions").mockImplementation(async () => {
    usePhpStore().versions = [{ componentId: 'php-8.3', displayName: 'PHP 8.3', version: '8.3.20', installDir: 'fixture/php', executablePath: 'fixture/php/php-cgi.exe', status: 'Installed', isFastCgiRegistered: true, installedAtUtc: '2026-10-04T00:00:00Z' }];
    usePhpStore().loaded = true; return usePhpStore().versions;
  });
  vi.spyOn(useSoftwareStore(), "fetchCatalog").mockImplementation(async () => { useSoftwareStore().loaded = true; return { manifest: [], defaults: [], installed: [] }; });
  app = renderer.createApp({ ...PhpView, render: () => h("section") } as Component).use(pinia);
  app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: State }).setupState;
  await flush(); return state;
}
async function selectSite(site: string) { useSitesStore().selectedSiteName = site; await flush(); }

it("preserves partial cleanup instead of announcing the PHP version was removed", async () => {
  const state = await mount();
  vi.spyOn(useSoftwareStore(), "removeComponent").mockRejectedValueOnce(new SoftwareRemovalIncompleteError({ componentId: "php-8.3", warnings: ["locked runtime"] }));
  await state.removeVersion("php-8.3");
  expect(useSoftwareStore().removeComponent).toHaveBeenCalledExactlyOnceWith("php-8.3");
  expect(state.notice).toBe("software.remove.partial"); expect(state.noticeError).toBe(true);
});

it("does not expose old PHP settings while the selected site's request is pending", async () => {
  const first = deferred<SitePhpSettings>(), second = deferred<SitePhpSettings>();
  vi.mocked(getSitePhpSettings).mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise);
  const state = await mount(); await selectSite("b.test");
  first.resolve(settings("a.test", "512M")); await flush();
  expect(state.settingsLoaded).toBe(false);
  second.resolve(settings("b.test", "128M")); await flush();
  expect(state.settings.memoryLimit).toBe("128M"); expect(state.settingsLoaded).toBe(true);
});
it("ignores an old read failure after the new site's settings loaded", async () => {
  const first = deferred<SitePhpSettings>(); vi.mocked(getSitePhpSettings).mockReturnValueOnce(first.promise);
  const state = await mount(); await selectSite("b.test");
  first.reject(new Error("old read failed")); await flush();
  expect(state.settingsError).toBe(""); expect(state.settingsLoaded).toBe(true);
});
it("does not put an old save result into another site's form", async () => {
  const state = await mount(), pending = deferred<SitePhpSettings>();
  vi.mocked(updateSitePhpSettings).mockReturnValueOnce(pending.promise);
  const saving = state.saveSettings(); await selectSite("b.test");
  pending.resolve({ ...settings("a.test", "1024M"), openBasedirEnabled: false }); await saving;
  expect(state.settings.memoryLimit).toBe("256M"); expect(state.settings.openBasedirEnabled).toBe(true); expect(state.notice).toBe("");
});
it("does not dispatch duplicate saves while an earlier save is pending", async () => {
  const state = await mount(), pending = deferred<SitePhpSettings>();
  vi.mocked(updateSitePhpSettings).mockReturnValue(pending.promise);
  const first = state.saveSettings(), second = state.saveSettings();
  pending.resolve(settings("a.test")); await Promise.all([first, second]);
  expect(updateSitePhpSettings).toHaveBeenCalledTimes(1);
});
it("does not apply a read result after the screen unmounts", async () => {
  const pending = deferred<SitePhpSettings>(); vi.mocked(getSitePhpSettings).mockReturnValueOnce(pending.promise);
  const state = await mount(); app!.unmount(); app = undefined;
  pending.resolve(settings("a.test")); await flush(); expect(state.settingsLoaded).toBe(false);
});
