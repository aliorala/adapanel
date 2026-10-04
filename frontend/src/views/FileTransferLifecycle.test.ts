import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createPinia } from "pinia";
import { createRenderer, h, nextTick, ssrContextKey, type App, type Component, type Ref } from "vue";
import type { AxiosResponse, InternalAxiosRequestConfig } from "axios";
import FilesView from "./FilesView.vue";
import { apiClient, invalidateSessionRequests } from "../api/client";
import { useAuthStore } from "../stores/auth";
import { useSitesStore } from "../stores/sites";
import { useSubscriptionStore } from "../stores/subscription";
import type { FileEntry, FileWorkspace } from "../api/files";

const dialogs = vi.hoisted(() => ({ confirm: vi.fn(), prompt: vi.fn() }));
vi.mock("../router", () => ({ default: { currentRoute: { value: { query: {} } }, push: vi.fn() } }));
vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string, params?: object) => params ? `${key} ${JSON.stringify(params)}` : key }) }));
vi.mock("../components/ConfirmDialog.vue", () => ({ useConfirm: () => ({ confirm: dialogs.confirm }) }));
vi.mock("../components/PromptDialog.vue", () => ({ usePrompt: () => ({ prompt: dialogs.prompt }) }));
const rootPath = "sites/a.test";
function entry(name = "source.txt", path = rootPath): FileEntry { return { name, path: `${path}/${name}`, isDirectory: false, size: 10, lastModifiedUtc: null, extension: ".txt" }; }
function workspace(path = rootPath): FileWorkspace { return { path, parentPath: "", displayName: path, physicalPath: "", isDirectory: true, content: "", entries: [entry("source.txt", path), entry("next.txt", path)] }; }
function response(config: InternalAxiosRequestConfig, data: unknown, success = true): AxiosResponse { return { config, data: { success, data }, status: 200, statusText: "OK", headers: {} }; }
function transfer(config: InternalAxiosRequestConfig) { const request = JSON.parse(String(config.data)), copied = config.url === "/files/copy", flag = copied ? "copied" : "moved"; return response(config, { [copied ? "copiedCount" : "movedCount"]: request.paths.length, failedCount: 0, items: request.paths.map((sourcePath: string) => ({ sourcePath, targetPath: `${request.destinationPath}/${request.newName || sourcePath.split("/").pop()}`, [flag]: true, errorKey: "" })), workspace: workspace(request.destinationPath) }); }
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(yes => { resolve = yes; }); return { promise, resolve }; }
async function flush() { for (let i = 0; i < 40; i++) await nextTick(); }
type State = { workspace: FileWorkspace; workspaceLoad: { message: Ref<string> }; notice: string; noticeError: boolean; transferMode: "move" | "copy" | null; transferTarget: string; transferOverwrite: boolean; transferBusy: boolean; uploadDone: number; uploadBusy: boolean; selection: { selected: Ref<string[]>; toggle(path: string): void }; startTransfer(mode: "move" | "copy"): void; runTransfer(): Promise<void>; renamePath(entry: FileEntry): Promise<void>; uploadMany(files: File[], folders: string[]): Promise<void>; refresh(path?: string): Promise<unknown>; openPath(path: string): Promise<void>; };
let app: App | undefined, calls: InternalAxiosRequestConfig[], write: (config: InternalAxiosRequestConfig) => Promise<AxiosResponse>, read: (config: InternalAxiosRequestConfig) => Promise<AxiosResponse>;
const original = apiClient.defaults.adapter;
beforeEach(() => {
  vi.resetAllMocks(); invalidateSessionRequests(); calls = [];
  vi.stubGlobal("window", { location: { pathname: "/panel" }, addEventListener: vi.fn(), removeEventListener: vi.fn() });
  vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ success: true, data: { requestToken: "fixture" } }), { status: 200 })));
  dialogs.confirm.mockImplementation(async options => { await options.action?.(); return true; }); dialogs.prompt.mockResolvedValue("renamed.txt");
  read = async config => response(config, workspace(config.params.path));
  write = async config => config.url === "/files/upload" ? response(config, { path: `${config.params.path}/${(config.data as FormData).get("file") instanceof File ? ((config.data as FormData).get("file") as File).name : "source.txt"}` }) : transfer(config);
  apiClient.defaults.adapter = async config => { calls.push(config); return config.url === "/backup" ? response(config, []) : config.method === "get" ? read(config) : write(config); };
});
afterEach(() => { app?.unmount(); app = undefined; apiClient.defaults.adapter = original; invalidateSessionRequests(); vi.unstubAllGlobals(); });
async function mount() {
  const pinia = createPinia(), auth = useAuthStore(pinia), sites = useSitesStore(pinia), subscription = useSubscriptionStore(pinia);
  auth.session = { isAuthenticated: true, requiresSetupWizard: false, user: { id: "admin", role: "Administrator", userName: "admin", email: "admin@example.test", preferredLanguage: "en", isActive: true, isTwoFactorEnabled: false, lastLoginAtUtc: null } };
  sites.sites = [{ name: "a.test" }, { name: "b.test" }] as never; sites.selectedSiteName = "a.test";
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  app = renderer.createApp({ ...FilesView, render: () => h("section") } as Component).use(pinia); app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: State }).setupState; await flush(); state.notice = ""; return { state, auth, sites, subscription };
}
for (const mode of ["move", "copy"] as const) {
  it(`${mode} does not claim completion from an empty per-item result`, async () => {
    const { state } = await mount(); state.selection.toggle(entry().path); state.startTransfer(mode); state.transferTarget = `${rootPath}/target`;
    write = async config => response(config, { [mode === "move" ? "movedCount" : "copiedCount"]: 0, failedCount: 0, items: [], workspace: workspace(`${rootPath}/target`) });
    await state.runTransfer(); expect(state.noticeError).toBe(true); expect(state.notice).toContain("panel.files.resultUnconfirmed"); expect(state.transferMode).toBe(mode);
  });
  it.each(["site", "session", "unmount"])(`${mode} does not refresh or report an obsolete result after %s`, async context => {
    const { state, auth, sites } = await mount(); state.selection.toggle(entry().path); state.startTransfer(mode); state.transferTarget = `${rootPath}/target`;
    const gate = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig; write = config => (request = config, gate.promise);
    const running = state.runTransfer(); await flush();
    if (context === "site") { sites.selectedSiteName = "b.test"; await flush(); } else if (context === "session") auth.session = { ...auth.session!, user: { ...auth.session!.user! } }; else { app!.unmount(); app = undefined; }
    const reads = calls.filter(config => config.method === "get").length; gate.resolve(transfer(request)); await running;
    expect(calls.filter(config => config.method === "get")).toHaveLength(reads); expect(state.notice).toBe("");
    if (context === "site") expect(state.workspace.path).toBe("sites/b.test");
  });
  it(`${mode} confirms the captured request and clears its completed selection`, async () => {
    const { state } = await mount(); state.selection.toggle(entry().path); state.startTransfer(mode); state.transferTarget = `${rootPath}/target`;
    const gate = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig; write = config => (request = config, gate.promise);
    const running = state.runTransfer(); await flush();
    gate.resolve(transfer(request)); await running;
    expect(JSON.parse(String(request.data))).toEqual({ paths: [entry().path], destinationPath: `${rootPath}/target`, overwrite: false });
    expect(state.transferMode).toBeNull(); expect(state.selection.selected.value).toEqual([]); expect(state.noticeError).toBe(false);
  });
  it(`${mode} keeps a measured outcome distinct from a failed source refresh`, async () => {
    const { state } = await mount(); state.selection.toggle(entry().path); state.startTransfer(mode); state.transferTarget = `${rootPath}/target`; read = async () => { throw new Error("list failed"); };
    await state.runTransfer(); expect(state.noticeError).toBe(true); expect(state.notice).toContain("panel.files.refreshAfterOperationFailed"); expect(state.notice).toContain(mode === "move" ? "panel.files.movedAll" : "panel.files.copiedAll");
    expect(state.workspaceLoad.message.value).toBe("panel.files.refreshAfterOperationFailed");
  });
}
it.each(["site", "session", "unmount"])("rename does not submit a prompt accepted after %s changes", async context => {
  const { state, auth, sites } = await mount(), answer = deferred<string>(); dialogs.prompt.mockReturnValueOnce(answer.promise);
  const running = state.renamePath(entry());
  if (context === "site") { sites.selectedSiteName = "b.test"; await flush(); } else if (context === "session") auth.session = { ...auth.session!, user: { ...auth.session!.user! } }; else { app!.unmount(); app = undefined; }
  answer.resolve("renamed.txt"); await running; expect(calls.filter(config => config.method === "post")).toHaveLength(0);
});
it.each(["site", "session", "unmount"])("upload stops queued files after %s changes", async context => {
  const { state, auth, sites } = await mount(), gate = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
  write = config => calls.filter(call => call.method === "post").length === 1 ? (request = config, gate.promise) : Promise.resolve(response(config, { path: `${rootPath}/second.txt` }));
  const running = state.uploadMany([new File(["a"], "first.txt"), new File(["b"], "second.txt")], []); await flush();
  if (context === "site") { sites.selectedSiteName = "b.test"; await flush(); } else if (context === "session") auth.session = { ...auth.session!, user: { ...auth.session!.user! } }; else { app!.unmount(); app = undefined; }
  gate.resolve(response(request, { path: `${rootPath}/first.txt` })); await running;
  expect(calls.filter(config => config.method === "post")).toHaveLength(1); expect(state.notice).toBe("");
});
it("upload does not count an unconfirmed file as successful and still attempts the next authorized file", async () => {
  const { state } = await mount(); let index = 0;
  write = async config => response(config, { path: `${rootPath}/${index++ === 0 ? "wrong" : "second"}.txt` });
  await state.uploadMany([new File(["a"], "first.txt"), new File(["b"], "second.txt")], []);
  expect(state.uploadDone).toBe(1); expect(state.noticeError).toBe(true); expect(state.notice).toContain("panel.files.resultUnconfirmed"); expect(calls.filter(config => config.method === "post")).toHaveLength(2);
});
it("a successful upload refresh failure cannot hide behind an all-uploaded message", async () => {
  const { state } = await mount(); write = async config => response(config, { path: `${rootPath}/first.txt` }); read = async () => { throw new Error("list failed"); };
  await state.uploadMany([new File(["a"], "first.txt")], []);
  expect(state.uploadDone).toBe(1); expect(state.noticeError).toBe(true); expect(state.notice).toContain("panel.files.uploadedAll"); expect(state.notice).toContain("panel.files.refreshAfterOperationFailed");
  expect(state.workspaceLoad.message.value).toBe("panel.files.refreshAfterOperationFailed");
});

it("continues the captured upload queue after a neutral refresh of the same folder", async () => {
  const { state } = await mount(), gate = deferred<AxiosResponse>(); let first!: InternalAxiosRequestConfig;
  write = config => calls.filter(call => call.method === "post").length === 1 ? (first = config, gate.promise) : Promise.resolve(response(config, { path: `${rootPath}/second.txt` }));
  const running = state.uploadMany([new File(["a"], "first.txt"), new File(["b"], "second.txt")], []); await flush();
  await state.refresh(); gate.resolve(response(first, { path: `${rootPath}/first.txt` })); await running;
  expect(calls.filter(config => config.method === "post")).toHaveLength(2); expect(state.uploadDone).toBe(2);
  expect(state.notice).toContain("panel.files.uploadedAll"); expect(state.noticeError).toBe(false);
});

it("reports the measured copy after a neutral refresh of the same folder", async () => {
  const { state } = await mount(), gate = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
  state.selection.toggle(entry().path); state.startTransfer("copy"); state.transferTarget = `${rootPath}/target`;
  write = config => (request = config, gate.promise);
  const running = state.runTransfer(); await flush(); await state.refresh(); gate.resolve(transfer(request)); await running;
  expect(state.transferMode).toBeNull(); expect(state.notice).toContain("panel.files.copiedAll"); expect(state.noticeError).toBe(false);
});

it("stops queued uploads when navigation to another folder is still waiting for its GET", async () => {
  const { state } = await mount(), post = deferred<AxiosResponse>(), get = deferred<AxiosResponse>(); let first!: InternalAxiosRequestConfig, navigation!: InternalAxiosRequestConfig;
  write = config => (first = config, post.promise);
  const running = state.uploadMany([new File(["a"], "first.txt"), new File(["b"], "second.txt")], []); await flush();
  read = config => (navigation = config, get.promise);
  const navigating = state.openPath(`${rootPath}/other`); await flush();
  post.resolve(response(first, { path: `${rootPath}/first.txt` })); await running;
  expect(calls.filter(config => config.method === "post")).toHaveLength(1); expect(state.notice).toBe("");
  get.resolve(response(navigation, workspace(`${rootPath}/other`))); await navigating;
  expect(state.workspace.path).toBe(`${rootPath}/other`);
});
