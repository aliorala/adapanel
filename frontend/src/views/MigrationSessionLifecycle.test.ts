import { afterEach, expect, it, vi } from 'vitest';
import { createRenderer, h, nextTick, ssrContextKey, type App, type Component } from 'vue';
import MigrationView from './MigrationView.vue';
import * as api from '../api/migration';
import { invalidateSessionRequests } from '../api/client';

vi.mock('vue-i18n', () => ({ useI18n: () => ({ t: (key: string) => key }) }));
vi.mock('../api/migration', async original => ({ ...await original<object>(), migrateSite: vi.fn(), migrateSiteStream: vi.fn(),
  getMigrationRunningJobs: vi.fn().mockResolvedValue({ running: [], interrupted: [] }) }));
let app: App | undefined;
afterEach(() => { app?.unmount(); app=undefined; vi.clearAllMocks(); });
type State = { form: {siteName:string}; plesk:{host:string}; selectedDomains:string[]; runBulk():Promise<void>; run():Promise<void>; notice:string; liveSteps:{step:string}[]; bulkLog:unknown[] };
async function mount() {
  const renderer=createRenderer<object,object>({createElement:()=>({}),createText:()=>({}),createComment:()=>({}),insert(){},remove(){},setText(){},setElementText(){},patchProp(){},parentNode:()=>null,nextSibling:()=>null});
  app=renderer.createApp({...MigrationView,render:()=>h('section')} as Component); app.provide(ssrContextKey,{});
  const state=(app.mount({}).$ as unknown as {setupState:State}).setupState;
  for(let i=0;i<10;i++) await nextTick(); return state;
}
for(const invalidate of ['session','unmount'] as const) {
  it(`does not begin another bulk mutation after ${invalidate}`,async()=>{
    const state=await mount(); state.plesk.host='fake.example'; state.selectedDomains=['first.test','second.test'];
    let resolve!:(result:api.MigrateSiteResult)=>void;
    vi.mocked(api.migrateSite).mockImplementationOnce(()=>new Promise(r=>{resolve=r;}));
    const pending=state.runBulk();
    if(invalidate==='session') invalidateSessionRequests(); else { app?.unmount(); app=undefined; }
    resolve({siteName:'first.test',overallSuccess:true,steps:[]}); await pending;
    expect(api.migrateSite).toHaveBeenCalledTimes(1); expect(state.bulkLog).toEqual([]); expect(state.notice).toBe('');
  });
  it(`ignores stream events from a previous ${invalidate}`,async()=>{
    const state=await mount(); state.form.siteName='first.test';
    let deliver!:(event:api.MigrationProgress)=>void, resolve!:()=>void;
    vi.mocked(api.migrateSiteStream).mockImplementationOnce(async(_request,onEvent)=>{deliver=onEvent;await new Promise<void>(r=>{resolve=r;});});
    const pending=state.run();
    if(invalidate==='session') invalidateSessionRequests(); else { app?.unmount(); app=undefined; }
    deliver({phase:'plan',steps:['database']}); deliver({phase:'done',overallSuccess:true}); resolve(); await pending;
    expect(state.liveSteps).toEqual([]); expect(state.notice).toBe('');
  });
}
