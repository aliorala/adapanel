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
import tr from '../i18n/tr.json';
vi.mock('../components/ConfirmDialog.vue', () => ({ useConfirm: () => ({ confirm: vi.fn(async () => true) }) }));
type State = { activeTab: string; canWriteRecords: boolean; canManageDnsServer: boolean; loadError: string; load(): Promise<void>;
  saveRecord(): Promise<void>; removeRecord(id: string): Promise<void>; restoreDefaults(): Promise<void>; transferNow(): Promise<void>; toggleDnssec(enabled: boolean): Promise<void>; switchToPrimary(): Promise<void> };
type Language = 'en' | 'tr';
let app: App | undefined, response: DnsZone & Record<string, unknown>, writes: number;
const original = apiClient.defaults.adapter;
const i18n = (locale: Language) => createI18n({ legacy: false, locale, fallbackLocale: false, messageCompiler: safeMessageCompiler, messages: { en, tr } });
async function mount(locale: Language, role = 'Administrator') {
  const pinia = createPinia(), auth = useAuthStore(pinia);
  auth.session = { isAuthenticated: true, requiresSetupWizard: false, user: { id: 'admin', userName: 'admin', email: 'admin@example.test', role, preferredLanguage: locale, isActive: true, isTwoFactorEnabled: false, lastLoginAtUtc: null } };
  useSitesStore(pinia).selectedSiteName = 'example.test';
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  app = renderer.createApp({ ...DnsView, render: () => h('section') } as Component).use(pinia).use(i18n(locale)); app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: State }).setupState;
  for (let n = 0; n < 40; n++) await nextTick();
  return { state, html: () => renderToString(createSSRApp({ ...DnsView, setup: () => state } as Component).use(i18n(locale))) };
}
beforeEach(() => {
  invalidateSessionRequests(); writes = 0;
  response = { siteName:'example.test', zoneName:'example.test', nameServers:['ns1.example.test'], zoneTransferIps:[], masterServers:[], effectiveNotifyServers:[], windowsZoneType:'Primary',
    records:[{id:'a',type:'A',host:'@',value:'192.0.2.10',ttl:3600,priority:null,isManaged:false}], isSecondary:false, isWindowsDnsAvailable:true, isWindowsZonePresent:true, source:'windows-dns', statusMessage:'dns.windows_zone_present',isDnssecSigned:false,dnssecDsRecords:[],isSharedZone:true,modeConfigurationConflict:false,transferConfigurationConflict:false };
  vi.stubGlobal('window', { location: { pathname: '/panel', hostname: 'localhost' } });
  vi.stubGlobal('fetch',vi.fn(async()=>new Response(JSON.stringify({success:true,data:{requestToken:'fixture-only'}}),{status:200})));
  apiClient.defaults.adapter = async config => {
    if (config.method !== 'get') {
      writes++;
      if(config.url?.endsWith('/mode')) {
        const body=JSON.parse(String(config.data)); expect(body.secondary).toBe(false);
        response.isSecondary=false; response.modeConfigurationConflict=false; response.statusMessage='dns.windows_zone_present';
      }
    }
    return { config, data: { success: true, data: {...response} }, headers: {}, status: 200, statusText: 'OK' };
  };
});
afterEach(() => { app?.unmount(); app = undefined; apiClient.defaults.adapter = original; invalidateSessionRequests(); vi.unstubAllGlobals(); });

for (const locale of ['en','tr'] as const) {
  it.each(['mode-primary','mode-secondary','transfer'])(`${locale}: explains %s conflict while preserving records and preventing native actions`, async conflict => {
    response.modeConfigurationConflict = conflict.startsWith('mode'); response.transferConfigurationConflict = conflict === 'transfer';
    response.effectiveNotifyServers = conflict === 'transfer' ? ['192.0.2.54'] : [];
    response.isSecondary = conflict === 'mode-secondary'; response.statusMessage = 'dns.shared_configuration_conflict';
    const view = await mount(locale), html = await view.html();
    expect(html).toContain(locale === 'en' ? 'Shared DNS zone' : 'Ortak DNS bölgesi');
    expect(html).toContain(locale === 'en' ? 'Conflicting zone settings' : 'Bölge ayarları çelişiyor');
    expect(html).toContain('192.0.2.10'); expect(view.state.canWriteRecords).toBe(false); expect(view.state.canManageDnsServer).toBe(true);
    await view.state.saveRecord(); await view.state.removeRecord('a'); await view.state.restoreDefaults(); await view.state.transferNow(); await view.state.toggleDnssec(true);
    expect(writes).toBe(0);
    view.state.activeTab = 'settings'; const settings = await view.html();
    if (conflict === 'mode-secondary') {
      expect(settings).toContain('id="dns-master-servers"');
      expect(settings).not.toContain((locale==='en'?en:tr)['panel.dns.secondaryActiveHint'].split('{masters}')[0]);
    }
    if (conflict === 'transfer') expect(settings).not.toContain((locale==='en'?en:tr)['panel.dns.effectiveNotifyHint'].split('{list}')[0]);
    expect(settings).not.toMatch(/status-pill ok[^>]*>\s*(?:Primary|Birincil)/);
  });
  it(`${locale}: keeps coherent shared-zone record editing available`, async () => {
    const view = await mount(locale); expect(view.state.canWriteRecords).toBe(true); await view.state.saveRecord();
    expect(writes).toBe(1); expect(await view.html()).toContain(locale === 'en' ? 'Shared DNS zone' : 'Ortak DNS bölgesi');
  });
  it(`${locale}: offers primary mode as a conflict resolution when this site already says primary`, async () => {
    response.modeConfigurationConflict=true; response.statusMessage='dns.shared_configuration_conflict';
    const view=await mount(locale); view.state.activeTab='settings'; const html=await view.html();
    const label=(locale==='en'?en:tr)['panel.dns.switchToPrimary'];
    expect(html).toContain(label); await view.state.switchToPrimary(); expect(writes).toBe(1);
    expect(view.state.canWriteRecords).toBe(true); expect(await view.html()).not.toContain(locale==='en'?'Conflicting zone settings':'Bölge ayarları çelişiyor');
  });
}
it.each(['modeConfigurationConflict','transferConfigurationConflict'])('rejects a malformed optional %s flag', async key => {
  response[key] = 'false'; const view = await mount('en'); expect(view.state.canWriteRecords).toBe(false); expect(view.state.loadError).not.toBe('');
});
