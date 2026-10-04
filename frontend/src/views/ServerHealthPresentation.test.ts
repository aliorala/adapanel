import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { createPinia } from 'pinia';
import { createI18n } from 'vue-i18n';
import { createRenderer, createSSRApp, h, nextTick, ssrContextKey, type App, type Component } from 'vue';
import { renderToString } from '@vue/server-renderer';
import ServerHealthView from './ServerHealthView.vue';
import { apiClient, invalidateSessionRequests } from '../api/client';
import { useAuthStore } from '../stores/auth';
import { safeMessageCompiler } from '../i18n';
import en from '../i18n/en.json';
import tr from '../i18n/tr.json';

type State = { load(): Promise<void>; canRestart(name: string): boolean };
type Language = 'tr' | 'en';
let app: App | undefined, serviceStatus = 'Running', failedRead = false, bound = true;
const original = apiClient.defaults.adapter;
const i18n = (locale: Language) => createI18n({ legacy: false, locale, fallbackLocale: false, messageCompiler: safeMessageCompiler, messages: { en, tr } });
async function mount(locale: Language) {
  const pinia = createPinia(), auth = useAuthStore(pinia);
  auth.session = { isAuthenticated: true, requiresSetupWizard: false, user: { id: 'admin', userName: 'admin', email: 'admin@example.test', role: 'Administrator',
    preferredLanguage: locale, isActive: true, isTwoFactorEnabled: false, lastLoginAtUtc: null } };
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  app = renderer.createApp({ ...ServerHealthView, render: () => h('section') } as Component).use(pinia).use(i18n(locale)); app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: State }).setupState;
  for (let n = 0; n < 50; n++) await nextTick();
  return state;
}
const render = async (state: State, locale: Language) => (await renderToString(createSSRApp({ ...ServerHealthView, setup: () => state } as Component).use(i18n(locale)))).replace(/<!--[\s\S]*?-->/g, '');
const serviceRow = (html: string) => html.match(/<article class="health-row"[^>]*>[\s\S]*?<\/article>/)?.[0] ?? '';
const dnsCard = (html: string) => html.match(/<article class="dns-card[\s\S]*?<\/article>/g)?.find(card => card.includes('class="dns-facts"')) ?? '';
beforeEach(() => {
  invalidateSessionRequests(); serviceStatus = 'Running'; failedRead = false; bound = true;
  vi.stubGlobal('window', { location: { pathname: '/panel', hostname: 'localhost' } });
  apiClient.defaults.adapter = async config => {
    expect(config.method).toBe('get');
    if (failedRead) throw new Error('Health measurement failed');
    const data = config.url === '/server-health' ? [{ name: 'W3SVC', label: 'IIS', status: serviceStatus }] :
      config.url === '/dns/server/health' ? { installed: serviceStatus !== 'NotInstalled', serviceStatus, startMode: 'Auto', delayedAutoStart: true,
        listeningAddresses: bound ? ['192.0.2.1'] : [], expectedAddresses: ['192.0.2.1'], missingAddresses: bound ? [] : ['192.0.2.1'],
        publiclyBound: bound, zoneCount: 1, needsRepair: !bound, lastAutoRepairUtc: null, lastAutoRepairStatus: '', lastAutoRepairSucceeded: null, details: '' } :
      { totalSites: 0, updatedSites: 0, failedSites: 0, serverFindings: [], results: [] };
    return { config, data: { success: true, data }, headers: {}, status: 200, statusText: 'OK' };
  };
});
afterEach(() => { app?.unmount(); app = undefined; apiClient.defaults.adapter = original; invalidateSessionRequests(); vi.unstubAllGlobals(); });

const knownStates = [
  ['StartPending', 'Starting', 'Başlatılıyor'], ['StopPending', 'Stopping', 'Durduruluyor'],
  ['ContinuePending', 'Resuming', 'Devam ettiriliyor'], ['PausePending', 'Pausing', 'Duraklatılıyor'], ['Paused', 'Paused', 'Duraklatıldı']
] as const;
for (const locale of ['en', 'tr'] as const) {
  it.each(knownStates)(`${locale}: shows the measured %s state and keeps restart unavailable`, async (status, english, turkish) => {
    serviceStatus = status; const state = await mount(locale), row = serviceRow(await render(state, locale));
    expect(row).not.toBe(''); expect(row).toContain(locale === 'en' ? english : turkish);
    expect(row).not.toContain((locale === 'en' ? en : tr)['panel.health.statusUnknown']);
    expect(row).toContain('status-pill warn'); expect(row).toMatch(/<button[^>]*disabled/); expect(state.canRestart('W3SVC')).toBe(false);
  });
  it.each(['Running', 'Stopped', 'StartPending', 'Paused'])(`${locale}: reports DNS service state separately from the listener measurement (%s)`, async status => {
    serviceStatus = status; bound = false;
    const card = dnsCard(await render(await mount(locale), locale));
    expect(card).toContain(locale === 'en' ? 'Service status' : 'Servis durumu');
    const label = status === 'Running' ? (locale === 'en' ? 'Running' : 'Çalışıyor') : status === 'Stopped' ? (locale === 'en' ? 'Stopped' : 'Durdu') : status === 'StartPending' ? (locale === 'en' ? 'Starting' : 'Başlatılıyor') : (locale === 'en' ? 'Paused' : 'Duraklatıldı');
    expect(card).toContain(label); expect(card).not.toContain(locale === 'en' ? 'The DNS service is running but' : 'DNS servisi çalışıyor ama');
    expect(card).not.toContain(locale === 'en' ? 'Not answering externally' : 'Dışarıya yanıt vermiyor');
  });
  it(`${locale}: does not claim a successful Internet query from a local listener measurement`, async () => {
    const card = dnsCard(await render(await mount(locale), locale));
    expect(card).toContain(locale === 'en' ? 'TCP and UDP' : 'TCP ve UDP');
    expect(card).not.toContain(locale === 'en' ? 'Answering externally' : 'Dışarıya yanıt veriyor');
  });
  it(`${locale}: retains Unknown and blocks restart after a previously successful read fails`, async () => {
    serviceStatus = 'StartPending'; const state = await mount(locale); failedRead = true; await state.load();
    const html = await render(state, locale), row = serviceRow(html), messages = locale === 'en' ? en : tr;
    expect(row).toContain(messages['panel.health.statusUnknown']); expect(row).not.toContain('status-pill warn');
    expect(row).toMatch(/<button[^>]*disabled/); expect(html).toContain(messages['panel.health.lastKnownServices']); expect(dnsCard(html)).toBe('');
  });
}
