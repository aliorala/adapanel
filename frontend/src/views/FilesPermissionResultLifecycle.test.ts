import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { createPinia } from 'pinia';
import { createRenderer, h, nextTick, ssrContextKey, type App, type Component } from 'vue';
import { AxiosError, type AxiosResponse, type InternalAxiosRequestConfig } from 'axios';
import FilesView from './FilesView.vue';
import { apiClient, invalidateSessionRequests } from '../api/client';
import { useAuthStore } from '../stores/auth';
import { useSitesStore } from '../stores/sites';
import type { FileEntry, FileWorkspace } from '../api/files';

const dialogs = vi.hoisted(() => ({ confirm: vi.fn(), prompt: vi.fn() }));
vi.mock('../router', () => ({ default: { currentRoute: { value: { query: {} } }, push: vi.fn() } }));
vi.mock('vue-i18n', () => ({ useI18n: () => ({ t: (key: string) => key }) }));
vi.mock('../components/ConfirmDialog.vue', () => ({ useConfirm: () => ({ confirm: dialogs.confirm }) }));
vi.mock('../components/PromptDialog.vue', () => ({ usePrompt: () => ({ prompt: dialogs.prompt }) }));
const root = 'sites/a.test', target = `${root}/klasör`;
const entry = (name = 'belge.txt', path = target): FileEntry => ({ path: `${path}/${name}`, name, isDirectory: false, size: 10, extension: name.endsWith('.zip') ? '.zip' : '.txt', lastModifiedUtc: null });
function workspace(path = target, entries = [entry()]): FileWorkspace { return { path, parentPath: path.slice(0, path.lastIndexOf('/')), displayName: path.split('/').pop()!, physicalPath: '', isDirectory: true, content: '', contentLoaded: false, entries }; }
function response(config: InternalAxiosRequestConfig, data: unknown): AxiosResponse { return { config, data: { success: true, data }, status: 200, statusText: 'OK', headers: {} }; }
function failure(config: InternalAxiosRequestConfig) { return new AxiosError('Request failed', 'ERR_BAD_RESPONSE', config, undefined, { ...response(config, null), status: 500, data: { success: false, message: 'files.acl_failed', details: 'ACL command failed processing 1 item.' } }); }
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(yes => { resolve = yes; }); return { promise, resolve }; }
async function flush() { for (let i = 0; i < 40; i++) await nextTick(); }
type Operation = { status: 'running' | 'succeeded' | 'unverified' | 'failed'; path: string; principal: string; rights: string; isDirectory: boolean; error: string; detail: string };
type State = { workspace: FileWorkspace; permissionOperation: Operation | null; permissionsBusy: boolean; fileMutationBusy: boolean; permissionPrincipal: string; permissionRights: string; notice: string; noticeError: boolean; archiveOperation: { status: string } | null; savePermissions(): Promise<void>; refresh(path?: string): Promise<unknown>; openPath(path: string): Promise<unknown>; clearNotice(): void; dismissPermissionOperation(): void; createFolder(): Promise<void>; removePath(entry: FileEntry): Promise<void>; compress(path: string): Promise<void>; extract(path: string): Promise<void> };
let app: App | undefined, calls: InternalAxiosRequestConfig[];
let read: (config: InternalAxiosRequestConfig) => Promise<AxiosResponse>, write: (config: InternalAxiosRequestConfig) => Promise<AxiosResponse>;
const original = apiClient.defaults.adapter;
const writes = () => calls.filter(c => c.method !== 'get');
beforeEach(() => {
  vi.resetAllMocks(); invalidateSessionRequests(); calls = [];
  vi.stubGlobal('window', { location: { pathname: '/panel' }, addEventListener: vi.fn(), removeEventListener: vi.fn() });
  vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify({ success: true, data: { requestToken: 'fixture' } }), { status: 200 })));
  dialogs.confirm.mockImplementation(async options => { await options.action?.(); return true; }); dialogs.prompt.mockResolvedValue('alt');
  read = async config => response(config, workspace(config.params.path));
  write = async config => response(config, { ...workspace(JSON.parse(String(config.data)).path), permissionsVerified: true, permissionsDetail: 'Processed 2 items; failed processing 0 items.' });
  apiClient.defaults.adapter = async config => { calls.push(config); return config.url === '/backup' ? response(config, []) : config.method === 'get' ? read(config) : write(config); };
});
afterEach(() => { app?.unmount(); app = undefined; apiClient.defaults.adapter = original; invalidateSessionRequests(); vi.unstubAllGlobals(); });
async function mount() {
  const pinia = createPinia(), auth = useAuthStore(pinia), sites = useSitesStore(pinia);
  auth.session = { isAuthenticated: true, requiresSetupWizard: false, user: { id: 'admin', role: 'Administrator', userName: 'admin', email: 'admin@example.test', preferredLanguage: 'en', isActive: true, isTwoFactorEnabled: false, lastLoginAtUtc: null } };
  sites.sites = [{ name: 'a.test' }, { name: 'b.test' }] as never; sites.selectedSiteName = 'a.test';
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  app = renderer.createApp({ ...FilesView, render: () => h('section') } as Component).use(pinia); app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: State }).setupState; await flush(); await state.openPath(target); state.clearNotice();
  return { state, auth, sites };
}

it('removes the previous verified success notice when the next permission request starts', async () => {
  const { state } = await mount(); await state.savePermissions(); expect(state.notice).toBe('panel.files.permissionsSaved');
  state.permissionRights = 'F'; const post = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
  write = config => (request = config, post.promise); const pending = state.savePermissions(); await flush();
  const noticeWhilePending = state.notice;
  post.resolve(response(request, { ...workspace(), permissionsVerified: null, permissionsDetail: 'Processing summary unavailable.' })); await pending;
  expect(noticeWhilePending).toBe(''); expect(state.noticeError).toBe(true); expect(state.notice).toContain('panel.files.permissionsUnverified');
});
it('keeps accepted target and command measurement after notice dismissal or a later form edit', async () => {
  const { state } = await mount(); state.permissionPrincipal = 'IIS_IUSRS'; state.permissionRights = 'R'; await state.savePermissions();
  state.clearNotice(); state.permissionPrincipal = 'OtherAccount'; state.permissionRights = 'F';
  expect(state.permissionOperation).toEqual({ status: 'succeeded', path: target, principal: 'IIS_IUSRS', rights: 'R', isDirectory: true, error: '', detail: 'Processed 2 items; failed processing 0 items.' });
  expect(JSON.parse(String(writes()[0]!.data))).toEqual({ path: target, principal: 'IIS_IUSRS', rights: 'R' });
  state.dismissPermissionOperation(); expect(state.permissionOperation).toBeNull();
});
it.each([undefined, null, false])('retains unverified (%s) instead of a previous verified result', async verified => {
  const { state } = await mount(); await state.savePermissions();
  write = async config => response(config, { ...workspace(), permissionsVerified: verified, permissionsDetail: 'Processing could not be measured.' });
  await state.savePermissions(); state.clearNotice();
  expect(state.permissionOperation).toMatchObject({ status: 'unverified', path: target, detail: 'Processing could not be measured.', error: '' });
});
it('keeps the command failure separately from its technical details and never infers success from a later GET', async () => {
  const { state } = await mount(); write = async config => { throw failure(config); }; await state.savePermissions(); state.clearNotice();
  expect(state.permissionOperation).toMatchObject({ status: 'failed', error: 'files.acl_failed', detail: 'ACL command failed processing 1 item.' });
  await state.refresh(); expect(state.permissionOperation?.status).toBe('failed'); expect(writes()).toHaveLength(1);
});
it.each(['unverified', 'failed'] as const)('keeps %s technical details in the result instead of repeating them in the notice', async outcome => {
  const { state } = await mount();
  write = async config => {
    if (outcome === 'failed') throw failure(config);
    return response(config, { ...workspace(), permissionsVerified: null, permissionsDetail: 'Native processing summary could not be measured.' });
  };
  await state.savePermissions();
  expect(state.permissionOperation).toMatchObject({ status: outcome, detail: outcome === 'failed'
    ? 'ACL command failed processing 1 item.' : 'Native processing summary could not be measured.' });
  expect(state.notice).toBe(outcome === 'failed' ? 'files.acl_failed' : 'panel.files.permissionsUnverified');
  expect(state.noticeError).toBe(true);
});
it('does not retain a success card for a response concerning a different file', async () => {
  const { state } = await mount(); write = async config => response(config, { ...workspace(`${root}/other`), permissionsVerified: true });
  await state.savePermissions(); expect(state.permissionOperation).toMatchObject({ status: 'failed', path: target, error: 'panel.files.resultUnconfirmed' });
});
it('keeps the pending operation locked and visible across a neutral same-folder refresh', async () => {
  const { state } = await mount(), post = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
  write = config => (request = config, post.promise); const pending = state.savePermissions(); await flush();
  await state.refresh(); await state.savePermissions(); await state.createFolder();
  const snapshot = state.permissionOperation && { ...state.permissionOperation }, busy = state.fileMutationBusy, writeCount = writes().length;
  state.dismissPermissionOperation(); const remained = state.permissionOperation?.status;
  post.resolve(response(request, { ...workspace(), permissionsVerified: true })); await pending;
  expect(snapshot).toMatchObject({ status: 'running', path: target }); expect(remained).toBe('running'); expect(busy).toBe(true); expect(writeCount).toBe(1); expect(dialogs.prompt).not.toHaveBeenCalled(); expect(state.fileMutationBusy).toBe(false);
  expect(state.permissionOperation?.status).toBe('succeeded');
});
it.each(['path', 'site', 'session', 'unmount'])('removes a retained result on %s departure and ignores a late second response', async context => {
  const { state, auth, sites } = await mount(); await state.savePermissions(); expect(state.permissionOperation?.status).toBe('succeeded');
  const post = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig; write = config => (request = config, post.promise);
  const pending = state.savePermissions(); await flush();
  if (context === 'path') await state.openPath(`${root}/other`);
  else if (context === 'site') { sites.selectSite('b.test'); await flush(); }
  else if (context === 'session') { auth.session = { ...auth.session!, user: { ...auth.session!.user! } }; await flush(); }
  else { app!.unmount(); app = undefined; }
  post.resolve(response(request, { ...workspace(), permissionsVerified: true })); await pending;
  expect(state.permissionOperation).toBeNull(); expect(state.notice).not.toBe('panel.files.permissionsSaved');
});
it('keeps the terminal result after a creation prompt is cancelled and retires it only at accepted dispatch', async () => {
  const { state } = await mount(); await state.savePermissions(); const completed = { ...state.permissionOperation! }, completedNotice = state.notice;
  dialogs.prompt.mockResolvedValueOnce(null); await state.createFolder(); expect(state.permissionOperation).toEqual(completed); expect(state.notice).toBe(completedNotice);
  const post = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig; write = config => (request = config, post.promise);
  const pending = state.createFolder(); await flush(); const atDispatch = state.permissionOperation, noticeAtDispatch = state.notice;
  post.resolve(response(request, workspace(`${target}/alt`))); await pending;
  expect(atDispatch).toBeNull(); expect(state.permissionOperation).toBeNull(); expect(noticeAtDispatch).toBe('');
});
it('preserves the result on cancelled deletion and retires it when the confirmed delete is dispatched', async () => {
  const { state } = await mount(); await state.savePermissions(); const completed = { ...state.permissionOperation! };
  dialogs.confirm.mockResolvedValueOnce(false); await state.removePath(entry()); expect(state.permissionOperation).toEqual(completed);
  const post = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig; write = config => (request = config, post.promise);
  const pending = state.removePath(entry()); await flush(); const atDispatch = state.permissionOperation;
  post.resolve(response(request, workspace(target, []))); await pending; expect(atDispatch).toBeNull();
});
it('keeps the permission result on cancelled archive confirmation but retires it for a new archive write', async () => {
  const { state } = await mount(); await state.savePermissions(); const completed = { ...state.permissionOperation! };
  dialogs.confirm.mockResolvedValueOnce(false); await state.extract(`${target}/data.zip`); expect(state.permissionOperation).toEqual(completed);
  write = async config => response(config, workspace(target, [entry(), entry('belge.txt.zip')])); await state.compress(entry().path);
  expect(state.permissionOperation).toBeNull(); expect(state.archiveOperation?.status).toBe('succeeded');
});
it('replaces a completed archive result with the permission request without changing archive behavior', async () => {
  const { state } = await mount(); write = async config => response(config, workspace(target, [entry(), entry('belge.txt.zip')])); await state.compress(entry().path);
  expect(state.archiveOperation?.status).toBe('succeeded');
  write = async config => response(config, { ...workspace(), permissionsVerified: true }); await state.savePermissions();
  expect(state.archiveOperation).toBeNull(); expect(state.permissionOperation?.status).toBe('succeeded');
});
