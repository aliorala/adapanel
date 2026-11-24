'use strict';
const $ = id => document.getElementById(id);
let csrf = '', busy = false, records = [], measured = 0;
const errors = {invalid_credentials:'Kullanıcı adı veya şifre yanlış.',wrong_current_password:'Mevcut şifre yanlış.',password_policy:'Şifreler aynı olmalı; en az 6 karakter kullanın. Çok uzun şifreleri kısaltın.',same_password:'Yeni şifre mevcut şifreden farklı olmalı.',too_many_attempts:'Çok fazla deneme yapıldı. 10 dakika sonra tekrar deneyin.',unauthorized:'Oturumunuz sona erdi. Lütfen yeniden giriş yapın.',csrf_rejected:'Oturum bilgisi değişti. Sayfayı yenileyip tekrar deneyin.'};
function message(text, error=false){$('status').textContent=text;$('status').classList.toggle('error',error);}
function showSession(authenticated){$('login').hidden=authenticated;$('dashboard').hidden=!authenticated;$('logout').hidden=!authenticated;if(!authenticated){records=[];$('rows').replaceChildren();$('active').textContent='—';$('registered').textContent='—';$('password-form').reset();}}
async function api(action,body){
  const controller=new AbortController(), timeout=setTimeout(()=>controller.abort(),15000);
  try{
    const response=await fetch('api.php?action='+action,{method:body?'POST':'GET',credentials:'same-origin',cache:'no-store',signal:controller.signal,headers:body?{'Content-Type':'application/json','X-CSRF-Token':csrf}:{},body:body?JSON.stringify(body):undefined});
    const data=await response.json();
    if(!response.ok){if(data.error==='unauthorized')showSession(false);throw new Error(errors[data.error]||'İşlem tamamlanamadı. Lütfen tekrar deneyin.');}
    if(data.csrf)csrf=data.csrf;
    return data;
  }finally{clearTimeout(timeout);}
}
async function run(work){if(busy)return;busy=true;document.querySelectorAll('button').forEach(b=>b.disabled=true);try{await work();}catch(error){message(error.name==='AbortError'?'Bağlantı zaman aşımına uğradı. Lütfen tekrar deneyin.':error.message,true);}finally{busy=false;document.querySelectorAll('button').forEach(b=>b.disabled=false);}}
function renderRows(){
  const query=$('search').value.trim().toLocaleLowerCase('tr');
  const shown=records.filter(item=>[item.id,item.version,item.ip,item.country,item.city,item.location,item.datacenter].some(value=>String(value).toLocaleLowerCase('tr').includes(query)));
  $('rows').replaceChildren();$('table').hidden=shown.length===0;$('empty').hidden=shown.length>0;
  $('empty').textContent=records.length?'Aramanızla eşleşen kurulum bulunamadı.':'Henüz bildirim alınmadı. Günlük sürüm bildirimi gönderen paneller burada görünür.';
  for(const item of shown){
    const tr=document.createElement('tr');
    const stamp=value=>Number.isSafeInteger(value)?new Date(value*1000).toLocaleString('tr-TR'):'Tarih bilinmiyor';
    const historic=item.shareDetails!==true;
    const detail=value=>value?String(value)+(historic?' · Geçmiş kayıt':''):(historic?'Paylaşılmadı':'Belirlenemedi');
    const geo=value=>value?String(value)+' · '+stamp(item.geoUpdatedAt)+(item.geoIp?' · IP: '+item.geoIp:'')+(historic?' · Geçmiş kayıt':''):(historic?'Paylaşılmadı':'Belirlenemedi');
    const source=item.ipSource==='reported_egress'?' · Otomatik çıkış IP’si (istemci bildirimi; güvenlik doğrulaması değildir)':item.ipSource==='connection'?' · Bağlantı IP’si':' · Önceki kayıt';
    for(const value of [item.id.slice(0,12),item.version,detail(item.ip)+(item.ip?source:'')+(item.connectionIp?' · Son bağlantı: '+item.connectionIp+' · '+stamp(item.connectionIpAt):''),geo(item.country),geo(item.city),detail(item.location),detail(item.datacenter),item.lastDetailsAt?stamp(item.lastDetailsAt)+(historic?' · Ayrıntılar kapalı, geçmiş kayıt':''):'Paylaşılmadı',stamp(item.lastSeen)]){const td=document.createElement('td');td.textContent=value;tr.append(td);}
    const active=item.lastSeen*1000>measured-48*3600000&&item.lastSeen*1000<=measured;
    const td=document.createElement('td'),badge=document.createElement('span');badge.textContent=active?'Aktif':'Aktif değil';if(!active)badge.className='inactive';td.append(badge);tr.append(td);$('rows').append(tr);
  }
}
async function refresh(){
  $('active').textContent='—';$('registered').textContent='—';$('time').textContent='';records=[];$('rows').replaceChildren();$('table').hidden=true;$('empty').hidden=true;
  const data=await api('stats');
  if(data.schemaVersion!==1||data.windowHours!==48||!Number.isSafeInteger(data.activeInstallations)||data.activeInstallations<0||!Array.isArray(data.installations)||!Number.isFinite(Date.parse(data.measuredAtUtc)))throw new Error('İstatistik cevabı doğrulanamadı.');
  records=data.installations;measured=Date.parse(data.measuredAtUtc);
  $('active').textContent=data.activeInstallations.toLocaleString('tr');$('registered').textContent=records.length.toLocaleString('tr');$('time').textContent='Son ölçüm: '+new Date(measured).toLocaleString('tr-TR');renderRows();
}
$('login-form').addEventListener('submit',event=>{event.preventDefault();run(async()=>{message('Giriş yapılıyor…');const session=await api('session');csrf=session.csrf;const result=await api('login',{username:$('username').value.trim(),password:$('password').value});$('password').value='';showSession(result.authenticated);await refresh();message('');});});
$('refresh').addEventListener('click',()=>run(async()=>{await refresh();message('Veriler güncellendi.');}));
$('logout').addEventListener('click',()=>run(async()=>{await api('logout',{});showSession(false);csrf='';message('Çıkış yapıldı.');}));
$('search').addEventListener('input',renderRows);
$('password-form').addEventListener('submit',event=>{event.preventDefault();run(async()=>{
  if($('new-password').value!==$('confirm-password').value)throw new Error('Yeni şifreler aynı değil.');
  await api('password',{currentPassword:$('current-password').value,newPassword:$('new-password').value,confirmation:$('confirm-password').value});
  $('password-form').reset();message('Şifreniz güncellendi. Diğer açık oturumlar sonlandırıldı.');
});});
run(async()=>{const session=await api('session');showSession(session.authenticated);if(session.authenticated)await refresh();});
