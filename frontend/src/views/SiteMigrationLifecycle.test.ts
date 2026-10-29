import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { createRenderer, h, nextTick, reactive, ssrContextKey, type App, type Component } from 'vue';
import SiteMigrationView from './SiteMigrationView.vue';
import { importSiteArchive, migrateSiteFiles } from '../api/siteHosting';
import { invalidateSessionRequests } from '../api/client';
let sites: {selectedSiteName:string};
vi.mock('../stores/sites',()=>({useSitesStore:()=>sites}));
vi.mock('../composables/useSubscriptionGate',()=>({useSubscriptionGate:()=>({writeGate:()=>({})})}));
vi.mock('vue-i18n',()=>({useI18n:()=>({t:(key:string)=>key})}));
vi.mock('../api/siteHosting',()=>({importSiteArchive:vi.fn(),migrateSiteFiles:vi.fn()}));
let app:App|undefined;
beforeEach(()=>{sites=reactive({selectedSiteName:'first.test'});});
afterEach(()=>{app?.unmount();app=undefined;vi.resetAllMocks();});
type State={archiveUrl:string;ftp:{host:string;userName:string};notice:string;busy:boolean;run():Promise<void>;runFtp():Promise<void>};
async function mount(){
const renderer=createRenderer<object,object>({createElement:()=>({}),createText:()=>({}),createComment:()=>({}),insert(){},remove(){},setText(){},setElementText(){},patchProp(){},parentNode:()=>null,nextSibling:()=>null});
app=renderer.createApp({...SiteMigrationView,render:()=>h('section')} as Component);app.provide(ssrContextKey,{});return(app.mount({}).$ as unknown as{setupState:State}).setupState;
}
for(const operation of ['archive','ftp'] as const)for(const context of ['site','session','unmount'] as const){
it(`discards late ${operation} notice after ${context}`,async()=>{
 const state=await mount();state.archiveUrl='https://fake.example/file.zip';state.ftp.host='fake.example';state.ftp.userName='fixture';
 let resolve!:(result:never)=>void;
 if(operation==='archive')vi.mocked(importSiteArchive).mockImplementationOnce(()=>new Promise(r=>{resolve=r;}));
 else vi.mocked(migrateSiteFiles).mockImplementationOnce(()=>new Promise(r=>{resolve=r;}));
 const pending=operation==='archive'?state.run():state.runFtp();
 if(context==='site'){sites.selectedSiteName='second.test';await nextTick();}else if(context==='session')invalidateSessionRequests();else{app?.unmount();app=undefined;}
 resolve({filesCopied:3,filesDownloaded:3} as never);await pending;expect(state.notice).toBe('');
});}
