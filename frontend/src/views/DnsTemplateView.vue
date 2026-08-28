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
      <button class="secondary compact" type="button" @click="addRow">{{ t("panel.dnsTemplate.addRow") }}</button>
      <button class="compact" type="button" :disabled="saving" @click="save">
        {{ saving ? t("panel.dnsTemplate.saving") : t("panel.common.save") }}
      </button>
    </div>
  </section>
</template>

<script setup lang="ts">
import { onMounted, ref } from "vue";
import { useI18n } from "vue-i18n";
import { getDnsTemplate, updateDnsTemplate, type DnsTemplateRecord } from "../api/dns";
import FieldHint from "../components/FieldHint.vue";
import { extractApiMessage } from "../stores/auth";

const { t } = useI18n();
const records = ref<DnsTemplateRecord[]>([]);
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
  loading.value = true;
  notice.value = "";
  try {
    applyTemplate(await getDnsTemplate());
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
  saving.value = true;
  notice.value = "";
  try {
    applyTemplate(await updateDnsTemplate(records.value, splitLines(secondaryServersText.value)));
    showNotice(t("panel.dnsTemplate.saved"), false);
  } catch (error) {
    showNotice(resolveMessage(error), true);
  } finally {
    saving.value = false;
  }
}

/** Sunucudan gelen şablonu ekrana yansıtır (yükleme ve kaydetme aynı yolu kullanır). */
function applyTemplate(template: { records: DnsTemplateRecord[]; secondaryDnsServers: string[] }) {
  records.value = template.records;
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
  .tmpl-row {
    grid-template-columns: 1fr 1fr;
  }
  .tmpl-row.head {
    display: none;
  }
}
</style>
