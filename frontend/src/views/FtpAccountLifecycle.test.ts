import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { createPinia } from 'pinia';
import { createRenderer, h, nextTick, ref, ssrContextKey, type App, type Component, type ComponentPublicInstance, type Ref } from 'vue';
import { AxiosError, type AxiosResponse, type InternalAxiosRequestConfig } from 'axios';
import FtpView from './FtpView.vue';
import { apiClient, invalidateSessionRequests } from '../api/client';
import { useAuthStore } from '../stores/auth';
import { useSitesStore } from '../stores/sites';
import type { FtpUser } from '../api/ftp';

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
type State = { users: FtpUser[]; usersLoaded: boolean; usersLoad: { message: Ref<string> }; notice: string; noticeError: boolean; accountBusy: boolean; creating: boolean; showCreateForm: boolean; accountOperation: Operation | null; form: { userName: string; password: string; siteName: string; homePath: string }; createUser(): Promise<void>; changeFtpPassword(user: string): Promise<void>; removeUser(user: string): Promise<void>; refresh(): Promise<boolean>; refreshAccountListing(): Promise<void>; showNotice(message: string, error: boolean): void; toggleCreateForm(): void; dismissAccountOperation(): void };
let app: App | undefined, site: Ref<string | undefined>, calls: InternalAxiosRequestConfig[];
let read: (config: InternalAxiosRequestConfig) => Promise<AxiosResponse>, write: (config: InternalAxiosRequestConfig) => Promise<AxiosResponse>;
const original = apiClient.defaults.adapter;
const writes = () => calls.filter(c => c.url?.startsWith('/ftp/users') && c.method !== 'get');
const reads = () => calls.filter(c => c.url === '/ftp/users' && c.method === 'get');
beforeEach(() => {
  vi.resetAllMocks(); invalidateSessionRequests(); calls = []; site = ref('a.test');
  vi.stubGlobal('window', { location: { hostname: 'panel.test', pathname: '/panel' } });
  vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify({ success: true, data: { requestToken: 'fixture' } }), { status: 200 })));
  dialogs.confirm.mockImplementation(async options => { await options.action?.(); return true; }); dialogs.prompt.mockResolvedValue('FixturePassword123!');
  read = async config => response(config, [user()]);
  write = async config => response(config, config.url === '/ftp/users' ? user('new_ftp') : { userName: 'existing_ftp' });
  apiClient.defaults.adapter = async config => {
    calls.push(config);
    if (config.url === '/ftp/ensure' || config.url === '/ftp/status') return response(config, status);
    if (config.url === '/ftp/tls') return response(config, { requireSsl: false });
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
function fill(state: State) { state.showCreateForm = true; Object.assign(state.form, { userName: 'new_ftp', password: 'FixturePassword123!', homePath: '' }); }

it.each([false, null])('keeps the created identity and expiry warning after notice dismissal and failed refresh (%s)', async expiry => {
  const { state } = await mount(); fill(state);
  write = async config => response(config, { ...user('new_ftp'), passwordNeverExpires: expiry });
  read = async config => { throw failure(config, 'ftp.list_unavailable'); };
  await state.createUser(); state.showNotice('', false);
  expect(state.accountOperation).toMatchObject({ kind: 'create', status: 'succeeded', userName: 'new_ftp', siteName: 'a.test', homePath: 'C:/sites/a.test', warningKey: expiry === false ? 'ftp.user_created_password_expires' : 'ftp.user_created_expiry_unverified', refreshError: 'panel.ftp.accountRefreshFailed' });
  expect(state.users.some(u => u.userName === 'new_ftp')).toBe(true);
  expect(JSON.stringify(state.accountOperation)).not.toContain('FixturePassword'); expect(state.form.password).toBe(''); expect(state.accountBusy).toBe(false);
  read = async config => response(config, [user(), user('new_ftp')]); await state.refreshAccountListing();
  expect(state.accountOperation).toMatchObject({ status: 'succeeded', refreshError: '' }); expect(writes()).toHaveLength(1);
});
it.each(['create', 'delete', 'password'] as const)('reconciles a failed %s without converting it into success', async kind => {
  const { state } = await mount(); fill(state); write = async config => { throw failure(config); };
  read = async config => response(config, kind === 'delete' ? [] : [user(), user('partially_created')]);
  if (kind === 'create') await state.createUser(); else if (kind === 'delete') await state.removeUser('existing_ftp'); else await state.changeFtpPassword('existing_ftp');
  expect(reads()).toHaveLength(2); expect(state.users.map(u => u.userName)).toEqual(kind === 'delete' ? [] : ['existing_ftp', 'partially_created']);
  expect(state.accountOperation).toMatchObject({ kind, status: 'failed', error: 'ftp.fixture_failed', refreshError: '' }); expect(state.noticeError).toBe(true);
});
it('retains the original failure when reconciliation also fails and a later GET succeeds', async () => {
  const { state } = await mount(); write = async config => { throw failure(config); }; read = async config => { throw failure(config, 'ftp.list_unavailable'); };
  await state.removeUser('existing_ftp');
  expect(state.accountOperation).toMatchObject({ status: 'failed', error: 'ftp.fixture_failed', refreshError: 'panel.ftp.accountRefreshFailed' });
  read = async config => response(config, []); await state.refreshAccountListing();
  expect(state.accountOperation).toMatchObject({ status: 'failed', error: 'ftp.fixture_failed', refreshError: '' }); expect(writes()).toHaveLength(1);
});
it('prevents row writes while account creation and its list refresh are pending', async () => {
  const { state } = await mount(); fill(state); const post = deferred<AxiosResponse>(), get = deferred<AxiosResponse>(); let postRequest!: InternalAxiosRequestConfig, getRequest!: InternalAxiosRequestConfig;
  write = async config => config.url === '/ftp/users' ? (postRequest = config, post.promise) : response(config, { userName: 'existing_ftp' }); const creating = state.createUser(); await flush();
  await state.changeFtpPassword('existing_ftp'); await state.removeUser('existing_ftp');
  expect(dialogs.prompt).not.toHaveBeenCalled(); expect(dialogs.confirm).not.toHaveBeenCalled();
  read = config => (getRequest = config, get.promise); post.resolve(response(postRequest, user('new_ftp'))); await flush();
  expect(state.accountBusy).toBe(true); expect(state.accountOperation?.status).toBe('refreshing');
  await state.removeUser('existing_ftp'); expect(dialogs.confirm).not.toHaveBeenCalled();
  get.resolve(response(getRequest, [user(), user('new_ftp')])); await creating; expect(state.accountBusy).toBe(false);
});
it.each(['password', 'delete'] as const)('does not create while a %s decision is pending', async kind => {
  const { state } = await mount(); fill(state); const answer = deferred<string | boolean | null>();
  if (kind === 'password') dialogs.prompt.mockReturnValueOnce(answer.promise); else dialogs.confirm.mockReturnValueOnce(answer.promise);
  const pending = kind === 'password' ? state.changeFtpPassword('existing_ftp') : state.removeUser('existing_ftp');
  await state.createUser(); expect(writes()).toHaveLength(0); answer.resolve(kind === 'password' ? null : false); await pending;
});
it('closing and reopening a pending creation preserves the reopened form without allowing a duplicate request', async () => {
  const { state } = await mount(); fill(state); const post = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
  write = config => (request = config, post.promise); const creating = state.createUser(); await flush();
  state.toggleCreateForm(); state.toggleCreateForm(); const reopenedName = state.form.userName;
  const duplicate = state.createUser(); await flush(); expect(writes()).toHaveLength(1);
  post.resolve(response(request, user('new_ftp'))); await creating; await duplicate;
  expect(state.form.userName).toBe(reopenedName); expect(state.showCreateForm).toBe(true); expect(state.accountBusy).toBe(false);
  expect(state.accountOperation).toMatchObject({ status: 'succeeded', userName: 'new_ftp' });
});
it.each(['password', 'delete'] as const)('does not dispatch a %s answer into a replacement session for the same account', async kind => {
  const { state, auth } = await mount(); const answer = deferred<string | boolean | null>(); let options: { action?: () => Promise<void> } | undefined;
  if (kind === 'password') dialogs.prompt.mockReturnValueOnce(answer.promise);
  else dialogs.confirm.mockImplementationOnce(async captured => { options = captured; await answer.promise; await options?.action?.(); return true; });
  const pending = kind === 'password' ? state.changeFtpPassword('existing_ftp') : state.removeUser('existing_ftp');
  auth.session = { ...auth.session!, user: { ...auth.session!.user! } }; await flush();
  answer.resolve(kind === 'password' ? 'FixturePassword123!' : true); await pending; expect(writes()).toHaveLength(0); expect(state.accountOperation ?? null).toBeNull();
});
it.each(['site', 'session', 'unmount'])('does not reconcile or show an old failed write after %s departure', async kind => {
  const { state, auth } = await mount(); fill(state); const post = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
  write = config => (request = config, post.promise); const pending = state.createUser(); await flush();
  if (kind === 'site') site.value = 'b.test'; else if (kind === 'session') auth.session = { ...auth.session!, user: { ...auth.session!.user! } }; else { app!.unmount(); app = undefined; }
  await flush(); const readCount = reads().length;
  post.resolve({ ...response(request, null), data: { success: false, message: 'ftp.fixture_failed' } }); await pending;
  expect(reads()).toHaveLength(readCount); expect(state.accountOperation ?? null).toBeNull(); expect(state.notice).toBe('');
});
it('keeps a newer reconciliation list when a pre-mutation GET arrives last', async () => {
  const { state } = await mount(); const old = deferred<AxiosResponse>(); let request!: InternalAxiosRequestConfig;
  read = config => (request = config, old.promise); const reading = state.refresh(); await flush();
  write = async config => { throw failure(config); }; read = async config => response(config, []); await state.removeUser('existing_ftp');
  old.resolve(response(request, [user()])); await reading; expect(state.users).toEqual([]); expect(state.accountOperation?.status).toBe('failed');
});
it('keeps confirmed password and delete outcomes after the notice is dismissed', async () => {
  const { state } = await mount(); await state.changeFtpPassword('existing_ftp'); state.showNotice('', false);
  expect(state.accountOperation).toMatchObject({ kind: 'password', status: 'succeeded', userName: 'existing_ftp', siteName: 'a.test', homePath: 'C:/sites/a.test' });
  read = async config => { throw failure(config); }; await state.removeUser('existing_ftp'); state.showNotice('', false);
  expect(state.accountOperation).toMatchObject({ kind: 'delete', status: 'succeeded', refreshError: 'panel.ftp.accountRefreshFailed' }); expect(state.users).toEqual([]);
  state.dismissAccountOperation(); expect(state.accountOperation).toBeNull();
});
it.each(['password', 'delete'] as const)('preserves a completed result when the next %s decision is cancelled', async kind => {
  const { state } = await mount(); await state.changeFtpPassword('existing_ftp');
  const completed = { ...state.accountOperation! }, writeCount = writes().length, readCount = reads().length;
  if (kind === 'password') { dialogs.prompt.mockResolvedValueOnce(null); await state.changeFtpPassword('existing_ftp'); }
  else { dialogs.confirm.mockResolvedValueOnce(false); await state.removeUser('existing_ftp'); }
  expect(state.accountOperation).toEqual(completed); expect(writes()).toHaveLength(writeCount); expect(reads()).toHaveLength(readCount); expect(state.accountBusy).toBe(false);
});
it('keeps a successful creation without an expiry warning and clears the secret after completion', async () => {
  const { state } = await mount(); fill(state); read = async config => response(config, [user(), user('new_ftp')]);
  await state.createUser(); state.showNotice('', false);
  expect(state.accountOperation).toMatchObject({ kind: 'create', status: 'succeeded', userName: 'new_ftp', warningKey: '', error: '', refreshError: '' });
  expect(state.form.password).toBe(''); expect(state.showCreateForm).toBe(false); expect(state.accountBusy).toBe(false);
});
it('does not submit a queued password when a pending list read reveals that the account moved', async () => {
  const { state } = await mount(), get = deferred<AxiosResponse>(), answer = deferred<string | null>(); let request!: InternalAxiosRequestConfig;
  read = config => (request = config, get.promise); const refreshing = state.refresh(); await flush();
  dialogs.prompt.mockReturnValueOnce(answer.promise); const changing = state.changeFtpPassword('existing_ftp');
  get.resolve(response(request, [user('existing_ftp', 'b.test')])); await refreshing;
  answer.resolve('FixturePassword123!'); await changing; expect(writes()).toHaveLength(0); expect(state.accountBusy).toBe(false);
});
