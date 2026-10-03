import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { createRenderer, h, nextTick, ssrContextKey, type App, type Component } from "vue";
import SiteHostingView from "./SiteHostingView.vue";
import { getSiteHostingSettings, updateSiteHostingSettings, runComposerInstall, type SiteHostingSettings } from "../api/siteHosting";
import { getQuota, setQuota, removeQuota, installFsrm, type SiteDiskQuota } from "../api/quota";
import { getGitDeployment, configureGitDeployment, deployGit, type GitDeployment } from "../api/git";
import { updateDocumentRoot, enableFrameworkRewrite, repairSitePermissions } from "../api/sites";
import { useSitesStore } from "../stores/sites";
import { invalidateSessionRequests } from "../api/client";

vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key }) }));
vi.mock("../api/siteHosting", async original => ({ ...await original<object>(), getSiteHostingSettings: vi.fn(), updateSiteHostingSettings: vi.fn(), runComposerInstall: vi.fn() }));
vi.mock("../api/quota", () => ({ getQuota: vi.fn(), setQuota: vi.fn(), removeQuota: vi.fn(), installFsrm: vi.fn() }));
vi.mock("../api/git", () => ({ getGitDeployment: vi.fn(), configureGitDeployment: vi.fn(), deployGit: vi.fn() }));
vi.mock("../api/sites", async original => ({ ...await original<object>(), updateDocumentRoot: vi.fn(), enableFrameworkRewrite: vi.fn(), repairSitePermissions: vi.fn() }));

function settings(site = "a.test"): SiteHostingSettings {
  return { siteName: site, redirectUrl: `https://${site}`, maximumConnections: 10, maximumBandwidthBytesPerSecond: 0,
    directoryBrowsingEnabled: false, detailedErrorsEnabled: false, staticCompressionEnabled: true, dynamicCompressionEnabled: false,
    maximumUploadBytes: 31_457_280, hotlinkProtectionEnabled: false, hstsEnabled: false, hstsMaxAgeSeconds: 31_536_000,
    hstsIncludeSubDomains: false, hstsPreload: false, deniedIpAddresses: [], allowedReferrerHosts: [], protectedExtensions: [],
    defaultDocuments: ["index.php"], customErrorPages: [{ statusCode: 404, path: `/${site}.html` }], mimeTypes: [] };
}
function quota(site = "a.test", enabled = true): SiteDiskQuota {
  return { siteName: site, path: `C:/sites/${site}`, fsrmAvailable: true, quotaEnabled: enabled, limitBytes: 20 * 1_048_576, usedBytes: 100, softLimit: true };
}
function git(site = "a.test", configured = true): GitDeployment {
  return { siteName: site, configured, repositoryUrl: configured ? `https://git.test/${site}` : "", branch: configured ? "production" : "", deployPath: "", lastCommit: "", lastDeployUtc: null, lastDeployStatus: "", webhookPath: "/webhook", webhookSecret: site, webhookHeaderName: "X-Hook" };
}
let app: App | undefined;
beforeEach(() => {
  vi.mocked(getSiteHostingSettings).mockImplementation(async site => settings(site));
  vi.mocked(getQuota).mockImplementation(async site => quota(site));
  vi.mocked(getGitDeployment).mockImplementation(async site => git(site));
  vi.mocked(updateSiteHostingSettings).mockImplementation(async site => settings(site));
});
afterEach(() => { app?.unmount(); app = undefined; vi.resetAllMocks(); });
function deferred<T>() { let resolve!: (value: T) => void, reject!: (error: unknown) => void; const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; }
type State = {
  settingsLoaded: boolean; busy: boolean; installingFsrm: boolean; form: { redirectUrl: string }; notice: string;
  documentRoot: string; errorPagesText: string; composerOutput: string; gitOutput: string; showLegacyWebhook: boolean;
  quota: SiteDiskQuota | null; quotaForm: { limitMegabytes: number; softLimit: boolean };
  git: GitDeployment | null; gitForm: { repositoryUrl: string; branch: string };
  load(): Promise<void>; save(): Promise<void>; saveDocumentRoot(): Promise<void>; enableRewrite(): Promise<void>;
  repairPermissions(): Promise<void>; runComposer(): Promise<void>; installFsrmRole(): Promise<void>;
  saveSiteQuota(): Promise<void>; removeSiteQuota(): Promise<void>; saveGit(): Promise<void>; deploySite(): Promise<void>;
};
async function flush() { for (let i = 0; i < 12; i++) await nextTick(); }
async function mount() {
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  const pinia = createPinia(); setActivePinia(pinia); useSitesStore().selectedSiteName = "a.test";
  app = renderer.createApp({ ...SiteHostingView, render: () => h("section") } as Component).use(pinia); app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: State }).setupState; await flush(); return state;
}
async function selectSite(site: string) { useSitesStore().selectedSiteName = site; await flush(); }

it("hides old settings and credentials as soon as another site starts loading", async () => {
  const state = await mount(), pending = deferred<SiteHostingSettings>(); state.showLegacyWebhook = true;
  state.composerOutput = "a.test output"; state.gitOutput = "a.test deployment";
  vi.mocked(getSiteHostingSettings).mockReturnValueOnce(pending.promise); await selectSite("b.test");
  expect(state.settingsLoaded).toBe(false); expect(state.git).toBeNull(); expect(state.quota).toBeNull();
  expect(state.composerOutput).toBe(""); expect(state.gitOutput).toBe(""); expect(state.showLegacyWebhook).toBe(false);
  await state.save(); expect(updateSiteHostingSettings).not.toHaveBeenCalled();
  pending.resolve(settings("b.test")); await flush(); expect(state.settingsLoaded).toBe(true);
});
it("ignores an old settings response without continuing its quota and Git lookups", async () => {
  const first = deferred<SiteHostingSettings>(), second = deferred<SiteHostingSettings>();
  vi.mocked(getSiteHostingSettings).mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise);
  const state = await mount(); await selectSite("b.test"); first.resolve(settings()); await flush();
  expect(state.settingsLoaded).toBe(false); expect(state.busy).toBe(true); expect(getQuota).not.toHaveBeenCalled(); expect(getGitDeployment).not.toHaveBeenCalled();
  second.resolve(settings("b.test")); await flush(); expect(state.form.redirectUrl).toBe("https://b.test");
});
it("ignores old quota results and does not continue into another site's Git load", async () => {
  const pending = deferred<SiteDiskQuota>(); vi.mocked(getQuota).mockReturnValueOnce(pending.promise);
  const state = await mount(); await selectSite("b.test"); pending.resolve(quota()); await flush();
  expect(state.quota?.siteName).toBe("b.test"); expect(getGitDeployment).toHaveBeenCalledTimes(1); expect(getGitDeployment).toHaveBeenCalledWith("b.test");
});
it("ignores old Git credentials after a later site has loaded", async () => {
  const pending = deferred<GitDeployment>(); vi.mocked(getGitDeployment).mockReturnValueOnce(pending.promise);
  const state = await mount(); await selectSite("b.test"); pending.resolve(git()); await flush();
  expect(state.git?.siteName).toBe("b.test"); expect(state.gitForm.repositoryUrl).toBe("https://git.test/b.test");
});
it("clears another site's Git and quota form values when the new site is unconfigured", async () => {
  const state = await mount(); vi.mocked(getGitDeployment).mockResolvedValueOnce(git("b.test", false)); vi.mocked(getQuota).mockResolvedValueOnce(quota("b.test", false));
  await selectSite("b.test"); expect(state.gitForm).toEqual({ repositoryUrl: "", branch: "main" }); expect(state.quotaForm).toEqual({ limitMegabytes: 1024, softLimit: false });
});
it("does not continue reading or publish loaded state after unmount", async () => {
  const pending = deferred<SiteHostingSettings>(); vi.mocked(getSiteHostingSettings).mockReturnValueOnce(pending.promise);
  const state = await mount(); app!.unmount(); app = undefined; pending.resolve(settings()); await flush();
  expect(getQuota).not.toHaveBeenCalled(); expect(state.settingsLoaded).toBe(false);
});
const writes = [
  ["save", updateSiteHostingSettings, settings()], ["runComposer", runComposerInstall, { exitCode: 0, output: "a.test output", error: "" }],
  ["saveSiteQuota", setQuota, quota()], ["removeSiteQuota", removeQuota, quota("a.test", false)], ["installFsrmRole", installFsrm, quota()],
  ["saveGit", configureGitDeployment, git()], ["deploySite", deployGit, { success: true, deployment: git(), output: "a.test deploy" }],
  ["enableRewrite", enableFrameworkRewrite, undefined], ["repairPermissions", repairSitePermissions, { state: "granted", detail: "" }]
] as const;
it.each(writes)("isolates %s completion and rejects duplicate dispatch", async (method, api, result) => {
  const state = await mount(), pending = deferred<unknown>(); vi.mocked(api).mockReturnValueOnce(pending.promise as never);
  const writing = state[method](); await state[method](); expect(api).toHaveBeenCalledTimes(1);
  await selectSite("b.test"); state.errorPagesText = "404 /b-draft.html"; state.notice = "b.test notice";
  pending.resolve(result); await writing;
  expect(state.errorPagesText).toBe("404 /b-draft.html"); expect(state.notice).toBe("b.test notice");
  expect(state.quota?.siteName).toBe("b.test"); expect(state.git?.siteName).toBe("b.test"); expect(state.composerOutput).toBe(""); expect(state.gitOutput).toBe("");
});
it("does not refresh global site state after an old document-root update finishes", async () => {
  const state = await mount(), pending = deferred<Awaited<ReturnType<typeof updateDocumentRoot>>>();
  const fetch = vi.spyOn(useSitesStore(), "fetchSites").mockResolvedValue([]); state.documentRoot = "C:/sites/a/public";
  vi.mocked(updateDocumentRoot).mockReturnValueOnce(pending.promise); const writing = state.saveDocumentRoot();
  await selectSite("b.test"); pending.resolve(undefined as never); await writing;
  expect(updateDocumentRoot).toHaveBeenCalledWith("a.test", "C:/sites/a/public"); expect(fetch).not.toHaveBeenCalled(); expect(state.notice).toBe("");
});
it("preserves normal read and settings-save behavior", async () => {
  const state = await mount(); expect(state.settingsLoaded).toBe(true); expect(state.busy).toBe(false);
  state.form.redirectUrl = "https://target.test"; await state.save();
  expect(updateSiteHostingSettings).toHaveBeenCalledWith("a.test", expect.objectContaining({ redirectUrl: "https://target.test", defaultDocuments: ["index.php"] }));
  expect(state.notice).toBe("panel.hosting.saved"); expect(state.busy).toBe(false);
});
it("does not apply a response from before leaving and returning to the same site", async () => {
  const state = await mount(), pending = deferred<SiteHostingSettings>(); vi.mocked(updateSiteHostingSettings).mockReturnValueOnce(pending.promise);
  const saving = state.save(); await selectSite("b.test"); await selectSite("a.test"); state.errorPagesText = "404 /new-visit.html";
  pending.resolve(settings()); await saving; expect(state.errorPagesText).toBe("404 /new-visit.html"); expect(state.notice).toBe("");
});
it("does not unlock another site's operation when an older write finishes", async () => {
  const state = await mount(), first = deferred<SiteHostingSettings>(), second = deferred<SiteHostingSettings>();
  vi.mocked(updateSiteHostingSettings).mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise);
  const initial = state.save(); await selectSite("b.test"); const latest = state.save(); first.resolve(settings()); await initial;
  expect(state.busy).toBe(true); second.resolve(settings("b.test")); await latest; expect(state.busy).toBe(false);
});
it("does not report another site's write failure", async () => {
  const state = await mount(), pending = deferred<SiteHostingSettings>(); vi.mocked(updateSiteHostingSettings).mockReturnValueOnce(pending.promise);
  const writing = state.save(); await selectSite("b.test"); pending.reject(new Error("a.test write failed")); await writing;
  expect(state.notice).toBe(""); expect(state.busy).toBe(false);
});
it("does not continue a settings read after the authentication session changes", async () => {
  const pending = deferred<SiteHostingSettings>(); vi.mocked(getSiteHostingSettings).mockReturnValueOnce(pending.promise);
  const state = await mount(); invalidateSessionRequests(); pending.resolve(settings()); await flush();
  expect(getQuota).not.toHaveBeenCalled(); expect(state.settingsLoaded).toBe(false);
});
it("keeps writes blocked after a failed settings read and recovers on refresh", async () => {
  vi.mocked(getSiteHostingSettings).mockRejectedValueOnce(new Error("read failed")); const state = await mount();
  expect(state.settingsLoaded).toBe(false); expect(state.busy).toBe(false); expect(state.notice).toBe("common.unexpected_error");
  await state.save(); expect(updateSiteHostingSettings).not.toHaveBeenCalled(); await state.load();
  expect(state.settingsLoaded).toBe(true); expect(state.notice).toBe("");
});
it("blocks installation and other writes when site selection is cleared", async () => {
  const state = await mount(); await selectSite(""); await state.installFsrmRole(); await state.save();
  expect(installFsrm).not.toHaveBeenCalled(); expect(updateSiteHostingSettings).not.toHaveBeenCalled(); expect(state.settingsLoaded).toBe(false);
});
it("preserves successful quota and Git operations on the current site", async () => {
  const state = await mount(); state.quotaForm.limitMegabytes = 400; state.quotaForm.softLimit = false;
  vi.mocked(setQuota).mockResolvedValueOnce({ ...quota(), limitBytes: 400 * 1_048_576, softLimit: false }); await state.saveSiteQuota();
  expect(setQuota).toHaveBeenCalledWith("a.test", 400, false); expect(state.quota?.limitBytes).toBe(400 * 1_048_576);
  state.gitForm.repositoryUrl = "https://git.test/new"; state.gitForm.branch = "release";
  vi.mocked(configureGitDeployment).mockResolvedValueOnce({ ...git(), repositoryUrl: "https://git.test/new", branch: "release" }); await state.saveGit();
  expect(configureGitDeployment).toHaveBeenCalledWith("a.test", "https://git.test/new", "release", false);
  expect(state.git?.branch).toBe("release"); expect(state.notice).toBe("panel.hosting.gitSaved"); expect(state.busy).toBe(false);
});
