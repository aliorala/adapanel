import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { createPinia } from 'pinia';
import { createI18n } from 'vue-i18n';
import { createRenderer, h, nextTick, ssrContextKey, type App, type Component } from 'vue';
import { AxiosError, type AxiosResponse, type InternalAxiosRequestConfig } from 'axios';
import FilesView from './FilesView.vue';
import ConfirmDialog, { closeConfirmDialog, dialogState } from '../components/ConfirmDialog.vue';
import { apiClient, invalidateSessionRequests } from '../api/client';
import { useAuthStore } from '../stores/auth';
import { useSitesStore } from '../stores/sites';
import { useSubscriptionStore } from '../stores/subscription';
import { safeMessageCompiler } from '../i18n';
import en from '../i18n/en.json';
import type { FileWorkspace } from '../api/files';

vi.mock('../router', () => ({ default: { currentRoute: { value: { query: {} } }, push: vi.fn() } }));
const root = 'sites/a.test', path = `${root}/config.txt`;
function workspace(value = root, content = 'original'): FileWorkspace { return { path: value, parentPath: value === root ? 'sites' : root, displayName: value, physicalPath: '', isDirectory: !value.endsWith('.txt'), content: value.endsWith('.txt') ? content : '', contentLoaded: value.endsWith('.txt'), entries: [] }; }
function response(config: InternalAxiosRequestConfig, data: unknown): AxiosResponse { return { config, data: { success: true, data }, status: 200, statusText: 'OK', headers: {} }; }
function failure(config: InternalAxiosRequestConfig) { return new AxiosError('Response lost', 'ERR_BAD_RESPONSE', config, undefined, { ...response(config, null), status: 500, data: { success: false, message: 'panel.files.resultUnconfirmed' } }); }
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(yes => { resolve = yes; }); return { promise, resolve }; }
async function flush() { for (let i = 0; i < 40; i++) await nextTick(); }
type State = { workspace: FileWorkspace; fileContent: string; savedContent: string; isDirty: boolean; notice: string; noticeError: boolean; saving: boolean; openPath(path: string): Promise<unknown>; refresh(path?: string): Promise<unknown>; saveCurrentFile(): Promise<boolean> };
type Dialog = { accept(): Promise<void>; alternative(): void; dismiss(): void };
let app: App | undefined, calls: InternalAxiosRequestConfig[];
let write: (config: InternalAxiosRequestConfig) => Promise<AxiosResponse>;
const original = apiClient.defaults.adapter;
const writes = () => calls.filter(c => c.method === 'put');
const reads = () => calls.filter(c => c.url === '/files' && c.method === 'get');
beforeEach(() => {
  invalidateSessionRequests(); calls = []; closeConfirmDialog();
  vi.stubGlobal('window', { location: { pathname: '/panel' }, addEventListener: vi.fn(), removeEventListener: vi.fn() });
  vi.stubGlobal('document', { activeElement: null, body: { style: { overflow: '' } } });
  vi.stubGlobal('HTMLElement', class {});
  vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify({ success: true, data: { requestToken: 'fixture' } }), { status: 200 })));
  write = async config => { const request = JSON.parse(String(config.data)); return response(config, workspace(request.path, request.content)); };
  apiClient.defaults.adapter = async config => { calls.push(config); return config.url === '/backup' ? response(config, []) : config.method === 'get' ? response(config, workspace(config.params.path)) : write(config); };
});
afterEach(async () => { closeConfirmDialog(); await flush(); app?.unmount(); app = undefined; apiClient.defaults.adapter = original; invalidateSessionRequests(); vi.unstubAllGlobals(); });
async function mount() {
  const pinia = createPinia(), auth = useAuthStore(pinia), sites = useSitesStore(pinia), subscription = useSubscriptionStore(pinia);
  auth.session = { isAuthenticated: true, requiresSetupWizard: false, user: { id: 'customer', role: 'Customer', userName: 'customer', email: 'customer@example.test', preferredLanguage: 'en', isActive: true, isTwoFactorEnabled: false, lastLoginAtUtc: null } };
  sites.sites = [{ name: 'a.test' }] as never; sites.selectedSiteName = 'a.test';
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  let state!: State, dialog!: Dialog;
  // Both production setup functions and the central dialog action lifecycle are real.
  // Only host rendering is replaced; no confirm callback or file API is mocked.
  app = renderer.createApp({ render: () => h('main', [
    h({ ...FilesView, render: () => h('section') } as Component, { ref: (instance: unknown) => { if (instance) state = (instance as { $: { setupState: State } }).$.setupState; } }),
    h({ ...ConfirmDialog, render: () => h('div') } as Component, { ref: (instance: unknown) => { if (instance) dialog = (instance as { $: { setupState: Dialog } }).$.setupState; } })
  ]) }).use(pinia).use(createI18n({ legacy: false, locale: 'en', fallbackLocale: false, messageCompiler: safeMessageCompiler, messages: { en } }));
  app.provide(ssrContextKey, {}); app.mount({}); await flush(); await state.openPath(path); state.fileContent = 'customer draft';
  return { state, dialog, subscription };
}
function suspend(subscription: ReturnType<typeof useSubscriptionStore>) { subscription.usage = { isSuspended: true } as never; subscription.applyReadOnly(); }

it('explains read-only status and does not offer an unusable Save when leaving an existing dirty draft', async () => {
  const { state, dialog, subscription } = await mount(); suspend(subscription);
  const leaving = state.openPath(root); await flush(); const options = dialogState.options;
  dialog.dismiss(); await leaving;
  expect(options?.message).toContain(en['panel.subscription.readOnlyAction']);
  expect(options?.confirmLabel).toBe(en['panel.files.discard']);
  expect(options?.cancelLabel).toBe(en['panel.files.stay']);
  expect(options?.alternativeLabel).toBeUndefined();
  expect(state.fileContent).toBe('customer draft'); expect(state.isDirty).toBe(true); expect(state.workspace.path).toBe(path); expect(writes()).toHaveLength(0);
});
it('allows explicit discard in read-only mode without attempting a save', async () => {
  const { state, dialog, subscription } = await mount(); suspend(subscription);
  const leaving = state.openPath(root); await flush();
  if (dialogState.options?.alternativeLabel === en['panel.files.discard']) dialog.alternative(); else await dialog.accept();
  await leaving; expect(state.workspace.path).toBe(root); expect(writes()).toHaveLength(0);
});
it('explains a subscription lock arriving while the real save confirmation is open and retains the draft', async () => {
  const { state, dialog, subscription } = await mount(); const leaving = state.openPath(root); await flush(); suspend(subscription);
  await dialog.accept(); await leaving;
  expect(state.notice).toContain(en['panel.subscription.readOnlyAction']); expect(state.noticeError).toBe(true);
  expect(state.workspace.path).toBe(path); expect(state.fileContent).toBe('customer draft'); expect(state.isDirty).toBe(true); expect(writes()).toHaveLength(0);
});
it('saves the exact draft through the actual dialog before navigating to the accepted folder', async () => {
  const { state, dialog } = await mount(); const leaving = state.openPath(root); await flush(); await dialog.accept(); await leaving;
  expect(JSON.parse(String(writes()[0]?.data))).toEqual({ path, content: 'customer draft' });
  expect(writes()).toHaveLength(1); expect(reads()).toHaveLength(3); expect(state.workspace.path).toBe(root); expect(dialogState.open).toBe(false);
});
it('does not navigate or replace the editor after an unconfirmed save in the real dialog', async () => {
  const { state, dialog } = await mount(); write = async config => { throw failure(config); };
  const leaving = state.openPath(root); await flush(); await dialog.accept(); await leaving;
  expect(state.fileContent).toBe('customer draft'); expect(state.isDirty).toBe(true); expect(state.workspace.path).toBe(path);
  expect(state.noticeError).toBe(true); expect(writes()).toHaveLength(1); expect(reads()).toHaveLength(2);
});
it('keeps the active save decision reserved while PUT is pending and rejects a second destination', async () => {
  const { state, dialog } = await mount(); const post = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
  write = config => (request = config, post.promise); const leaving = state.openPath(root); await flush(); const accepting = dialog.accept(); await flush();
  dialog.dismiss(); await state.openPath(`${root}/other`); expect(dialogState.open).toBe(true); expect(state.saving).toBe(true); expect(reads()).toHaveLength(2);
  post.resolve(response(request, workspace(path, 'customer draft'))); await accepting; await leaving;
  expect(state.workspace.path).toBe(root); expect(reads()).toHaveLength(3); expect(writes()).toHaveLength(1);
});
