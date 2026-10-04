import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { createPinia } from 'pinia';
import { createI18n } from 'vue-i18n';
import { createRenderer, createSSRApp, h, nextTick, ssrContextKey, type App, type Component } from 'vue';
import { renderToString } from '@vue/server-renderer';
import PhpView from './PhpView.vue';
import { apiClient, invalidateSessionRequests } from '../api/client';
import { useSoftwareStore, type SoftwareCatalog } from '../stores/software';
import { usePhpStore } from '../stores/php';
import { useAuthStore } from '../stores/auth';
import { useSitesStore } from '../stores/sites';
import { useSubscriptionStore } from '../stores/subscription';
import { safeMessageCompiler } from '../i18n';
import en from '../i18n/en.json';
import tr from '../i18n/tr.json';

const confirmation = vi.hoisted(() => ({ action: undefined as undefined | (() => Promise<unknown>), immediate: true }));
vi.mock('../components/ConfirmDialog.vue', () => ({ useConfirm: () => ({ confirm: async (options: { action: () => Promise<unknown> }) => { confirmation.action = options.action; if (confirmation.immediate) await options.action(); return true; } }) }));
type State = { activeTab: string; notice: string; noticeError: boolean; refreshAll(): Promise<void>; installVersion(id: string): Promise<void>; assignVersion(id: string): Promise<void>; removeVersion(id: string): Promise<void>; enableRedis(): Promise<void> };
type Language = 'tr' | 'en';
const version = { componentId: 'php-8.3', displayName: 'PHP 8.3', version: '8.3.20', installDir: 'fixture/php', executablePath: 'fixture/php/php-cgi.exe', status: 'Installed', isFastCgiRegistered: true, installedAtUtc: '2026-10-04T00:00:00Z' };
const component = { id: 'php-8.4', displayName: 'PHP 8.4', version: '8.4.2', type: 'zip', installDir: 'fixture/php84', downloadUrl: '', sha256: '', postInstall: [], dependencies: [], serviceName: '', silentArgs: '', note: '', default: false };
const catalog = (): SoftwareCatalog => ({ manifest: [component], defaults: [], installed: [] });
const settings = (site: string) => ({ siteName: site, memoryLimit: '256M', maxExecutionTime: 120, uploadMaxFilesize: '64M', postMaxSize: '64M', displayErrors: false, openBasedirEnabled: true, configured: true });
let app: App | undefined, phpFailure: boolean, catalogFailure: boolean, readBarrier: Promise<void> | undefined, writes: string[], reads: string[], failAfterInstall: boolean, removalFailure: string;
const original = apiClient.defaults.adapter;
const i18n = (locale: Language) => createI18n({ legacy: false, locale, fallbackLocale: false, messageCompiler: safeMessageCompiler, messages: { en, tr } });
const flush = async () => { for (let n=0;n<50;n++) await nextTick(); };
async function mount(locale: Language='en', role='Administrator') {
  const pinia=createPinia(), auth=useAuthStore(pinia), php=usePhpStore(pinia), software=useSoftwareStore(pinia), sites=useSitesStore(pinia);
  auth.session={ isAuthenticated:true, requiresSetupWizard:false, user:{id:'admin',userName:'admin',email:'admin@example.test',role,preferredLanguage:locale,isActive:true,isTwoFactorEnabled:false,lastLoginAtUtc:null} };
  sites.selectedSiteName='example.test'; vi.spyOn(sites,'fetchSites').mockResolvedValue([]);
  const renderer=createRenderer<object,object>({createElement:()=>({}),createText:()=>({}),createComment:()=>({}),insert(){},remove(){},setText(){},setElementText(){},patchProp(){},parentNode:()=>null,nextSibling:()=>null});
  app=renderer.createApp({...PhpView,render:()=>h('section')} as Component).use(pinia).use(i18n(locale)); app.provide(ssrContextKey,{});
  const state=(app.mount({}).$ as unknown as {setupState:State}).setupState; await flush();
  return {state,php,software,auth,sites,pinia,html:async()=> (await renderToString(createSSRApp({...PhpView,setup:()=>state} as Component).use(i18n(locale)))).replace(/<!--[\s\S]*?-->/g,'')};
}
beforeEach(()=>{
  invalidateSessionRequests(); phpFailure=false;catalogFailure=false;readBarrier=undefined;writes=[];reads=[];failAfterInstall=false;removalFailure='';confirmation.action=undefined;confirmation.immediate=true;
  vi.stubGlobal('window',{location:{pathname:'/panel',hostname:'localhost'}});
  vi.stubGlobal('fetch',vi.fn(async()=>new Response(JSON.stringify({success:true,data:{requestToken:'fixture-only'}}),{status:200})));
  apiClient.defaults.adapter=async config=>{
    let data:unknown, message='software.install.completed';
    if(config.method==='get') {
      reads.push(config.url!);
      if(config.url==='/php/versions'){if(readBarrier)await readBarrier;if(phpFailure)throw new Error('fixture.php_unavailable');data=[version];}
      else if(config.url==='/software'){if(catalogFailure)throw new Error('fixture.catalog_unavailable');data=catalog();}
      else if(config.url?.endsWith('/php/settings'))data=settings(config.url.split('/')[2]);
      else throw new Error('Unexpected GET '+config.url);
    } else {
      writes.push(config.url!);
      if(config.url==='/php/install'){
        if(failAfterInstall)phpFailure=true;
        data={componentId:'php-8.4',displayName:'PHP 8.4',version:'8.4.2',versionMeasured:true,status:'Installed',installDir:'fixture/php84',serviceName:'',generatedPassword:'',message:'software.install.completed'};
      }
      else if(config.url==='/software/php-8.3') {
        if(removalFailure==='read')catalogFailure=true;
        if(removalFailure==='write')throw new Error('fixture.partial_remove_failed');
        data={componentId:'php-8.3',warnings:removalFailure==='partial'?['fixture locked runtime']:[]};
      }
      else if(config.url==='/php/enable-redis')data={enabled:true,message:'already',fileName:'fixture.dll'};
      else if(config.url?.endsWith('/php'))data={...version,siteName:config.url.split('/')[2],handlerName:'PHP'};
      else throw new Error('Unexpected write '+config.url);
    }
    return {config,data:{success:true,data,message,code:200},status:200,statusText:'OK',headers:{}};
  };
});
afterEach(()=>{app?.unmount();app=undefined;apiClient.defaults.adapter=original;invalidateSessionRequests();vi.restoreAllMocks();vi.unstubAllGlobals();});

for(const locale of ['tr','en'] as const) {
  const messages=locale==='tr'?tr:en;
  it(`${locale}: failed first installed read ends loading and offers a retry without claiming no versions`,async()=>{
    phpFailure=true; const view=await mount(locale), html=await view.html();
    expect(view.php.loading).toBe(false);expect(html).not.toContain(messages['panel.common.loading']);expect(html).not.toContain(messages['panel.php.noInstalledVersions']);
    expect(html).toContain(messages['panel.common.loadFailed']);expect(html).toContain(messages['panel.common.retry']);
  });
  it(`${locale}: failed available catalog ends loading and offers a read-only retry`,async()=>{
    catalogFailure=true;const view=await mount(locale);view.state.activeTab='available';await flush();const html=await view.html();
    expect(html).not.toContain(messages['panel.common.loading']);expect(html).not.toContain(messages['panel.php.noManifestVersions']);expect(html).toContain(messages['panel.common.retry']);
    catalogFailure=false;await view.state.refreshAll();expect(view.software.loaded).toBe(true);expect(writes).toEqual([]);
  });
  it(`${locale}: retained rows are labelled last known and actions are disabled after read failure`,async()=>{
    const view=await mount(locale);phpFailure=true;await view.state.refreshAll();const html=await view.html();
    expect(html).toContain('PHP 8.3');expect(html).toContain(locale==='tr'?'Önceki PHP listesi':'Previously read PHP');
    expect(html).toMatch(/<button[^>]*disabled[^>]*>[\s]*[^<]*<\/button>/);
    await view.state.assignVersion('php-8.3');await view.state.removeVersion('php-8.3');await view.state.enableRedis();expect(writes).toEqual([]);
  });
}
it('blocks install while catalog measurement is unconfirmed, including direct handler calls',async()=>{
  const view=await mount();catalogFailure=true;await view.state.refreshAll();await view.state.installVersion('php-8.4');expect(writes).toEqual([]);
});
it('does not offer administrator-only reads and mutations to a reseller',async()=>{
  const view=await mount('en','Reseller'),html=await view.html();expect(reads).not.toContain('/software');
  expect(html).not.toContain(en['panel.php.enableRedis']);expect(html).not.toContain(en['panel.php.available']);
  await view.state.installVersion('php-8.4');await view.state.removeVersion('php-8.3');await view.state.enableRedis();expect(writes).toEqual([]);
  await view.state.assignVersion('php-8.3');expect(writes).toEqual(['/sites/example.test/php']);
});
it.each(['unmount','session','refresh','subscription'])('retires a removal confirmation after %s changes',async departure=>{
  const view=await mount();confirmation.immediate=false;await view.state.removeVersion('php-8.3');expect(confirmation.action).toBeTypeOf('function');
  if(departure==='unmount'){app!.unmount();app=undefined;}
  else if(departure==='session')view.auth.session={...view.auth.session!};
  else if(departure==='refresh'){await view.state.refreshAll();}
  else useSubscriptionStore(view.pinia).$patch({usage:{isSuspended:true}});
  await confirmation.action!();expect(writes).toEqual([]);
});
it('does not start duplicate refresh reads and leaves loading only while measurement is pending',async()=>{
  const view=await mount();let release!:()=>void;readBarrier=new Promise<void>(resolve=>{release=resolve;});
  const first=view.state.refreshAll();await flush();const before=reads.length;const second=view.state.refreshAll();await flush();const during=reads.length;
  const html=await view.html();release();await Promise.all([first,second]);
  expect(during).toBe(before);expect(html).toContain(en['panel.common.loading']);expect(view.php.loaded).toBe(true);expect(writes).toEqual([]);
});
it('preserves the completed installation message when the PHP list refresh fails and retry only reads',async()=>{
  const view=await mount();failAfterInstall=true;await view.state.installVersion('php-8.4');
  expect(writes).toEqual(['/php/install']);expect(view.state.noticeError).toBe(true);expect(view.state.notice).toContain(en['panel.software.refreshAfterInstallFailed']);
  expect(view.state.notice).toContain(en['software.install.completed']);phpFailure=false;await view.state.refreshAll();expect(writes).toEqual(['/php/install']);
});
it('distinguishes an acknowledged install missing from measured inventory and retries only the read',async()=>{
  const view=await mount();await view.state.installVersion('php-8.4');
  expect(writes).toEqual(['/php/install']);expect(view.state.noticeError).toBe(true);
  expect(view.state.notice).toContain(en['software.install.completed']);
  expect(view.state.notice).toContain(en['panel.php.installationUnconfirmed']);
  expect(view.state.notice).not.toContain(en['panel.software.refreshAfterInstallFailed']);
  expect(view.php.versions.map(row=>row.componentId)).toEqual(['php-8.3']);
  const before=reads.filter(path=>path==='/php/versions').length;
  await view.state.refreshAll();
  expect(reads.filter(path=>path==='/php/versions')).toHaveLength(before+1);
  expect(writes).toEqual(['/php/install']);
});
it.each(['read','partial','write'])('invalidates PHP inventory after a removal whose %s outcome needs reconciliation',async failure=>{
  const view=await mount();removalFailure=failure;await view.state.removeVersion('php-8.3');
  expect(view.state.noticeError).toBe(true);expect(writes).toEqual(['/software/php-8.3']);
  await view.state.assignVersion('php-8.3');await view.state.removeVersion('php-8.3');
  expect(writes).toEqual(['/software/php-8.3']);expect(view.php.loaded).toBe(false);
  catalogFailure=false;await view.state.refreshAll();expect(view.php.loaded).toBe(true);
});
