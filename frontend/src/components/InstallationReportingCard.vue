<template>
  <section class="form-surface">
    <div class="section-head"><h3>{{ text.title }}</h3><button type="button" class="secondary compact" :disabled="busy" @click="load">{{ text.refresh }}</button></div>
    <p class="meta">{{ text.description }}</p>
    <p class="notice">{{ text.disclosure }}</p>
    <p v-if="message" :class="['notice', { error: failed }]" :role="failed ? 'alert' : 'status'">{{ message }}</p>
    <template v-if="saved">
      <fieldset :disabled="busy" class="reporting-fields">
        <label class="check-field"><input v-model="enabled" type="checkbox" /><span>{{ text.consent }}</span></label>
        <p class="field-hint">{{ text.optional }}</p>
        <button type="button" class="compact" @click="save">{{ text.save }}</button>
      </fieldset>
      <p class="meta">{{ text.status }}: {{ saved.enabled ? text.on : text.off }}</p>
      <dl class="reporting-status">
        <div><dt>{{ text.attempt }}</dt><dd>{{ formatDate(saved.lastAttemptAtUtc) }}</dd></div>
        <div><dt>{{ text.last }}</dt><dd>{{ formatDate(saved.lastAcceptedAtUtc) }}</dd></div>
        <div><dt>{{ text.next }}</dt><dd>{{ saved.nextAttemptAtUtc ? formatDate(saved.nextAttemptAtUtc) : text.scheduled }}</dd></div>
        <div><dt>{{ text.lastError }}</dt><dd>{{ deliveryError }}</dd></div>
      </dl>
      <p v-if="saved.lastError" class="notice warn-banner">{{ text.deliveryFailure }}</p>
    </template>
  </section>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue';
import { useI18n } from 'vue-i18n';
import { getInstallationReporting, saveInstallationReporting, type InstallationReporting } from '../api/installationReporting';
const { locale } = useI18n();
const text = computed(() => locale.value.startsWith('tr') ? {
  title: 'Aktif AdaPanel kurulum sayımı', refresh: 'Yenile', save: 'Tercihi kaydet',
  description: 'AdaPanel sürümü ve rastgele kurulum kimliği günde bir gönderilir. Ayrıntılı kurulum bilgileri yeni kurulumlarda açıktır; önceden kapatılmış ayrıntı tercihi korunur. Bu tercih lisansı veya panelin çalışmasını etkilemez.',
  disclosure: 'adapanel.host adresine günlük sürüm bildirimi gönderilir. Ayrıntı paylaşımı açıksa bağlantının IP adresi, IP üzerinden bulunan ülke/şehir bilgisi de özel yönetimde tutulur. Ayrıntılar kapalıyken yeni IP veya konum bilgisi kaydedilmez; daha önce paylaşılan son gerçek bilgiler, son ayrıntı bildiriminin tarihine ait geçmiş bilgi olarak korunur ve güncellenmez. Rastgele kurulum kimliği değişmez. Günlük hız sınırı için kısa süreli anahtarlı IP özeti kullanılır. Toplam sayı ve ayrıntılar yalnız AdaPanel’in özel yönetim ekranında görülür; herkese açık yayımlanmaz. Aktiflik son 48 saatteki bildirimlere göre hesaplanır. 30 gün bildirim alınmayan kayıtlar saklama süresinden çıkarılır. Hosting erişim günlükleri ayrı tutulabilir.',
  consent: 'Ayrıntılı kurulum bilgilerini paylaş',
  optional: 'Bu ayar yalnız ek bilgileri kontrol eder; kapalıyken de sürüm ve rastgele kurulum kimliği günde bir gönderilir. Ayrıntılar açıkken panel ipwho.is hizmetinden kendi çıkış IP adresini otomatik öğrenir. Toplayıcı bağlantı IP’si yerelse bu bildirimi konum sorgusunda kullanabilir; bu bir güvenlik doğrulaması değildir. IP üzerinden ülke/şehir yaklaşık bulunur.',
  status: 'Ayrıntı paylaşımı', on: 'Açık', off: 'Kapalı', last: 'Son kabul edilen bildirim', never: 'Henüz yok',
  attempt: 'Son gönderim denemesi', next: 'Sonraki deneme', lastError: 'Son gönderim hatası', scheduled: 'İlk arka plan denemesi bekleniyor',
  ok: 'Tercih kaydedildi. Bildirimin teslim durumu aşağıda gösteriliyor.', error: 'İşlem tamamlanamadı. Yenile ile güncel durumu okuyup tekrar deneyin.',
  noError: 'Yok', pending: 'Deneme başlatıldı; kabul yanıtı henüz kaydedilmedi', network: 'Bağlantı kurulamadı', timeout: 'Bağlantı zaman aşımına uğradı', invalidResponse: 'Sunucu geçerli kabul yanıtı vermedi', invalidIdentity: 'Kayıtlı kurulum kimliği geçersiz', httpError: 'Sunucu bildirimi kabul etmedi', unknownError: 'Bildirim tamamlanamadı',
  deliveryFailure: 'Son deneme için kabul doğrulanamadı. Panel çalışmaya devam eder; yukarıdaki zamanda yeniden denenecek. Başarılı bildirimler günde bir gönderilir.'
} : {
  title: 'Active AdaPanel installation count', refresh: 'Refresh', save: 'Save preference',
  description: 'The AdaPanel version and a random installation identifier are sent daily. Detailed installation reporting is enabled for new installations; previously disabled detail preferences are preserved. This preference does not affect licensing or panel operation.',
  disclosure: 'Daily version reports are sent to adapanel.host. When detail sharing is enabled, the connection IP address and its approximate country/city are also stored privately. When details are disabled, no new IP or location is recorded. Previously shared real details are retained as historical information dated to the last detailed report and are not refreshed. The random installation identifier stays the same. A short-lived keyed IP hash is used for daily rate limiting. Only AdaPanel administrators can view the count and details in the private management area. No public count is published. Activity is based on reports within 48 hours. Records with no reports for 30 days expire. Hosting access logs may be retained separately.',
  consent: 'Share detailed installation information',
  optional: 'This setting only controls additional details; the version and random installation identifier continue to be sent daily when disabled. When details are enabled, the panel asks ipwho.is to detect its egress IP automatically. If the collector sees a local peer, this reported IP may be used for approximate country/city lookup; it is not security verification.',
  status: 'Detail sharing', on: 'Enabled', off: 'Disabled', last: 'Last accepted report', never: 'None yet',
  attempt: 'Last delivery attempt', next: 'Next attempt', lastError: 'Last delivery error', scheduled: 'Waiting for the first background attempt',
  ok: 'Preference saved. Report delivery status is shown below.', error: 'The operation could not complete. Refresh the current state and try again.',
  noError: 'None', pending: 'Attempt started; no acknowledgement has been recorded yet', network: 'Connection failed', timeout: 'Connection timed out', invalidResponse: 'The server did not return a valid acknowledgement', invalidIdentity: 'The stored installation identifier is invalid', httpError: 'The server did not accept the report', unknownError: 'The report could not complete',
  deliveryFailure: 'Acceptance of the last attempt could not be verified. The panel continues operating and will retry at the time shown above. Successful reports are sent once a day.'
});
const saved = ref<InstallationReporting | null>(null);
const enabled = ref(true);
const busy = ref(false), failed = ref(false), message = ref('');
let disposed = false;
onBeforeUnmount(() => { disposed = true; });
function formatDate(value: string | null) { return value ? new Date(value).toLocaleString(locale.value) : text.value.never; }
const deliveryError = computed(() => {
  const error = saved.value?.lastError;
  if (!error) return text.value.noError;
  if (error === 'pending') return text.value.pending;
  if (error === 'network') return text.value.network;
  if (error === 'timeout') return text.value.timeout;
  if (error === 'invalid_response') return text.value.invalidResponse;
  if (error === 'invalid_identity') return text.value.invalidIdentity;
  if (/^http_\d{3}$/.test(error)) return `${text.value.httpError} (${error.slice(5)})`;
  return text.value.unknownError;
});
function publish(value: InstallationReporting) {
  saved.value = value; enabled.value = value.enabled;
}
async function load() {
  if (busy.value) return;
  busy.value = true; message.value = ''; failed.value = false;
  try { const value = await getInstallationReporting(); if (!disposed) publish(value); }
  catch { if (!disposed) { failed.value = true; message.value = text.value.error; } }
  finally { if (!disposed) busy.value = false; }
}
async function save() {
  if (busy.value || !saved.value) return;
  busy.value = true; failed.value = false; message.value = '';
  try {
    const value = await saveInstallationReporting({ enabled: enabled.value, revision: saved.value.revision });
    if (disposed) return;
    publish(value);
    message.value = text.value.ok;
  } catch { if (!disposed) { failed.value = true; message.value = text.value.error; } }
  finally { if (!disposed) busy.value = false; }
}
onMounted(load);
</script>
<style scoped>
.reporting-fields { border: 0; padding: 0; margin: 16px 0; min-width: 0; }
.reporting-status { display: grid; gap: 8px; }
.reporting-status > div { display: flex; flex-wrap: wrap; gap: 4px 12px; }
.reporting-status dt { font-weight: 600; }
.reporting-status dd { margin: 0; overflow-wrap: anywhere; }
</style>
