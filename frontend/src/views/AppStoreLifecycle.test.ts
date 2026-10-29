import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { createRenderer, h, nextTick, ssrContextKey, type App, type Component } from "vue";
import AppStoreView from "./AppStoreView.vue";
import { getAppTemplates, installAppTemplate, type AppTemplate, type AppInstallResult } from "../api/store";
import { useSitesStore } from "../stores/sites";
import { useSubscriptionStore } from "../stores/subscription";
import type { SiteRecord } from "../api/sites";
const shared = vi.hoisted(() => ({ epoch: 0, confirm: vi.fn() }));
vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key }) }));
vi.mock("../components/ConfirmDialog.vue", () => ({ useConfirm: () => ({ confirm: shared.confirm }) }));
vi.mock("../api/client", async original => ({ ...await original<object>(), getSessionEpoch: () => shared.epoch }));
vi.mock("../api/store", () => ({ getAppTemplates: vi.fn(), installAppTemplate: vi.fn() }));
type State = { siteMap: Record<string, string>; busyTemplate: string; templates: AppTemplate[]; templatesLoaded: boolean; catalogFailed: boolean; notice: string; noticeError: boolean; sitesUnavailable: boolean; wpAdmin: { adminUrl: string; adminUser: string; adminPassword: string }; refresh(): Promise<void>; install(id: string): Promise<void> };
const template = (id = "wordpress"): AppTemplate => ({ id, displayName: id, runtime: "PHP", category: "CMS", description: "", downloadUrl: "", requiresDatabase: true, defaultRootFolder: "", requirements: [], automaticSetup: [], featured: true });
const sites = () => [{ id: 1, name: "site-a", physicalPath: "C:/sites/a/public", applicationRootPath: "C:/sites/a" }, { id: 2, name: "site-b", physicalPath: "C:/sites/b/public", applicationRootPath: "C:/sites/b" }] as SiteRecord[];
const result = (): AppInstallResult => ({ templateId: "wordpress", adminUrl: "https://site-a.test/wp-admin/", adminUser: "admin", adminPassword: "FixturePassword!" });
const contentError = () => ({ response: { data: { message: "store.site_has_content" } } });
function deferred<T>() { let resolve!: (value: T) => void; let reject!: (value: unknown) => void; const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; }
async function flush() { for (let i = 0; i < 12; i++) await nextTick(); }
let app: App | undefined;
beforeEach(() => { shared.epoch = 0; shared.confirm.mockResolvedValue(false); vi.mocked(getAppTemplates).mockResolvedValue([template(), template("other")]); vi.mocked(installAppTemplate).mockResolvedValue(result()); });
afterEach(() => { app?.unmount(); app = undefined; vi.restoreAllMocks(); vi.resetAllMocks(); });
async function mount() {
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  const pinia = createPinia(); setActivePinia(pinia); const store = useSitesStore(); store.sites = sites(); store.selectedSiteName = "site-a"; store.loaded = true;
  vi.spyOn(store, "fetchSites").mockImplementation(async () => { store.sites = sites(); store.loaded = true; return store.sites; });
  app = renderer.createApp({ ...AppStoreView, render: () => h("section") } as Component).use(pinia); app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: State }).setupState; await flush(); return { state, store };
}
it("does not issue a clean install for a different target after confirmation", async () => {
  const { state } = await mount(); vi.mocked(installAppTemplate).mockRejectedValueOnce(contentError()); shared.confirm.mockImplementationOnce(async () => { state.siteMap.wordpress = "site-b"; return true; }); await state.install("wordpress"); expect(installAppTemplate).toHaveBeenCalledOnce();
});
it("invalidates clean-install consent after switching away and back", async () => {
  const { state } = await mount(); vi.mocked(installAppTemplate).mockRejectedValueOnce(contentError()); shared.confirm.mockImplementationOnce(async () => { state.siteMap.wordpress = "site-b"; state.siteMap.wordpress = "site-a"; return true; }); await state.install("wordpress"); expect(installAppTemplate).toHaveBeenCalledOnce();
});
it("does not offer a cleanup dialog when the original target changed before the error", async () => {
  const { state } = await mount(), pending = deferred<AppInstallResult>(); vi.mocked(installAppTemplate).mockReturnValueOnce(pending.promise); const installing = state.install("wordpress"); state.siteMap.wordpress = "site-b"; pending.reject(contentError()); await installing; expect(shared.confirm).not.toHaveBeenCalled();
});
for (const change of ["unmount", "session", "readonly", "missing-site"] as const) {
  it(`rejects pending cleanup consent after ${change}`, async () => {
    const { state, store } = await mount(); vi.mocked(installAppTemplate).mockRejectedValueOnce(contentError()); shared.confirm.mockImplementationOnce(async () => {
      if (change === "unmount") { app!.unmount(); app = undefined; } else if (change === "session") shared.epoch++; else if (change === "readonly") useSubscriptionStore().usage = { isSuspended: true } as never; else store.sites = [];
      return true;
    }); await state.install("wordpress"); expect(installAppTemplate).toHaveBeenCalledOnce();
  });
}
it("cleans only the same confirmed site and template", async () => {
  const { state } = await mount(); vi.mocked(installAppTemplate).mockRejectedValueOnce(contentError()); shared.confirm.mockResolvedValueOnce(true); await state.install("wordpress");
  expect(shared.confirm.mock.calls[0][0]).toMatchObject({ requireText: "site-a" }); expect(installAppTemplate).toHaveBeenNthCalledWith(2, { templateId: "wordpress", siteName: "site-a", targetPath: "", cleanInstall: true }); expect(state.wpAdmin.adminPassword).toBe("FixturePassword!");
});
it("does not retry a cancelled clean install", async () => {
  const { state } = await mount(); vi.mocked(installAppTemplate).mockRejectedValueOnce(contentError()); await state.install("wordpress"); expect(installAppTemplate).toHaveBeenCalledOnce(); expect(state.busyTemplate).toBe("");
});
it("shows files installed with manual WordPress setup required as an incomplete outcome", async () => {
  const { state } = await mount();
  vi.mocked(installAppTemplate).mockResolvedValueOnce({ templateId: "wordpress", adminUrl: "", adminUser: "", adminPassword: "", requiresManualSetup: true });
  await state.install("wordpress");
  expect(state.notice).toBe("panel.appStore.manualSetupRequired");
  expect(state.noticeError).toBe(true);
  expect(state.wpAdmin.adminPassword).toBe("");
});
it("does not overlap ordinary installations", async () => {
  const { state } = await mount(), pending = deferred<AppInstallResult>(); vi.mocked(installAppTemplate).mockReturnValueOnce(pending.promise); const installing = state.install("wordpress"); await state.install("wordpress"); await state.install("other"); expect(installAppTemplate).toHaveBeenCalledOnce(); pending.resolve(result()); await installing;
});
it("keeps the common operation lock while cleanup consent is open", async () => {
  const { state, store } = await mount(), pending = deferred<boolean>(); vi.mocked(installAppTemplate).mockRejectedValueOnce(contentError()); shared.confirm.mockReturnValueOnce(pending.promise); const installing = state.install("wordpress"); await flush(); const readCount = vi.mocked(store.fetchSites).mock.calls.length;
  await state.install("other"); await state.refresh(); expect(installAppTemplate).toHaveBeenCalledOnce(); expect(store.fetchSites).toHaveBeenCalledTimes(readCount); pending.resolve(false); await installing;
});
for (const invalid of ["unknown-template", "unknown-site", "empty-site", "readonly"] as const) {
  it(`does not install for ${invalid}`, async () => {
    const { state } = await mount(); if (invalid === "unknown-site") state.siteMap.wordpress = "unlisted"; if (invalid === "empty-site") state.siteMap.wordpress = ""; if (invalid === "readonly") useSubscriptionStore().usage = { isSuspended: true } as never;
    await state.install(invalid === "unknown-template" ? "invalid" : "wordpress"); expect(installAppTemplate).not.toHaveBeenCalled();
  });
}
it("does not deploy until the initial catalog has been measured", async () => {
  const pending = deferred<AppTemplate[]>(); vi.mocked(getAppTemplates).mockReturnValueOnce(pending.promise); const { state } = await mount(); state.siteMap.wordpress = "site-a"; await state.install("wordpress"); expect(installAppTemplate).not.toHaveBeenCalled(); pending.resolve([template()]); await flush();
});
it("does not use a stale catalog after a failed refresh", async () => {
  const { state } = await mount(); vi.mocked(getAppTemplates).mockRejectedValueOnce(new Error("offline")); await state.refresh(); await state.install("wordpress"); expect(installAppTemplate).not.toHaveBeenCalled(); expect(state.catalogFailed).toBe(true);
});
it("does not use a stale site list after a failed refresh", async () => {
  const { state, store } = await mount(); vi.mocked(store.fetchSites).mockRejectedValueOnce(new Error("offline")); await state.refresh(); await state.install("wordpress"); expect(installAppTemplate).not.toHaveBeenCalled(); expect(state.sitesUnavailable).toBe(true);
});
it("keeps the newest catalog when reads resolve out of order", async () => {
  const { state } = await mount(), pending = deferred<AppTemplate[]>(); vi.mocked(getAppTemplates).mockReturnValueOnce(pending.promise); const old = state.refresh(); vi.mocked(getAppTemplates).mockResolvedValueOnce([template("new")]); await state.refresh(); pending.resolve([template("old")]); await old; expect(state.templates[0].id).toBe("new");
});
it("shows the catalog even if site discovery fails", async () => {
  const { state, store } = await mount(); vi.mocked(store.fetchSites).mockRejectedValueOnce({ response: { data: { message: "sites.iis_not_available" } } }); await state.refresh(); expect(state.templatesLoaded).toBe(true); expect(state.sitesUnavailable).toBe(true);
});
it("does not publish old site's credentials on the newly selected target", async () => {
  const { state } = await mount(), pending = deferred<AppInstallResult>(); vi.mocked(installAppTemplate).mockReturnValueOnce(pending.promise); const installing = state.install("wordpress"); state.siteMap.wordpress = "site-b"; pending.resolve(result()); await installing; expect(state.wpAdmin.adminPassword).toBe(""); expect(state.notice).toBe("");
});
it("clears displayed secrets when their target changes", async () => {
  const { state } = await mount(); await state.install("wordpress"); state.siteMap.wordpress = "site-b"; await flush(); expect(state.wpAdmin.adminPassword).toBe("");
});
it("clears displayed secrets when the screen closes", async () => {
  const { state } = await mount(); await state.install("wordpress"); app!.unmount(); app = undefined; expect(state.wpAdmin.adminPassword).toBe("");
});
it("does not refresh or publish secrets after an installation's screen closes", async () => {
  const { state, store } = await mount(), pending = deferred<AppInstallResult>(); vi.mocked(installAppTemplate).mockReturnValueOnce(pending.promise); const installing = state.install("wordpress"), reads = vi.mocked(store.fetchSites).mock.calls.length; app!.unmount(); app = undefined; pending.resolve(result()); await installing; expect(state.wpAdmin.adminPassword).toBe(""); expect(store.fetchSites).toHaveBeenCalledTimes(reads);
});
it("does not publish or follow up after an installation's session expires", async () => {
  const { state, store } = await mount(), pending = deferred<AppInstallResult>(); vi.mocked(installAppTemplate).mockReturnValueOnce(pending.promise); const installing = state.install("wordpress"), reads = vi.mocked(store.fetchSites).mock.calls.length; shared.epoch++; pending.resolve(result()); await installing; expect(state.wpAdmin.adminPassword).toBe(""); expect(store.fetchSites).toHaveBeenCalledTimes(reads);
});
it("does not claim success or expose credentials from the wrong template result", async () => {
  const { state } = await mount(); vi.mocked(installAppTemplate).mockResolvedValueOnce({ ...result(), templateId: "other" }); await state.install("wordpress"); expect(state.notice).toBe("panel.appStore.resultUnconfirmed"); expect(state.noticeError).toBe(true); expect(state.wpAdmin.adminPassword).toBe("");
});
it("preserves installation credentials while reporting a subsequent discovery failure", async () => {
  const { state, store } = await mount(); vi.mocked(store.fetchSites).mockRejectedValueOnce(new Error("discovery failed")); await state.install("wordpress"); expect(state.wpAdmin.adminPassword).toBe("FixturePassword!"); expect(state.notice).toContain("panel.appStore.installedRefreshFailed"); expect(state.noticeError).toBe(true); expect(installAppTemplate).toHaveBeenCalledOnce();
});
for (const field of ["id", "physicalPath", "applicationRootPath"] as const) {
  it(`invalidates cleanup if the target site's ${field} changes while confirmation is open`, async () => {
    const { state, store } = await mount(); vi.mocked(installAppTemplate).mockRejectedValueOnce(contentError()); shared.confirm.mockImplementationOnce(async () => {
      store.sites[0] = { ...store.sites[0], [field]: field === "id" ? 99 : "C:/replacement" }; return true;
    }); await state.install("wordpress"); expect(installAppTemplate).toHaveBeenCalledOnce();
  });
}
it("invalidates cleanup when the target root changes away and back", async () => {
  const { state, store } = await mount(); vi.mocked(installAppTemplate).mockRejectedValueOnce(contentError()); shared.confirm.mockImplementationOnce(async () => {
    const original = store.sites[0].physicalPath; store.sites[0].physicalPath = "C:/replacement"; store.sites[0].physicalPath = original; return true;
  }); await state.install("wordpress"); expect(installAppTemplate).toHaveBeenCalledOnce();
});
it("does not install while a shared site refresh is pending or has failed", async () => {
  const { state, store } = await mount(); store.loading = true; await state.install("wordpress"); store.loading = false; store.loaded = false; await state.install("wordpress"); expect(installAppTemplate).not.toHaveBeenCalled();
});
it("clears a completed installation notice when changing its credential target", async () => {
  const { state } = await mount(); await state.install("wordpress"); state.siteMap.wordpress = "site-b"; expect(state.notice).toBe("");
});
it("clears credentials when the post-install discovery no longer contains the original site", async () => {
  const { state, store } = await mount(); vi.mocked(store.fetchSites).mockImplementationOnce(async () => { store.sites = [sites()[1]]; return store.sites; }); await state.install("wordpress"); expect(state.wpAdmin.adminPassword).toBe(""); expect(state.notice).toBe("");
});
