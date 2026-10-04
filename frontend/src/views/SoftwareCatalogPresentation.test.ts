import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { createPinia } from 'pinia';
import { createI18n } from 'vue-i18n';
import { createRenderer, createSSRApp, h, nextTick, ssrContextKey, type App, type Component } from 'vue';
import { renderToString } from '@vue/server-renderer';
import SoftwareView from './SoftwareView.vue';
import { apiClient, invalidateSessionRequests } from '../api/client';
import { useSoftwareStore, type SoftwareCatalog, type ComponentInstallResult } from '../stores/software';
import { useAuthStore } from '../stores/auth';
import { safeMessageCompiler } from '../i18n';
import en from '../i18n/en.json';
import tr from '../i18n/tr.json';

vi.mock('./AppStoreView.vue', () => ({ default: { render: () => null } }));
vi.mock('../components/ConfirmDialog.vue', () => ({ useConfirm: () => ({ confirm: vi.fn() }) }));
type Language = 'tr' | 'en';
type State = { activeWorkspace: string; refreshCatalog(): Promise<void>; canWrite: boolean; installComponent(id: string): Promise<void>; installDefaultStack(): Promise<void> };
let app: App | undefined, failRead: boolean, catalog: SoftwareCatalog, readBarrier: Promise<void> | undefined, installResults: ComponentInstallResult[], posts: number;
const original = apiClient.defaults.adapter;
const i18n = (locale: Language) => createI18n({ legacy: false, locale, fallbackLocale: false, messageCompiler: safeMessageCompiler, messages: { en, tr } });
const flush = async () => { for (let n = 0; n < 40; n++) await nextTick(); };
const populated = (): SoftwareCatalog => ({
  manifest: [{ id: 'nginx', displayName: 'Nginx', version: '1.2', type: 'zip', installDir: 'fixture/nginx', downloadUrl: '', sha256: '', postInstall: [], dependencies: [], serviceName: 'nginx', silentArgs: '', note: '', default: false }], defaults: [],
  installed: [{ id: 'nginx-record', componentId: 'nginx', displayName: 'Nginx', version: '1.2', type: 'Zip', installDir: 'fixture/nginx', serviceName: 'nginx', status: 'Installed', lastError: '', generatedPassword: '', isSystemDetected: false, detectionSource: '', availableVersion: '', updateAvailable: false, versionCheckSupported: true, versionCheckFailed: false, installedAtUtc: '2026-10-04T00:00:00Z' }]
});
async function mount(locale: Language) {
  const pinia = createPinia(), auth = useAuthStore(pinia), store = useSoftwareStore(pinia);
  auth.session = { isAuthenticated: true, requiresSetupWizard: false, user: { id: 'admin', userName: 'admin', email: 'admin@example.test', role: 'Administrator', preferredLanguage: locale, isActive: true, isTwoFactorEnabled: false, lastLoginAtUtc: null } };
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  app = renderer.createApp({ ...SoftwareView, render: () => h('section') } as Component).use(pinia).use(i18n(locale)); app.provide(ssrContextKey, {});
  const state = (app.mount({}).$ as unknown as { setupState: State }).setupState;
  state.activeWorkspace = 'components'; await flush();
  return { state, store, html: async () => (await renderToString(createSSRApp({ ...SoftwareView, setup: () => state } as Component).use(i18n(locale)))).replace(/<!--[\s\S]*?-->/g, '') };
}
beforeEach(() => {
  invalidateSessionRequests(); failRead = false; catalog = { manifest: [], defaults: [], installed: [] }; readBarrier = undefined; installResults = []; posts = 0;
  vi.stubGlobal('window', { location: { pathname: '/panel', hostname: 'localhost' } });
  vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify({ success: true, data: { requestToken: 'fixture-only' } }), { status: 200 })));
  apiClient.defaults.adapter = async config => {
    if (config.method === 'post' && ['/software/install', '/software/install-defaults'].includes(config.url ?? '')) {
      posts++; failRead = true;
      return { config, data: { success: true, data: config.url === '/software/install' ? installResults[0] : installResults }, status: 200, statusText: 'OK', headers: {} };
    }
    expect(config.method).toBe('get');
    if (config.url === '/software') { if (readBarrier) await readBarrier; if (failRead) throw new Error('Fixture catalog measurement failed'); }
    else if (config.url === '/software/operations/progress') return { config, data: { success: true, data: { steps: [] } }, status: 200, statusText: 'OK', headers: {} };
    else expect(config.url).toBe('/software/running');
    return { config, data: { success: true, data: config.url === '/software' ? catalog : { running: [], interrupted: [], serverTimeUtc: '2026-10-04T00:00:00Z' } }, status: 200, statusText: 'OK', headers: {} };
  };
});
afterEach(() => { app?.unmount(); app = undefined; apiClient.defaults.adapter = original; invalidateSessionRequests(); vi.unstubAllGlobals(); });

for (const locale of ['en', 'tr'] as const) {
  const messages = locale === 'en' ? en : tr;
  const unconfirmed = locale === 'en' ? 'Catalog could not be verified' : 'Bileşen listesi doğrulanamadı';
  const lastKnown = locale === 'en' ? 'Previously read results are shown' : 'Önceki okumanın sonuçları gösteriliyor';
  it(`${locale}: ends the loading presentation when the first read has failed`, async () => {
    failRead = true; const view = await mount(locale); expect(view.store.loading).toBe(false); expect(view.store.loaded).toBe(false);
    const html = await view.html(); expect(html).toContain(unconfirmed); expect(html).toContain(messages['panel.software.scanSystem']);
    expect(html).not.toContain(messages['panel.common.loading']); expect(html).not.toContain(messages['panel.software.noItems']); expect(html).not.toContain(messages['panel.software.noInstalled']);
    expect(view.state.canWrite).toBe(false);
  });
  it(`${locale}: identifies retained rows as last known after a failed refresh`, async () => {
    catalog = populated(); const view = await mount(locale); expect(view.store.loaded).toBe(true);
    failRead = true; await view.state.refreshCatalog(); const html = await view.html();
    expect(html).toContain('Nginx'); expect(html).toContain(lastKnown); expect(view.state.canWrite).toBe(false);
    expect(html).not.toContain(messages['panel.common.loading']);
  });
  it(`${locale}: shows loading only during an outstanding read and clears stale warnings after retry`, async () => {
    let release!: () => void; readBarrier = new Promise<void>(resolve => { release = resolve; });
    const view = await mount(locale); expect(view.store.loading).toBe(true);
    const pending = await view.html(); expect(pending).toContain(messages['panel.common.loading']); expect(pending).not.toContain(unconfirmed);
    failRead = true; release(); await flush(); expect(view.store.loading).toBe(false); expect(await view.html()).toContain(unconfirmed);
    readBarrier = undefined; failRead = false; await view.state.refreshCatalog();
    const retried = await view.html(); expect(view.store.loaded).toBe(true); expect(retried).not.toContain(unconfirmed); expect(retried).not.toContain(lastKnown);
    expect(retried).toContain(messages['panel.software.noItems']); expect(retried).toContain(messages['panel.software.noInstalled']);
  });
  it(`${locale}: reports a measured empty catalog without an error or loading claim`, async () => {
    const view = await mount(locale), html = await view.html(); expect(view.store.loaded).toBe(true);
    expect(html).toContain(messages['panel.software.noItems']); expect(html).toContain(messages['panel.software.noInstalled']);
    expect(html).not.toContain(unconfirmed); expect(html).not.toContain(messages['panel.common.loading']);
  });
  it.each([false, true])(`${locale}: renders each measured installation result and preserves credentials across a read-only retry (defaults=%s)`, async defaults => {
    catalog = populated(); catalog.installed = [];
    installResults = [{ componentId: 'nginx', displayName: 'Nginx', version: '1.2', status: 'Installed', installDir: 'fixture/nginx', serviceName: 'nginx', generatedPassword: 'DEMO-ONLY-NOT-A-REAL-PASSWORD', message: 'software.install.completed' }];
    if (defaults) {
      catalog.manifest.push({ ...catalog.manifest[0], id: 'mariadb-11', displayName: 'MariaDB' }); catalog.defaults = [...catalog.manifest];
      installResults.push({ ...installResults[0], componentId: 'mariadb-11', displayName: 'MariaDB', status: 'Failed', version: '', generatedPassword: '', message: 'software.install_failed' });
    }
    const view = await mount(locale);
    if (defaults) await view.state.installDefaultStack(); else await view.state.installComponent('nginx');
    const html = await view.html(), resultCard = html.match(/<section class="installation-result"[\s\S]*?<\/section>/)?.[0] ?? '';
    expect(resultCard).toContain(messages['panel.software.installationResult']); expect(resultCard).toContain('Nginx');
    expect(resultCard).toContain(messages['panel.software.state.installed']);
    expect(resultCard).toMatch(/<input[^>]*value="DEMO-ONLY-NOT-A-REAL-PASSWORD"[^>]*readonly/);
    expect(resultCard).toContain(messages['panel.software.refreshAfterInstallFailed']);
    if (defaults) { expect(resultCard).toContain('MariaDB'); expect(resultCard).toContain(messages['panel.software.state.failed']); }
    expect(view.state.canWrite).toBe(false); expect(posts).toBe(1);
    failRead = false; await view.state.refreshCatalog(); const retried = await view.html();
    expect(posts).toBe(1); expect(retried).toContain('DEMO-ONLY-NOT-A-REAL-PASSWORD'); expect(retried).not.toContain(messages['panel.software.refreshAfterInstallFailed']);
  });
}
