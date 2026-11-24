import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { effectScope } from 'vue';
import { useBrandingWorkspace } from './useBrandingWorkspace';
import * as api from '../api/panelSettings';
vi.mock('../api/panelSettings',async original=>({...await original<object>(),getBranding:vi.fn(),getBrandingSettings:vi.fn(),updateBrandingSettings:vi.fn(),uploadBrandingImage:vi.fn(),removeBrandingImage:vi.fn()}));
const brand=(name:string):api.Branding=>({...api.emptyBranding(),panelName:name,accentColor:'#123456',faviconDataUrl:`icon-${name}`,logoDataUrl:`logo-${name}`});
function deferred<T>(){let resolve!:(value:T)=>void;const promise=new Promise<T>(r=>{resolve=r;});return{resolve,promise};}
const scopes:ReturnType<typeof effectScope>[]=[];
function fixture(){
 let actor='first',active=true;
 const apply=vi.fn(),notify=vi.fn();const scope=effectScope();scopes.push(scope);
 const workspace=scope.run(()=>useBrandingWorkspace({active:()=>active,actor:()=>actor,allowed:()=>true,apply,notify,error:()=> 'read-failed',saved:()=> 'saved',refreshFailed:()=> 'saved-refresh-failed'}))!;
 return{workspace,apply,notify,invalidate:(kind:string)=>{if(kind==='actor')actor='second';else if(kind==='unmount')active=false;else{workspace.invalidate();workspace.form.value=api.emptyBranding();}}};
}
beforeEach(()=>{vi.mocked(api.getBranding).mockResolvedValue(brand('saved'));vi.mocked(api.getBrandingSettings).mockResolvedValue(brand('settings'));vi.mocked(api.updateBrandingSettings).mockResolvedValue(brand('saved'));vi.mocked(api.uploadBrandingImage).mockResolvedValue(brand('asset'));vi.mocked(api.removeBrandingImage).mockResolvedValue(brand('asset'));});
afterEach(()=>{scopes.splice(0).forEach(scope=>scope.stop());vi.resetAllMocks();});
it('does not let an old effective GET replace a completed save or its DOM application',async()=>{
 const f=fixture(),old=deferred<api.Branding>();vi.mocked(api.getBranding).mockReturnValueOnce(old.promise);
 const pending=f.workspace.loadEffective();f.workspace.form.value=brand('draft');await f.workspace.save();old.resolve(brand('old'));await pending;
 expect(f.apply).toHaveBeenCalledTimes(1);expect(f.apply).toHaveBeenLastCalledWith(brand('saved'));expect(f.workspace.form.value.panelName).toBe('saved');
});
it('does not let an old settings GET replace a completed save',async()=>{
 const f=fixture(),old=deferred<api.Branding>();vi.mocked(api.getBrandingSettings).mockReturnValueOnce(old.promise);
 const pending=f.workspace.loadSettings();f.workspace.form.value=brand('draft');await f.workspace.save();old.resolve(brand('old'));await pending;expect(f.workspace.form.value.panelName).toBe('saved');
});
it('preserves edits made while own settings are being read',async()=>{
 const f=fixture(),old=deferred<api.Branding>();vi.mocked(api.getBrandingSettings).mockReturnValueOnce(old.promise);
 const pending=f.workspace.loadSettings();f.workspace.form.value.panelName='new draft';old.resolve(brand('old'));await pending;expect(f.workspace.form.value.panelName).toBe('new draft');
});
it('saves a snapshot without replacing a newer text draft',async()=>{
 const f=fixture(),reply=deferred<api.Branding>();f.workspace.form.value=brand('submitted');vi.mocked(api.updateBrandingSettings).mockReturnValueOnce(reply.promise);
 const pending=f.workspace.save();f.workspace.form.value.panelName='new draft';reply.resolve(brand('saved'));await pending;
 expect(api.updateBrandingSettings).toHaveBeenCalledWith({panelName:'submitted',accentColor:'#123456',logoLinkUrl:''});expect(f.workspace.form.value.panelName).toBe('new draft');
});
for(const operation of ['upload','remove'] as const)it(`${operation} changes only the asset and keeps unsaved text`,async()=>{
 const f=fixture();f.workspace.form.value=brand('unsaved text');
 if(operation==='upload')await f.workspace.upload('logo',{target:{files:[{}],value:'fixture'}} as unknown as Event);else await f.workspace.remove('logo');
 expect(f.workspace.form.value.panelName).toBe('unsaved text');expect(f.workspace.form.value.faviconDataUrl).toBe('icon-unsaved text');expect(f.workspace.form.value.logoDataUrl).toBe('logo-asset');
});
for(const operation of ['save','upload','remove'] as const)for(const invalidation of ['actor','unmount','reset'] as const)it(`ignores ${operation} completion after ${invalidation}`,async()=>{
 const f=fixture(),reply=deferred<api.Branding>();f.workspace.form.value=brand('draft');
 if(operation==='save')vi.mocked(api.updateBrandingSettings).mockReturnValueOnce(reply.promise);else if(operation==='upload')vi.mocked(api.uploadBrandingImage).mockReturnValueOnce(reply.promise);else vi.mocked(api.removeBrandingImage).mockReturnValueOnce(reply.promise);
 const pending=operation==='save'?f.workspace.save():operation==='remove'?f.workspace.remove('logo'):f.workspace.upload('logo',{target:{files:[{}],value:'fixture'}} as unknown as Event);
 f.invalidate(invalidation);const before={...f.workspace.form.value};reply.resolve(brand('stale'));await pending;
 expect(f.workspace.form.value).toEqual(before);expect(f.apply).not.toHaveBeenCalled();expect(f.notify).not.toHaveBeenCalled();expect(api.getBranding).not.toHaveBeenCalled();
});
it('reports a saved-but-refresh-failed result as a warning instead of green success',async()=>{
 const f=fixture();vi.mocked(api.getBranding).mockRejectedValueOnce(new Error('refresh failed'));await f.workspace.save();expect(f.notify).toHaveBeenLastCalledWith('saved-refresh-failed',true);
});
it('serializes writes and rejects a second in-flight mutation',async()=>{
 const f=fixture(),reply=deferred<api.Branding>();vi.mocked(api.updateBrandingSettings).mockReturnValueOnce(reply.promise);const pending=f.workspace.save();await f.workspace.remove('logo');expect(api.removeBrandingImage).not.toHaveBeenCalled();reply.resolve(brand('saved'));await pending;expect(f.workspace.busy.value).toBe(false);
});
it('does not apply an old effective brand after reset',async()=>{
 const f=fixture(),reply=deferred<api.Branding>();vi.mocked(api.getBranding).mockReturnValueOnce(reply.promise);const pending=f.workspace.loadEffective();f.invalidate('reset');reply.resolve(brand('old'));await pending;expect(f.apply).not.toHaveBeenCalled();
});
it('does not clear a newer file selection when an obsolete upload finishes',async()=>{
 const f=fixture(),reply=deferred<api.Branding>();const first={},second={};const input={files:[first],value:'first'};
 vi.mocked(api.uploadBrandingImage).mockReturnValueOnce(reply.promise);const pending=f.workspace.upload('logo',{target:input} as unknown as Event);
 f.invalidate('reset');input.files=[second];input.value='second';reply.resolve(brand('old'));await pending;expect(input.value).toBe('second');
});
