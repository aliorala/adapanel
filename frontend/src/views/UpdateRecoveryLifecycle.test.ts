import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { createPinia } from 'pinia';
import { createI18n } from 'vue-i18n';
import { createRenderer, createSSRApp, h, nextTick, ssrContextKey, type App, type Component } from 'vue';
import { renderToString } from '@vue/server-renderer';
import type { AxiosResponse, InternalAxiosRequestConfig } from 'axios';
import UpdateView from './UpdateView.vue';
import { apiClient, invalidateSessionRequests } from '../api/client';
import type { UpdateAttempt, UpdateStatus } from '../api/update';
import { useAuthStore } from '../stores/auth';
import { safeMessageCompiler } from '../i18n';
import en from '../i18n/en.json';

const { confirm } = vi.hoisted(() => ({ confirm: vi.fn() }));
vi.mock('../components/ConfirmDialog.vue', () => ({ useConfirm: () => ({ confirm }) }));
type State = { status: UpdateStatus | null; monitoring: boolean; installPhase: string; installElapsedSeconds: number;
  canInstall: boolean; canDownload: boolean; notice: string; noticeError: boolean;
  refresh(force?: boolean): Promise<void>; installUpdate(): Promise<void> };
const valid = (extra: Partial<UpdateStatus> = {}): UpdateStatus => ({ currentVersion: '0.8.87', latestVersion: '0.8.88', isUpdateAvailable: true,
  isCheckFailed: false, lastAttempt: null, downloadUrl: 'https://example.test/0.8.88.exe', packageSha256: 'a'.repeat(64),
  preparedVersion: '0.8.88', preparedPackageSha256: 'a'.repeat(64), packagePath: 'C:\\fixture\\update.exe', isPackageReady: true,
  isInstallScheduled: false, installScheduledAtUtc: null, notes: '', githubRepo: 'fixture/panel', checkedAtUtc: new Date().toISOString(), ...extra });
const attempt = (extra: Partial<UpdateAttempt> = {}): UpdateAttempt => ({ phase: 'succeeded', installerExitCode: 0, error: '', errorKey: '',
  isDecidedByPanel: false, updatedAtUtc: new Date().toISOString(), isFinished: true, isFailed: false, ...extra });
const scheduled = (age = 20_000) => valid({ isInstallScheduled: true, installScheduledAtUtc: new Date(Date.now() - age).toISOString() });
function response(config: InternalAxiosRequestConfig, data: unknown): AxiosResponse { return { config, data: { success: true, data }, headers: {}, status: 200, statusText: 'OK' }; }
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(yes => { resolve = yes; }); return { promise, resolve }; }
let app: App | undefined, calls: InternalAxiosRequestConfig[], initial: UpdateStatus, reload: ReturnType<typeof vi.fn>;
let read: (config: InternalAxiosRequestConfig) => Promise<AxiosResponse>;
let scheduledPackageReadable: boolean;
const original = apiClient.defaults.adapter;
const gets = () => calls.filter(config => config.method === 'get');
const posts = () => calls.filter(config => config.method === 'post');
const i18n = () => createI18n({ legacy: false, locale: 'en', fallbackLocale: false, messageCompiler: safeMessageCompiler, messages: { en } });
async function flush() { for (let index = 0; index < 40; index++) await nextTick(); }
function unmount() { app?.unmount(); app = undefined; }
async function mount() {
  const pinia = createPinia(), auth = useAuthStore(pinia);
  auth.session = { isAuthenticated: true, requiresSetupWizard: false,
    user: { id: 'admin', userName: 'admin', email: 'a@example.test', role: 'Administrator', preferredLanguage: 'en', isActive: true, isTwoFactorEnabled: false, lastLoginAtUtc: null } };
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  app = renderer.createApp({ ...UpdateView, render: () => h('section') } as Component).use(pinia).use(i18n()); app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: State }).setupState;
  await flush(); return state;
}
async function renderState(state: State) {
  return renderToString(createSSRApp({ ...UpdateView, setup: () => state } as Component).use(i18n()));
}
beforeEach(() => {
  vi.useFakeTimers(); vi.setSystemTime(new Date('2026-10-04T15:00:00Z')); invalidateSessionRequests(); calls = []; initial = valid(); reload = vi.fn();
  scheduledPackageReadable = true;
  confirm.mockReset(); confirm.mockResolvedValue(true);
  vi.stubGlobal('window', { location: { pathname: '/panel', reload }, setTimeout, clearTimeout, setInterval, clearInterval });
  vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify({ success: true, data: { requestToken: 'fixture' } }), { status: 200 })));
  read = async config => response(config, initial);
  apiClient.defaults.adapter = async config => {
    calls.push(config);
    return config.method === 'get' ? read(config) : response(config, valid({ isInstallScheduled: true, isPackageReady: scheduledPackageReadable, installScheduledAtUtc: new Date().toISOString() }));
  };
});
afterEach(() => { unmount(); vi.clearAllTimers(); vi.useRealTimers(); apiClient.defaults.adapter = original; invalidateSessionRequests(); vi.unstubAllGlobals(); });

it('resumes a scheduled installation on entering the view and verifies its target without a second POST', async () => {
  initial = scheduled(); const state = await mount();
  read = async config => response(config, valid({ currentVersion: '0.8.88', isUpdateAvailable: false, isPackageReady: false, lastAttempt: attempt() }));
  await vi.advanceTimersByTimeAsync(6500);
  expect(gets()).toHaveLength(2); expect(posts()).toHaveLength(0);
  expect(state.installPhase).toBe('done'); expect(reload).toHaveBeenCalledOnce();
});
it('shows a failed resumed attempt instead of leaving its scheduled card indefinitely', async () => {
  initial = scheduled(); const state = await mount();
  read = async config => response(config, valid({ lastAttempt: attempt({ phase: 'failed', isFailed: true, installerExitCode: 5 }) }));
  await vi.advanceTimersByTimeAsync(5000);
  expect(state.installPhase).toBe('failed'); expect(state.monitoring).toBe(false); expect(state.noticeError).toBe(true);
  expect(posts()).toHaveLength(0); expect(reload).not.toHaveBeenCalled();
});
it('observes a scheduled target even when the running installer package is no longer readable', async () => {
  initial = { ...scheduled(), isPackageReady: false }; const state = await mount();
  read = async config => response(config, valid({ currentVersion: '0.8.88', isUpdateAvailable: false, isPackageReady: false, lastAttempt: attempt() }));
  await vi.advanceTimersByTimeAsync(5000);
  expect(state.installPhase).toBe('done'); expect(posts()).toHaveLength(0); expect(gets()).toHaveLength(2);
});
it('observes the acknowledged POST target when scheduling has committed but package readiness changed', async () => {
  const state = await mount(); scheduledPackageReadable = false; await state.installUpdate();
  expect(posts()).toHaveLength(1); expect(state.monitoring).toBe(true); expect(state.installPhase).toBe('scheduled');
  read = async config => response(config, valid({ currentVersion: '0.8.88', isPackageReady: false, isUpdateAvailable: false }));
  await vi.advanceTimersByTimeAsync(5000); expect(state.installPhase).toBe('done');
});
it.each([-60, 60])('uses server-reported elapsed time when the browser clock differs by %s minutes', async minutes => {
  initial = scheduled(20_000); vi.setSystemTime(new Date(Date.now() + minutes * 60_000)); const state = await mount();
  await vi.advanceTimersByTimeAsync(1000);
  expect(state.monitoring).toBe(true); expect(state.installElapsedSeconds).toBe(21); expect(state.installPhase).toBe('scheduled');
});
it.each(['missing-target', 'missing-time', 'future-time'] as const)('does not invent monitoring context for %s scheduled metadata', async kind => {
  initial = { ...scheduled(), ...(kind === 'missing-target' ? { preparedVersion: '' } : { installScheduledAtUtc: kind === 'missing-time' ? null : new Date(Date.now() + 60_000).toISOString() }) };
  const state = await mount(); await vi.advanceTimersByTimeAsync(10_000);
  expect(state.monitoring).toBe(false); expect(state.noticeError).toBe(true); expect(gets()).toHaveLength(1); expect(posts()).toHaveLength(0);
  initial = valid(); await state.refresh(true); expect(state.canInstall).toBe(true);
});
it('uses the original scheduled time for a resumed deadline rather than granting another fifteen minutes', async () => {
  initial = scheduled(14 * 60_000 + 50_000); const state = await mount();
  await vi.advanceTimersByTimeAsync(10_000);
  expect(state.installPhase).toBe('timeout'); expect(state.monitoring).toBe(false);
  expect(state.installElapsedSeconds).toBeGreaterThanOrEqual(900); expect(posts()).toHaveLength(0);
});
it('does not start a new observation window for an already expired schedule', async () => {
  initial = scheduled(16 * 60_000); const state = await mount(); await vi.advanceTimersByTimeAsync(1000);
  expect(state.installPhase).toBe('timeout'); expect(state.monitoring).toBe(false); expect(gets()).toHaveLength(1); expect(posts()).toHaveLength(0);
});
it('requires a new terminal attempt when resuming a same-version reinstall', async () => {
  const oldAttempt = attempt({ updatedAtUtc: new Date(Date.now() - 60_000).toISOString() });
  initial = { ...scheduled(), currentVersion: '0.8.88', isUpdateAvailable: false, lastAttempt: oldAttempt };
  const state = await mount(); await vi.advanceTimersByTimeAsync(5000);
  expect(state.installPhase).not.toBe('done'); expect(reload).not.toHaveBeenCalled();
  initial = { ...initial, isInstallScheduled: false, lastAttempt: attempt() };
  await vi.advanceTimersByTimeAsync(6500);
  expect(state.installPhase).toBe('done'); expect(posts()).toHaveLength(0); expect(reload).toHaveBeenCalledOnce();
});
it('does not publish a resumed poll or reload after leaving the view', async () => {
  initial = scheduled(); const state = await mount(); const pending = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
  read = config => (request = config, pending.promise); await vi.advanceTimersByTimeAsync(5000); expect(gets()).toHaveLength(2);
  unmount(); pending.resolve(response(request, valid({ currentVersion: '0.8.88', isUpdateAvailable: false }))); await flush(); await vi.advanceTimersByTimeAsync(10_000);
  expect(state.installPhase).not.toBe('done'); expect(reload).not.toHaveBeenCalled(); expect(gets()).toHaveLength(2);
});
it('keeps a freshly measured prepared package installable when only the remote release check failed', async () => {
  const state = await mount(); initial = valid({ isCheckFailed: true }); await state.refresh(true);
  expect(state.canInstall).toBe(true); expect(state.canDownload).toBe(false);
  const html = await renderState(state); expect(html).toContain(en['panel.update.state.ready.title']); expect(html).toContain(en['panel.update.checkFailedPrepared']);
});
it('does not turn elapsed time into a measured percentage before the first installation poll', async () => {
  const state = await mount(); await state.installUpdate(); await vi.advanceTimersByTimeAsync(1000);
  expect(gets()).toHaveLength(2); expect(state.installElapsedSeconds).toBe(1);
  expect(await renderState(state)).not.toContain('aria-valuenow=');
});
it('does not assign sixty percent to an attempt that failed without any observed loss of connection', async () => {
  const state = await mount(); await state.installUpdate();
  read = async config => response(config, valid({ lastAttempt: attempt({ phase: 'failed', isFailed: true, installerExitCode: 5 }) }));
  await vi.advanceTimersByTimeAsync(5000); expect(state.installPhase).toBe('failed');
  expect(await renderState(state)).not.toContain('aria-valuenow=');
});
it('does not describe a failed HTTP poll as proof that installer files are being replaced', async () => {
  const state = await mount(); await state.installUpdate(); read = async () => { throw new Error('gateway unavailable'); };
  await vi.advanceTimersByTimeAsync(5000);
  expect(await renderState(state)).not.toContain('files are being replaced');
});
it('keeps the observed reconnection stage while subsequent valid polls still await the target version', async () => {
  const state = await mount(); await state.installUpdate(); read = async () => { throw new Error('gateway unavailable'); };
  await vi.advanceTimersByTimeAsync(5000); read = async config => response(config, initial);
  await vi.advanceTimersByTimeAsync(5000); expect(state.installPhase).toBe('verifying');
  await vi.advanceTimersByTimeAsync(5000); expect(state.installPhase).toBe('verifying'); expect(state.monitoring).toBe(true);
});
it('does not claim the panel failed to return when every poll answered with the old version', async () => {
  const state = await mount(); await state.installUpdate(); await vi.advanceTimersByTimeAsync(15 * 60_000);
  expect(gets().length).toBeGreaterThan(100); expect(state.installPhase).toBe('timeout'); expect(state.monitoring).toBe(false);
  expect(state.notice).not.toContain('panel did not come back'); expect(reload).not.toHaveBeenCalled();
});
