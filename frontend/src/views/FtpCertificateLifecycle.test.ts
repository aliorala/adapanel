import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { createPinia } from 'pinia';
import { createRenderer, h, nextTick, ref, ssrContextKey, type App, type Component, type ComponentPublicInstance, type Ref } from 'vue';
import { AxiosError, type AxiosResponse, type InternalAxiosRequestConfig } from 'axios';
import FtpView from './FtpView.vue';
import { apiClient, invalidateSessionRequests } from '../api/client';
import { useAuthStore } from '../stores/auth';
import { useSitesStore } from '../stores/sites';
import type { FtpUser, FtpCertificate } from '../api/ftp';

const dialogs = vi.hoisted(() => ({ confirm: vi.fn(), prompt: vi.fn() }));
vi.mock('vue-i18n', () => ({ useI18n: () => ({ t: (key: string) => key }) }));
vi.mock('../components/ConfirmDialog.vue', () => ({ useConfirm: () => ({ confirm: dialogs.confirm }) }));
vi.mock('../components/PromptDialog.vue', () => ({ usePrompt: () => ({ prompt: dialogs.prompt }) }));
const user = (name = 'existing_ftp', site = 'a.test'): FtpUser => ({ userName: name, siteName: site, homePath: `C:/sites/${site}`, isEnabled: true, existsOnSystem: true, createdAtUtc: '2026-10-04T12:00:00Z', passwordNeverExpires: true });
const status = { serverAddress: 'fixture.test', controlPort: 21, passivePortStart: 50000, passivePortEnd: 51000, isConfigured: true, statusMessage: 'ftp.server_ready', serviceState: 'Running', siteState: 'Started', isSiteRunning: true, hasControlPortConflict: false, controlPortOwner: '' };
function response(config: InternalAxiosRequestConfig, data: unknown, message = ''): AxiosResponse { return { config, data: { success: true, data, message }, status: 200, statusText: 'OK', headers: {} }; }
function failure(config: InternalAxiosRequestConfig, message = 'ftp.fixture_failed') { return new AxiosError('Request failed', 'ERR_BAD_RESPONSE', config, undefined, { ...response(config, null), status: 500, data: { success: false, message } }); }
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(yes => { resolve = yes; }); return { promise, resolve }; }
async function flush() { for (let i = 0; i < 35; i++) await nextTick(); }
type Operation = { kind: 'create' | 'password' | 'delete'; status: 'running' | 'refreshing' | 'succeeded' | 'failed'; userName: string; siteName: string; homePath: string; warningKey: string; error: string; refreshError: string };
type State = { certificateHost: string; certificate: FtpCertificate | null; certificateSaving: boolean; requireSsl: boolean; notice: string; noticeError: boolean; saveCertificate(): Promise<void>; toggleTls(value: boolean): Promise<void>; };
let app: App | undefined, site: Ref<string | undefined>, calls: InternalAxiosRequestConfig[];
let certificateRead: unknown;
let read: (config: InternalAxiosRequestConfig) => Promise<AxiosResponse>, write: (config: InternalAxiosRequestConfig) => Promise<AxiosResponse>;
const original = apiClient.defaults.adapter;
const writes = () => calls.filter(c => c.url?.startsWith('/ftp/users') && c.method !== 'get');
const reads = () => calls.filter(c => c.url === '/ftp/users' && c.method === 'get');
beforeEach(() => {
  vi.resetAllMocks(); invalidateSessionRequests(); calls = []; certificateRead = { requireSsl: true, hostName: 'ftp.fixture.test' }; site = ref(undefined);
  vi.stubGlobal('window', { location: { hostname: 'panel.test', pathname: '/panel' } });
  vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify({ success: true, data: { requestToken: 'fixture' } }), { status: 200 })));
  dialogs.confirm.mockImplementation(async options => { await options.action?.(); return true; }); dialogs.prompt.mockResolvedValue('FixturePassword123!');
  read = async config => response(config, [user()]);
  write = async config => response(config, config.url === '/ftp/users' ? user('new_ftp') : { userName: 'existing_ftp' });
  apiClient.defaults.adapter = async config => {
    calls.push(config);
    if (config.url === '/ftp/ensure' || config.url === '/ftp/status') return response(config, status);
    if (config.url === '/ftp/tls') return response(config, certificateRead);
    return config.method === 'get' ? read(config) : write(config);
  };
});
afterEach(() => { app?.unmount(); app = undefined; apiClient.defaults.adapter = original; invalidateSessionRequests(); vi.unstubAllGlobals(); });
async function mount() {
  const pinia = createPinia(), auth = useAuthStore(pinia), sites = useSitesStore(pinia);
  auth.session = { isAuthenticated: true, requiresSetupWizard: false, user: { id: 'admin', role: 'Administrator', userName: 'admin', email: 'admin@example.test', preferredLanguage: 'en', isActive: true, isTwoFactorEnabled: false, lastLoginAtUtc: null } };
  sites.sites = [{ name: 'a.test' }, { name: 'b.test' }] as never;
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  let vm: ComponentPublicInstance | undefined;
  const component = { ...FtpView, render: () => h('section') } as Component;
  app = renderer.createApp({ render: () => h(component, { siteName: site.value, ref: (value: unknown) => { if (value) vm = value as ComponentPublicInstance; } }) }).use(pinia);
  app.provide(ssrContextKey, {}); app.mount({}); await flush();
  return { state: (vm!.$ as unknown as { setupState: State }).setupState, auth };
}

const cert = (verified: boolean | null = true): FtpCertificate => ({ hostName: 'ftp.fixture.test', certificateThumbprint: 'ABCD', certificateSubject: 'CN=ftp.fixture.test', certificateVerified: verified, certificateDetail: verified ? 'AUTH TLS verified' : '502 SSL/TLS authentication not allowed' });
it('uses only the saved certificate host, never the panel IP or FTP address', async () => {
  certificateRead = { requireSsl: true }; const { state } = await mount(); expect(state.certificateHost).toBe('');
  await state.saveCertificate(); expect(calls.filter(c => c.url === '/ftp/certificate')).toHaveLength(0);
});
it('applies the certificate without changing the TLS-required policy', async () => {
  const { state } = await mount(); write = async config => response(config, cert()); await state.saveCertificate();
  const posts = calls.filter(c => c.url === '/ftp/certificate'); expect(posts).toHaveLength(1); expect(JSON.parse(posts[0].data)).toEqual({ hostName: 'ftp.fixture.test' });
  expect(calls.filter(c => c.url === '/ftp/tls' && c.method !== 'get')).toHaveLength(0); expect(state.requireSsl).toBe(true); expect(state.notice).toBe('panel.ftp.certApplied'); expect(state.noticeError).toBe(false);
});
it.each([false, null, undefined])('does not claim success without positive proof (%s)', async verified => {
  const { state } = await mount(); write = async config => response(config, { ...cert(), certificateVerified: verified }); await state.saveCertificate(); expect(state.noticeError).toBe(true); expect(state.notice).toBe('panel.ftp.certNotVerified');
});
it.each([{ hostName: 'other.test' }, { certificateThumbprint: '' }, { certificateVerified: 'true' }])('rejects invalid verified certificate responses (%j)', async invalid => {
  const { state } = await mount(); write = async config => response(config, { ...cert(), ...invalid }); await state.saveCertificate(); expect(state.noticeError).toBe(true); expect(state.notice).toBe('panel.ftp.resultUnconfirmed');
});
it('keeps an AUTH TLS failure visible after a successful read', async () => {
  const { state } = await mount(); write = async config => { throw failure(config, 'ftp.certificate_tls_probe_failed'); }; await state.saveCertificate(); expect(state.notice).toBe('ftp.certificate_tls_probe_failed'); expect(state.noticeError).toBe(true);
});
it('blocks concurrent certificate and policy writes', async () => {
  const { state } = await mount(); const post = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig; write = config => (request = config, post.promise);
  const pending = state.saveCertificate(); await flush(); await state.saveCertificate(); await state.toggleTls(false);
  expect(calls.filter(c => c.method === 'post' && c.url === '/ftp/certificate')).toHaveLength(1); expect(calls.filter(c => c.method === 'put')).toHaveLength(0);
  post.resolve(response(request, cert())); await pending; expect(state.certificateSaving).toBe(false);
});
it.each(['site', 'session', 'unmount'])('ignores an old certificate result after %s departure', async kind => {
  const { state, auth } = await mount(); const post = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig; write = config => (request = config, post.promise);
  const pending = state.saveCertificate(); await flush();
  if (kind === 'site') site.value = 'b.test'; else if (kind === 'session') auth.session = { ...auth.session!, user: { ...auth.session!.user! } }; else { app!.unmount(); app = undefined; }
  await flush(); const readCount = calls.filter(c => c.url === '/ftp/tls').length; post.resolve(response(request, cert())); await pending;
  expect(state.notice).toBe(''); expect(calls.filter(c => c.url === '/ftp/tls')).toHaveLength(readCount);
});
it('does not allow non-administrator certificate writes', async () => {
  const { state, auth } = await mount(); auth.session = { ...auth.session!, user: { ...auth.session!.user!, role: 'Customer' } }; await flush(); state.certificateHost = 'ftp.fixture.test'; await state.saveCertificate(); expect(calls.filter(c => c.url === '/ftp/certificate')).toHaveLength(0);
});
