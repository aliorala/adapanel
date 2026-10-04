import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createPinia } from "pinia";
import { createRenderer, createSSRApp, h, nextTick, ssrContextKey, type App, type Component, type Ref } from "vue";
import { renderToString } from "vue/server-renderer";
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
const root = "sites/a.test", filePath = `${root}/config.txt`;
function entry(name = "config.txt", base = root): FileEntry { return { name, path: `${base}/${name}`, isDirectory: false, size: 10, lastModifiedUtc: null, extension: ".txt" }; }
function folder(path = root): FileWorkspace { return { path, parentPath: "sites", displayName: path, physicalPath: "", isDirectory: true, content: "", entries: [entry("config.txt", path), entry("second.txt", path)] }; }
function file(content = "original", path = filePath): FileWorkspace & { contentLoaded: boolean } { return { ...folder(path), parentPath: root, isDirectory: false, content, contentLoaded: true, entries: [] }; }
function response(config: InternalAxiosRequestConfig, data: unknown, success = true): AxiosResponse { return { config, data: { success, data }, status: 200, statusText: "OK", headers: {} }; }
function deleted(config: InternalAxiosRequestConfig) { const path = JSON.parse(String(config.data)).path as string, parent = path.slice(0, path.lastIndexOf("/")); const data = folder(parent); data.entries = data.entries.filter(item => item.path !== path); return response(config, data); }
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(yes => { resolve = yes; }); return { promise, resolve }; }
async function flush() { for (let i = 0; i < 40; i++) await nextTick(); }
type State = { workspace: FileWorkspace; fileContent: string; savedContent: string; isDirty: boolean; notice: string; noticeError: boolean; saving: boolean; workspaceLoad: { message: Ref<string> }; selection: { selected: Ref<string[]>; toggle(path: string): void }; saveCurrentFile(): Promise<boolean>; removePath(entry: FileEntry): Promise<void>; removeSelected(): Promise<void>; openPath(path: string): Promise<void>; refresh(path?: string): Promise<unknown>; };
let app: App | undefined, calls: InternalAxiosRequestConfig[], write: (config: InternalAxiosRequestConfig) => Promise<AxiosResponse>, read: (config: InternalAxiosRequestConfig) => Promise<AxiosResponse>;
const original = apiClient.defaults.adapter;
beforeEach(() => {
  vi.resetAllMocks(); invalidateSessionRequests(); calls = [];
  vi.stubGlobal("window", { location: { pathname: "/panel" }, addEventListener: vi.fn(), removeEventListener: vi.fn() });
  vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ success: true, data: { requestToken: "fixture" } }), { status: 200 })));
  dialogs.confirm.mockImplementation(async options => { await options.action?.(); return true; });
  read = async config => response(config, config.params.path.endsWith(".txt") ? file("original", config.params.path) : folder(config.params.path));
  write = async config => config.method === "delete" ? deleted(config) : response(config, file(JSON.parse(String(config.data)).content));
  apiClient.defaults.adapter = async config => { calls.push(config); return config.url === "/backup" ? response(config, []) : config.method === "get" ? read(config) : write(config); };
});
afterEach(() => { app?.unmount(); app = undefined; apiClient.defaults.adapter = original; invalidateSessionRequests(); vi.unstubAllGlobals(); });
async function mount(edit = false) {
  const pinia = createPinia(), auth = useAuthStore(pinia), sites = useSitesStore(pinia), subscription = useSubscriptionStore(pinia);
  auth.session = { isAuthenticated: true, requiresSetupWizard: false, user: { id: "admin", role: "Administrator", userName: "admin", email: "admin@example.test", preferredLanguage: "en", isActive: true, isTwoFactorEnabled: false, lastLoginAtUtc: null } };
  sites.sites = [{ name: "a.test" }, { name: "b.test" }] as never; sites.selectedSiteName = "a.test";
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  app = renderer.createApp({ ...FilesView, render: () => h("section") } as Component).use(pinia); app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: State }).setupState; await flush();
  if (edit) { await state.openPath(filePath); state.fileContent = "edited"; }
  state.notice = ""; return { state, auth, sites, subscription };
}
type Fixture = Awaited<ReturnType<typeof mount>>;
async function changeContext(fixture: Fixture, context: string) {
  if (context === "site") { fixture.sites.selectSite("b.test"); await flush(); }
  else if (context === "session") fixture.auth.session = { ...fixture.auth.session!, user: { ...fixture.auth.session!.user! } };
  else if (context === "lock") {
    fixture.subscription.usage = { isSuspended: true } as never;
    fixture.subscription.applyReadOnly();
  }
  else { app!.unmount(); app = undefined; }
}

it.each(["failed", "wrong-file", "wrong-content"])("keeps the unsaved editor and navigation protection after an unconfirmed %s save", async kind => {
  const { state } = await mount(true);
  write = async config => response(config, file(kind === "wrong-content" ? "original" : "edited", kind === "wrong-file" ? "sites/b.test/config.txt" : filePath), kind !== "failed");
  expect(await state.saveCurrentFile()).toBe(false); expect(state.fileContent).toBe("edited"); expect(state.isDirty).toBe(true);
  expect(state.noticeError).toBe(true); expect(state.notice).toContain("panel.files.resultUnconfirmed");
});
it.each(["session", "unmount"])("does not publish a saved draft after %s changes while PUT is pending", async context => {
  const fixture = await mount(true), gate = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
  write = config => (request = config, gate.promise); const pending = fixture.state.saveCurrentFile(); await flush(); await changeContext(fixture, context);
  gate.resolve(response(request, file("edited"))); expect(await pending).toBe(false);
  expect(fixture.state.savedContent).toBe("original"); expect(fixture.state.notice).toBe("");
});
it("clears dirty state only after the submitted file and exact content are measured", async () => {
  const { state } = await mount(true); expect(await state.saveCurrentFile()).toBe(true);
  expect(state.isDirty).toBe(false); expect(state.notice).toContain("panel.files.saved");
  expect(JSON.parse(String(calls.find(config => config.method === "put")!.data))).toEqual({ path: filePath, content: "edited" });
});
it("preserves a rejected save for correction and a normal retry", async () => {
  const { state } = await mount(true); write = async () => { throw new Error("disk full"); };
  expect(await state.saveCurrentFile()).toBe(false); expect(state.isDirty).toBe(true);
  state.fileContent = "corrected"; write = async config => response(config, file("corrected"));
  expect(await state.saveCurrentFile()).toBe(true); expect(state.isDirty).toBe(false);
});

it.each(["site", "session", "unmount", "lock"])("does not delete from a confirmation accepted after %s changes", async context => {
  const fixture = await mount();
  dialogs.confirm.mockImplementationOnce(async options => { await changeContext(fixture, context); await options.action?.(); return true; });
  await fixture.state.removePath(entry()); expect(calls.filter(config => config.method === "delete")).toHaveLength(0);
});
it.each(["site", "session", "unmount"])("ignores a late single-delete result after %s changes", async context => {
  const fixture = await mount(), gate = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
  write = config => (request = config, gate.promise); const pending = fixture.state.removePath(entry()); await flush(); await changeContext(fixture, context);
  const reads = calls.filter(config => config.method === "get").length; gate.resolve(deleted(request)); await pending;
  expect(calls.filter(config => config.method === "get")).toHaveLength(reads); expect(fixture.state.notice).toBe("");
});
it("does not report deletion when the response still contains the confirmed file", async () => {
  const { state } = await mount(); write = async config => response(config, folder());
  await state.removePath(entry()); expect(state.noticeError).toBe(true); expect(state.notice).toContain("panel.files.resultUnconfirmed");
});
it("keeps a measured deletion distinct from the following failed list read", async () => {
  const { state } = await mount(); read = async () => { throw new Error("read failed"); };
  await state.removePath(entry()); expect(state.notice).toContain("panel.files.deleted"); expect(state.noticeError).toBe(true);
  expect(state.workspaceLoad.message.value).toBe("panel.files.refreshAfterOperationFailed");
});
it("reports a normally confirmed single-file deletion", async () => {
  const { state } = await mount(); await state.removePath(entry());
  expect(calls.filter(config => config.method === "delete")).toHaveLength(1); expect(state.noticeError).toBe(false); expect(state.notice).toContain("panel.files.deleted");
});
it("does not start bulk deletion when suspension arrives during confirmation", async () => {
  const fixture = await mount(); fixture.state.selection.toggle(entry().path);
  dialogs.confirm.mockImplementationOnce(async () => { await changeContext(fixture, "lock"); return true; });
  await fixture.state.removeSelected(); expect(calls.filter(config => config.method === "delete")).toHaveLength(0);
});
it("stops the remaining bulk DELETE requests when suspension arrives after the first request", async () => {
  const fixture = await mount(), gate = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
  fixture.state.selection.toggle(entry().path); fixture.state.selection.toggle(entry("second.txt").path);
  write = config => calls.filter(call => call.method === "delete").length === 1 ? (request = config, gate.promise) : Promise.resolve(deleted(config));
  const pending = fixture.state.removeSelected(); await flush(); await changeContext(fixture, "lock"); gate.resolve(deleted(request)); await pending;
  expect(calls.filter(config => config.method === "delete")).toHaveLength(1);
});
it("continues a confirmed bulk deletion through a same-folder list refresh", async () => {
  const { state } = await mount(), gate = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
  state.selection.toggle(entry().path); state.selection.toggle(entry("second.txt").path);
  write = config => calls.filter(call => call.method === "delete").length === 1 ? (request = config, gate.promise) : Promise.resolve(deleted(config));
  const pending = state.removeSelected(); await flush(); await state.refresh(); gate.resolve(deleted(request)); await pending;
  expect(calls.filter(config => config.method === "delete")).toHaveLength(2); expect(state.notice).toContain("panel.bulk.deletedAll");
});
it("keeps a mixed bulk deletion truthful when the follow-up read also fails", async () => {
  const { state } = await mount(); state.selection.toggle(entry().path); state.selection.toggle(entry("second.txt").path);
  write = async config => calls.filter(call => call.method === "delete").length === 1 ? deleted(config) : response(config, folder(), false);
  read = async () => { throw new Error("read failed"); }; await state.removeSelected();
  expect(state.notice).toContain("panel.bulk.deletedPartial"); expect(state.notice).toContain('"done":1'); expect(state.noticeError).toBe(true);
  expect(state.workspaceLoad.message.value).toBe("panel.files.refreshAfterOperationFailed");
});

it.each(["too-large", "missing-measurement"])("offers download but prevents editing a file with %s content", async kind => {
  const { state } = await mount();
  const data = { ...file(""), contentLoaded: false, contentUnavailableReasonKey: "files.content_too_large" };
  if (kind === "missing-measurement") { delete (data as Partial<typeof data>).contentLoaded; data.contentUnavailableReasonKey = ""; }
  read = async config => response(config, data); await state.openPath(filePath);
  const html = await renderToString(createSSRApp({ ...FilesView, setup: () => state } as Component));
  expect(html).toMatch(/<textarea(?=[^>]*class="file-editor")(?=[^>]*disabled)[^>]*>/);
  expect(html).toContain(kind === "too-large" ? "files.content_too_large" : "panel.files.contentUnmeasured");
  expect(html).toContain("/api/v1/files/download?path=sites%2Fa.test%2Fconfig.txt");
  expect(state.isDirty).toBe(false); expect(calls.filter(config => config.method === "put")).toHaveLength(0);
});

it("allows editing and saving a genuinely loaded empty file", async () => {
  const { state } = await mount(); read = async config => response(config, file("")); await state.openPath(filePath);
  const html = await renderToString(createSSRApp({ ...FilesView, setup: () => state } as Component));
  expect(html.match(/<textarea[^>]*class="file-editor"[^>]*>/)?.[0]).not.toContain("disabled");
  state.fileContent = "new text"; expect(await state.saveCurrentFile()).toBe(true); expect(state.isDirty).toBe(false);
});
