<template>
  <section class="wp-updates">
    <div class="section-head">
      <div>
        <h4>{{ t("panel.wpUpdates.title") }}</h4>
        <p class="meta">{{ t("panel.wpUpdates.hint") }}</p>
      </div>
      <button class="secondary compact" :disabled="loading || busy" @click="load">
        {{ t("panel.common.refresh") }}
      </button>
    </div>

    <p v-if="error" class="notice error" role="alert">{{ error }}</p>
    <p v-else-if="loading && !updates" class="empty-state">{{ t("panel.common.loading") }}</p>

    <template v-else-if="updates">
      <!-- ÖLÇÜLEMEDİYSE "güncel" DEMİYORUZ. Boş liste + sessizlik, açığı olan bir siteyi güvende
           gösterirdi; sebebi ayrıntısıyla yazıyoruz. -->
      <p v-if="!updates.measured" class="notice error" role="alert">
        {{ t("panel.wpUpdates.notMeasured") }}
        <small v-if="updates.error" class="detail">{{ updates.error }}</small>
      </p>

      <template v-else>
        <p v-if="guncellenebilir.length === 0" class="notice ok" role="status">
          {{ t("panel.wpUpdates.allCurrent") }}
        </p>

        <template v-else>
          <!-- GÜNCELLEME SİTE BOZABİLİR. Bunu saklamak yerine önden söylüyoruz: kullanıcı
               yedeğini alıp öyle basmalı. -->
          <p class="notice warn">{{ t("panel.wpUpdates.riskHint") }}</p>

          <div class="data-table">
            <div class="data-row table-head wpupd-row">
              <span>
                <input
                  type="checkbox"
                  :checked="hepsiSecili"
                  :indeterminate.prop="bazisiSecili"
                  :aria-label="t('panel.wpUpdates.selectAll')"
                  @change="hepsiniSec(($event.target as HTMLInputElement).checked)" />
              </span>
              <span>{{ t("panel.wpUpdates.item") }}</span>
              <span>{{ t("panel.wpUpdates.version") }}</span>
              <span>{{ t("panel.wpUpdates.type") }}</span>
            </div>
            <div v-for="oge in guncellenebilir" :key="oge.kind + ':' + oge.name" class="data-row wpupd-row">
              <span>
                <input v-model="secili" type="checkbox" :value="anahtar(oge)" :aria-label="oge.title" />
              </span>
              <span class="cell-main">
                <strong>{{ oge.title }}</strong>
                <small v-if="oge.status">{{ oge.status }}</small>
              </span>
              <span class="cell-main">
                <strong>{{ oge.currentVersion }} → {{ oge.availableVersion }}</strong>
              </span>
              <span class="status-pill">{{ t(`panel.wpUpdates.kind.${oge.kind}`) }}</span>
            </div>
          </div>

          <div class="row-actions">
            <button v-bind="writeGate(busy || secili.length === 0)" @click="uygula">
              {{ busy ? t("panel.wpUpdates.updating") : t("panel.wpUpdates.updateSelected", { count: secili.length }) }}
            </button>
          </div>
        </template>
      </template>

      <!-- SONUÇLAR: kısmi başarı gizlenmez; hangi öğenin geride kaldığı yazılır. -->
      <div v-if="sonuclar.length > 0" class="data-table result-table">
        <div v-for="(sonuc, index) in sonuclar" :key="index" class="data-row result-row">
          <span :class="['status-pill', sonuc.success ? 'ok' : 'danger']">
            {{ sonuc.success ? t("panel.wpUpdates.ok") : t("panel.wpUpdates.failed") }}
          </span>
          <span class="cell-main">
            <strong>{{ sonuc.name }}</strong>
            <small v-if="sonuc.message" class="detail">{{ sonuc.message }}</small>
          </span>
        </div>
      </div>
    </template>
  </section>
</template>

<script setup lang="ts">
import { computed, onMounted, ref, watch } from "vue";
import { useI18n } from "vue-i18n";
import {
  applyWordPressUpdates,
  getWordPressUpdates,
  type WordPressUpdatable,
  type WordPressUpdateItemResult,
  type WordPressUpdates
} from "../api/wordpress";
import { extractApiDetails, extractApiMessage } from "../stores/auth";
import { useSubscriptionGate } from "../composables/useSubscriptionGate";

const props = defineProps<{ siteName: string }>();
const { t } = useI18n();
// Askidaki abonelikte YAZMA dugmeleri bastan devre disi + sebebi title/aria ile soylenir
// (bkz. composables/useSubscriptionGate.ts). Okuma dugmeleri kapiya girmez.
const { writeGate } = useSubscriptionGate();

const updates = ref<WordPressUpdates | null>(null);
const sonuclar = ref<WordPressUpdateItemResult[]>([]);
const secili = ref<string[]>([]);
const loading = ref(false);
const busy = ref(false);
const error = ref("");

const guncellenebilir = computed(() => updates.value?.items.filter((item) => item.updateAvailable) ?? []);
const hepsiSecili = computed(
  () => guncellenebilir.value.length > 0 && secili.value.length === guncellenebilir.value.length
);
const bazisiSecili = computed(() => secili.value.length > 0 && !hepsiSecili.value);

function anahtar(oge: WordPressUpdatable) {
  return `${oge.kind}:${oge.name}`;
}

function hepsiniSec(secildi: boolean) {
  secili.value = secildi ? guncellenebilir.value.map(anahtar) : [];
}

function resolveApiMessage(err: unknown) {
  const anahtarAdi = extractApiMessage(err);
  const cevrilmis = t(anahtarAdi);
  const ayrinti = extractApiDetails(err);
  return (cevrilmis === anahtarAdi ? anahtarAdi : cevrilmis) + (ayrinti ? `\n\n${ayrinti}` : "");
}

async function load() {
  if (!props.siteName) {
    return;
  }
  loading.value = true;
  error.value = "";
  sonuclar.value = [];
  try {
    updates.value = await getWordPressUpdates(props.siteName);
    secili.value = [];
  } catch (err) {
    error.value = resolveApiMessage(err);
  } finally {
    loading.value = false;
  }
}

async function uygula() {
  busy.value = true;
  error.value = "";
  try {
    const sonuc = await applyWordPressUpdates(props.siteName, {
      core: secili.value.includes("core:core"),
      plugins: secili.value.filter((k) => k.startsWith("plugin:")).map((k) => k.slice("plugin:".length)),
      themes: secili.value.filter((k) => k.startsWith("theme:")).map((k) => k.slice("theme:".length))
    });
    sonuclar.value = sonuc.results;
    // Listeyi TAZELE: hangi öğenin gerçekten güncellendiğini sunucudan okuyoruz, kendi
    // varsayımımızdan değil.
    await load();
  } catch (err) {
    error.value = resolveApiMessage(err);
  } finally {
    busy.value = false;
  }
}

watch(() => props.siteName, () => void load());
onMounted(() => void load());
</script>

<style scoped>
.wp-updates {
  display: grid;
  gap: 10px;
}

.wpupd-row {
  grid-template-columns: 36px minmax(0, 2fr) minmax(0, 1.2fr) 0.8fr;
  align-items: start;
}

.result-row {
  grid-template-columns: 110px minmax(0, 1fr);
  align-items: start;
}

.result-table {
  margin-top: 4px;
}

.detail {
  display: block;
  margin-top: 2px;
  font-family: ui-monospace, SFMono-Regular, Consolas, monospace;
  font-size: 0.76rem;
  white-space: pre-wrap;
  word-break: break-word;
}
</style>
