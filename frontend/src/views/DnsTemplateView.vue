<template>
  <section class="panel module-shell dns-template">
    <div class="module-head">
      <div>
        <h3>{{ t("panel.dnsTemplate.title") }}</h3>
        <!-- Yer tutucu açıklaması paragraf yerine "?" ipucunda: başlık kısa kalıyor. -->
        <p class="meta">
          {{ t("panel.dnsTemplate.subtitle") }}
          <FieldHint :label="t('panel.dnsTemplate.value')" :text="t('panel.dnsTemplate.placeholderHint')" />
        </p>
      </div>
      <button class="secondary compact" :disabled="loading" @click="load">{{ t("panel.dnsTemplate.refresh") }}</button>
    </div>

    <p v-if="notice" :class="['notice', 'page-notice', { error: noticeError }]" :role="noticeError ? 'alert' : 'status'">{{ notice }}</p>

    <p v-if="!templateLoaded" class="empty-state" role="status" aria-live="polite">{{ t("panel.common.loading") }}</p>

    <div v-else class="tmpl-table">
      <div class="tmpl-row head">
        <span>{{ t("panel.dnsTemplate.type") }}</span>
        <span>{{ t("panel.dnsTemplate.host") }}</span>
        <span>{{ t("panel.dnsTemplate.value") }}</span>
        <span>{{ t("panel.dnsTemplate.ttl") }}</span>
        <span>{{ t("panel.dnsTemplate.priority") }}</span>
        <span></span>
      </div>
      <!--
        ERİŞİLEBİLİR AD: bu ızgarada sütun başlıkları AYRI bir satırda (`.tmpl-row.head`) duruyor
        ve alanlarla programatik bir bağı YOK. Görsel kullanıcı başlığı yukarıda görüp "bu kutu
        TTL" diyebiliyor; ekran okuyucu ise 5 alanı da adsız okuyordu ("düzenleme, boş"). Beş
        kayıtlık bir şablonda 25 adsız alan demek, ekranın klavyeyle doldurulamaması demek.
        Başlık metinleri `aria-label` olarak alana bağlandı; kaçıncı kayıt olduğu da söyleniyor
        çünkü satırlar birbirinin aynısı.
      -->
      <div v-for="(record, index) in records" :key="index" class="tmpl-row">
        <select
          v-model="record.type"
          :aria-label="t('panel.dnsTemplate.fieldAria', { field: t('panel.dnsTemplate.type'), index: index + 1 })">
          <option v-for="type in types" :key="type" :value="type">{{ type }}</option>
        </select>
        <input
          v-model.trim="record.host"
          placeholder="@"
          spellcheck="false"
          :aria-label="t('panel.dnsTemplate.fieldAria', { field: t('panel.dnsTemplate.host'), index: index + 1 })" />
        <input
          v-model.trim="record.value"
          placeholder="{ip}"
          spellcheck="false"
          :aria-label="t('panel.dnsTemplate.fieldAria', { field: t('panel.dnsTemplate.value'), index: index + 1 })" />
        <input
          v-model.number="record.ttl"
          type="number"
          min="60"
          max="604800"
          :aria-label="t('panel.dnsTemplate.fieldAria', { field: t('panel.dnsTemplate.ttl'), index: index + 1 })" />
        <!-- Öncelik yalnızca MX/SRV için; boş bırakılırsa MX'e 10 uygulanır. -->
        <input
          v-model.number="record.priority"
          type="number"
          min="0"
          max="65535"
          :disabled="record.type !== 'MX' && record.type !== 'SRV'"
          placeholder="—"
          :aria-label="t('panel.dnsTemplate.fieldAria', { field: t('panel.dnsTemplate.priority'), index: index + 1 })" />
        <!-- Düğme metni her satırda "Sil"; hangi kaydı sildiği ancak GÖRSEL konumdan anlaşılıyordu. -->
        <button
          class="danger compact"
          type="button"
          :aria-label="t('panel.dnsTemplate.removeRecord', { index: index + 1 })"
          @click="records.splice(index, 1)">{{ t("panel.common.delete") }}</button>
      </div>
      <p v-if="records.length === 0" class="empty-state">{{ t("panel.dnsTemplate.empty") }}</p>
    </div>

    <!-- SUNUCU GENELİ İKİNCİL (slave) DNS SUNUCULARI.
         Plesk'te bu iki yerde durur ("Slave DNS Manager" + "Transfer Restrictions Template");
         burada tek yer. Buradaki her IP, panelin birincil olduğu HER bölgeden AXFR alabilir ve
         bölge değiştiğinde NOTIFY alır — yani bölge bölge IP girmeyi (ve birini unutmayı) bitirir. -->
    <div class="secondary-dns">
      <label class="stacked-field" for="dns-secondary-servers">
        <span class="field-label">
          {{ t("panel.dnsTemplate.secondaryServers") }}
          <FieldHint :label="t('panel.dnsTemplate.secondaryServers')" :text="t('panel.dnsTemplate.secondaryServersHelp')" />
        </span>
        <textarea id="dns-secondary-servers" v-model="secondaryServersText" rows="3" spellcheck="false" placeholder="192.0.2.53&#10;2001:db8::53"></textarea>
      </label>
    </div>

    <div class="row-actions">
      <button class="secondary compact" type="button" :disabled="loading || saving || !templateLoaded" @click="ipMappings.push({ privateAddress: '', publicAddress: '' })">{{ t('panel.dnsTemplate.addIpMapping') }}</button>
      <button class="secondary compact" type="button" @click="addRow">{{ t("panel.dnsTemplate.addRow") }}</button>
      <button class="compact" type="button" :disabled="saving" @click="save">
        {{ saving ? t("panel.dnsTemplate.saving") : t("panel.common.save") }}
      </button>
    </div>
    <section class="nat-settings" v-if="templateLoaded">
      <h4>{{ t('panel.dnsTemplate.natTitle') }}</h4>
      <p class="meta">{{ t('panel.dnsTemplate.natHint') }}</p>
      <div v-for="(mapping, index) in ipMappings" :key="index" class="nat-row">
        <label>{{ t('panel.dnsTemplate.privateIp') }}<input v-model.trim="mapping.privateAddress" :disabled="loading || saving" placeholder="192.168.1.80" spellcheck="false" /></label>
        <label>{{ t('panel.dnsTemplate.publicIp') }}<input v-model.trim="mapping.publicAddress" :disabled="loading || saving" placeholder="203.0.113.10" spellcheck="false" /></label>
        <button class="secondary compact" :disabled="loading || saving" @click="ipMappings.splice(index, 1)">{{ t('panel.common.delete') }}</button>
      </div>
      <label><input type="checkbox" v-model="applyMappings" :disabled="loading || saving" /> {{ t('panel.dnsTemplate.applyMappings') }}</label>
      <ul v-if="mappingResults.length" aria-live="polite">
        <li v-for="result in mappingResults" :key="result.siteName" :class="{ error: !result.succeeded }">
          {{ result.siteName }} — {{ result.succeeded ? t('panel.dnsTemplate.mappingDone', { count: result.changedRecords }) : t('panel.dnsTemplate.mappingFailed') }}
          <details v-if="!result.succeeded"><summary>{{ t('panel.dnsTemplate.mappingDetails') }}</summary><pre>{{ result.error }}</pre></details>
        </li>
      </ul>
    </section>
  </section>
</template>

<script setup lang="ts">
import { onMounted, onUnmounted, ref } from "vue";
import { useI18n } from "vue-i18n";
import { getDnsTemplate, updateDnsTemplate, type DnsTemplateRecord, type DnsTemplate, type DnsIpMapping, type DnsIpMappingResult } from "../api/dns";
import { useConfirm } from "../components/ConfirmDialog.vue";
import FieldHint from "../components/FieldHint.vue";
import { extractApiMessage } from "../stores/auth";

const { t } = useI18n();
const records = ref<DnsTemplateRecord[]>([]);
const ipMappings = ref<DnsIpMapping[]>([]);
const mappingResults = ref<DnsIpMappingResult[]>([]);
const applyMappings = ref(true);
const { confirm } = useConfirm();
let epoch = 0;
onUnmounted(() => { epoch++; });
// Sunucu geneli ikincil (slave) DNS sunucuları; her satır bir IP.
const secondaryServersText = ref("");
const loading = ref(false);
// Şablon BİR KEZ başarıyla okundu mu. `loading` hata durumunda da false olur; boş kayıt listesi
// "şablonda kayıt yok" diye okunuyor, kullanıcı var olan şablonun üstüne yazabiliyordu.
const templateLoaded = ref(false);
const saving = ref(false);
const notice = ref("");
const noticeError = ref(false);
const types = ["A", "AAAA", "CNAME", "MX", "TXT", "NS", "SRV", "CAA"];

onMounted(load);

async function load() {
  if (loading.value || saving.value) return;
  const request = ++epoch;
  loading.value = true;
  notice.value = "";
  try {
    const template = await getDnsTemplate();
    if (request === epoch) applyTemplate(template);
  } catch (error) {
    showNotice(resolveMessage(error), true);
  } finally {
    loading.value = false;
  }
}

function addRow() {
  records.value.push({ type: "A", host: "@", value: "{ip}", ttl: 3600 });
}

async function save() {
  if (loading.value || saving.value || !templateLoaded.value) return;
  const request = ++epoch;
  const acceptedRecords = records.value.map(record => ({ ...record }));
  const acceptedMappings = ipMappings.value.map(mapping => ({ ...mapping }));
  const acceptedSecondaries = splitLines(secondaryServersText.value);
  const applyExisting = applyMappings.value;
  saving.value = true;
  notice.value = "";
  try {
    if (applyExisting && acceptedMappings.length && !await confirm({
      title: t('panel.dnsTemplate.natTitle'), message: t('panel.dnsTemplate.confirmMappings'),
      confirmLabel: t('panel.common.save'), tone: 'danger',
      impact: acceptedMappings.map(mapping => `${mapping.privateAddress} → ${mapping.publicAddress}`)
    })) return;
    if (request !== epoch) return;
    const template = await updateDnsTemplate(acceptedRecords, acceptedSecondaries, acceptedMappings, applyExisting);
    if (request !== epoch) return;
    applyTemplate(template);
    const failed = template.mappingResults?.some(result => !result.succeeded) ?? false;
    showNotice(t(failed ? 'panel.dnsTemplate.mappingPartial' : "panel.dnsTemplate.saved"), failed);
  } catch (error) {
    showNotice(resolveMessage(error), true);
  } finally {
    saving.value = false;
  }
}

/** Sunucudan gelen şablonu ekrana yansıtır (yükleme ve kaydetme aynı yolu kullanır). */
function applyTemplate(template: DnsTemplate) {
  records.value = template.records;
  ipMappings.value = template.ipMappings ?? [];
  mappingResults.value = template.mappingResults ?? [];
  secondaryServersText.value = (template.secondaryDnsServers ?? []).join("\n");
  // Buraya YALNIZCA sunucu gerçekten cevap verdiğinde gelinir (yükleme ya da kaydetme).
  templateLoaded.value = true;
}

/** Satır ya da virgülle ayrılmış listeyi temizler; boş satırlar atılır. */
function splitLines(value: string) {
  return value.split(/[\n,]/).map((item) => item.trim()).filter(Boolean);
}

function showNotice(message: string, isError: boolean) {
  notice.value = message;
  noticeError.value = isError;
}

function resolveMessage(error: unknown) {
  const key = extractApiMessage(error);
  const translated = t(key);
  return translated === key ? key : translated;
}
</script>

<style scoped>
.dns-template {
  display: grid;
  gap: 12px;
}
.nat-settings { display: grid; gap: 12px; }
.nat-row { display: grid; grid-template-columns: 1fr 1fr auto; gap: 12px; align-items: end; }
.nat-row label { display: grid; gap: 6px; min-width: 0; }
.nat-row input { width: 100%; min-width: 0; }
.nat-settings pre { white-space: pre-wrap; overflow-wrap: anywhere; }

.tmpl-table {
  display: grid;
  gap: 6px;
}

.tmpl-row {
  display: grid;
  grid-template-columns: 0.8fr 1fr 1.6fr 0.7fr 0.6fr auto;
  gap: 8px;
  align-items: center;
}

.tmpl-row.head {
  font-weight: 700;
  color: var(--text-muted);
  font-size: 0.82rem;
}

.tmpl-row input,
.tmpl-row select {
  width: 100%;
}

.secondary-dns textarea {
  width: 100%;
}

@media (max-width: 820px) {
  .nat-row { grid-template-columns: 1fr; }
  .tmpl-row {
    grid-template-columns: 1fr 1fr;
  }
  .tmpl-row.head {
    display: none;
  }
}
</style>
