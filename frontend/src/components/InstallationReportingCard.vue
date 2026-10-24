<template>
  <section class="form-surface">
    <div class="section-head"><h3>{{ text.title }}</h3><button type="button" class="secondary compact" :disabled="busy" @click="load">{{ text.refresh }}</button></div>
    <p class="meta">{{ text.description }}</p>
    <p class="notice">{{ text.disclosure }}</p>
    <p v-if="message" :class="['notice', { error: failed }]" :role="failed ? 'alert' : 'status'">{{ message }}</p>
    <template v-if="saved">
      <fieldset :disabled="busy" class="reporting-fields">
        <label class="check-field"><input v-model="enabled" type="checkbox" /><span>{{ text.consent }}</span></label>
        <div class="form-grid">
          <label class="stacked-field"><span>{{ text.location }}</span><input v-model="location" maxlength="100" autocomplete="off" /></label>
          <label class="stacked-field"><span>{{ text.datacenter }}</span><input v-model="datacenter" maxlength="100" autocomplete="off" /></label>
        </div>
        <p class="field-hint">{{ text.optional }}</p>
        <button type="button" class="compact" @click="save">{{ text.save }}</button>
      </fieldset>
      <p class="meta">{{ text.status }}: {{ saved.enabled ? text.on : text.off }}. {{ text.last }}: {{ saved.lastAcceptedAtUtc ? new Date(saved.lastAcceptedAtUtc).toLocaleString() : text.never }}</p>
      <p v-if="saved.lastError && saved.lastError !== 'pending'" class="notice warn-banner">{{ text.deliveryFailure }}</p>
    </template>
  </section>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue';
import { useI18n } from 'vue-i18n';
import { getInstallationReporting, saveInstallationReporting, type InstallationReporting } from '../api/installationReporting';
const { locale } = useI18n();
const text = computed(() => locale.value.startsWith('tr') ? {
  title: 'Aktif AdaPanel kurulum sayımı', refresh: 'Yenile', save: 'Tercihi kaydet',
  description: 'AdaPanel’in kullanımını ölçmek için isteğe bağlı günlük bildirim. Bu tercih lisansı veya panelin çalışmasını etkilemez.',
  disclosure: 'Açıldığında adapanel.host adresine rastgele kurulum kimliği, panel sürümü ve aşağıdaki isteğe bağlı alanlar gönderilir. Bağlantının IP adresi kaydedilir. Toplam sayı ve ayrıntılar yalnız AdaPanel’in özel yönetim ekranında görülür; herkese açık yayımlanmaz. Aktiflik son 48 saatteki bildirimlere göre hesaplanır. 30 gün bildirim alınmayan kayıtlar saklama süresinden çıkarılır. Hosting erişim günlükleri ayrı tutulabilir.',
  consent: 'Bu bilgilerin günlük gönderilmesine izin veriyorum', location: 'Konum (isteğe bağlı ülke/şehir)', datacenter: 'Datacenter / sağlayıcı (isteğe bağlı)',
  optional: 'Bu alanlar sizin beyanınızdır. IP üzerinden otomatik konum sorgusu yapılmaz. Paylaşımı kapatınca gönderim durur; son bildirim 48 saat dolunca aktif sayıdan düşer.',
  status: 'Kaydedilen tercih', on: 'Açık', off: 'Kapalı', last: 'Son kabul edilen bildirim', never: 'Henüz yok',
  ok: 'Tercih kaydedildi. Günlük bildirim arka planda işlenecek.', error: 'İşlem tamamlanamadı. Yenile ile güncel durumu okuyup tekrar deneyin.',
  deliveryFailure: 'Son bildirim kabul edilmedi. Panel normal çalışır; bir sonraki günlük denemede yeniden gönderilir.'
} : {
  title: 'Active AdaPanel installation count', refresh: 'Refresh', save: 'Save preference',
  description: 'Optional daily reporting to measure AdaPanel adoption. This preference does not affect licensing or panel operation.',
  disclosure: 'When enabled, a random installation identifier, panel version and the optional fields below are sent to adapanel.host. The connection IP address is recorded. Only AdaPanel administrators can view the count and details in the private management area. No public count is published. Activity is based on reports within 48 hours. Records with no reports for 30 days expire. Hosting access logs may be retained separately.',
  consent: 'I allow these details to be sent daily', location: 'Location (optional country/city)', datacenter: 'Datacenter / provider (optional)',
  optional: 'These fields are self-reported. No automatic IP geolocation lookup is performed. Disabling stops reporting; the last report leaves the active count after 48 hours.',
  status: 'Saved preference', on: 'Enabled', off: 'Disabled', last: 'Last accepted report', never: 'None yet',
  ok: 'Preference saved. Daily reporting will run in the background.', error: 'The operation could not complete. Refresh the current state and try again.',
  deliveryFailure: 'The last report was not accepted. The panel continues operating; reporting retries on the next daily attempt.'
});
const saved = ref<InstallationReporting | null>(null);
const enabled = ref(false), location = ref(''), datacenter = ref('');
const busy = ref(false), failed = ref(false), message = ref('');
function publish(value: InstallationReporting) {
  saved.value = value; enabled.value = value.enabled; location.value = value.location; datacenter.value = value.datacenter;
}
async function load() {
  if (busy.value) return;
  busy.value = true; message.value = ''; failed.value = false;
  try { publish(await getInstallationReporting()); }
  catch { failed.value = true; message.value = text.value.error; }
  finally { busy.value = false; }
}
async function save() {
  if (busy.value || !saved.value) return;
  busy.value = true; failed.value = false; message.value = '';
  try {
    publish(await saveInstallationReporting({ enabled: enabled.value, location: location.value, datacenter: datacenter.value, revision: saved.value.revision }));
    message.value = text.value.ok;
  } catch { failed.value = true; message.value = text.value.error; }
  finally { busy.value = false; }
}
onMounted(load);
</script>
<style scoped>.reporting-fields { border: 0; padding: 0; margin: 16px 0; min-width: 0; } .reporting-fields .form-grid { margin: 16px 0; }</style>
