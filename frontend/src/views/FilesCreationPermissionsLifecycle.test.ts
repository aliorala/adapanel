import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { createPinia } from "pinia";
import { createRenderer, h, nextTick, ssrContextKey, type App, type Component, type Ref } from "vue";
import type { AxiosResponse, InternalAxiosRequestConfig } from "axios";
import FilesView from "./FilesView.vue";
import { apiClient, invalidateSessionRequests } from "../api/client";
import { useAuthStore } from "../stores/auth";
import { useSitesStore } from "../stores/sites";
import type { FileWorkspace } from "../api/files";

const dialogs = vi.hoisted(() => ({ confirm: vi.fn(), prompt: vi.fn() }));
vi.mock("../router", () => ({ default: { currentRoute: { value: { query: {} } }, push: vi.fn() } }));
vi.mock("vue-i18n", () => ({ useI18n: () => ({ t: (key: string) => key }) }));
vi.mock("../components/ConfirmDialog.vue", () => ({ useConfirm: () => ({ confirm: dialogs.confirm }) }));
vi.mock("../components/PromptDialog.vue", () => ({ usePrompt: () => ({ prompt: dialogs.prompt }) }));
const root = "sites/a.test", target = `${root}/özel.txt`;
function workspace(path = root, directory = true): FileWorkspace { return { path, parentPath: path.slice(0, path.lastIndexOf("/")), displayName: path.split("/").pop()!, physicalPath: "", isDirectory: directory, content: "", contentLoaded: !directory, entries: [] }; }
function response(config: InternalAxiosRequestConfig, data: unknown, success = true): AxiosResponse { return { config, data: { success, data }, status: 200, statusText: "OK", headers: {} }; }
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(yes => { resolve = yes; }); return { promise, resolve }; }
async function flush() { for (let i = 0; i < 40; i++) await nextTick(); }
type State = { workspace: FileWorkspace; notice: string; noticeError: boolean; createBusy: boolean; permissionsBusy: boolean; permissionPrincipal: string; permissionRights: string; workspaceLoad: { message: Ref<string> }; createNewFile(): Promise<void>; createFolder(): Promise<void>; savePermissions(): Promise<void>; openPath(path: string): Promise<unknown>; refresh(path?: string): Promise<unknown> };
let app: App | undefined, calls: InternalAxiosRequestConfig[], read: (config: InternalAxiosRequestConfig) => Promise<AxiosResponse>, write: (config: InternalAxiosRequestConfig) => Promise<AxiosResponse>;
const original = apiClient.defaults.adapter;
const writes = () => calls.filter(call => call.method !== "get");
beforeEach(() => {
  vi.resetAllMocks(); invalidateSessionRequests(); calls = [];
  vi.stubGlobal("window", { location: { pathname: "/panel" }, addEventListener: vi.fn(), removeEventListener: vi.fn() });
  vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ success: true, data: { requestToken: "fixture" } }), { status: 200 })));
  dialogs.confirm.mockResolvedValue(true); dialogs.prompt.mockResolvedValue("özel.txt");
  read = async config => response(config, workspace(config.params.path, !config.params.path.endsWith(".txt")));
  write = async config => response(config, { ...workspace(JSON.parse(String(config.data)).path, config.url !== "/files"), ...(config.url === "/files/acl" ? { permissionsVerified: true, permissionsDetail: "" } : {}) });
  apiClient.defaults.adapter = async config => { calls.push(config); return config.url === "/backup" ? response(config, []) : config.method === "get" ? read(config) : write(config); };
});
afterEach(() => { app?.unmount(); app = undefined; apiClient.defaults.adapter = original; invalidateSessionRequests(); vi.unstubAllGlobals(); });
async function mount(role = "Administrator") {
  const pinia = createPinia(), auth = useAuthStore(pinia), sites = useSitesStore(pinia);
  auth.session = { isAuthenticated: true, requiresSetupWizard: false, user: { id: "admin", role, userName: "admin", email: "admin@example.test", preferredLanguage: "en", isActive: true, isTwoFactorEnabled: false, lastLoginAtUtc: null } };
  sites.sites = [{ name: "a.test" }, { name: "b.test" }] as never; sites.selectedSiteName = "a.test";
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  app = renderer.createApp({ ...FilesView, render: () => h("section") } as Component).use(pinia); app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: State }).setupState; await flush(); state.notice = ""; return { state, auth, sites };
}
const create = (state: State, kind: string) => kind === "file" ? state.createNewFile() : state.createFolder();
const key = (kind: string) => kind === "file" ? "panel.files.fileCreated" : "panel.files.folderCreated";
it.each(["file", "folder"])("reserves the %s prompt and write against a repeated click", async kind => {
  const { state } = await mount(), answer = deferred<string | null>(); dialogs.prompt.mockReturnValueOnce(answer.promise);
  const pending = create(state, kind); await create(state, kind);
  expect(dialogs.prompt).toHaveBeenCalledOnce(); expect(writes()).toHaveLength(0);
  answer.resolve("özel.txt"); await pending; expect(writes()).toHaveLength(1);
});
it.each(["file", "folder"])("does not open a %s prompt from a previous session", async kind => {
  const { state, auth } = await mount(); auth.session = { ...auth.session!, user: { ...auth.session!.user! } };
  await create(state, kind); expect(dialogs.prompt).not.toHaveBeenCalled(); expect(writes()).toHaveLength(0);
});
it.each(["file", "folder"])("discards a %s prompt accepted after the actor session changes", async kind => {
  const { state, auth } = await mount(), answer = deferred<string | null>(); dialogs.prompt.mockReturnValueOnce(answer.promise);
  const pending = create(state, kind); auth.session = { ...auth.session!, user: { ...auth.session!.user! } };
  answer.resolve("özel.txt"); await pending; expect(writes()).toHaveLength(0);
});
it.each(["file", "folder"])("keeps an accepted %s prompt usable after a neutral same-folder refresh", async kind => {
  const { state } = await mount(), answer = deferred<string | null>(); dialogs.prompt.mockReturnValueOnce(answer.promise);
  const pending = create(state, kind); await state.refresh(); answer.resolve("özel.txt"); await pending;
  expect(writes()).toHaveLength(1); expect(state.notice).toContain(key(kind));
});
it.each(["file", "folder"])("retains measured %s creation when its follow-up read fails", async kind => {
  const { state } = await mount(); read = async () => { throw new Error("read failed"); };
  await create(state, kind); expect(state.notice).toContain(key(kind)); expect(state.noticeError).toBe(true);
  expect(state.workspaceLoad.message.value).toBe("panel.files.refreshAfterOperationFailed");
});
it.each(["file", "folder"])("does not publish old %s creation after the site changes during its follow-up read", async kind => {
  const { state, sites } = await mount(), gate = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
  read = config => config.params.path.startsWith(root) ? (request = config, gate.promise) : Promise.resolve(response(config, workspace(config.params.path)));
  const pending = create(state, kind); await flush(); sites.selectSite("b.test"); await flush();
  gate.resolve(response(request, workspace(kind === "file" ? target : root, kind !== "file"))); await pending;
  expect(state.workspace.path).toBe("sites/b.test"); expect(state.notice).toBe("");
});
it.each(["file", "folder"])("does not claim %s creation after an unsuccessful API envelope", async kind => {
  const { state } = await mount(); write = async config => response(config, workspace(target, kind === "folder"), false);
  await create(state, kind); expect(state.noticeError).toBe(true); expect(state.notice).toContain("panel.files.resultUnconfirmed");
  expect(state.workspace.path).toBe(root);
});
it.each(["file", "folder"])("preserves normal Unicode %s creation and its expected navigation", async kind => {
  const { state } = await mount(); await create(state, kind);
  expect(state.notice).toContain(key(kind)); expect(state.noticeError).toBe(false);
  expect(state.workspace.path).toBe(kind === "file" ? target : root);
  expect(JSON.parse(String(writes()[0]!.data))).toMatchObject({ path: target });
});
it("allows correcting a failed creation and retrying", async () => {
  const { state } = await mount(); write = async () => { throw new Error("already exists"); };
  await state.createNewFile(); expect(state.noticeError).toBe(true);
  write = async config => response(config, workspace(target, false)); await state.createNewFile();
  expect(state.noticeError).toBe(false); expect(state.workspace.path).toBe(target);
});
it("does not overwrite the current workspace with an old permission result or repeat the pending write", async () => {
  const { state } = await mount(); await state.openPath(`${root}/folder`);
  const gate = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig; write = config => (request = config, gate.promise);
  const pending = state.savePermissions(); await flush(); const second = state.savePermissions(); await flush(); const sent = writes().length;
  await state.openPath(`${root}/other`); gate.resolve(response(request, workspace(`${root}/folder`))); await Promise.all([pending, second]);
  expect(sent).toBe(1);
  expect(state.workspace.path).toBe(`${root}/other`); expect(state.notice).toBe("");
});
it.each(["session", "unmount"])("ignores a permission acknowledgement after %s changes", async context => {
  const { state, auth } = await mount(); await state.openPath(`${root}/folder`);
  const gate = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig; write = config => (request = config, gate.promise);
  const pending = state.savePermissions(); await flush();
  if (context === "session") auth.session = { ...auth.session!, user: { ...auth.session!.user! } }; else { app!.unmount(); app = undefined; }
  gate.resolve(response(request, workspace(`${root}/folder`))); await pending; expect(state.notice).toBe("");
});
it("does not report saved permissions for the wrong target", async () => {
  const { state } = await mount(); await state.openPath(`${root}/folder`);
  write = async config => response(config, workspace("sites/b.test/private")); await state.savePermissions();
  expect(state.noticeError).toBe(true); expect(state.notice).toContain("panel.files.resultUnconfirmed");
});
it("sends the selected permission target/principal/rights and confirms measured completion", async () => {
  const { state } = await mount(); await state.openPath(`${root}/folder`);
  state.permissionPrincipal = "IIS_IUSRS"; state.permissionRights = "R";
  await state.savePermissions(); expect(JSON.parse(String(writes()[0]!.data))).toEqual({ path: `${root}/folder`, principal: "IIS_IUSRS", rights: "R" });
  expect(state.notice).toBe("panel.files.permissionsSaved"); expect(state.noticeError).toBe(false);
});
it.each([undefined, null, false])("reports an unverified permission command (%s) without claiming all entries changed", async permissionsVerified => {
  const { state } = await mount(); await state.openPath(`${root}/folder`);
  write = async config => response(config, { ...workspace(`${root}/folder`), permissionsVerified, permissionsDetail: "Cannot measure every entry" });
  await state.savePermissions(); expect(state.noticeError).toBe(true); expect(state.notice).toContain("panel.files.permissionsUnverified");
  expect(state.notice).not.toContain("panel.files.permissionsSaved");
});
