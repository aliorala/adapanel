import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { createRenderer, h, nextTick, reactive, ssrContextKey, type App, type Component } from 'vue';
import SiteBuilderView from './SiteBuilderView.vue';
import * as api from '../api/siteBuilder';
import { allowNavigation } from '../composables/navigationGuard';

const confirm = vi.hoisted(() => vi.fn());
const session = vi.hoisted(() => ({ epoch: 0 }));
vi.mock('../api/client', () => ({ getSessionEpoch: () => session.epoch }));
let sites: ReturnType<typeof reactive<{ selectedSiteName: string; sites: {name: string}[]; fetchSites: () => Promise<void>; selectSite: (name: string) => void }>>;
vi.mock('../stores/sites', () => ({ useSitesStore: () => sites }));
vi.mock('../components/ConfirmDialog.vue', () => ({ useConfirm: () => ({ confirm }) }));
vi.mock('vue-i18n', () => ({ useI18n: () => ({ t: (key: string) => key }) }));
vi.mock('../api/siteBuilder', async importOriginal => {
  const original = await importOriginal<typeof api>();
  return { ...original, getSiteBuilderCatalog: vi.fn(), getSiteBuilderImages: vi.fn(), getSiteBuilderProject: vi.fn(), getSiteBuilderPreview: vi.fn(), getSiteBuilderPublishPlan: vi.fn(), saveSiteBuilderProject: vi.fn(), publishSiteBuilder: vi.fn(), deleteSiteBuilderImage: vi.fn() };
});
vi.mock('../stores/auth', () => ({ extractApiMessage: () => 'failed', extractApiDetails: () => '' }));
let app: App | undefined;
type State = { plan: api.SiteBuilderPublishPlan | null; tab: string; project: api.SiteBuilderProject; previewHtml: string; loading: boolean; dirty: boolean; previewLoad: {message: {value: string}}; removeImage: (name: string) => Promise<void>; selectSiteFromEvent: (event: Event) => Promise<void>; changeSite: (name: string) => Promise<void>; openPublishTab: () => Promise<void>; save: () => Promise<void>; publish: () => Promise<void>; loadPreview: () => Promise<void> };
const project = (siteTitle: string) => ({ siteTitle, pages: [], sections: [] } as unknown as api.SiteBuilderProject);
const flush = async () => { for (let i=0;i<12;i++) await nextTick(); };
function mount(): State {
  const renderer = createRenderer<object, object>({ createElement: () => ({}), createText: () => ({}), createComment: () => ({}), insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {}, parentNode: () => null, nextSibling: () => null });
  app = renderer.createApp({ ...SiteBuilderView, render: () => h('section') } as Component);
  app.provide(ssrContextKey, {});
  return (app.mount({}).$ as unknown as {setupState: State}).setupState;
}
beforeEach(() => {
  session.epoch = 0;
  vi.stubGlobal('window', { addEventListener: vi.fn(), removeEventListener: vi.fn() });
  sites=reactive({ selectedSiteName:'first.example', sites:[{name:'first.example'},{name:'second.example'}], fetchSites:async()=>{}, selectSite(name:string){this.selectedSiteName=name;} });
  vi.mocked(api.getSiteBuilderCatalog).mockResolvedValue({} as api.SiteBuilderCatalog);
  vi.mocked(api.getSiteBuilderImages).mockResolvedValue([]);
  vi.mocked(api.getSiteBuilderProject).mockImplementation(async name => project(name));
  vi.mocked(api.getSiteBuilderPreview).mockImplementation(async name => name);
});
afterEach(() => { app?.unmount(); app=undefined; vi.resetAllMocks(); vi.unstubAllGlobals(); });
it('discards a late project response from a previously selected site', async () => {
  let resolve!: (value: api.SiteBuilderProject)=>void;
  vi.mocked(api.getSiteBuilderProject).mockImplementationOnce(()=>new Promise(r=>{resolve=r;}));
  const state=mount(); await flush();
  sites.selectSite('second.example'); await flush();
  resolve(project('stale')); await flush();
  expect(state.project.siteTitle).toBe('second.example');
  expect(state.previewHtml).toBe('second.example');
  expect(state.loading).toBe(false);
});
it('keeps unsaved edits when navigation or site selection is cancelled', async () => {
  const state=mount(); await flush(); state.project.siteTitle='Edited';
  confirm.mockResolvedValue(false);
  expect(await allowNavigation()).toBe(false);
  await state.changeSite('second.example');
  expect(sites.selectedSiteName).toBe('first.example');
  expect(state.project.siteTitle).toBe('Edited');
});
it('clears the dirty state only after a successful save', async () => {
  const state=mount(); await flush(); state.project.siteTitle='Edited';
  vi.mocked(api.saveSiteBuilderProject).mockResolvedValue(project('Edited'));
  await state.save();
  expect(state.dirty).toBe(false);
  expect(await allowNavigation()).toBe(true);
  expect(confirm).not.toHaveBeenCalled();
});
it('requires saving before generating a publish plan for edited content', async () => {
  const state=mount(); await flush(); state.project.siteTitle='Edited';
  await state.openPublishTab();
  expect(api.getSiteBuilderPublishPlan).not.toHaveBeenCalled();
});
it('shows a preview error instead of an endless loading state', async () => {
  const state=mount(); await flush();
  vi.mocked(api.getSiteBuilderPreview).mockRejectedValueOnce(new Error('offline'));
  await state.loadPreview();
  expect(state.previewHtml).toBe('');
  expect(state.previewLoad.message.value).toBe('failed');
});

it('retains a failed save as unsaved content', async () => {
  const state=mount(); await flush(); state.project.siteTitle='Edited';
  vi.mocked(api.saveSiteBuilderProject).mockRejectedValueOnce(new Error('offline'));
  await state.save();
  expect(state.project.siteTitle).toBe('Edited');
  expect(state.dirty).toBe(true);
});
it('keeps the native selector on the loaded site after cancelling a change', async () => {
  const state=mount(); await flush(); state.project.siteTitle='Edited';
  confirm.mockResolvedValue(false);
  const select={ value:'second.example' };
  await state.selectSiteFromEvent({target:select} as unknown as Event);
  expect(select.value).toBe('first.example');
  expect(sites.selectedSiteName).toBe('first.example');
});
it('deletes image references without replacing unsaved project content', async () => {
  const initial=project('Original');
  initial.pages=[{slug:'gallery',sections:[{imageName:'image.png',items:[{imageName:'image.png'}]}]}] as api.SiteBuilderPage[];
  vi.mocked(api.getSiteBuilderProject).mockResolvedValue(initial);
  const state=mount(); await flush(); state.project.siteTitle='Edited';
  confirm.mockResolvedValue(true);
  await state.removeImage('IMAGE.png');
  expect(api.deleteSiteBuilderImage).toHaveBeenCalledWith('first.example','IMAGE.png');
  expect(state.project.siteTitle).toBe('Edited');
  expect(state.project.pages[0].sections[0].imageName).toBe('');
  expect(state.project.pages[0].sections[0].items[0].imageName).toBe('');
  expect(state.dirty).toBe(true);
  expect(api.getSiteBuilderProject).toHaveBeenCalledTimes(1);
});

it('discards a publish plan returned after switching sites', async () => {
  const state=mount(); await flush();
  let resolve!: (value:api.SiteBuilderPublishPlan)=>void;
  vi.mocked(api.getSiteBuilderPublishPlan).mockImplementationOnce(()=>new Promise(r=>{resolve=r;}));
  const pending=state.openPublishTab(); await flush();
  await state.changeSite('second.example'); await flush();
  resolve({siteName:'first.example'} as api.SiteBuilderPublishPlan); await pending;
  expect(state.plan).toBe(null);
  expect(state.tab).toBe('design');
});
it('discards a late preview from the previous site', async () => {
  const state=mount(); await flush();
  let resolve!: (value:string)=>void;
  vi.mocked(api.getSiteBuilderPreview).mockImplementationOnce(()=>new Promise(r=>{resolve=r;}));
  const pending=state.loadPreview();
  await state.changeSite('second.example'); await flush();
  resolve('old preview'); await pending;
  expect(state.previewHtml).toBe('second.example');
});

for (const operation of ['save', 'publish'] as const) {
  for (const invalidate of ['site', 'session', 'unmount'] as const) {
    it(`discards late ${operation} after ${invalidate} changes without new followup requests`, async () => {
      const state=mount(); await flush();
      let resolve!: (value: never)=>void;
      if (operation === 'save') vi.mocked(api.saveSiteBuilderProject).mockImplementationOnce(()=>new Promise(r=>{resolve=r;}));
      else vi.mocked(api.publishSiteBuilder).mockImplementationOnce(()=>new Promise(r=>{resolve=r;}));
      const pending=state[operation]();
      if (invalidate === 'site') { sites.selectSite('second.example'); await flush(); }
      else if (invalidate === 'session') session.epoch++;
      else { app?.unmount(); app=undefined; }
      const title=state.project?.siteTitle;
      const projectCalls=vi.mocked(api.getSiteBuilderProject).mock.calls.length;
      const previewCalls=vi.mocked(api.getSiteBuilderPreview).mock.calls.length;
      const planCalls=vi.mocked(api.getSiteBuilderPublishPlan).mock.calls.length;
      resolve((operation === 'save' ? project('stale saved') : { writtenFiles: ['index.html'] }) as never);
      await pending;
      expect(state.project?.siteTitle).toBe(title);
      expect(vi.mocked(api.getSiteBuilderProject).mock.calls.length).toBe(projectCalls);
      expect(vi.mocked(api.getSiteBuilderPreview).mock.calls.length).toBe(previewCalls);
      expect(vi.mocked(api.getSiteBuilderPublishPlan).mock.calls.length).toBe(planCalls);
    });
  }
}
for (const stage of ['project', 'plan'] as const) {
  for (const invalidate of ['site', 'session', 'unmount'] as const) {
    it(`discards publish ${stage} followup after ${invalidate} changes`, async () => {
      const state=mount(); await flush();
      let resolve!: (value: never)=>void;
      vi.mocked(api.publishSiteBuilder).mockResolvedValue({ writtenFiles: ['index.html'] } as api.SiteBuilderPublishResult);
      if (stage === 'project') vi.mocked(api.getSiteBuilderProject).mockImplementationOnce(()=>new Promise(r=>{resolve=r;}));
      else vi.mocked(api.getSiteBuilderPublishPlan).mockImplementationOnce(()=>new Promise(r=>{resolve=r;}));
      const pending=state.publish(); await flush();
      if (invalidate === 'site') { sites.selectSite('second.example'); await flush(); }
      else if (invalidate === 'session') session.epoch++;
      else { app?.unmount(); app=undefined; }
      const title=state.project?.siteTitle, plan=state.plan;
      const calls=vi.mocked(api.getSiteBuilderPublishPlan).mock.calls.length;
      resolve((stage === 'project' ? project('stale published') : {siteName:'stale'}) as never);
      await pending;
      expect(state.project?.siteTitle).toBe(title);
      expect(state.plan).toBe(plan);
      expect(vi.mocked(api.getSiteBuilderPublishPlan).mock.calls.length).toBe(calls);
    });
  }
}