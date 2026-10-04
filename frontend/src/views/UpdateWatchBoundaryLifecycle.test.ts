import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { createPinia } from 'pinia';
import { createRenderer, h, nextTick, ssrContextKey, type App, type Component } from 'vue';
import type { AxiosResponse, InternalAxiosRequestConfig } from 'axios';
import UpdateView from './UpdateView.vue';
import { apiClient, invalidateSessionRequests } from '../api/client';
import type { UpdateAttempt, UpdateStatus } from '../api/update';
import { useAuthStore } from '../stores/auth';

vi.mock('vue-i18n', () => ({ useI18n: () => ({ t: (key: string) => key }) }));
const { confirm } = vi.hoisted(() => ({ confirm: vi.fn() }));
vi.mock('../components/ConfirmDialog.vue', () => ({ useConfirm: () => ({ confirm }) }));
type State = { status: UpdateStatus | null; statusConfirmed: boolean; monitoring: boolean; installPhase: string; installElapsedSeconds: number;
  notice: string; noticeError: boolean; canInstall: boolean; refresh(force?: boolean): Promise<void>; installUpdate(): Promise<void> };
const base = Date.parse('2026-10-05T12:00:00Z');
let monotonicOrigin = 0;
const serverNow = () => base + performance.now() - monotonicOrigin;
const timestamp = () => new Date(serverNow()).toISOString();
const valid = (extra: Partial<UpdateStatus> = {}): UpdateStatus => ({ currentVersion: '0.8.88', latestVersion: '0.8.89', isUpdateAvailable: true,
  isCheckFailed: false, lastAttempt: null, downloadUrl: 'https://example.test/0.8.89.exe', packageSha256: 'a'.repeat(64),
  preparedVersion: '0.8.89', preparedPackageSha256: 'a'.repeat(64), packagePath: 'C:\\fixture\\update.exe', isPackageReady: true,
  isInstallScheduled: false, installScheduledAtUtc: null, notes: '', githubRepo: 'fixture/panel', checkedAtUtc: timestamp(), ...extra });
const terminal = (extra: Partial<UpdateAttempt> = {}): UpdateAttempt => ({ phase: 'succeeded', installerExitCode: 0, error: '', errorKey: '',
  isDecidedByPanel: false, updatedAtUtc: timestamp(), isFinished: true, isFailed: false, ...extra });
function response(config: InternalAxiosRequestConfig, data: unknown): AxiosResponse { return { config, data: { success: true, data }, headers: {}, status: 200, statusText: 'OK' }; }
let app: App | undefined, calls: InternalAxiosRequestConfig[], initial: UpdateStatus, reload: ReturnType<typeof vi.fn>;
let read: (config: InternalAxiosRequestConfig) => Promise<AxiosResponse>;
const original = apiClient.defaults.adapter;
const gets = () => calls.filter(config => config.method === 'get');
const posts = () => calls.filter(config => config.method === 'post');
async function flush() { for (let index = 0; index < 40; index++) await nextTick(); }
function unmount() { app?.unmount(); app = undefined; }
async function mount() {
  const pinia = createPinia(), auth = useAuthStore(pinia);
  auth.session = { isAuthenticated: true, requiresSetupWizard: false,
    user: { id: 'admin', userName: 'admin', email: 'a@example.test', role: 'Administrator', preferredLanguage: 'en', isActive: true, isTwoFactorEnabled: false, lastLoginAtUtc: null } };
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  app = renderer.createApp({ ...UpdateView, render: () => h('section') } as Component).use(pinia); app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: State }).setupState;
  await flush(); return state;
}
beforeEach(() => {
  vi.useFakeTimers(); vi.setSystemTime(base); monotonicOrigin = performance.now(); invalidateSessionRequests(); calls = []; initial = valid(); reload = vi.fn();
  confirm.mockReset(); confirm.mockResolvedValue(true);
  vi.stubGlobal('window', { location: { pathname: '/panel', reload }, setTimeout, clearTimeout, setInterval, clearInterval });
  vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify({ success: true, data: { requestToken: 'fixture' } }), { status: 200 })));
  read = async config => response(config, { ...initial, checkedAtUtc: timestamp() });
  apiClient.defaults.adapter = async config => {
    calls.push(config);
    return config.method === 'get' ? read(config) : response(config, { ...initial, isInstallScheduled: true, installScheduledAtUtc: timestamp(), checkedAtUtc: timestamp() });
  };
});
afterEach(() => { unmount(); vi.clearAllTimers(); vi.useRealTimers(); apiClient.defaults.adapter = original; invalidateSessionRequests(); vi.unstubAllGlobals(); });

it.each([-60, 60])('keeps elapsed time and the deadline stable when the browser clock changes by %s minutes during observation', async minutes => {
  const state = await mount(); await state.installUpdate(); await vi.advanceTimersByTimeAsync(2000);
  vi.setSystemTime(Date.now() + minutes * 60_000); await vi.advanceTimersByTimeAsync(1000);
  expect(state.installElapsedSeconds).toBe(3); expect(state.monitoring).toBe(true); expect(state.installPhase).toBe('scheduled');
  await vi.advanceTimersByTimeAsync(897_000);
  expect(state.installPhase).toBe('timeout'); expect(state.monitoring).toBe(false); expect(state.installElapsedSeconds).toBe(900);
});
it.each(['version', 'checksum', 'schedule'] as const)('does not complete the old watch when a different %s identifies a replacement scheduled installation', async field => {
  const state = await mount(); await state.installUpdate();
  read = async config => response(config, valid({ currentVersion: '0.8.89', isInstallScheduled: true, installScheduledAtUtc: field === 'schedule' ? timestamp() : new Date(base).toISOString(),
    preparedVersion: field === 'version' ? '0.8.90' : '0.8.89', preparedPackageSha256: field === 'checksum' ? 'b'.repeat(64) : 'a'.repeat(64) }));
  await vi.advanceTimersByTimeAsync(6500);
  expect(state.installPhase).not.toBe('done'); expect(state.statusConfirmed).toBe(false); expect(state.monitoring).toBe(false);
  expect(state.noticeError).toBe(true); expect(reload).not.toHaveBeenCalled(); expect(posts()).toHaveLength(1);
  read = async config => response(config, valid()); await state.refresh(true); expect(state.canInstall).toBe(true);
});
it('does not attribute a terminal file dated before the scheduled attempt to a same-version reinstall', async () => {
  initial = valid({ currentVersion: '0.8.89', isUpdateAvailable: false }); const state = await mount(); await state.installUpdate();
  read = async config => response(config, valid({ currentVersion: '0.8.89', isUpdateAvailable: false,
    lastAttempt: terminal({ updatedAtUtc: new Date(base - 10_000).toISOString() }) }));
  await vi.advanceTimersByTimeAsync(6500);
  expect(state.installPhase).not.toBe('done'); expect(state.monitoring).toBe(true); expect(reload).not.toHaveBeenCalled();
  read = async config => response(config, valid({ currentVersion: '0.8.89', isUpdateAvailable: false, lastAttempt: terminal() }));
  await vi.advanceTimersByTimeAsync(5000); expect(state.installPhase).toBe('done'); expect(reload).toHaveBeenCalledOnce();
});
it.each([
  { label: 'contradictory terminal flags', extra: { phase: 'failed', isFailed: false } },
  { label: 'unfinished phase', extra: { phase: 'installing' } },
  { label: 'unmeasured timestamp', extra: { updatedAtUtc: 'not-a-date' } },
  { label: 'future timestamp', extra: { updatedAtUtc: new Date(base + 60_000).toISOString() } },
  { label: 'invalid exit code', extra: { installerExitCode: '0' } },
  { label: 'non-string error', extra: { error: {} } }
])('does not infer reinstall success from $label and requires a fresh measured status', async ({ extra }) => {
  initial = valid({ currentVersion: '0.8.89', isUpdateAvailable: false }); const state = await mount(); await state.installUpdate();
  read = async config => response(config, valid({ currentVersion: '0.8.89', isUpdateAvailable: false, lastAttempt: { ...terminal(), ...extra } as UpdateAttempt }));
  await vi.advanceTimersByTimeAsync(6500);
  expect(state.installPhase).not.toBe('done'); expect(state.monitoring).toBe(false); expect(state.statusConfirmed).toBe(false);
  expect(state.noticeError).toBe(true); expect(reload).not.toHaveBeenCalled(); expect(posts()).toHaveLength(1);
});
it('rejects a malformed stored attempt on a fresh page rather than rendering it as a dated result', async () => {
  initial = valid({ lastAttempt: terminal({ phase: 'failed', isFailed: false }) });
  const state = await mount(); expect(state.status).toBeNull(); expect(state.statusConfirmed).toBe(false); expect(state.noticeError).toBe(true);
});
it('preserves a measured failure while a manual refresh fails, then allows a successful fresh check', async () => {
  const state = await mount(); await state.installUpdate();
  read = async config => response(config, valid({ lastAttempt: terminal({ phase: 'failed', isFailed: true, errorKey: 'update.install_failed', installerExitCode: 1 }) }));
  await vi.advanceTimersByTimeAsync(5000); expect(state.installPhase).toBe('failed');
  const failure = state.status?.lastAttempt; read = async () => { throw new Error('offline'); }; await state.refresh(true);
  expect(state.status?.lastAttempt).toEqual(failure); expect(state.installPhase).toBe('failed'); expect(state.statusConfirmed).toBe(false);
  read = async config => response(config, valid()); await state.refresh(true); expect(state.installPhase).toBe(''); expect(state.statusConfirmed).toBe(true);
});
it('accepts a genuinely new successful same-version attempt after the server clears the pending package metadata', async () => {
  initial = valid({ currentVersion: '0.8.89', isUpdateAvailable: false }); const state = await mount(); await state.installUpdate();
  read = async config => response(config, valid({ currentVersion: '0.8.89', isUpdateAvailable: false, isPackageReady: false,
    preparedVersion: '', preparedPackageSha256: '', packagePath: '', lastAttempt: terminal() }));
  await vi.advanceTimersByTimeAsync(6500); expect(state.installPhase).toBe('done'); expect(reload).toHaveBeenCalledOnce(); expect(posts()).toHaveLength(1);
});
