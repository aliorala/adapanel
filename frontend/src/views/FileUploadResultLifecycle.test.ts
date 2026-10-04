import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { createPinia } from 'pinia';
import { createRenderer, h, nextTick, ssrContextKey, type App, type Component } from 'vue';
import { AxiosError, type AxiosResponse, type InternalAxiosRequestConfig } from 'axios';
import FilesView from './FilesView.vue';
import { apiClient, invalidateSessionRequests } from '../api/client';
import { useAuthStore } from '../stores/auth';
import { useSitesStore } from '../stores/sites';
import { useSubscriptionStore } from '../stores/subscription';
import type { FileWorkspace } from '../api/files';

const dialogs = vi.hoisted(() => ({ confirm: vi.fn(), prompt: vi.fn() }));
vi.mock('../router', () => ({ default: { currentRoute: { value: { query: {} } }, push: vi.fn() } }));
vi.mock('vue-i18n', () => ({ useI18n: () => ({ t: (key: string, values?: object) => values ? `${key} ${JSON.stringify(values)}` : key }) }));
vi.mock('../components/ConfirmDialog.vue', () => ({ useConfirm: () => ({ confirm: dialogs.confirm }) }));
vi.mock('../components/PromptDialog.vue', () => ({ usePrompt: () => ({ prompt: dialogs.prompt }) }));
const root = 'sites/a.test';
function workspace(path = root, names: string[] = []): FileWorkspace { return { path, parentPath: 'sites', displayName: path, physicalPath: '', isDirectory: true, content: '', entries: names.map(name => ({ path: `${path}/${name}`, name, isDirectory: false, size: 10, extension: '.txt', lastModifiedUtc: null })) }; }
function response(config: InternalAxiosRequestConfig, data: unknown): AxiosResponse { return { config, data: { success: true, data }, status: 200, statusText: 'OK', headers: {} }; }
function failure(config: InternalAxiosRequestConfig, key = 'files.upload_interrupted') { return new AxiosError('Request failed', 'ERR_BAD_RESPONSE', config, undefined, { ...response(config, null), status: 500, data: { success: false, message: key, details: 'Upload fixture diagnostic.' } }); }
const file = (name = 'first.txt', content = 'content') => new File([content], name);
const uploaded = (config: InternalAxiosRequestConfig) => response(config, { path: `${config.params.path}/${((config.data as FormData).get('file') as File).name}` });
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(yes => { resolve = yes; }); return { promise, resolve }; }
async function flush() { for (let i = 0; i < 40; i++) await nextTick(); }
type Operation = { status: 'running' | 'refreshing' | 'succeeded' | 'partial' | 'failed' | 'stopped'; destinationPath: string; total: number; processed: number; done: number; unconfirmed: number; skipped: number; notSent: number; stopRequested: boolean; stopReason: string; items: { id: number; name: string; size: number; requestedPath: string; path: string; status: 'queued' | 'running' | 'succeeded' | 'unconfirmed' | 'skipped' | 'not-sent'; error: string; detail: string }[]; folders: string[]; refreshError: string };
type State = { workspace: FileWorkspace; notice: string; noticeError: boolean; fileMutationBusy: boolean; uploadBusy: boolean; uploadDone: number; uploadPercent: number; uploadOperation: Operation | null; uploadMany(files: File[], folders: string[]): Promise<void>; onDrop(event: DragEvent): Promise<void>; refresh(path?: string): Promise<unknown>; openPath(path: string): Promise<unknown>; clearNotice(): void; stopQueuedUploads(): void; dismissUploadOperation(): void; refreshUploadListing(): Promise<void>; createFolder(): Promise<void>; compress(path: string): Promise<void> };
let app: App | undefined, calls: InternalAxiosRequestConfig[];
let read: (config: InternalAxiosRequestConfig) => Promise<AxiosResponse>, write: (config: InternalAxiosRequestConfig) => Promise<AxiosResponse>;
const original = apiClient.defaults.adapter;
const uploads = () => calls.filter(c => c.url === '/files/upload');
const fileReads = () => calls.filter(c => c.url === '/files' && c.method === 'get');
beforeEach(() => {
  vi.resetAllMocks(); invalidateSessionRequests(); calls = [];
  vi.stubGlobal('window', { location: { pathname: '/panel' }, addEventListener: vi.fn(), removeEventListener: vi.fn() });
  vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify({ success: true, data: { requestToken: 'fixture' } }), { status: 200 })));
  dialogs.confirm.mockImplementation(async options => { await options.action?.(); return true; }); dialogs.prompt.mockResolvedValue('new-folder');
  read = async config => response(config, workspace(config.params.path)); write = async config => uploaded(config);
  apiClient.defaults.adapter = async config => { calls.push(config); return config.url === '/backup' ? response(config, []) : config.method === 'get' ? read(config) : write(config); };
});
afterEach(() => { app?.unmount(); app = undefined; apiClient.defaults.adapter = original; invalidateSessionRequests(); vi.unstubAllGlobals(); });
async function mount() {
  const pinia = createPinia(), auth = useAuthStore(pinia), sites = useSitesStore(pinia), subscription = useSubscriptionStore(pinia);
  auth.session = { isAuthenticated: true, requiresSetupWizard: false, user: { id: 'customer', role: 'Customer', userName: 'customer', email: 'customer@example.test', preferredLanguage: 'en', isActive: true, isTwoFactorEnabled: false, lastLoginAtUtc: null } };
  sites.sites = [{ name: 'a.test' }, { name: 'b.test' }] as never; sites.selectedSiteName = 'a.test';
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  app = renderer.createApp({ ...FilesView, render: () => h('section') } as Component).use(pinia); app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: State }).setupState; await flush(); state.clearNotice(); return { state, auth, sites, subscription };
}

it('preserves confirmed, unconfirmed and skipped-folder outcomes after closing the short notice', async () => {
  const { state } = await mount(); write = async config => { if (((config.data as FormData).get('file') as File).name === 'second.txt') throw failure(config); return uploaded(config); };
  await state.uploadMany([file('first.txt', 'private bytes'), file('second.txt'), file('third.txt')], ['photos']);
  expect(state.notice).not.toContain('Upload fixture diagnostic.'); expect(state.notice).not.toContain('second.txt'); state.clearNotice();
  expect(state.uploadOperation).toMatchObject({ status: 'partial', destinationPath: root, total: 3, processed: 3, done: 2, unconfirmed: 1, skipped: 0, notSent: 0, folders: ['photos'], refreshError: '',
    items: [{ name: 'first.txt', path: `${root}/first.txt`, status: 'succeeded' }, { name: 'second.txt', path: '', status: 'unconfirmed', error: 'files.upload_interrupted', detail: 'Upload fixture diagnostic.' }, { name: 'third.txt', status: 'succeeded' }] });
  expect(JSON.stringify(state.uploadOperation)).not.toContain('private bytes'); expect(uploads()).toHaveLength(3);
});
it('does not infer successful writes from a refreshed listing after all upload responses failed', async () => {
  const { state } = await mount(); write = async config => { throw failure(config); }; read = async config => response(config, workspace(root, ['first.txt']));
  await state.uploadMany([file()], []);
  expect(state.uploadOperation).toMatchObject({ status: 'failed', done: 0, unconfirmed: 1, processed: 1, items: [{ path: '', status: 'unconfirmed' }] });
  expect(state.workspace.entries[0]?.name).toBe('first.txt'); expect(fileReads()).toHaveLength(2);
});
it('keeps malformed successful responses unconfirmed while continuing the remaining authorized files', async () => {
  const { state } = await mount(); write = async config => uploads().length === 1 ? response(config, { path: `${root}/wrong.txt` }) : uploaded(config);
  await state.uploadMany([file(), file('second.txt')], []);
  expect(state.uploadOperation).toMatchObject({ status: 'partial', done: 1, unconfirmed: 1, items: [{ status: 'unconfirmed', error: 'panel.files.resultUnconfirmed' }, { status: 'succeeded' }] }); expect(uploads()).toHaveLength(2);
});
it.each(['confirmed', 'unconfirmed'])('skips the later case-insensitive duplicate after a %s first response instead of overwriting its target', async outcome => {
  const { state } = await mount(); write = async config => { if (outcome === 'unconfirmed') throw failure(config); return uploaded(config); };
  await state.uploadMany([file('Report.txt', 'first content'), file('REPORT.TXT', 'second content')], []);
  expect(uploads()).toHaveLength(1); expect(state.uploadOperation).toMatchObject({ status: 'partial', processed: 2, skipped: 1, items: [{ name: 'Report.txt' }, { name: 'REPORT.TXT', path: '', status: 'skipped', error: 'panel.files.uploadDuplicateSkipped' }] });
  expect(new Set(state.uploadOperation!.items.map(item => item.id)).size).toBe(2);
});
it('retains existing-target replacement compatibility and accepts an empty Unicode-named file', async () => {
  const { state } = await mount(); read = async config => response(config, workspace(root, ['boş-çığ.txt'])); await state.refresh();
  await state.uploadMany([file('boş-çığ.txt', '')], []);
  expect(state.uploadOperation).toMatchObject({ status: 'succeeded', done: 1, processed: 1, items: [{ name: 'boş-çığ.txt', size: 0, path: `${root}/boş-çığ.txt`, status: 'succeeded' }] }); expect(uploads()).toHaveLength(1);
});
it.each(['confirmed', 'unconfirmed'])('keeps %s upload results through a failed final read and a GET-only retry', async outcome => {
  const { state } = await mount(); if (outcome === 'unconfirmed') write = async config => { throw failure(config); };
  read = async config => { throw failure(config, 'files.fixture_list_failed'); }; await state.uploadMany([file()], []);
  expect(state.uploadOperation?.refreshError).toBe('panel.files.refreshAfterOperationFailed'); const items = state.uploadOperation!.items.map(item => ({ ...item })); state.clearNotice();
  read = async config => response(config, workspace(root, ['unrelated.txt'])); await state.refreshUploadListing();
  expect(state.uploadOperation).toMatchObject({ status: outcome === 'confirmed' ? 'succeeded' : 'failed', items, refreshError: '' }); expect(uploads()).toHaveLength(1); expect(fileReads()).toHaveLength(3);
});
it.each(['older-first', 'newer-first'])('settles the final outcome when a neutral GET supersedes upload reconciliation (%s)', async order => {
  const { state } = await mount(); const older = deferred<AxiosResponse>(), newer = deferred<AxiosResponse>(); let oldRequest!: InternalAxiosRequestConfig, newRequest!: InternalAxiosRequestConfig;
  read = config => (oldRequest = config, older.promise); const uploading = state.uploadMany([file()], []); await flush();
  read = config => (newRequest = config, newer.promise); const manual = state.refresh(); await flush();
  if (order === 'older-first') { older.resolve(response(oldRequest, workspace())); await uploading; }
  newer.resolve(response(newRequest, workspace(root, ['latest.txt']))); await manual;
  if (order === 'newer-first') { older.resolve(response(oldRequest, workspace())); await uploading; }
  expect(state.uploadOperation).toMatchObject({ status: 'succeeded', done: 1, processed: 1, refreshError: '' }); expect(state.workspace.entries[0]?.name).toBe('latest.txt'); state.dismissUploadOperation(); expect(state.uploadOperation).toBeNull();
});
it.each(['confirmed', 'unconfirmed'])('stops only queued files after the in-flight response is %s, then reconciles without a second POST', async outcome => {
  const { state } = await mount(); const post = deferred<void>(); write = async config => { await post.promise; if (outcome === 'unconfirmed') throw failure(config); return uploaded(config); };
  const uploading = state.uploadMany([file(), file('second.txt'), file('third.txt')], []); await flush();
  state.stopQueuedUploads(); expect(state.fileMutationBusy).toBe(true); expect(state.uploadOperation?.stopRequested).toBe(true);
  post.resolve(); await uploading;
  expect(state.uploadOperation).toMatchObject({ status: 'stopped', processed: 1, done: outcome === 'confirmed' ? 1 : 0, unconfirmed: outcome === 'unconfirmed' ? 1 : 0, notSent: 2, stopReason: 'panel.files.uploadStoppedByUser', items: [{ status: outcome === 'confirmed' ? 'succeeded' : 'unconfirmed' }, { status: 'not-sent' }, { status: 'not-sent' }] });
  expect(uploads()).toHaveLength(1); expect(fileReads()).toHaveLength(2);
});
it('reports remaining files as not sent when the same subscription becomes read-only mid-queue', async () => {
  const { state, subscription } = await mount(); const post = deferred<void>(); write = async config => { await post.promise; return uploaded(config); };
  const uploading = state.uploadMany([file(), file('second.txt')], []); await flush();
  subscription.usage = { isSuspended: true } as never; subscription.applyReadOnly();
  post.resolve(); await uploading;
  expect(state.uploadOperation).toMatchObject({ status: 'stopped', done: 1, notSent: 1, stopReason: 'panel.subscription.readOnlyAction' }); expect(uploads()).toHaveLength(1); expect(fileReads()).toHaveLength(2);
});
it('shows settled queue progress rather than successful-file count while keeping all mutations reserved', async () => {
  const { state } = await mount(); const post = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
  write = async config => { if (uploads().length === 1) throw failure(config); request = config; return post.promise; };
  const uploading = state.uploadMany([file(), file('FIRST.TXT'), file('last.txt')], []); await flush();
  const progress = state.uploadPercent; const statuses = state.uploadOperation?.items.map(item => item.status); state.dismissUploadOperation();
  await state.createFolder(); await state.uploadMany([file('other.txt')], []);
  expect(dialogs.prompt).not.toHaveBeenCalled(); expect(uploads()).toHaveLength(2); expect(progress).toBe(67); expect(statuses).toEqual(['unconfirmed', 'skipped', 'running']); expect(state.uploadOperation).not.toBeNull();
  post.resolve(uploaded(request)); await uploading; expect(state.uploadOperation).toMatchObject({ status: 'partial', done: 1, processed: 3, unconfirmed: 1, skipped: 1 });
});
it.each(['path', 'site', 'session', 'unmount'])('hides the upload result and prevents queued requests or refresh after %s departure', async context => {
  const { state, sites, auth } = await mount(); const post = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
  write = config => (request = config, post.promise); const uploading = state.uploadMany([file(), file('second.txt')], []); await flush(); expect(state.uploadOperation?.status).toBe('running');
  if (context === 'path') await state.openPath(`${root}/other`); else if (context === 'site') { sites.selectSite('b.test'); await flush(); }
  else if (context === 'session') { auth.session = { ...auth.session!, user: { ...auth.session!.user! } }; await flush(); } else { app!.unmount(); app = undefined; }
  const reads = fileReads().length; post.resolve(uploaded(request)); await uploading;
  expect(state.uploadOperation).toBeNull(); expect(uploads()).toHaveLength(1); expect(fileReads()).toHaveLength(reads); expect(state.notice).toBe('');
});
it('keeps folders-only local outcomes visible without POST and leaves them unchanged for an empty selection', async () => {
  const { state } = await mount(); await state.uploadMany([], ['photos', 'logs']); state.clearNotice();
  expect(state.uploadOperation).toMatchObject({ status: 'partial', total: 0, processed: 0, done: 0, folders: ['photos', 'logs'], items: [] });
  const before = state.uploadOperation; await state.uploadMany([], []); expect(state.uploadOperation).toBe(before); expect(uploads()).toHaveLength(0); expect(fileReads()).toHaveLength(1);
});
it('preserves a terminal outcome when a create prompt is cancelled and retires it only at the next write dispatch', async () => {
  const { state } = await mount(); await state.uploadMany([file()], []); const completed = state.uploadOperation;
  dialogs.prompt.mockResolvedValueOnce(null); await state.createFolder(); expect(state.uploadOperation).toBe(completed);
  const post = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig; write = config => (request = config, post.promise);
  const creating = state.createFolder(); await flush(); expect(state.uploadOperation).toBeNull();
  post.resolve(response(request, workspace(`${root}/new-folder`))); await creating;
});
it('keeps the measured outcome reserved during reconciliation and does not mislabel completed uploads as stopped', async () => {
  const { state } = await mount(); const get = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
  read = config => (request = config, get.promise); const uploading = state.uploadMany([file()], []); await flush();
  expect(state.uploadOperation).toMatchObject({ status: 'refreshing', done: 1, processed: 1 }); expect(state.fileMutationBusy).toBe(true);
  state.stopQueuedUploads(); state.dismissUploadOperation(); expect(state.uploadOperation?.stopRequested).toBe(false);
  get.resolve(response(request, workspace())); await uploading; expect(state.uploadOperation?.status).toBe('succeeded');
});
it('retires the completed upload card when a subsequent archive write is dispatched', async () => {
  const { state } = await mount(); read = async config => response(config, workspace(root, ['first.txt']));
  await state.uploadMany([file()], []); expect(state.uploadOperation?.status).toBe('succeeded');
  const post = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig; write = config => (request = config, post.promise);
  const compressing = state.compress(`${root}/first.txt`); await flush(); const uploadAtDispatch = state.uploadOperation;
  post.resolve(response(request, workspace(root, ['first.txt', 'first.txt.zip']))); await compressing;
  expect(uploadAtDispatch).toBeNull();
});
function dropFile(state: State) {
  return state.onDrop({ preventDefault: vi.fn(), dataTransfer: { types: ['Files'], files: [file()],
    items: [{ kind: 'file', webkitGetAsEntry: () => ({ isDirectory: false }), getAsFile: () => file() }] } } as unknown as DragEvent);
}
it('does not drop files into the old folder while navigation is waiting for its new listing', async () => {
  const { state } = await mount(); const target = `${root}/other`, get = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
  read = config => config.params.path === target ? (request = config, get.promise) : Promise.resolve(response(config, workspace(root)));
  const navigating = state.openPath(target); await flush(); await dropFile(state); const premature = uploads().length;
  get.resolve(response(request, workspace(target))); await navigating;
  read = async config => response(config, workspace(config.params.path)); await dropFile(state);
  expect(premature).toBe(0); expect(uploads()).toHaveLength(1); expect(uploads()[0]?.params.path).toBe(target);
});
it('rejects a drop on an unconfirmed old listing and allows it after a successful read retry', async () => {
  const { state } = await mount(); read = async config => { throw failure(config, 'files.fixture_list_failed'); };
  await state.refresh(); await dropFile(state); const premature = uploads().length;
  read = async config => response(config, workspace(config.params.path)); await state.refresh(); await dropFile(state);
  expect(premature).toBe(0); expect(uploads()).toHaveLength(1); expect(state.uploadOperation).toMatchObject({ status: 'succeeded', done: 1 });
});
