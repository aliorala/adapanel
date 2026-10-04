import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { createPinia } from 'pinia';
import { createI18n } from 'vue-i18n';
import { createRenderer, createSSRApp, h, nextTick, ssrContextKey, type App, type Component } from 'vue';
import { renderToString } from '@vue/server-renderer';
import PhpView from './PhpView.vue';
import { apiClient, invalidateSessionRequests } from '../api/client';
import { useAuthStore } from '../stores/auth';
import { usePhpStore } from '../stores/php';
import { useSitesStore } from '../stores/sites';
import { useSubscriptionStore } from '../stores/subscription';
import { safeMessageCompiler } from '../i18n';
import en from '../i18n/en.json';
import tr from '../i18n/tr.json';

vi.mock('../components/ConfirmDialog.vue', () => ({ useConfirm: () => ({ confirm: vi.fn() }) }));
type State = { notice: string; noticeError: boolean; noticeWarning: boolean; settings: ReturnType<typeof siteSettings>; assignVersion(id: string): Promise<void>; saveSettings(): Promise<void> };
const version = { componentId: 'php-8.3', displayName: 'PHP 8.3', version: '8.3.20', installDir: 'fixture/php', executablePath: 'fixture/php/php-cgi.exe', status: 'Installed', isFastCgiRegistered: true, installedAtUtc: '2026-10-04T00:00:00Z' };
const siteSettings = (siteName='example.test') => ({ siteName, memoryLimit: '256M', maxExecutionTime: 120, uploadMaxFilesize: '64M', postMaxSize: '64M', displayErrors: false, openBasedirEnabled: true, configured: true });
const order = (kind: string) => ({ measured: kind !== 'unverified', handlerFound: kind !== 'missing', precedesCatchAll: kind === 'ordered', handlerIndex: 0, catchAllIndex: 1, blockingHandlerName: '', insertedAtFront: true });
let app: App | undefined, outcome: string, writes: string[], barrier: Promise<void> | undefined;
const original = apiClient.defaults.adapter;
const flush = async () => { for(let n=0;n<50;n++) await nextTick(); };
const i18n = (locale: 'tr'|'en') => createI18n({ legacy:false, locale, fallbackLocale:false, messageCompiler:safeMessageCompiler, messages:{en,tr} });
async function mount(locale: 'tr'|'en'='en') {
  const pinia=createPinia(), auth=useAuthStore(pinia), sites=useSitesStore(pinia), php=usePhpStore(pinia);
  auth.session={isAuthenticated:true,requiresSetupWizard:false,user:{id:'admin',userName:'admin',email:'admin@example.test',role:'Administrator',preferredLanguage:locale,isActive:true,isTwoFactorEnabled:false,lastLoginAtUtc:null}};
  sites.selectedSiteName='example.test'; vi.spyOn(sites,'fetchSites').mockResolvedValue([]);
  const renderer=createRenderer<object,object>({createElement:()=>({}),createText:()=>({}),createComment:()=>({}),insert(){},remove(){},setText(){},setElementText(){},patchProp(){},parentNode:()=>null,nextSibling:()=>null});
  app=renderer.createApp({...PhpView,render:()=>h('section')} as Component).use(pinia).use(i18n(locale));app.provide(ssrContextKey,{});
  const state=(app.mount({}).$ as unknown as {setupState:State}).setupState; await flush();
  return {state,pinia,php,auth,sites,html:async()=>(await renderToString(createSSRApp({...PhpView,setup:()=>state} as Component).use(i18n(locale)))).replace(/<!--[\s\S]*?-->/g,'')};
}
beforeEach(()=>{
  invalidateSessionRequests();outcome='ordered';writes=[];barrier=undefined;
  vi.stubGlobal('window',{location:{pathname:'/panel',hostname:'localhost'}});
  vi.stubGlobal('fetch',vi.fn(async()=>new Response(JSON.stringify({success:true,data:{requestToken:'fixture-only'}}),{status:200})));
  apiClient.defaults.adapter=async config=>{
    let data:unknown;
    if(config.method==='get'){
      if(config.url==='/php/versions')data=[version];
      else if(config.url==='/software')data={manifest:[],installed:[],defaults:[]};
      else if(config.url?.endsWith('/php/settings'))data=siteSettings(config.url.split('/')[2]);
      else throw new Error('Unexpected GET '+config.url);
    } else {
      writes.push(config.url!); if(barrier) await barrier;
      if(config.url?.endsWith('/php/settings'))data={...siteSettings(),memoryLimit:'128M',maxExecutionTime:60,uploadMaxFilesize:'16M',postMaxSize:'32M',displayErrors:true,openBasedirEnabled:false};
      else if(config.url?.endsWith('/php'))data={...version,isFastCgiRegistered:outcome!=='fastcgi',siteName:'example.test',handlerName:'PHP',...(outcome==='legacy'?{}:{handlerOrder:order(outcome==='fastcgi'?'ordered':outcome),handlerOrderNoticeKey:outcome==='ordered'?'':`aspnet.handler_${outcome==='unverified'?'order_unverified':outcome}`})};
      else throw new Error('Unexpected write '+config.url);
    }
    return {config,data:{success:true,data,message:'php.assignment.success',code:200},headers:{},status:200,statusText:'OK'};
  };
});
afterEach(()=>{app?.unmount();app=undefined;apiClient.defaults.adapter=original;invalidateSessionRequests();vi.restoreAllMocks();vi.unstubAllGlobals();});

for(const locale of ['tr','en'] as const){
  const messages=locale==='tr'?tr:en;
  it.each(['unverified','missing','shadowed','legacy'])(`${locale}: preserves %s assignment outcome as a visible warning`,async kind=>{
    outcome=kind;const view=await mount(locale);await view.state.assignVersion('php-8.3');
    const key=kind==='legacy'||kind==='unverified'?'aspnet.handler_order_unverified':kind==='missing'?'aspnet.handler_missing':'aspnet.handler_shadowed';
    expect(view.state.notice).toBe(messages[key]);expect(view.state.noticeWarning).toBe(true);expect(view.state.noticeError).toBe(false);
    const html=await view.html();expect(html).toMatch(/class="[^"]*notice[^"]*warn-banner[^\"]*"[^>]*role="status"/);expect(html).not.toContain(messages['php.assignment.success']);
    expect(writes).toEqual(['/sites/example.test/php']);
  });
  it(`${locale}: only a measured ordered assignment shows plain success`,async()=>{
    const view=await mount(locale);await view.state.assignVersion('php-8.3');expect(view.state.notice).toBe(messages['php.assignment.success']);expect(view.state.noticeError).toBe(false);expect(view.state.noticeWarning).toBe(false);
  });
}
it('uses every returned saved setting instead of displaying rejected draft values',async()=>{
  const view=await mount();Object.assign(view.state.settings,{memoryLimit:'512M',maxExecutionTime:3600,uploadMaxFilesize:'128M',postMaxSize:'256M',displayErrors:false,openBasedirEnabled:true});
  await view.state.saveSettings();expect(view.state.settings).toMatchObject({memoryLimit:'128M',maxExecutionTime:60,uploadMaxFilesize:'16M',postMaxSize:'32M',displayErrors:true,openBasedirEnabled:false});
});
it.each(['subscription','logout','epoch'])('blocks direct settings saves after %s changes',async change=>{
  const view=await mount();if(change==='subscription')useSubscriptionStore(view.pinia).$patch({usage:{isSuspended:true}});
  else if(change==='logout')view.auth.session=null;else invalidateSessionRequests();
  await view.state.saveSettings();expect(writes).toEqual([]);
});
it('does not show a late assignment warning on a newly selected site',async()=>{
  outcome='shadowed';const view=await mount();let release!:()=>void;barrier=new Promise<void>(resolve=>release=resolve);
  const request=view.state.assignVersion('php-8.3');await flush();view.sites.selectedSiteName='second.test';await flush();release();await request;
  expect(view.state.notice).toBe('');expect((await view.html())).not.toMatch(/<span>Last applied assignment<\/span>\s*<strong>PHP 8.3<\/strong>/);
});
it('warns when handler order is correct but FastCGI registration is missing',async()=>{
  outcome='fastcgi';const view=await mount();await view.state.assignVersion('php-8.3');
  expect(view.state.notice).toContain(en['panel.php.fastCgiMissing']);expect(view.state.notice).not.toContain(en['php.assignment.success']);expect(view.state.noticeWarning).toBe(true);
});
