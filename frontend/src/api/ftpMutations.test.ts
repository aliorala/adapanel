import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { apiClient, invalidateSessionRequests } from './client';
import { createFtpUser, setFtpPassword, deleteFtpUser, setFtpTls } from './ftp';
import { UserFacingError } from '../utils/apiError';

const original = apiClient.defaults.adapter;
let body: unknown;
const payload = {userName:'fixture_ftp',siteName:'fixture.test',homePath:'',password:'FixturePassword123!'};
const user = {userName:'fixture_ftp',siteName:'fixture.test',homePath:'C:/sites/fixture.test',isEnabled:true,existsOnSystem:true,createdAtUtc:'2026-10-04T00:00:00Z',passwordNeverExpires:true};
beforeEach(()=>{
  invalidateSessionRequests(); vi.stubGlobal('window',{location:{pathname:'/panel'}});
  vi.stubGlobal('fetch',vi.fn(async()=>new Response(JSON.stringify({success:true,data:{requestToken:'fixture'}}),{status:200})));
  apiClient.defaults.adapter=async config=>({config,status:200,statusText:'OK',headers:{},data:body});
});
afterEach(()=>{apiClient.defaults.adapter=original;invalidateSessionRequests();vi.unstubAllGlobals();});
const routes=[
  {name:'create',run:()=>createFtpUser({...payload}),valid:user},
  {name:'password',run:()=>setFtpPassword(payload.userName,payload.password),valid:{userName:payload.userName}},
  {name:'delete',run:()=>deleteFtpUser(payload.userName),valid:{userName:payload.userName}},
  {name:'tls',run:()=>setFtpTls(true),valid:{requireSsl:true,verified:true}}
];
for(const route of routes){
  it.each(['failed-envelope','missing-success','missing-data','wrong-identity'])(`${route.name} rejects %s instead of claiming success`,async kind=>{
    const wrong=route.name==='tls'?{requireSsl:false,verified:true}:{...route.valid,userName:'other_ftp'};
    body={success:kind==='missing-success'?undefined:kind!=='failed-envelope',data:kind==='missing-data'?undefined:kind==='wrong-identity'?wrong:route.valid};
    await expect(route.run()).rejects.toEqual(new UserFacingError('panel.ftp.resultUnconfirmed'));
  });
  it(`${route.name} accepts its confirmed result`,async()=>{body={success:true,data:route.valid};await expect(route.run()).resolves.not.toThrow();});
}
it('rejects a created account assigned to a different site',async()=>{body={success:true,data:{...user,siteName:'other.test'}};await expect(createFtpUser({...payload})).rejects.toEqual(new UserFacingError('panel.ftp.resultUnconfirmed'));});
it('accepts canonical Windows account and site casing',async()=>{body={success:true,data:{...user,userName:'FIXTURE_FTP',siteName:'FIXTURE.TEST'}};await expect(createFtpUser({...payload})).resolves.toMatchObject({userName:'FIXTURE_FTP'});});
it('preserves the created account but marks omitted expiry measurement unknown',async()=>{const data:{passwordNeverExpires?:boolean}&Record<string,unknown>={...user};delete data.passwordNeverExpires;body={success:true,data};await expect(createFtpUser({...payload})).resolves.toMatchObject({passwordNeverExpires:null});});
it.each([false,null])('keeps the saved policy when the requested TLS change is not verified (%s)',async verified=>{body={success:true,data:{requireSsl:false,verified}};await expect(setFtpTls(true)).resolves.toEqual({requireSsl:false,verified});});
it.each([{requireSsl:'true',verified:true},{requireSsl:true,verified:'true'}])('rejects malformed TLS measurements',async data=>{body={success:true,data};await expect(setFtpTls(true)).rejects.toEqual(new UserFacingError('panel.ftp.resultUnconfirmed'));});
