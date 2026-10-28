'use strict';
const key = document.getElementById('key'), status = document.getElementById('status');
const results = document.getElementById('results'), rows = document.getElementById('rows'), load = document.getElementById('load');
let request = null, epoch = 0;
document.getElementById('logout').addEventListener('click', () => {
  epoch++; request?.abort(); key.value = ''; rows.replaceChildren(); results.hidden = true; status.textContent = 'Çıkış yapıldı.'; load.disabled = false;
});
load.addEventListener('click', async () => {
  const turn = ++epoch;
  request?.abort(); request = new AbortController(); const controller = request;
  const timeout = setTimeout(() => controller.abort(), 12000);
  results.hidden = true; rows.replaceChildren(); load.disabled = true; status.textContent = 'Yükleniyor…';
  try {
    if (location.protocol !== 'https:' || !/^[a-f0-9]{64}$/.test(key.value.trim())) throw new Error();
    const response = await fetch('admin-api.php', {headers: {Authorization: 'Bearer ' + key.value.trim()}, credentials:'omit',cache:'no-store',signal:controller.signal});
    if (!response.ok) throw new Error();
    const data = await response.json();
    if (turn !== epoch) return;
    if (data.schemaVersion !== 1 || !Array.isArray(data.installations) || !Number.isSafeInteger(data.activeInstallations)) throw new Error();
    document.getElementById('total').textContent = data.activeInstallations + ' aktif AdaPanel kurulumu';
    document.getElementById('time').textContent = 'Ölçüm: ' + new Date(data.measuredAtUtc).toLocaleString('tr-TR');
    for (const item of data.installations) {
      const tr = document.createElement('tr');
      const active = item.lastSeen * 1000 > Date.parse(data.measuredAtUtc) - 48 * 3600000;
      for (const value of [item.id.slice(0,12),item.version,new Date(item.firstSeen*1000).toLocaleString('tr-TR'),new Date(item.lastSeen*1000).toLocaleString('tr-TR'),active?'Aktif':'Aktif değil']) {
        const td = document.createElement('td');td.textContent=String(value);tr.append(td);
      }
      rows.append(tr);
    }
    results.hidden = false; status.textContent = data.installations.length ? 'Kayıtlar güncellendi.' : 'Henüz bildirim alınmadı. Eski sürümler otomatik olarak sayılamaz.';
  } catch { if (turn === epoch) status.textContent = 'Veri alınamadı. HTTPS bağlantısını ve erişim anahtarını kontrol edin. Bu durum sıfır kurulum anlamına gelmez.'; }
  finally { clearTimeout(timeout); if (turn === epoch) load.disabled = false; }
});
