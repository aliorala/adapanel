import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { createRenderer, h, nextTick, ssrContextKey, type App, type Component } from "vue";
import SiteHostingView from "./SiteHostingView.vue";
import { getSiteHostingSettings, type SiteHostingSettings } from "../api/siteHosting";
import { getQuota, removeQuota, type SiteDiskQuota } from "../api/quota";
import { getGitDeployment } from "../api/git";
import { useSitesStore } from "../stores/sites";
import { useAuthStore } from "../stores/auth";

const confirmAnswer = vi.hoisted(() => ({ value: true }));
vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key }) }));
vi.mock("../router", () => ({ default: { push: vi.fn() } }));
vi.mock("../components/ConfirmDialog.vue", () => ({ useConfirm: () => ({ confirm: async () => confirmAnswer.value }) }));
vi.mock("../api/siteHosting", async original => ({ ...await original<object>(), getSiteHostingSettings: vi.fn(), updateSiteHostingSettings: vi.fn(), runComposerInstall: vi.fn() }));
vi.mock("../api/quota", () => ({ getQuota: vi.fn(), setQuota: vi.fn(), removeQuota: vi.fn(), installFsrm: vi.fn() }));
vi.mock("../api/git", () => ({ getGitDeployment: vi.fn(), configureGitDeployment: vi.fn(), deployGit: vi.fn() }));

const settings = (site: string): SiteHostingSettings => ({ siteName: site, redirectUrl: "", maximumConnections: 10, maximumBandwidthBytesPerSecond: 0,
  directoryBrowsingEnabled: false, detailedErrorsEnabled: false, staticCompressionEnabled: true, dynamicCompressionEnabled: false,
  maximumUploadBytes: 31_457_280, hotlinkProtectionEnabled: false, hstsEnabled: false, hstsMaxAgeSeconds: 31_536_000,
  hstsIncludeSubDomains: false, hstsPreload: false, deniedIpAddresses: [], allowedReferrerHosts: [], protectedExtensions: [],
  defaultDocuments: ["index.php"], customErrorPages: [], mimeTypes: [] });
const quota = (site: string): SiteDiskQuota => ({ siteName: site, path: "C:/s", fsrmAvailable: true, quotaEnabled: true, limitBytes: 1048576, usedBytes: 1, softLimit: false });

type State = { settingsLoaded: boolean; sections: { id: string }[]; removeSiteQuota(): Promise<void> };
let app: App | undefined;
async function flush() { for (let i = 0; i < 12; i++) await nextTick(); }
async function mount(role: string) {
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  const pinia = createPinia(); setActivePinia(pinia); useSitesStore().selectedSiteName = "a.test";
  useAuthStore().session = { isAuthenticated: true, requiresSetupWizard: false, user: { id: "actor", role, userName: "actor", email: "a@example.test", preferredLanguage: "en", isTwoFactorEnabled: false, isActive: true, lastLoginAtUtc: null } } as never;
  app = renderer.createApp({ ...SiteHostingView, render: () => h("section") } as Component).use(pinia); app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: State }).setupState; await flush(); return state;
}
beforeEach(() => {
  confirmAnswer.value = true;
  vi.mocked(getSiteHostingSettings).mockImplementation(async site => settings(site));
  vi.mocked(getQuota).mockImplementation(async site => quota(site));
});
afterEach(() => { app?.unmount(); app = undefined; vi.resetAllMocks(); });

it("loads for a customer without calling the administrator-only Git endpoint and hides staff-only sections", async () => {
  const state = await mount("Customer");
  expect(state.settingsLoaded).toBe(true);
  expect(getGitDeployment).not.toHaveBeenCalled();
  const ids = state.sections.map(section => section.id);
  for (const hidden of ["git", "tools", "traffic", "htaccess"]) expect(ids, hidden).not.toContain(hidden);
  for (const shown of ["general", "documents", "access", "errorPages", "mime", "hsts", "quota"]) expect(ids, shown).toContain(shown);
});

it("keeps Git for the administrator and the staff sections for the reseller", async () => {
  const admin = await mount("Administrator");
  expect(getGitDeployment).toHaveBeenCalledTimes(1);
  expect(admin.sections.map(section => section.id)).toEqual(expect.arrayContaining(["git", "tools", "traffic", "htaccess"]));
  app?.unmount(); vi.mocked(getGitDeployment).mockClear();
  const reseller = await mount("Reseller");
  expect(getGitDeployment).not.toHaveBeenCalled();
  const ids = reseller.sections.map(section => section.id);
  expect(ids).toEqual(expect.arrayContaining(["tools", "traffic", "htaccess"]));
  expect(ids).not.toContain("git");
});

it("asks before removing a disk quota and does nothing when the user declines", async () => {
  const state = await mount("Administrator");
  confirmAnswer.value = false;
  await state.removeSiteQuota(); await flush();
  expect(removeQuota).not.toHaveBeenCalled();
  confirmAnswer.value = true;
  vi.mocked(removeQuota).mockResolvedValue({ ...quota("a.test"), quotaEnabled: false });
  await state.removeSiteQuota(); await flush();
  expect(removeQuota).toHaveBeenCalledTimes(1);
});
