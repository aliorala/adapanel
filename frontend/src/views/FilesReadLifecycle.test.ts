import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createPinia } from "pinia";
import { createRenderer, createSSRApp, h, nextTick, ssrContextKey, type App, type Component, type Ref } from "vue";
import { renderToString } from "vue/server-renderer";
import type { AxiosResponse, InternalAxiosRequestConfig } from "axios";
import FilesView from "./FilesView.vue";
import { apiClient, invalidateSessionRequests } from "../api/client";
import { useAuthStore } from "../stores/auth";
import { useSitesStore } from "../stores/sites";
import type { FileWorkspace } from "../api/files";

const dialogs = vi.hoisted(() => ({ confirm: vi.fn(), push: vi.fn() }));
vi.mock("../router", () => ({ default: { currentRoute: { value: { query: {} } }, push: dialogs.push } }));
vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key }) }));
vi.mock("../components/ConfirmDialog.vue", () => ({ useConfirm: () => ({ confirm: dialogs.confirm }) }));
vi.mock("../components/PromptDialog.vue", () => ({ usePrompt: () => ({ prompt: vi.fn() }) }));
const root = "sites/a.test", path = `${root}/config.txt`;
function folder(value = root): FileWorkspace { return { path: value, parentPath: "sites", displayName: value, physicalPath: "", isDirectory: true, content: "", entries: [] }; }
function file(value = path, content = "original"): FileWorkspace { return { ...folder(value), parentPath: root, isDirectory: false, content, contentLoaded: true }; }
function response(config: InternalAxiosRequestConfig, data: unknown, success = true): AxiosResponse { return { config, data: { success, data }, status: 200, statusText: "OK", headers: {} }; }
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(yes => { resolve = yes; }); return { promise, resolve }; }
async function flush() { for (let i = 0; i < 35; i++) await nextTick(); }
type State = { workspace: FileWorkspace | null; fileContent: string; canEditFile: boolean; loading: boolean; notice: string; workspaceLoad: { message: Ref<string> }; openPath(path: string): Promise<void>; refresh(path?: string): Promise<unknown>; changeSite(event: Event): Promise<void>; };
let app: App | undefined, calls: InternalAxiosRequestConfig[], read: (config: InternalAxiosRequestConfig) => Promise<AxiosResponse>;
const original = apiClient.defaults.adapter;
beforeEach(() => {
  vi.resetAllMocks(); invalidateSessionRequests(); calls = [];
  vi.stubGlobal("window", { location: { pathname: "/panel" }, addEventListener: vi.fn(), removeEventListener: vi.fn() });
  read = async config => response(config, config.params.path.endsWith(".txt") ? file(config.params.path) : folder(config.params.path));
  apiClient.defaults.adapter = async config => { calls.push(config); return config.url === "/backup" ? response(config, []) : read(config); };
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

it.each(["failed-envelope", "wrong-file"])("does not offer the wrong edit/download target after %s from GET", async kind => {
  const { state } = await mount();
  read = async config => response(config, file("sites/b.test/private.txt", "foreign secret"), kind !== "failed-envelope");
  await state.openPath(path);
  expect(state.workspace?.path).toBe(root); expect(state.fileContent).toBe(""); expect(state.canEditFile).toBe(false);
  expect(state.workspaceLoad.message.value).toBe("panel.files.resultUnconfirmed");
  const html = await renderToString(createSSRApp({ ...FilesView, setup: () => state } as Component));
  expect(html).not.toContain("private.txt"); expect(html).not.toContain("foreign secret");
});
it("does not expose a foreign directory entry as a download or delete action", async () => {
  const { state } = await mount(); const data = folder(); data.entries = [{ name: "secret.txt", path: "sites/b.test/secret.txt", isDirectory: false, size: 12, extension: ".txt", lastModifiedUtc: null }];
  read = async config => response(config, data); await state.refresh();
  expect(state.workspace?.entries).toEqual([]); expect(state.workspaceLoad.message.value).toBe("panel.files.resultUnconfirmed");
});
it("keeps the newest selected file when GET responses arrive in reverse order", async () => {
  const { state } = await mount(), first = deferred<AxiosResponse>(), second = deferred<AxiosResponse>(); let a!: InternalAxiosRequestConfig, b!: InternalAxiosRequestConfig;
  read = config => config.params.path === path ? (a = config, first.promise) : (b = config, second.promise);
  const older = state.openPath(path); await flush(); const newer = state.openPath(`${root}/second.txt`); await flush();
  second.resolve(response(b, file(`${root}/second.txt`, "newest"))); await newer;
  first.resolve(response(a, file(path, "old"))); await older;
  expect(state.workspace?.path).toBe(`${root}/second.txt`); expect(state.fileContent).toBe("newest");
});
it.each(["session", "unmount"])("ignores file content returned after %s changes", async context => {
  const { state, auth } = await mount(), gate = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
  read = config => (request = config, gate.promise); const pending = state.openPath(path); await flush();
  if (context === "session") auth.session = { ...auth.session!, user: { ...auth.session!.user! } }; else { app!.unmount(); app = undefined; }
  gate.resolve(response(request, file())); await pending;
  expect(state.workspace?.path).toBe(root); expect(state.fileContent).toBe("");
});
it.each(["session", "unmount"])("does not switch global site or route after an old unsaved prompt is discarded following %s", async context => {
  const { state, auth, sites } = await mount(); await state.openPath(path); state.fileContent = "draft";
  const gate = deferred<boolean>(); let discard!: () => void;
  dialogs.confirm.mockImplementationOnce(options => { discard = options.alternativeAction; return gate.promise; });
  const pending = state.changeSite({ target: { value: "b.test" } } as unknown as Event); await flush();
  if (context === "session") auth.session = { ...auth.session!, user: { ...auth.session!.user! } }; else { app!.unmount(); app = undefined; }
  discard(); gate.resolve(false); await pending;
  expect(sites.selectedSiteName).toBe("a.test"); expect(dialogs.push).not.toHaveBeenCalled();
});
it("still changes site after discarding the current editor draft", async () => {
  const { state, sites } = await mount(); await state.openPath(path); state.fileContent = "draft";
  dialogs.confirm.mockImplementationOnce(async options => { options.alternativeAction(); return false; });
  await state.changeSite({ target: { value: "b.test" } } as unknown as Event); await flush();
  expect(sites.selectedSiteName).toBe("b.test"); expect(dialogs.push).toHaveBeenCalledWith({ query: { section: "files", site: "b.test" } });
});

it("keeps binary or unsupported text available through the original download route without an editable substitute", async () => {
  const { state } = await mount(), binaryPath = `${root}/archive.bin`;
  read = async config => response(config, { ...file(binaryPath, ""), contentLoaded: false, contentUnavailableReasonKey: "files.content_not_editable" });
  await state.openPath(binaryPath);
  const html = await renderToString(createSSRApp({ ...FilesView, setup: () => state } as Component));
  expect(state.canEditFile).toBe(false); expect(state.fileContent).toBe("");
  expect(html).toMatch(/<textarea(?=[^>]*class="file-editor")(?=[^>]*disabled)[^>]*>/);
  expect(html).toContain("files.content_not_editable");
  expect(html).toContain("/api/v1/files/download?path=sites%2Fa.test%2Farchive.bin");
  expect(calls.filter(config => config.url?.includes("download"))).toHaveLength(0);
});
