import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { createPinia } from 'pinia';
import { createI18n } from 'vue-i18n';
import { createRenderer, createSSRApp, h, nextTick, ssrContextKey, type App, type Component } from 'vue';
import { renderToString } from '@vue/server-renderer';
import type { InternalAxiosRequestConfig } from 'axios';
import UpdateView from './UpdateView.vue';
import { apiClient, invalidateSessionRequests } from '../api/client';
import type { UpdateAttempt, UpdateStatus } from '../api/update';
import { useAuthStore } from '../stores/auth';
import { safeMessageCompiler } from '../i18n';
import en from '../i18n/en.json';

vi.mock('../components/ConfirmDialog.vue', () => ({ useConfirm: () => ({ confirm: vi.fn() }) }));
type State = { status: UpdateStatus | null; installPhase: string; refresh(force?: boolean): Promise<void> };
const attempt = (extra: Partial<UpdateAttempt> = {}): UpdateAttempt => ({ phase: 'failed', installerExitCode: 1,
  error: 'The installer could not write the target file.', errorKey: '', isDecidedByPanel: false,
  updatedAtUtc: '2026-10-04T12:00:00Z', isFinished: true, isFailed: true, ...extra });
const valid = (extra: Partial<UpdateStatus> = {}): UpdateStatus => ({ currentVersion: '0.8.87', latestVersion: '0.8.88',
  isUpdateAvailable: true, isCheckFailed: false, lastAttempt: attempt(), downloadUrl: 'https://example.test/panel.exe',
  packageSha256: 'a'.repeat(64), packagePath: 'C:\\fixture\\panel.exe', preparedVersion: '0.8.88', preparedPackageSha256: 'a'.repeat(64),
  isPackageReady: true, isInstallScheduled: false, installScheduledAtUtc: null, notes: '', githubRepo: 'fixture/panel', checkedAtUtc: '2026-10-04T12:01:00Z', ...extra });
let app: App | undefined, status: UpdateStatus, reads: number;
let readError: boolean;
const original = apiClient.defaults.adapter;
const i18n = () => createI18n({ legacy: false, locale: 'en', fallbackLocale: false, messageCompiler: safeMessageCompiler, messages: { en } });
async function mount() {
  const pinia = createPinia(), auth = useAuthStore(pinia);
  auth.session = { isAuthenticated: true, requiresSetupWizard: false, user: { id: 'admin', userName: 'admin', email: 'admin@example.test', role: 'Administrator', preferredLanguage: 'en', isActive: true, isTwoFactorEnabled: false, lastLoginAtUtc: null } };
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  app = renderer.createApp({ ...UpdateView, render: () => h('section') } as Component).use(pinia).use(i18n()); app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: State }).setupState;
  for(let i=0;i<40;i++) await nextTick();
  return state;
}
const render = (state: State) => renderToString(createSSRApp({ ...UpdateView, setup: () => state } as Component).use(i18n()));
beforeEach(() => {
  invalidateSessionRequests(); status = valid(); reads = 0; readError = false;
  vi.stubGlobal('window', { location: { pathname: '/panel', reload: vi.fn() }, setTimeout, clearTimeout, setInterval, clearInterval });
  apiClient.defaults.adapter = async (config: InternalAxiosRequestConfig) => {
    expect(config.method).toBe('get'); reads++;
    if(readError) throw new Error('Status endpoint unavailable');
    return { config, data: { success:true, data:status }, headers:{}, status:200, statusText:'OK' };
  };
});
afterEach(() => { app?.unmount(); app=undefined; apiClient.defaults.adapter=original; invalidateSessionRequests(); vi.unstubAllGlobals(); });

it('shows the previous failed attempt when reopening a screen with a verified prepared package', async () => {
  const html = await render(await mount());
  expect(html).toContain(en['panel.update.state.ready.title']);
  expect(html).toContain('The installer could not write the target file.');
  expect(html).toContain('datetime="2026-10-04T12:00:00Z"');
  expect(reads).toBe(1);
});
it('keeps a historical failure visible even when the currently running version is up to date', async () => {
  status = valid({ currentVersion:'0.8.88', isUpdateAvailable:false, isPackageReady:false });
  const html = await render(await mount());
  expect(html).toContain(en['panel.update.state.current.title']);
  expect(html).toContain('The installer could not write the target file.');
});
it('shows a completed attempt with its recorded time after the installer package has been cleaned', async () => {
  status = valid({ currentVersion:'0.8.88', isUpdateAvailable:false, isPackageReady:false,
    lastAttempt:attempt({ phase:'succeeded', isFailed:false, installerExitCode:0, error:'' }) });
  const html = await render(await mount());
  expect(html).toContain('datetime="2026-10-04T12:00:00Z"');
  expect(html).not.toContain('The installer could not write');
});
it('preserves the last result after a failed read and replaces it only with a successful fresh result', async () => {
  const state = await mount(); readError=true; await state.refresh(true);
  expect(await render(state)).toContain('The installer could not write the target file.');
  readError=false; status=valid({lastAttempt:null}); await state.refresh(true);
  expect(await render(state)).not.toContain('The installer could not write the target file.');
  expect(reads).toBe(3);
});
it('renders untrusted runner error text without creating HTML elements', async () => {
  status=valid({lastAttempt:attempt({error:'<img src=x onerror=alert(1)>'})});
  const html=await render(await mount());
  expect(html).toContain('&lt;img'); expect(html).not.toContain('<img');
});
it('does not manufacture a historical outcome when the server has no attempt', async () => {
  status=valid({lastAttempt:null}); const html=await render(await mount());
  expect(html).not.toContain('datetime='); expect(html).not.toContain('last-attempt');
});
it('does not show an unfinished runner record as a completed historical result', async () => {
  status=valid({lastAttempt:attempt({phase:'installing',isFinished:false,isFailed:false,error:''})});
  const html=await render(await mount()); expect(html).not.toContain('datetime=');
});
