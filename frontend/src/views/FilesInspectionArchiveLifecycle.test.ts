import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createPinia } from "pinia";
import { createRenderer, h, nextTick, ssrContextKey, type App, type Component } from "vue";
import type { AxiosResponse, InternalAxiosRequestConfig } from "axios";
import FilesView from "./FilesView.vue";
import { apiClient, invalidateSessionRequests } from "../api/client";
import { useAuthStore } from "../stores/auth";
import { useSitesStore } from "../stores/sites";
import type { DirectorySizeResult, FileEntry, FileWorkspace } from "../api/files";

const dialogs = vi.hoisted(() => ({ confirm: vi.fn() }));
vi.mock("../router", () => ({ default: { currentRoute: { value: { query: {} } }, push: vi.fn() } }));
vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string, params?: object) => params ? `${key} ${JSON.stringify(params)}` : key }) }));
vi.mock("../components/ConfirmDialog.vue", () => ({ useConfirm: () => ({ confirm: dialogs.confirm }) }));
vi.mock("../components/PromptDialog.vue", () => ({ usePrompt: () => ({ prompt: vi.fn() }) }));
const root = "sites/a.test", archive = `${root}/data.zip`;
const row = (name = "data.zip", directory = false, base = root): FileEntry => ({ path: `${base}/${name}`, name, isDirectory: directory, size: 10, extension: directory ? "" : ".zip", lastModifiedUtc: null });
const folder = (path = root): FileWorkspace => ({ path, parentPath: "sites", displayName: path, physicalPath: "", content: "", isDirectory: true, entries: [row("data.zip", false, path), row("data", true, path)] });
const measuredSize = (path = `${root}/data`, truncated = false): DirectorySizeResult => ({ path, totalBytes: 20, fileCount: 2, directoryCount: 1, truncated, truncatedReasonKey: truncated ? "files.size_truncated_entries" : "" });
const searchResult = (query = "data", truncated = false) => ({ path: root, query, items: [row()], scannedFiles: 2, truncated, truncatedReasonKey: truncated ? "files.search_truncated_files" : "" });
function response(config: InternalAxiosRequestConfig, data: unknown, success = true): AxiosResponse { return { config, data: { success, data }, status: 200, statusText: "OK", headers: {} }; }
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(yes => { resolve = yes; }); return { promise, resolve }; }
async function flush() { for (let i = 0; i < 40; i++) await nextTick(); }
type State = { workspace: FileWorkspace; notice: string; noticeError: boolean; archiveBusy: boolean; searchQuery: string; searchResults: FileEntry[]; searchRan: boolean; searchBusy: boolean; searchError: string; searchTruncated: boolean; searchTruncatedReason: string; searchScannedFiles: number | null; searchSubmittedQuery: string; searchPath: string; sizeBusyPath: string; folderSizes: Record<string, DirectorySizeResult>; clearSearch(): void; runSearch(): Promise<void>; calculateFolderSize(entry: FileEntry): Promise<void>; compress(path: string): Promise<void>; extract(path: string): Promise<void>; refresh(path?: string): Promise<unknown>; openPath(path: string): Promise<unknown> };
let app: App | undefined, calls: InternalAxiosRequestConfig[], read: (config: InternalAxiosRequestConfig) => Promise<AxiosResponse>, write: (config: InternalAxiosRequestConfig) => Promise<AxiosResponse>;
const original = apiClient.defaults.adapter;
const posts = (url: string) => calls.filter(call => call.method === "post" && call.url === url);
beforeEach(() => {
  vi.resetAllMocks(); invalidateSessionRequests(); calls = [];
  vi.stubGlobal("window", { location: { pathname: "/panel" }, addEventListener: vi.fn(), removeEventListener: vi.fn() });
  vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ success: true, data: { requestToken: "fixture" } }), { status: 200 })));
  dialogs.confirm.mockResolvedValue(true);
  read = async config => response(config, folder(config.params.path));
  write = async config => response(config, config.url === "/files/size" ? measuredSize() : config.url === "/files/search" ? searchResult(JSON.parse(String(config.data)).query) : folder());
  apiClient.defaults.adapter = async config => { calls.push(config); return config.url === "/backup" ? response(config, []) : config.method === "get" ? read(config) : write(config); };
});
afterEach(() => { app?.unmount(); app = undefined; apiClient.defaults.adapter = original; invalidateSessionRequests(); vi.unstubAllGlobals(); });
async function mount() {
  const pinia = createPinia(), auth = useAuthStore(pinia), sites = useSitesStore(pinia);
  auth.session = { isAuthenticated: true, requiresSetupWizard: false, user: { id: "admin", role: "Administrator", userName: "admin", email: "admin@example.test", preferredLanguage: "en", isActive: true, isTwoFactorEnabled: false, lastLoginAtUtc: null } };
  sites.sites = [{ name: "a.test" }, { name: "b.test" }] as never; sites.selectedSiteName = "a.test";
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  app = renderer.createApp({ ...FilesView, render: () => h("section") } as Component).use(pinia); app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: State }).setupState; await flush(); state.notice = ""; return { state, auth, sites };
}
it("clears a pending search without later repopulating results or blocking a new search", async () => {
  const { state } = await mount(), gate = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
  write = config => (request = config, gate.promise); state.searchQuery = "data"; const pending = state.runSearch(); await flush();
  state.clearSearch(); expect(state.searchBusy).toBe(false); expect(state.searchQuery).toBe("");
  write = async config => response(config, searchResult("next")); state.searchQuery = "next"; await state.runSearch();
  gate.resolve(response(request, searchResult())); await pending;
  expect(state.searchSubmittedQuery).toBe("next"); expect(state.searchResults).toHaveLength(1); expect(state.searchRan).toBe(true);
});
it("deduplicates the same pending search and exposes its busy state", async () => {
  const { state } = await mount(), gate = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
  write = config => (request = config, gate.promise); state.searchQuery = "data";
  const first = state.runSearch(); await flush(); const second = state.runSearch(); await flush(); const sent = posts("/files/search").length;
  gate.resolve(response(request, searchResult())); await Promise.all([first, second]);
  expect(sent).toBe(1); expect(state.searchBusy).toBe(false); expect(state.searchPath).toBe(root);
});
it("does not present an empty partial search as a complete no-matches result", async () => {
  const { state } = await mount(); state.searchQuery = "data";
  write = async config => response(config, { ...searchResult("data", true), items: [], scannedFiles: 5000 });
  await state.runSearch(); expect(state.searchResults).toEqual([]); expect(state.searchTruncated).toBe(true);
  expect(state.searchTruncatedReason).toBe("files.search_truncated_files"); expect(state.searchScannedFiles).toBe(5000);
});
it("keeps search errors distinct from zero matches and supports retry", async () => {
  const { state } = await mount(); state.searchQuery = "data"; write = async () => { throw new Error("read failed"); };
  await state.runSearch(); expect(state.searchError).not.toBe(""); expect(state.searchRan).toBe(false);
  write = async config => response(config, { ...searchResult(), items: [] }); await state.runSearch();
  expect(state.searchError).toBe(""); expect(state.searchRan).toBe(true); expect(state.searchResults).toEqual([]); expect(state.searchTruncated).toBe(false);
});
it.each(["site", "session", "unmount"])("does not publish an old directory size after %s changes", async context => {
  const { state, auth, sites } = await mount(), gate = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
  write = config => (request = config, gate.promise); const pending = state.calculateFolderSize(row("data", true)); await flush();
  if (context === "site") { sites.selectSite("b.test"); await flush(); }
  else if (context === "session") auth.session = { ...auth.session!, user: { ...auth.session!.user! } };
  else { app!.unmount(); app = undefined; }
  gate.resolve(response(request, measuredSize())); await pending;
  expect(state.folderSizes[root + "/data"]).toBeUndefined(); expect(state.notice).toBe("");
});
it("discards a size measurement from before a neutral folder refresh", async () => {
  const { state } = await mount(), gate = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
  write = config => (request = config, gate.promise); const pending = state.calculateFolderSize(row("data", true)); await flush();
  await state.refresh(); gate.resolve(response(request, measuredSize())); await pending;
  expect(state.folderSizes).toEqual({}); expect(state.notice).toBe("");
});
it("keeps partial size details and accepts a later complete recalculation", async () => {
  const { state } = await mount(); write = async config => response(config, measuredSize(undefined, true));
  await state.calculateFolderSize(row("data", true)); expect(state.folderSizes[root + "/data"].truncated).toBe(true); expect(state.noticeError).toBe(true);
  write = async config => response(config, measuredSize()); await state.calculateFolderSize(row("data", true));
  expect(state.folderSizes[root + "/data"].truncated).toBe(false); expect(state.noticeError).toBe(false);
});
it("invalidates only the requested size during failed recalculation and allows a measured retry", async () => {
  read = async config => response(config, { ...folder(), entries: [...folder().entries, row("other", true)] });
  write = async config => response(config, measuredSize(JSON.parse(String(config.data)).path));
  const { state } = await mount(), target = row("data", true), other = row("other", true);
  await state.calculateFolderSize(target); await state.calculateFolderSize(other);
  expect(state.folderSizes[target.path].totalBytes).toBe(20);
  expect(state.folderSizes[other.path].totalBytes).toBe(20);

  const gate = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
  write = config => (request = config, gate.promise);
  const pending = state.calculateFolderSize(target); await flush();
  const valueWhilePending = state.folderSizes[target.path];
  gate.resolve(response(request, null, false)); await pending;

  expect(valueWhilePending).toBeUndefined();
  expect(state.folderSizes[target.path]).toBeUndefined();
  expect(state.folderSizes[other.path].totalBytes).toBe(20);
  expect(state.sizeBusyPath).toBe(""); expect(state.noticeError).toBe(true);
  write = async config => response(config, { ...measuredSize(target.path), totalBytes: 40 });
  await state.calculateFolderSize(target);
  expect(state.folderSizes[target.path].totalBytes).toBe(40);
  expect(state.folderSizes[other.path].totalBytes).toBe(20);
  expect(state.noticeError).toBe(false);
  expect(posts("/files/size")).toHaveLength(4);
});
it.each(["compress", "extract"] as const)("does not accept a false %s success", async action => {
  const { state } = await mount(); write = async config => response(config, folder(), false);
  await state[action](action === "compress" ? `${root}/data` : archive);
  expect(state.noticeError).toBe(true); expect(state.notice).toContain("panel.files.resultUnconfirmed");
});
it.each(["compress", "extract"] as const)("keeps measured %s output when a pre-publication list GET returns later", async action => {
  const { state } = await mount(), post = deferred<AxiosResponse>(), get = deferred<AxiosResponse>(); let posted!: InternalAxiosRequestConfig, fetched!: InternalAxiosRequestConfig;
  write = config => (posted = config, post.promise); const pending = state[action](action === "compress" ? `${root}/data` : archive); await flush();
  read = config => (fetched = config, get.promise); const refreshing = state.refresh(); await flush();
  const result = { ...folder(), entries: [...folder().entries, row("new-output.txt")] };
  post.resolve(response(posted, result)); await pending; get.resolve(response(fetched, folder())); await refreshing;
  expect(state.workspace.entries.some(entry => entry.name === "new-output.txt")).toBe(true); expect(state.noticeError).toBe(false);
});
it("pins extraction's overwrite confirmation and stops after a site change", async () => {
  const { state, sites } = await mount(), answer = deferred<boolean>(); dialogs.confirm.mockReturnValueOnce(answer.promise);
  const pending = state.extract(archive); await flush();
  expect(posts("/files/extract")).toHaveLength(0); expect(dialogs.confirm).toHaveBeenCalledWith(expect.objectContaining({ tone: "danger", message: expect.stringContaining(root) }));
  sites.selectSite("b.test"); await flush(); answer.resolve(true); await pending; expect(posts("/files/extract")).toHaveLength(0);
});
it("does not dispatch extraction after its confirmation is cancelled", async () => {
  const { state } = await mount(); dialogs.confirm.mockResolvedValueOnce(false); await state.extract(archive);
  expect(posts("/files/extract")).toHaveLength(0); expect(state.archiveBusy).toBe(false);
});
it("keeps normal extraction pinned to the archive and its parent directory", async () => {
  const { state } = await mount(); await state.extract(archive);
  expect(JSON.parse(String(posts("/files/extract")[0]!.data))).toEqual({ path: archive, destinationPath: "" });
  expect(state.workspace.path).toBe(root); expect(state.notice).toBe("panel.files.extracted");
});
it.each(["search", "size", "compress"])("does not begin an old-session %s action", async action => {
  const { state, auth } = await mount(); auth.session = { ...auth.session!, user: { ...auth.session!.user! } }; state.searchQuery = "data";
  if (action === "search") await state.runSearch(); else if (action === "size") await state.calculateFolderSize(row("data", true)); else await state.compress(`${root}/data`);
  expect(calls.filter(call => call.method === "post")).toHaveLength(0);
});
