import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createPinia } from "pinia";
import { createRenderer, h, nextTick, ssrContextKey, type App, type Component, type Ref } from "vue";
import { AxiosError, type AxiosResponse, type InternalAxiosRequestConfig } from "axios";
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
const row = (name: string, base = root): FileEntry => ({ path: `${base}/${name}`, name, isDirectory: false, size: 10, extension: name.endsWith(".zip") ? ".zip" : ".txt", lastModifiedUtc: null });
const folder = (path = root, names = ["data.zip", "source.txt"]): FileWorkspace => ({ path, parentPath: "sites", displayName: path, physicalPath: "", content: "", isDirectory: true, entries: names.map(name => row(name, path)) });
function response(config: InternalAxiosRequestConfig, data: unknown): AxiosResponse { return { config, data: { success: true, data }, status: 200, statusText: "OK", headers: {} }; }
function failure(config: InternalAxiosRequestConfig, message = "files.invalid_archive") { return new AxiosError("Request failed", "ERR_BAD_RESPONSE", config, undefined, { ...response(config, null), status: 500, data: { success: false, message } }); }
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(yes => { resolve = yes; }); return { promise, resolve }; }
async function flush() { for (let i = 0; i < 40; i++) await nextTick(); }
type ArchiveOperation = { kind: "compress" | "extract"; sourcePath: string; destinationPath: string; status: "confirming" | "running" | "refreshing" | "succeeded" | "failed"; error: string; refreshError: string };
type State = { workspace: FileWorkspace; notice: string; noticeError: boolean; archiveBusy: boolean; archiveOperation: ArchiveOperation | null; workspaceLoad: { message: Ref<string> }; folderSizes: Record<string, DirectorySizeResult>; searchQuery: string; searchRan: boolean; searchResults: FileEntry[]; searchScannedFiles: number | null; compress(path: string): Promise<void>; extract(path: string): Promise<void>; refresh(path?: string): Promise<unknown>; refreshArchiveListing(): Promise<void>; dismissArchiveOperation(): void; removePath(entry: FileEntry): Promise<void>; uploadSelectedFiles(event: Event): Promise<void>; calculateFolderSize(entry: FileEntry): Promise<void>; runSearch(): Promise<void>; };
let app: App | undefined, calls: InternalAxiosRequestConfig[], read: (config: InternalAxiosRequestConfig) => Promise<AxiosResponse>, write: (config: InternalAxiosRequestConfig) => Promise<AxiosResponse>;
const original = apiClient.defaults.adapter;
const workspaceReads = () => calls.filter(call => call.method === "get" && call.url === "/files");
const posts = (url: string) => calls.filter(call => call.method === "post" && call.url === url);
beforeEach(() => {
  vi.resetAllMocks(); invalidateSessionRequests(); calls = [];
  vi.stubGlobal("window", { location: { pathname: "/panel" }, addEventListener: vi.fn(), removeEventListener: vi.fn() });
  vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ success: true, data: { requestToken: "fixture" } }), { status: 200 })));
  dialogs.confirm.mockImplementation(async options => { await options.action?.(); return true; });
  read = async config => response(config, folder(config.params.path));
  write = async config => response(config, folder());
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
function upload(state: State) { return state.uploadSelectedFiles({ target: { files: [new File(["new"], "uploaded.txt")], value: "fixture" } } as unknown as Event); }

it.each(["compress", "extract"] as const)("reads the current directory after failed %s without changing failure into success", async kind => {
  const { state } = await mount();
  write = async config => { throw failure(config); };
  read = async config => response(config, folder(root, ["data.zip", "source.txt", "partially-written.txt"]));
  await state[kind](kind === "compress" ? `${root}/source.txt` : archive);
  expect(workspaceReads()).toHaveLength(2);
  expect(state.workspace.entries.some(entry => entry.name === "partially-written.txt")).toBe(true);
  expect(state.archiveOperation).toMatchObject({ kind, destinationPath: root, status: "failed", error: "files.invalid_archive", refreshError: "" });
  expect(state.noticeError).toBe(true); expect(state.notice).toContain("files.invalid_archive");
});
it("keeps the original archive failure and marks a failed listing until manual retry succeeds", async () => {
  const { state } = await mount(); write = async config => { throw failure(config); };
  read = async config => { throw failure(config, "files.not_found"); };
  await state.extract(archive);
  expect(state.archiveOperation).toMatchObject({ status: "failed", error: "files.invalid_archive", refreshError: "panel.files.archiveRefreshFailed" });
  expect(state.workspaceLoad.message.value).toBe("panel.files.archiveRefreshFailed");
  expect(state.notice).toContain("files.invalid_archive"); expect(state.notice).not.toContain("files.not_found");
  read = async config => response(config, folder(root, ["data.zip", "partially-written.txt"]));
  await state.refresh();
  expect(state.workspaceLoad.message.value).toBe("");
  expect(state.archiveOperation).toMatchObject({ status: "failed", error: "files.invalid_archive", refreshError: "" });
});
it("exposes the captured archive during confirmation and request, then keeps its measured success", async () => {
  const { state } = await mount(), answer = deferred<boolean>(), post = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
  dialogs.confirm.mockReturnValueOnce(answer.promise); write = config => (request = config, post.promise);
  const pending = state.extract(archive); await flush(); const confirming = state.archiveOperation && { ...state.archiveOperation };
  answer.resolve(true); await flush(); const running = state.archiveOperation && { ...state.archiveOperation };
  post.resolve(response(request, folder())); await pending;
  expect(confirming).toMatchObject({ kind: "extract", sourcePath: archive, destinationPath: root, status: "confirming" });
  expect(running?.status).toBe("running");
  expect(state.archiveOperation?.status).toBe("succeeded"); expect(state.archiveBusy).toBe(false);
  expect(workspaceReads()).toHaveLength(1);
});
it("holds archive busy while a failed request's directory is being remeasured", async () => {
  const { state } = await mount(), get = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
  write = async config => { throw failure(config); }; read = config => (request = config, get.promise);
  const pending = state.extract(archive); await flush();
  const busy = state.archiveBusy, phase = state.archiveOperation?.status;
  if (request) get.resolve(response(request, folder())); await pending;
  expect(busy).toBe(true); expect(phase).toBe("refreshing"); expect(state.archiveOperation?.status).toBe("failed");
});
it("does not publish an archive operation or read files after cancelled confirmation", async () => {
  const { state } = await mount(); dialogs.confirm.mockResolvedValueOnce(false); await state.extract(archive);
  expect(state.archiveOperation ?? null).toBeNull(); expect(posts("/files/extract")).toHaveLength(0); expect(workspaceReads()).toHaveLength(1);
});
it.each(["site", "session", "unmount"])("does not refresh or expose an archive failure after %s changes", async context => {
  const { state, auth, sites } = await mount(), post = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
  write = config => (request = config, post.promise); const pending = state.extract(archive); await flush();
  if (context === "site") { sites.selectSite("b.test"); await flush(); }
  else if (context === "session") auth.session = { ...auth.session!, user: { ...auth.session!.user! } };
  else { app!.unmount(); app = undefined; }
  const readsBeforeResponse = workspaceReads().length;
  post.resolve({ ...response(request, null), data: { success: false, message: "files.invalid_archive" } }); await pending;
  expect(workspaceReads()).toHaveLength(readsBeforeResponse); expect(state.archiveOperation ?? null).toBeNull(); expect(state.notice).toBe("");
});
it("does not allow a deletion to race an archive response and resurrect its old row", async () => {
  const { state } = await mount(), post = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
  write = config => config.url === "/files/compress" ? (request = config, post.promise) : Promise.resolve(response(config, folder(root, ["data.zip"])));
  const pending = state.compress(`${root}/source.txt`); await flush();
  read = async config => response(config, folder(root, ["data.zip"]));
  await state.removePath(row("source.txt"));
  post.resolve(response(request, folder(root, ["data.zip", "source.txt", "source.txt.zip"]))); await pending;
  expect(calls.filter(call => call.method === "delete")).toHaveLength(0);
  await state.removePath(row("source.txt"));
  expect(calls.filter(call => call.method === "delete")).toHaveLength(1);
  expect(state.workspace.entries.some(entry => entry.name === "source.txt")).toBe(false);
});
it("does not dispatch uploads while an archive is publishing a directory snapshot", async () => {
  const { state } = await mount(), post = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
  write = config => config.url === "/files/extract" ? (request = config, post.promise) : Promise.resolve(response(config, { path: `${root}/uploaded.txt` }));
  const pending = state.extract(archive); await flush(); await upload(state);
  post.resolve(response(request, folder())); await pending;
  expect(posts("/files/upload")).toHaveLength(0);
});
it("does not begin an archive while an upload is pending, but permits it afterwards", async () => {
  const { state } = await mount(), post = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
  write = config => config.url === "/files/upload" ? (request = config, post.promise) : Promise.resolve(response(config, folder()));
  const pending = upload(state); await flush(); await state.extract(archive); const premature = posts("/files/extract").length;
  post.resolve(response(request, { path: `${root}/uploaded.txt` })); await pending;
  await state.extract(archive);
  expect(premature).toBe(0); expect(posts("/files/extract")).toHaveLength(1);
});
it("keeps upload completion reserved through its follow-up read before allowing an archive", async () => {
  const { state } = await mount(), get = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
  write = async config => response(config, { path: `${root}/uploaded.txt` });
  read = config => (request = config, get.promise);
  const pending = upload(state); await flush();
  await state.extract(archive);
  get.resolve(response(request, folder())); await pending;
  expect(posts("/files/extract")).toHaveLength(0);
});
it("retries only the archive listing, preserves the original failure, and dismisses only a terminal result", async () => {
  const { state } = await mount(); write = async config => { throw failure(config); };
  read = async config => { throw failure(config, "files.not_found"); }; await state.extract(archive);
  const get = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
  read = config => (request = config, get.promise);
  const pending = state.refreshArchiveListing(); await flush();
  const readCount = workspaceReads().length;
  state.dismissArchiveOperation(); await state.refreshArchiveListing(); await state.extract(archive);
  expect(state.archiveOperation?.status).toBe("refreshing");
  expect(workspaceReads()).toHaveLength(readCount); expect(posts("/files/extract")).toHaveLength(1);
  get.resolve(response(request, folder(root, ["data.zip", "partially-written.txt"]))); await pending;
  expect(state.archiveOperation).toMatchObject({ status: "failed", error: "files.invalid_archive", refreshError: "" });
  state.dismissArchiveOperation(); expect(state.archiveOperation).toBeNull();
});
it("invalidates prior search and size measurements when a failed archive may have changed files", async () => {
  const directory = { ...row("data"), isDirectory: true, extension: "" };
  read = async config => response(config, { ...folder(), entries: [...folder().entries, directory] });
  const { state } = await mount();
  write = async config => response(config, config.url === "/files/size"
    ? { path: directory.path, totalBytes: 20, fileCount: 1, directoryCount: 0, truncated: false, truncatedReasonKey: "" }
    : { path: root, query: "data", items: [row("data.zip")], scannedFiles: 2, truncated: false, truncatedReasonKey: "" });
  await state.calculateFolderSize(directory); state.searchQuery = "data"; await state.runSearch();
  expect(state.folderSizes[directory.path].totalBytes).toBe(20); expect(state.searchResults).toHaveLength(1);
  write = async config => { throw failure(config); }; await state.extract(archive);
  expect(state.folderSizes).toEqual({}); expect(state.searchResults).toEqual([]); expect(state.searchRan).toBe(false); expect(state.searchScannedFiles).toBeNull();
});
it.each(["compress", "extract"] as const)("clears the previous failure only when the next %s request starts", async kind => {
  const { state } = await mount(); write = async config => { throw failure(config); };
  await state.extract(archive); await state.refreshArchiveListing(); state.dismissArchiveOperation();
  expect(state.notice).toBe("files.invalid_archive");
  dialogs.confirm.mockResolvedValueOnce(false); await state.extract(archive);
  expect(state.notice).toBe("files.invalid_archive"); expect(posts("/files/extract")).toHaveLength(1);
  const post = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
  write = config => (request = config, post.promise);
  const pending = state[kind](kind === "compress" ? `${root}/source.txt` : archive); await flush();
  const pendingNotice = state.notice, pendingError = state.noticeError;
  post.resolve(response(request, folder(root, ["data.zip", "source.txt", "source.txt.zip"]))); await pending;
  expect(pendingNotice).toBe(""); expect(pendingError).toBe(false);
  expect(state.archiveOperation?.status).toBe("succeeded");
});
