import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { createRenderer, h, nextTick, ssrContextKey, type App, type Component } from 'vue';
import LicenseView from './LicenseView.vue';
import * as api from '../api/license';
import { invalidateSessionRequests } from '../api/client';
import ConfirmDialog, { closeConfirmDialog, dialogState } from '../components/ConfirmDialog.vue';
vi.mock('vue-i18n',()=>({useI18n:()=>({t:(key:string)=>key})}));
vi.mock('../api/license',()=>({getLicense:vi.fn(),activateLicense:vi.fn(),refreshLicense:vi.fn(),removeLicense:vi.fn()}));
let app:App|undefined, dialogApp:App|undefined;
const status=(suffix:string)=>({state:'Active',isConfigured:true,hasKey:true,keySuffix:suffix,messageKey:'license.active'} as api.LicenseStatus);
const deferred=<T,>()=>{let resolve!:(value:T)=>void;const promise=new Promise<T>(r=>{resolve=r;});return{promise,resolve};};
type State={status:api.LicenseStatus|null;licenseKey:string;busy:boolean;notice:string;loadError:{message:{value:string}};load():Promise<void>;activate():Promise<void>;refreshStatus():Promise<void>;remove():Promise<void>};
async function flush(){for(let i=0;i<10;i++)await nextTick();}
async function mount(){const renderer=createRenderer<object,object>({createElement:()=>({}),createText:()=>({}),createComment:()=>({}),insert(){},remove(){},setText(){},setElementText(){},patchProp(){},parentNode:()=>null,nextSibling:()=>null});app=renderer.createApp({...LicenseView,render:()=>h('section')} as Component);app.provide(ssrContextKey,{});const state=(app.mount({}).$ as unknown as{setupState:State}).setupState;await flush();return state;}
beforeEach(()=>{vi.stubGlobal('document',{body:{style:{overflow:''}},activeElement:null});vi.stubGlobal('HTMLElement',class {});vi.mocked(api.getLicense).mockResolvedValue(status('old'));});
afterEach(()=>{closeConfirmDialog();app?.unmount();app=undefined;dialogApp?.unmount();dialogApp=undefined;vi.resetAllMocks();vi.unstubAllGlobals();});
it('does not let an older GET replace activation or leave a stale load error',async()=>{
 const old=deferred<api.LicenseStatus>();vi.mocked(api.getLicense).mockReturnValueOnce(old.promise);
 const state=await mount();state.licenseKey='first';vi.mocked(api.activateLicense).mockResolvedValue(status('new'));await state.activate();old.resolve(status('old'));await flush();expect(state.status?.keySuffix).toBe('new');
});
it('activation preserves a newer key draft and clears a prior load error',async()=>{
 vi.mocked(api.getLicense).mockRejectedValueOnce(new Error('read failed'));const state=await mount();const saving=deferred<api.LicenseStatus>();vi.mocked(api.activateLicense).mockReturnValueOnce(saving.promise);state.licenseKey='first';const pending=state.activate();state.licenseKey='second';saving.resolve(status('new'));await pending;expect(state.licenseKey).toBe('second');expect(state.loadError.message.value).toBe('');
});
for(const context of ['session','unmount','license'] as const)it(`does not execute a remove confirmation for an obsolete ${context}`,async()=>{
 const state=await mount();const dialog=mountDialog();const waiting=state.remove();
 if(context==='session')invalidateSessionRequests();else if(context==='unmount'){app?.unmount();app=undefined;}else{vi.mocked(api.refreshLicense).mockResolvedValue(status('new'));await state.refreshStatus();}
 await dialog.accept();await waiting;expect(api.removeLicense).not.toHaveBeenCalled();
});
for(const operation of ['activate','refreshStatus'] as const)for(const context of ['session','unmount'] as const)it(`ignores late ${operation} after ${context}`,async()=>{
 const state=await mount();state.licenseKey='first';const reply=deferred<api.LicenseStatus>();if(operation==='activate')vi.mocked(api.activateLicense).mockReturnValueOnce(reply.promise);else vi.mocked(api.refreshLicense).mockReturnValueOnce(reply.promise);
 const pending=state[operation]();if(context==='session')invalidateSessionRequests();else{app?.unmount();app=undefined;}
 reply.resolve(status('new'));await pending;expect(state.status?.keySuffix).toBe('old');expect(state.notice).toBe('');
});
for(const context of ['session','unmount'] as const)it(`ignores a remove response after ${context}`,async()=>{
 const state=await mount();const reply=deferred<api.LicenseStatus>();vi.mocked(api.removeLicense).mockReturnValueOnce(reply.promise);
 const waiting=state.remove(),mutating=dialogState.options!.action!();
 if(context==='session')invalidateSessionRequests();else{app?.unmount();app=undefined;}
 reply.resolve(status('removed'));await mutating;closeConfirmDialog();await waiting;expect(state.status?.keySuffix).toBe('old');expect(state.notice).toBe('');
});
it('keeps a successful refresh when an older GET later fails',async()=>{
 let reject!:(error:unknown)=>void;vi.mocked(api.getLicense).mockReturnValueOnce(new Promise((_resolve,no)=>{reject=no;}));
 const state=await mount();vi.mocked(api.refreshLicense).mockResolvedValue(status('refreshed'));await state.refreshStatus();reject(new Error('old failure'));await flush();expect(state.status?.keySuffix).toBe('refreshed');expect(state.loadError.message.value).toBe('');
});

function mountDialog(){
 const renderer=createRenderer<object,object>({createElement:()=>({}),createText:()=>({}),createComment:()=>({}),insert(){},remove(){},setText(){},setElementText(){},patchProp(){},parentNode:()=>null,nextSibling:()=>null});
 dialogApp=renderer.createApp({...ConfirmDialog,render:()=>h('section')} as Component);dialogApp.provide(ssrContextKey,{});
 return(dialogApp.mount({}).$ as unknown as{setupState:{accept():Promise<void>}}).setupState;
}