import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { createRenderer, h, nextTick, ssrContextKey, type App, type Component } from "vue";
import SiteHtaccessView from "./SiteHtaccessView.vue";
import { getHtaccessReport, inspectHtaccess, validateHtaccess, type HtaccessSiteReport, type HtaccessParseResult } from "../api/htaccess";
import { getFileWorkspace, saveFile, type FileWorkspace } from "../api/files";
import { useSitesStore } from "../stores/sites";
const confirm = vi.hoisted(() => vi.fn());
vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => `translated:${key}` }) }));
vi.mock("../components/ConfirmDialog.vue", () => ({ useConfirm: () => ({ confirm }) }));
vi.mock("../api/htaccess", async original => ({ ...await original<object>(), getHtaccessReport: vi.fn(), inspectHtaccess: vi.fn(), validateHtaccess: vi.fn() }));
vi.mock("../api/files", async original => ({ ...await original<object>(), getFileWorkspace: vi.fn(), saveFile: vi.fn() }));
const valid = { usable: true, diagnostics: [] } as unknown as HtaccessParseResult;
function report(siteName: string): HtaccessSiteReport { return { siteName, mode: "observe", documentRoot: `C:/sites/${siteName}`, inspection: null } as unknown as HtaccessSiteReport; }
function workspace(site = "a.test"): FileWorkspace { return { physicalPath: `C:/sites/${site}` } as FileWorkspace; }
let app: App | undefined;
beforeEach(() => { vi.mocked(getHtaccessReport).mockImplementation(async site => report(site)); vi.mocked(validateHtaccess).mockResolvedValue(valid); vi.mocked(getFileWorkspace).mockImplementation(async path => workspace(path?.slice(6))); confirm.mockResolvedValue(true); });
afterEach(() => { app?.unmount(); app = undefined; vi.clearAllMocks(); });
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(yes => { resolve = yes; }); return { promise, resolve }; }
type State = { report: HtaccessSiteReport | null; busy: boolean; draft: string; draftPath: string; notice: string; saveDraft(): Promise<void> };
async function flush() { for (let i = 0; i < 8; i++) await nextTick(); }
async function mount() {
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  const pinia = createPinia(); setActivePinia(pinia); useSitesStore().selectedSiteName = "a.test";
  app = renderer.createApp({ ...SiteHtaccessView, render: () => h("section") } as Component).use(pinia); app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: State }).setupState; await flush(); state.draft = "RewriteEngine On"; return state;
}
async function selectSite(name: string) { useSitesStore().selectedSiteName = name; await flush(); }
it("does not save another site's htaccess after validation completes late", async () => {
  const state = await mount(), pending = deferred<HtaccessParseResult>(); vi.mocked(validateHtaccess).mockReturnValueOnce(pending.promise);
  const saving = state.saveDraft(); await selectSite("b.test"); pending.resolve(valid); await saving;
  expect(saveFile).not.toHaveBeenCalled(); expect(getFileWorkspace).not.toHaveBeenCalled();
});
it("does not redirect an accepted fatal-error confirmation to another site", async () => {
  const state = await mount(); vi.mocked(validateHtaccess).mockResolvedValueOnce({ ...valid, usable: false });
  confirm.mockImplementationOnce(async () => { await selectSite("b.test"); return true; });
  await state.saveDraft(); expect(saveFile).not.toHaveBeenCalled();
});
it("writes exactly the content that was validated even if the editor changes while waiting", async () => {
  const state = await mount(), pending = deferred<FileWorkspace>(); vi.mocked(getFileWorkspace).mockReturnValueOnce(pending.promise);
  const saving = state.saveDraft(); await flush(); state.draft = "RewriteRule [invalid";
  pending.resolve(workspace()); await saving;
  expect(saveFile).toHaveBeenCalledWith("sites/a.test/.htaccess", "RewriteEngine On");
});
it("does not write after the screen closes during workspace lookup", async () => {
  const state = await mount(), pending = deferred<FileWorkspace>(); vi.mocked(getFileWorkspace).mockReturnValueOnce(pending.promise);
  const saving = state.saveDraft(); await flush(); app!.unmount(); app = undefined; pending.resolve(workspace()); await saving;
  expect(saveFile).not.toHaveBeenCalled();
});
it("ignores old reports while the newly selected site is loading", async () => {
  const first = deferred<HtaccessSiteReport>(), second = deferred<HtaccessSiteReport>(); vi.mocked(getHtaccessReport).mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise);
  const state = await mount(); await selectSite("b.test"); first.resolve(report("a.test")); await flush();
  expect(state.report).toBeNull(); expect(state.busy).toBe(true); second.resolve(report("b.test")); await flush();
});
it("does not carry a draft to another site", async () => {
  const state = await mount(); await selectSite("b.test"); expect(state.draft).toBe("");
});
it("translates API errors instead of displaying raw message keys", async () => {
  const state = await mount(); vi.mocked(validateHtaccess).mockRejectedValueOnce(new Error("fixture failure"));
  await state.saveDraft(); expect(state.notice).toBe("translated:common.unexpected_error");
});
it("does not write if the site changes while looking up the file workspace", async () => {
  const state = await mount(), pending = deferred<FileWorkspace>(); vi.mocked(getFileWorkspace).mockReturnValueOnce(pending.promise);
  const saving = state.saveDraft(); await flush(); await selectSite("b.test"); pending.resolve(workspace()); await saving;
  expect(saveFile).not.toHaveBeenCalled();
});
it("keeps the newer site intact after an already dispatched write finishes", async () => {
  const state = await mount(), pending = deferred<FileWorkspace>(); vi.mocked(saveFile).mockReturnValueOnce(pending.promise);
  const saving = state.saveDraft(); await flush(); await selectSite("b.test"); state.draftPath = "/new-directory";
  pending.resolve(workspace()); await saving;
  expect(state.report?.siteName).toBe("b.test"); expect(state.draftPath).toBe("/new-directory");
  expect(inspectHtaccess).not.toHaveBeenCalled(); expect(getHtaccessReport).toHaveBeenCalledTimes(2);
});
it("preserves application-root versus document-root mapping for a normal save", async () => {
  const state = await mount(); state.report!.documentRoot = "C:/sites/a.test/public"; state.draftPath = "/blog";
  await state.saveDraft();
  expect(saveFile).toHaveBeenCalledWith("sites/a.test/public/blog/.htaccess", "RewriteEngine On");
  expect(inspectHtaccess).toHaveBeenCalledWith("a.test", "blog"); expect(state.draftPath).toBe("/blog");
});
