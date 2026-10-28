import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { apiClient, invalidateSessionRequests } from './client';
import { getFtpStatus, getFtpTls, getFtpUsers } from './ftp';
import { UserFacingError } from '../utils/apiError';

const original = apiClient.defaults.adapter;
let body: unknown;
beforeEach(() => {
  invalidateSessionRequests(); vi.stubGlobal('window', {location:{pathname:'/panel'}});
  apiClient.defaults.adapter = async config => ({config,status:200,statusText:'OK',headers:{},data:body});
});
afterEach(() => { apiClient.defaults.adapter = original; invalidateSessionRequests(); vi.unstubAllGlobals(); });
const user = {userName:'fixture',homePath:'C:/sites/a.test',siteName:'a.test',isEnabled:true,existsOnSystem:true,createdAtUtc:'legacy-date'};
const status = {serverAddress:'fixture.test',controlPort:21,passivePortStart:50000,passivePortEnd:50100,isConfigured:true,statusMessage:'ftp.server_ready',serviceState:'Running',siteState:'Started',isSiteRunning:true,hasControlPortConflict:false,controlPortOwner:''};
const routes = [
  {name:'users',run:getFtpUsers,valid:[user],bad:{users:[user]}},
  {name:'status',run:getFtpStatus,valid:status,bad:{...status,controlPort:'21'}},
  {name:'tls',run:getFtpTls,valid:{requireSsl:true},bad:{requireSsl:'false'}}
];
for(const route of routes) {
  it.each(['failed-envelope','missing-success','missing-data','malformed-data'])(`${route.name} rejects %s without presenting it as measured data`,async kind=>{
    body={success:kind==='missing-success'?undefined:kind!=='failed-envelope',data:kind==='missing-data'?undefined:kind==='malformed-data'?route.bad:route.valid};
    await expect(route.run()).rejects.toEqual(new UserFacingError('panel.ftp.resultUnconfirmed'));
  });
  it(`${route.name} accepts actual data`,async()=>{body={success:true,data:route.valid};await expect(route.run()).resolves.toEqual(route.valid);});
}
it('rejects incomplete account rows rather than rendering missing identity as an account',async()=>{
  body={success:true,data:[{...user,userName:''}]};await expect(getFtpUsers()).rejects.toEqual(new UserFacingError('panel.ftp.resultUnconfirmed'));
});
it('preserves measured unknown server status without converting it to a failure',async()=>{
  body={success:true,data:{...status,isConfigured:false,statusMessage:'ftp.server_status_unknown'}};
  await expect(getFtpStatus()).resolves.toMatchObject({isConfigured:false,statusMessage:'ftp.server_status_unknown'});
});
it('preserves an empty measured user list',async()=>{body={success:true,data:[]};await expect(getFtpUsers()).resolves.toEqual([]);});

it.each([[null,null],[undefined,undefined],[52000,null],[undefined,52500]])('keeps an incomplete passive range unknown (%s, %s)', async(start,end)=>{
  body={success:true,data:{...status,passivePortStart:start,passivePortEnd:end}};
  await expect(getFtpStatus()).resolves.toMatchObject({passivePortStart:null,passivePortEnd:null});
});
it.each([[52500,52000],[0,51000],[50000,65536],['52000',52500]])('rejects invalid passive ranges (%s, %s)',async(start,end)=>{
  body={success:true,data:{...status,passivePortStart:start,passivePortEnd:end}};
  await expect(getFtpStatus()).rejects.toEqual(new UserFacingError('panel.ftp.resultUnconfirmed'));
});
