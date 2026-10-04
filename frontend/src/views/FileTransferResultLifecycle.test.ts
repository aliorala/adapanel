import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { createPinia } from 'pinia';
import { createRenderer, h, nextTick, ssrContextKey, type App, type Component, type Ref } from 'vue';
import { AxiosError, type AxiosResponse, type InternalAxiosRequestConfig } from 'axios';
import FilesView from './FilesView.vue';
import { apiClient, invalidateSessionRequests } from '../api/client';
import { useAuthStore } from '../stores/auth';
import { useSitesStore } from '../stores/sites';
import type { FileEntry, FileWorkspace } from '../api/files';

const dialogs = vi.hoisted(() => ({ confirm: vi.fn(), prompt: vi.fn() }));
vi.mock('../router', () => ({ default: { currentRoute: { value: { query: {} } }, push: vi.fn() } }));
vi.mock('vue-i18n', () => ({ useI18n: () => ({ t: (key: string, values?: object) => values ? `${key} ${JSON.stringify(values)}` : key }) }));
vi.mock('../components/ConfirmDialog.vue', () => ({ useConfirm: () => ({ confirm: dialogs.confirm }) }));
vi.mock('../components/PromptDialog.vue', () => ({ usePrompt: () => ({ prompt: dialogs.prompt }) }));
const root = 'sites/a.test', destination = `${root}/hedef`;
const entry = (name = 'kaynak.txt', parent = root): FileEntry => ({ path: `${parent}/${name}`, name, isDirectory: false, size: 10, extension: '.txt', lastModifiedUtc: null });
function workspace(path = root, names = ['kaynak.txt', 'ikinci.txt']): FileWorkspace { return { path, parentPath: 'sites', displayName: path, physicalPath: '', isDirectory: true, content: '', entries: names.map(name => entry(name, path)) }; }
function response(config: InternalAxiosRequestConfig, data: unknown): AxiosResponse { return { config, data: { success: true, data }, status: 200, statusText: 'OK', headers: {} }; }
function failure(config: InternalAxiosRequestConfig, key = 'files.transfer_interrupted') { return new AxiosError('Request failed', 'ERR_BAD_RESPONSE', config, undefined, { ...response(config, null), status: 500, data: { success: false, message: key, details: 'Fixture technical diagnostic.' } }); }
function transfer(config: InternalAxiosRequestConfig, outcomes: Array<'success' | 'warning' | 'failed'> = []) {
  const request = JSON.parse(String(config.data)), copy = config.url === '/files/copy';
  const items = (request.paths as string[]).map((path, index) => ({ sourcePath: path,
    targetPath: outcomes[index] === 'failed' ? '' : `${request.destinationPath}/${request.newName || path.split('/').pop()}`,
    [copy ? 'copied' : 'moved']: outcomes[index] !== 'failed', errorKey: outcomes[index] === 'failed' ? 'files.move_target_exists' : outcomes[index] === 'warning' ? 'files.copy_link_skipped' : '' }));
  return response(config, { items, [copy ? 'copiedCount' : 'movedCount']: items.filter(item => item[copy ? 'copied' : 'moved']).length,
    failedCount: items.filter(item => !item[copy ? 'copied' : 'moved']).length, workspace: workspace(request.destinationPath) });
}
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(yes => { resolve = yes; }); return { promise, resolve }; }
async function flush() { for (let i = 0; i < 40; i++) await nextTick(); }
type Operation = { kind: 'move' | 'copy' | 'rename'; status: 'running' | 'refreshing' | 'succeeded' | 'partial' | 'failed'; sourcePath: string; destinationPath: string; newName: string; overwrite: boolean; total: number; done: number | null; failed: number | null; items: { sourcePath: string; targetPath: string; success: boolean; reason: string }[]; error: string; detail: string; refreshError: string };
type State = { workspace: FileWorkspace; transferOperation: Operation | null; notice: string; noticeError: boolean; transferBusy: boolean; fileMutationBusy: boolean; transferMode: 'copy' | 'move' | null; transferTarget: string; transferOverwrite: boolean; selection: { selected: Ref<string[]>; toggle(path: string): void }; startTransfer(kind: 'copy' | 'move'): void; runTransfer(): Promise<void>; renamePath(entry: FileEntry): Promise<void>; createFolder(): Promise<void>; clearNotice(): void; dismissTransferOperation(): void; refreshTransferListing(): Promise<void>; openTransferDestination(): Promise<unknown>; refresh(path?: string): Promise<unknown>; openPath(path: string): Promise<unknown> };
let app: App | undefined, calls: InternalAxiosRequestConfig[];
let read: (config: InternalAxiosRequestConfig) => Promise<AxiosResponse>, write: (config: InternalAxiosRequestConfig) => Promise<AxiosResponse>;
const original = apiClient.defaults.adapter;
const writes = () => calls.filter(c => c.method !== 'get');
const fileReads = () => calls.filter(c => c.url === '/files' && c.method === 'get');
beforeEach(() => {
  vi.resetAllMocks(); invalidateSessionRequests(); calls = [];
  vi.stubGlobal('window', { location: { pathname: '/panel' }, addEventListener: vi.fn(), removeEventListener: vi.fn() });
  vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify({ success: true, data: { requestToken: 'fixture' } }), { status: 200 })));
  dialogs.confirm.mockImplementation(async options => { await options.action?.(); return true; }); dialogs.prompt.mockResolvedValue('yeni-ad.txt');
  read = async config => response(config, workspace(config.params.path)); write = async config => transfer(config);
  apiClient.defaults.adapter = async config => { calls.push(config); return config.url === '/backup' ? response(config, []) : config.method === 'get' ? read(config) : write(config); };
});
afterEach(() => { app?.unmount(); app = undefined; apiClient.defaults.adapter = original; invalidateSessionRequests(); vi.unstubAllGlobals(); });
async function mount() {
  const pinia = createPinia(), auth = useAuthStore(pinia), sites = useSitesStore(pinia);
  auth.session = { isAuthenticated: true, requiresSetupWizard: false, user: { id: 'admin', role: 'Administrator', userName: 'admin', email: 'admin@example.test', preferredLanguage: 'en', isActive: true, isTwoFactorEnabled: false, lastLoginAtUtc: null } };
  sites.sites = [{ name: 'a.test' }, { name: 'b.test' }] as never; sites.selectedSiteName = 'a.test';
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  app = renderer.createApp({ ...FilesView, render: () => h('section') } as Component).use(pinia); app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: State }).setupState; await flush(); state.clearNotice(); return { state, auth, sites };
}
function select(state: State, mode: 'copy' | 'move') { for (const row of state.workspace.entries) state.selection.toggle(row.path); state.startTransfer(mode); state.transferTarget = destination; }

it.each(['copy', 'move'] as const)('keeps measured partial %s rows after dismissing the notice and clearing the selection', async kind => {
  const { state } = await mount(); select(state, kind); write = async config => transfer(config, ['success', 'failed']); await state.runTransfer();
  expect(state.notice).not.toContain(`${root}/ikinci.txt`); state.clearNotice();
  expect(state.transferOperation).toMatchObject({ kind, status: 'partial', sourcePath: root, destinationPath: destination, overwrite: false, total: 2, done: 1, failed: 1, error: '', refreshError: '',
    items: [{ sourcePath: `${root}/kaynak.txt`, targetPath: `${destination}/kaynak.txt`, success: true, reason: '' }, { sourcePath: `${root}/ikinci.txt`, targetPath: '', success: false, reason: 'files.move_target_exists' }] });
  expect(state.selection.selected.value).toEqual([]); expect(state.transferMode).toBeNull(); expect(writes()).toHaveLength(1);
});
it('keeps a copied-but-incomplete item distinct from both a complete copy and a failed item', async () => {
  const { state } = await mount(); select(state, 'copy'); write = async config => transfer(config, ['success', 'warning']); await state.runTransfer();
  expect(state.transferOperation).toMatchObject({ status: 'partial', done: 2, failed: 0, items: [{ success: true, reason: '' }, { success: true, reason: 'files.copy_link_skipped' }] });
});
it('reports all per-item failures without inventing actual destination paths', async () => {
  const { state } = await mount(); select(state, 'move'); write = async config => transfer(config, ['failed', 'failed']); await state.runTransfer();
  expect(state.transferOperation).toMatchObject({ status: 'failed', done: 0, failed: 2, items: [{ targetPath: '', success: false }, { targetPath: '', success: false }] });
});
it.each(['copy', 'move', 'rename'] as const)('remeasures the source after a %s exception but never converts unknown effects into success', async kind => {
  const { state } = await mount(); if (kind !== 'rename') select(state, kind);
  write = async config => { throw failure(config); }; read = async config => response(config, workspace(root, ['ikinci.txt']));
  if (kind === 'rename') await state.renamePath(entry()); else await state.runTransfer();
  expect(fileReads()).toHaveLength(2); expect(state.workspace.entries.map(item => item.name)).toEqual(['ikinci.txt']);
  expect(state.transferOperation).toMatchObject({ kind, status: 'failed', done: null, failed: null, items: [], error: 'files.transfer_interrupted', detail: 'Fixture technical diagnostic.', refreshError: '' });
  expect(state.notice).toBe('files.transfer_interrupted');
});
it.each(['copy', 'move', 'rename'] as const)('preserves a measured %s result when the source refresh fails and retries only GET', async kind => {
  const { state } = await mount(); if (kind !== 'rename') select(state, kind);
  read = async config => { throw failure(config, 'files.fixture_list_failed'); };
  if (kind === 'rename') await state.renamePath(entry()); else await state.runTransfer();
  const total = kind === 'rename' ? 1 : 2;
  expect(state.transferOperation).toMatchObject({ kind, status: 'succeeded', done: total, failed: 0, total, refreshError: 'panel.files.refreshAfterOperationFailed' });
  const completedItems = state.transferOperation!.items.map(item => ({ ...item })); state.clearNotice();
  read = async config => response(config, workspace(root, ['unrelated.txt'])); await state.refreshTransferListing();
  expect(state.transferOperation).toMatchObject({ status: 'succeeded', refreshError: '', items: completedItems }); expect(writes()).toHaveLength(1);
});
it('retains the original unknown failure if reconciliation fails and later succeeds', async () => {
  const { state } = await mount(); select(state, 'move'); write = async config => { throw failure(config); }; read = async config => { throw failure(config, 'files.fixture_list_failed'); };
  await state.runTransfer(); expect(state.transferOperation).toMatchObject({ status: 'failed', error: 'files.transfer_interrupted', refreshError: 'panel.files.refreshAfterOperationFailed' });
  read = async config => response(config, workspace(root, [])); await state.refreshTransferListing();
  expect(state.transferOperation).toMatchObject({ status: 'failed', done: null, failed: null, error: 'files.transfer_interrupted', refreshError: '' }); expect(writes()).toHaveLength(1);
});
it('publishes item measurement before a deferred refresh finishes and keeps the common write lock', async () => {
  const { state } = await mount(); select(state, 'copy'); const get = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
  read = config => (request = config, get.promise); const pending = state.runTransfer(); await flush();
  const snapshot = state.transferOperation && { ...state.transferOperation }, busy = state.fileMutationBusy;
  await state.createFolder(); state.dismissTransferOperation(); const afterDismiss = state.transferOperation?.status;
  get.resolve(response(request, workspace())); await pending;
  expect(snapshot).toMatchObject({ status: 'refreshing', done: 2, failed: 0 }); expect(busy).toBe(true); expect(afterDismiss).toBe('refreshing'); expect(dialogs.prompt).not.toHaveBeenCalled(); expect(state.fileMutationBusy).toBe(false);
});
it('keeps accepted rename name and exact endpoint result instead of the later form or list state', async () => {
  const { state } = await mount(); await state.renamePath(entry()); state.clearNotice();
  expect(JSON.parse(String(writes()[0]!.data))).toEqual({ paths: [entry().path], destinationPath: root, newName: 'yeni-ad.txt', overwrite: false });
  expect(state.transferOperation).toMatchObject({ kind: 'rename', newName: 'yeni-ad.txt', destinationPath: root, status: 'succeeded', items: [{ sourcePath: entry().path, targetPath: `${root}/yeni-ad.txt`, success: true }] });
});
it('rejects a wrong transfer record and reconciles without publishing its false success', async () => {
  const { state } = await mount(); select(state, 'copy');
  write = async config => { const result = transfer(config); result.data.data.items[0].targetPath = 'sites/b.test/private.txt'; return result; };
  await state.runTransfer(); expect(state.transferOperation).toMatchObject({ status: 'failed', done: null, failed: null, items: [], error: 'panel.files.resultUnconfirmed' }); expect(fileReads()).toHaveLength(2);
});
it.each(['path', 'site', 'session', 'unmount'])('does not publish or reconcile an old transfer after %s departure', async context => {
  const { state, sites, auth } = await mount(); select(state, 'copy'); const post = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
  write = config => (request = config, post.promise); const pending = state.runTransfer(); await flush(); const running = state.transferOperation?.status;
  if (context === 'path') await state.openPath(`${root}/other`); else if (context === 'site') { sites.selectSite('b.test'); await flush(); }
  else if (context === 'session') { auth.session = { ...auth.session!, user: { ...auth.session!.user! } }; await flush(); } else { app!.unmount(); app = undefined; }
  const reads = fileReads().length; post.resolve(transfer(request)); await pending;
  expect(running).toBe('running'); expect(state.transferOperation).toBeNull(); expect(fileReads()).toHaveLength(reads); expect(state.notice).toBe('');
});
it('keeps the source outcome after neutral refresh and ignores a late read from before reconciliation', async () => {
  const { state } = await mount(); select(state, 'move'); const old = deferred<AxiosResponse>(), post = deferred<void>(); let request!: InternalAxiosRequestConfig;
  write = async config => { await post.promise; throw failure(config); }; const pending = state.runTransfer(); await flush();
  read = config => (request = config, old.promise); const reading = state.refresh(); await flush();
  read = async config => response(config, workspace(root, [])); post.resolve(); await pending;
  old.resolve(response(request, workspace())); await reading;
  expect(state.workspace.entries).toEqual([]); expect(state.transferOperation?.status).toBe('failed');
  await state.refresh(); expect(state.transferOperation?.status).toBe('failed');
});
it.each(['older-first', 'newer-first'])('settles the measured result when a neutral refresh supersedes its reconciliation (%s)', async order => {
  const { state } = await mount(); select(state, 'copy');
  const older = deferred<AxiosResponse>(), newer = deferred<AxiosResponse>();
  let oldRequest!: InternalAxiosRequestConfig, newRequest!: InternalAxiosRequestConfig;
  read = config => (oldRequest = config, older.promise);
  const mutation = state.runTransfer(); await flush(); expect(state.transferOperation?.status).toBe('refreshing');
  read = config => (newRequest = config, newer.promise);
  const manualRead = state.refresh(); await flush();
  if (order === 'older-first') { older.resolve(response(oldRequest, workspace())); await mutation; }
  newer.resolve(response(newRequest, workspace(root, ['latest.txt']))); await manualRead;
  if (order === 'newer-first') { older.resolve(response(oldRequest, workspace())); await mutation; }
  expect(state.workspace.entries.map(item => item.name)).toEqual(['latest.txt']);
  expect(state.transferOperation).toMatchObject({ status: 'succeeded', done: 2, failed: 0, refreshError: '' });
  expect(writes()).toHaveLength(1); state.dismissTransferOperation(); expect(state.transferOperation).toBeNull();
});
it('preserves a result on cancelled rename/create prompts and retires it at the next actual write', async () => {
  const { state } = await mount(); select(state, 'copy'); await state.runTransfer(); const completed = { ...state.transferOperation! };
  dialogs.prompt.mockResolvedValueOnce(null); await state.renamePath(entry()); expect(state.transferOperation).toEqual(completed);
  dialogs.prompt.mockResolvedValueOnce(null); await state.createFolder(); expect(state.transferOperation).toEqual(completed);
  const post = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig; write = config => (request = config, post.promise);
  const pending = state.createFolder(); await flush(); const atDispatch = state.transferOperation;
  post.resolve(response(request, workspace(`${root}/yeni-ad.txt`))); await pending; expect(atDispatch).toBeNull();
});
it('opens the captured destination through a read without replaying the transfer', async () => {
  const { state } = await mount(); select(state, 'copy'); await state.runTransfer(); await state.openTransferDestination();
  expect(state.workspace.path).toBe(destination); expect(writes()).toHaveLength(1); expect(state.transferOperation).toBeNull();
});
it('opens the destination after both an unknown write failure and a failed source refresh without replaying POST', async () => {
  const { state } = await mount(); select(state, 'move');
  write = async config => { throw failure(config); };
  read = async config => { throw failure(config, 'files.fixture_list_failed'); };
  await state.runTransfer();
  expect(state.transferOperation).toMatchObject({ status: 'failed', done: null, refreshError: 'panel.files.refreshAfterOperationFailed' });
  const get = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
  read = config => (request = config, get.promise);
  const opening = state.openTransferDestination(); await flush();
  expect(request.method).toBe('get'); expect(request.url).toBe('/files'); expect(request.params.path).toBe(destination);
  get.resolve(response(request, workspace(destination, ['transferred-before-error.txt']))); await opening;
  expect(state.workspace.path).toBe(destination); expect(state.workspace.entries[0]?.name).toBe('transferred-before-error.txt');
  expect(state.transferOperation).toBeNull(); expect(writes()).toHaveLength(1); expect(fileReads()).toHaveLength(3);
});
