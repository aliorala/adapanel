import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { createPinia } from 'pinia';
import { createI18n } from 'vue-i18n';
import { createRenderer, createSSRApp, h, nextTick, ssrContextKey, type App, type Component } from 'vue';
import { renderToString } from '@vue/server-renderer';
import DnsView from './DnsView.vue';
import { apiClient, invalidateSessionRequests } from '../api/client';
import type { DnsZone } from '../api/dns';
import { useAuthStore } from '../stores/auth';
import { useSitesStore } from '../stores/sites';
import { safeMessageCompiler } from '../i18n';
import en from '../i18n/en.json';

vi.mock('../components/ConfirmDialog.vue', () => ({ useConfirm: () => ({ confirm: vi.fn() }) }));
type State = { load(): Promise<void>; activeTab: string; nameServersText: string };
const zone = (): DnsZone => ({ siteName: 'example.test', zoneName: 'example.test', nameServers: ['ns1.example.test'],
  zoneTransferIps: [], masterServers: [], effectiveNotifyServers: [], windowsZoneType: 'Primary',
  records: [{ id: 'a', type: 'A', host: '@', value: '192.0.2.10', ttl: 3600, priority: null, isManaged: false }],
  isSecondary: false, isWindowsDnsAvailable: false, isWindowsZonePresent: false, source: 'panel',
  statusMessage: 'dns.windows_unavailable', isDnssecSigned: false, dnssecDsRecords: [] });
let app: App | undefined, readError = false, reads = 0;
const original = apiClient.defaults.adapter;
const i18n = () => createI18n({ legacy: false, locale: 'en', fallbackLocale: false, messageCompiler: safeMessageCompiler, messages: { en } });
async function mount(role = 'Administrator') {
  const pinia = createPinia(), auth = useAuthStore(pinia);
  auth.session = { isAuthenticated: true, requiresSetupWizard: false, user: { id: 'admin', userName: 'admin', email: 'admin@example.test', role,
    preferredLanguage: 'en', isActive: true, isTwoFactorEnabled: false, lastLoginAtUtc: null } };
  useSitesStore(pinia).selectedSiteName = 'example.test';
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  app = renderer.createApp({ ...DnsView, render: () => h('section') } as Component).use(pinia).use(i18n()); app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: State }).setupState;
  for (let n = 0; n < 40; n++) await nextTick();
  return state;
}
const render = (state: State) => renderToString(createSSRApp({ ...DnsView, setup: () => state } as Component).use(i18n()));
const button = (html: string, label: string) => html.match(new RegExp(`<button[^>]*>\\s*${label}\\s*</button>`))?.[0] ?? '';
beforeEach(() => {
  invalidateSessionRequests(); readError = false; reads = 0;
  vi.stubGlobal('window', { location: { pathname: '/panel', hostname: 'localhost' } });
  apiClient.defaults.adapter = async config => {
    expect(config.method).toBe('get'); reads++;
    if (readError) throw new Error('DNS read failed');
    return { config, data: { success: true, data: zone() }, headers: {}, status: 200, statusText: 'OK' };
  };
});
afterEach(() => { app?.unmount(); app = undefined; apiClient.defaults.adapter = original; invalidateSessionRequests(); vi.unstubAllGlobals(); });

it('offers retry after the first failed read without reporting an empty zone or perpetual loading', async () => {
  readError = true; const state = await mount(); const html = await render(state);
  expect(html).toContain(en['panel.common.loadFailed']); expect(button(html, en['panel.common.retry'])).not.toContain('disabled');
  expect(html).toContain(en['panel.dns.stateUnconfirmed']); expect(html).not.toContain(en['panel.dns.noRecords']);
  expect(html).not.toContain(en['panel.common.loading']); expect(html).not.toContain(en['panel.dns.lastKnownZone']);
  expect(button(html, en['panel.dns.addRecordAction'])).toContain('disabled');
  state.activeTab = 'settings'; expect(await render(state)).not.toContain(en['panel.common.loading']);
});
it('labels retained records as last known and disables mutation after a refresh fails', async () => {
  const state = await mount(); state.nameServersText = 'ns2.example.test'; readError = true; await state.load();
  const html = await render(state); expect(html).toContain('192.0.2.10'); expect(html).toContain(en['panel.dns.lastKnownZone']);
  expect(button(html, en['panel.dns.delete'])).toContain('disabled'); expect(button(html, en['panel.dns.installWindows'])).toContain('disabled');
  state.activeTab = 'settings'; const settings = await render(state);
  expect(settings).toContain('ns2.example.test'); expect(button(settings, en['panel.dns.saveZone'])).toContain('disabled');
});
it('clears the stale warning on successful retry and preserves the unsaved zone draft', async () => {
  const state = await mount(); state.nameServersText = 'ns2.example.test'; readError = true; await state.load();
  readError = false; await state.load(); state.activeTab = 'settings'; const html = await render(state);
  expect(html).not.toContain(en['panel.dns.lastKnownZone']); expect(html).not.toContain(en['panel.common.loadFailed']);
  expect(html).toContain('ns2.example.test'); expect(button(html, en['panel.dns.saveZone'])).not.toContain('disabled'); expect(reads).toBe(3);
});
it('keeps reseller record controls while hiding administrator-only server actions', async () => {
  const state = await mount('Reseller'); let html = await render(state);
  expect(button(html, en['panel.dns.addRecordAction'])).not.toBe(''); expect(button(html, en['panel.dns.addRecordAction'])).not.toContain('disabled');
  expect(button(html, en['panel.dns.installWindows'])).toBe('');
  state.activeTab = 'settings'; html = await render(state); expect(button(html, en['panel.dns.switchToSecondary'])).toBe('');
  expect(button(html, en['panel.dns.saveZone'])).not.toBe('');
});
it('explains the unavailable DNS workspace without querying it for a customer', async () => {
  const html = await render(await mount('Customer')); expect(html).toContain(en['panel.dns.accessHint']);
  expect(html).not.toContain('192.0.2.10'); expect(reads).toBe(0);
});
