import { afterEach, expect, it, vi } from 'vitest';
import { createRenderer, h, nextTick, ssrContextKey, type App, type Component } from 'vue';
import MigrationView from './MigrationView.vue';
import * as api from '../api/migration';
import { invalidateSessionRequests } from '../api/client';

vi.mock('vue-i18n', () => ({ useI18n: () => ({ t: (key: string) => key }) }));
vi.mock('../api/migration', async original => ({ ...await original<object>(),
  discoverPlesk: vi.fn(), discoverCpanel: vi.fn(), validateMigration: vi.fn(),
  getMigrationRunningJobs: vi.fn().mockResolvedValue({ running: [], interrupted: [] }) }));
type State = { source: string; plesk: {host:string;apiKey:string}; form: {siteName:string;fileHost:string};
  discoveredDomains: api.PleskDomain[]; selectedDomains:string[]; discovering:boolean; discoverRan:boolean;
  discoverAcceptFingerprint:string; checks:api.MigrationCheck[]; validating:boolean; notice:string;
  discover():Promise<void>; validate():Promise<void>; pickDomain(name:string):void };
let app:App|undefined;
async function mount() {
  const renderer=createRenderer<object,object>({createElement:()=>({}),createText:()=>({}),createComment:()=>({}),insert(){},remove(){},setText(){},setElementText(){},patchProp(){},parentNode:()=>null,nextSibling:()=>null});
  app=renderer.createApp({...MigrationView,render:()=>h('section')} as Component); app.provide(ssrContextKey,{});
  const state=(app.mount({}).$ as unknown as {setupState:State}).setupState;
  state.plesk.host='first.example'; state.plesk.apiKey='fixture'; state.form.siteName='first.test';
  for(let i=0;i<8;i++) await nextTick(); return state;
}
function deferred<T>() { let resolve!:(value:T)=>void,reject!:(error:unknown)=>void;
  const promise=new Promise<T>((yes,no)=>{resolve=yes;reject=no;}); return {promise,resolve,reject}; }
const domains={domains:[{name:'first.test'}]};
const validation={allPassed:true,checks:[{name:'site',ok:true,message:'ok'}]};
afterEach(()=>{app?.unmount();app=undefined;vi.clearAllMocks();});

it.each(['host','source','session','unmount'])('ignores discovery arriving after %s changes',async kind=>{
  const state=await mount(), pending=deferred<api.PleskDiscoverResult>();
  vi.mocked(api.discoverPlesk).mockReturnValueOnce(pending.promise); const work=state.discover();
  if(kind==='host') state.plesk.host='second.example';
  else if(kind==='source') state.source='cpanel';
  else if(kind==='session') invalidateSessionRequests();
  else {app!.unmount();app=undefined;}
  pending.resolve(domains); await work;
  expect(state.discoveredDomains).toEqual([]); expect(state.discoverRan).toBe(false);
});
it('clears old domains, selection and trust when source credentials change',async()=>{
  const state=await mount();vi.mocked(api.discoverPlesk).mockResolvedValueOnce(domains);await state.discover();
  state.selectedDomains=['first.test'];state.discoverAcceptFingerprint='old-fingerprint';state.plesk.apiKey='new-credential';
  await nextTick();expect(state.discoveredDomains).toEqual([]);expect(state.selectedDomains).toEqual([]);
  expect(state.discoverAcceptFingerprint).toBe('');state.pickDomain('first.test');expect(state.form.fileHost).toBe('');
});
it('old discovery failure does not end a newer request or publish an error',async()=>{
  const state=await mount(),old=deferred<api.PleskDiscoverResult>(),fresh=deferred<api.PleskDiscoverResult>();
  vi.mocked(api.discoverPlesk).mockReturnValueOnce(old.promise).mockReturnValueOnce(fresh.promise);
  const first=state.discover();state.plesk.host='second.example';await nextTick();const second=state.discover();
  old.reject(new Error('old failure'));await first;expect(state.discovering).toBe(true);expect(state.notice).toBe('');
  fresh.resolve({domains:[{name:'second.test'}]});await second;expect(state.discoveredDomains[0].name).toBe('second.test');
});
it.each(['draft','session','unmount'])('ignores validation arriving after %s changes',async kind=>{
  const state=await mount(),pending=deferred<api.MigrationValidationResult>();
  vi.mocked(api.validateMigration).mockReturnValueOnce(pending.promise);const work=state.validate();
  if(kind==='draft') state.form.siteName='second.test';else if(kind==='session') invalidateSessionRequests();
  else {app!.unmount();app=undefined;}
  pending.resolve(validation);await work;expect(state.checks).toEqual([]);expect(state.notice).toBe('');
});
it('invalidates completed validation when the form changes',async()=>{
  const state=await mount();vi.mocked(api.validateMigration).mockResolvedValueOnce(validation);await state.validate();
  expect(state.checks).toHaveLength(1);state.form.fileHost='changed.example';await nextTick();
  expect(state.checks).toEqual([]);expect(state.notice).toBe('');
});
it('old validation failure cannot replace a newer validation',async()=>{
  const state=await mount(),old=deferred<api.MigrationValidationResult>(),fresh=deferred<api.MigrationValidationResult>();
  vi.mocked(api.validateMigration).mockReturnValueOnce(old.promise).mockReturnValueOnce(fresh.promise);
  const first=state.validate();state.form.fileHost='second.example';const second=state.validate();
  old.reject(new Error('obsolete'));await first;expect(state.validating).toBe(true);expect(state.notice).toBe('');
  fresh.resolve(validation);await second;expect(state.checks).toEqual(validation.checks);expect(state.validating).toBe(false);
});
it('does not revive a result after changing source away and back',async()=>{
  const state=await mount(),pending=deferred<api.PleskDiscoverResult>();
  vi.mocked(api.discoverPlesk).mockReturnValueOnce(pending.promise);const work=state.discover();
  state.plesk.host='second.example';state.plesk.host='first.example';pending.resolve(domains);await work;
  expect(state.discoveredDomains).toEqual([]);
});
it('does not dispatch duplicate reads while the same input is pending',async()=>{
  const state=await mount(),discovery=deferred<api.PleskDiscoverResult>(),check=deferred<api.MigrationValidationResult>();
  vi.mocked(api.discoverPlesk).mockReturnValueOnce(discovery.promise);vi.mocked(api.validateMigration).mockReturnValueOnce(check.promise);
  const first=state.discover(),second=state.validate();await state.discover();await state.validate();
  expect(api.discoverPlesk).toHaveBeenCalledTimes(1);expect(api.validateMigration).toHaveBeenCalledTimes(1);
  discovery.resolve(domains);check.resolve(validation);await Promise.all([first,second]);
});
it('keeps successful current discovery and validation usable',async()=>{
  const state=await mount();vi.mocked(api.discoverPlesk).mockResolvedValueOnce(domains);await state.discover();
  state.pickDomain('first.test');await nextTick();expect(state.form.fileHost).toBe('first.example');
  vi.mocked(api.validateMigration).mockResolvedValueOnce(validation);await state.validate();
  expect(state.checks).toEqual(validation.checks);expect(state.notice).toBe('panel.migration.allChecksOk');
});
