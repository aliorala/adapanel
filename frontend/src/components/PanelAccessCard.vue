<template>
  <section class="form-surface panel-access">
    <div class="section-head">
      <div>
        <h3>{{ t("panel.panelAccess.title") }}</h3>
        <p class="meta">{{ t("panel.panelAccess.subtitle") }}</p>
      </div>
      <button
        type="button"
        class="secondary compact head-button"
        :disabled="loading || busy"
        @click="refresh">
        {{ t("panel.panelAccess.refresh") }}
      </button>
    </div>

    <!-- Özelliğin var oluş sebebi. Kurulum tamamlansa bile görünür kalır: yönetici neden
         bir alan adı girdiğini altı ay sonra da hatırlamalı. -->
    <p class="notice warning">{{ t("panel.panelAccess.selfSignedWarning") }}</p>

    <p v-if="notice" :class="['notice', 'page-notice', { error: noticeError }]" :role="noticeError ? 'alert' : 'status'">{{ notice }}</p>

    <!-- Veri gelmeden DURUM GÖSTERME: yarım veriyle "kurulu değil" demek yöneticiyi ikinci kez
         kurulum yapmaya iter (panelin yeni kuralı). -->
    <p v-if="!loaded" class="empty-state" role="status" aria-live="polite">
      {{ t("panel.common.loading") }}
    </p>

    <template v-else>
      <div class="form-grid">
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.panelAccess.hostNameLabel") }}</span>
          <input
            v-model="form.hostName"
            maxlength="253"
            autocomplete="off"
            spellcheck="false"
            :placeholder="t('panel.panelAccess.hostNamePlaceholder')" />
          <span class="field-hint">{{ t("panel.panelAccess.hostNameHint") }}</span>
        </label>
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.panelAccess.contactEmailLabel") }}</span>
          <input v-model="form.contactEmail" type="email" maxlength="256" autocomplete="off" />
          <span class="field-hint">{{ t("panel.panelAccess.contactEmailHint") }}</span>
        </label>
      </div>

      <label class="check-field">
        <input v-model="form.useStaging" type="checkbox" />
        <span>{{ t("panel.panelAccess.useStaging") }}</span>
      </label>
      <p class="field-hint">{{ t("panel.panelAccess.useStagingHint") }}</p>

      <div class="row-actions">
        <button type="button" class="compact" :disabled="busy" @click="submit">
          {{ busy ? t("panel.panelAccess.setupRunning") : t("panel.panelAccess.setup") }}
        </button>
      </div>

      <!-- Alan adı hiç ayarlanmamışsa adım listesi anlamsızdır: özellik pasif, bozuk değil. -->
      <p v-if="!status?.isConfigured" class="meta disabled-note">
        {{ t("panel.panelAccess.disabled") }}
      </p>

      <template v-else>
        <div class="access-summary">
          <div class="summary-item">
            <span class="field-label">{{ t("panel.panelAccess.panelUrl") }}</span>
            <CodeSample
              :code="status?.panelUrl ?? ''"
              :copy-label="t('panel.panelAccess.copy')"
              :copied-label="t('panel.panelAccess.copied')" />
          </div>
          <div class="summary-item">
            <span class="field-label">{{ t("panel.panelAccess.mcpEndpoint") }}</span>
            <CodeSample
              :code="status?.mcpEndpoint ?? ''"
              :copy-label="t('panel.panelAccess.copy')"
              :copied-label="t('panel.panelAccess.copied')" />
          </div>
        </div>

        <p class="meta">
          {{ t("panel.panelAccess.siteName") }}: <strong>{{ status?.siteName }}</strong>
          · {{ t("panel.panelAccess.backendUrl") }}: <strong>{{ status?.backendUrl }}</strong>
        </p>

        <p v-if="status?.certificateIsSelfSigned" class="notice error" role="alert">
          {{ t("panel.panelAccess.certificateSelfSigned") }}
        </p>

        <!-- HER ADIM AYRI SATIR: bir adım patladıysa hangisi olduğu tek bakışta görülmeli. -->
        <div class="data-table step-table">
          <div class="data-row table-head step-row">
            <span>{{ t("panel.panelAccess.stepsTitle") }}</span>
            <span>{{ t("panel.panelAccess.colValue") }}</span>
            <span>{{ t("panel.panelAccess.colStatus") }}</span>
          </div>

          <div v-for="step in status?.steps ?? []" :key="step.key" class="data-row step-row">
            <div class="cell-main">
              <strong>{{ t(`panel.panelAccess.step.${step.key}`) }}</strong>
              <small>{{ t(`panel.panelAccess.stepHint.${step.key}`) }}</small>
            </div>
            <div class="cell-main">
              <span class="cell-muted">{{ step.value || "—" }}</span>
              <!-- Teknik ayrıntı ÇEVRİLMEZ: hata metni/komut çıktısı olduğu gibi görünmeli. -->
              <small v-if="step.detail" class="step-detail">{{ step.detail }}</small>
            </div>
            <span :class="['status-pill', toneOf(step.status)]">
              {{ t(`panel.panelAccess.status.${step.status}`) }}
            </span>
          </div>
        </div>

        <p class="meta">
          {{ t("panel.panelAccess.checkedAt", { time: checkedAtText }) }}
        </p>
      </template>
    </template>
  </section>
</template>

<script setup lang="ts">
/**
 * "Panel erişimi": paneli yöneticinin KENDİ alan adıyla, geçerli (Let's Encrypt) bir sertifikayla
 * dışarıya açar.
 *
 * Var oluş sebebi: panel kendinden imzalı sertifikayla çalışır. Tarayıcı uyarıyı geçebilir ama
 * panele /mcp üzerinden kendi altyapısından bağlanan yapay zeka istemcileri GEÇEMEZ — bağlantı
 * sessizce reddedilir ve kullanıcı hiçbir açıklama görmez.
 *
 * KURAL: burada hiçbir varsayılan/örnek alan adı KODA GÖMÜLÜ DEĞİLDİR. Panel bir üründür;
 * yer tutucu metin bile i18n dosyasından gelir.
 */
import { computed, onMounted, reactive, ref } from "vue";
import { useI18n } from "vue-i18n";
import CodeSample from "./CodeSample.vue";
import {
  getPanelAccessStatus,
  setupPanelAccess,
  type PanelAccessStatus,
  type PanelAccessStatusLevel
} from "../api/panelAccess";
import { extractApiDetails, extractApiMessage } from "../stores/auth";

const { t } = useI18n();

const status = ref<PanelAccessStatus | null>(null);
// Durum okunmadan hiçbir şey gösterilmez (ayrı bayrak: status null "yok" anlamına gelmesin).
const loaded = ref(false);
const loading = ref(false);
const busy = ref(false);
const notice = ref("");
const noticeError = ref(false);

const form = reactive({
  hostName: "",
  contactEmail: "",
  useStaging: false
});

const checkedAtText = computed(() => {
  const raw = status.value?.checkedAtUtc ?? "";
  if (!raw) {
    return "";
  }
  // Sunucu UTC gönderir; sonek yoksa UTC kabul edip yerel saate çevir.
  const normalized = /[zZ]|[+-]\d{2}:\d{2}$/.test(raw) ? raw : `${raw}Z`;
  const date = new Date(normalized);
  return Number.isNaN(date.getTime()) ? "" : date.toLocaleString();
});

onMounted(refresh);

async function refresh() {
  loading.value = true;
  try {
    const result = await getPanelAccessStatus();
    status.value = result;
    // Kullanıcı forma bir şey yazdıysa üzerine YAZMA (yenileme yazdığını silmesin).
    if (!form.hostName) {
      form.hostName = result.hostName;
    }
    loaded.value = true;
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
    // Hata durumunda da formu aç: yönetici en azından kurulumu deneyebilmeli.
    loaded.value = true;
  } finally {
    loading.value = false;
  }
}

async function submit() {
  const hostName = form.hostName.trim();
  if (!hostName.includes(".") || hostName.length < 4) {
    showNotice(t("panel.panelAccess.hostNameRequired"), true);
    return;
  }

  busy.value = true;
  showNotice("", false);
  try {
    const result = await setupPanelAccess({
      hostName,
      contactEmail: form.contactEmail.trim(),
      useStaging: form.useStaging
    });
    status.value = result;
    loaded.value = true;
    // "Kuruldu" demeyi HAK ETMEK gerekir: tek bir adım bile başarısızsa başarı mesajı gösterme.
    const done = result.overallStatus === "ok";
    showNotice(done ? t("panel.panelAccess.setupDone") : t("panel.panelAccess.setupIncomplete"), !done);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    busy.value = false;
  }
}

function toneOf(level: PanelAccessStatusLevel) {
  if (level === "ok") return "ok";
  if (level === "warn") return "warn";
  if (level === "fail") return "danger";
  // "unknown" ASLA yeşil değildir: ölçemedik demektir, sorun yok demek değil.
  return "muted";
}

function resolveApiMessage(error: unknown) {
  const key = extractApiMessage(error);
  const translated = t(key);
  const message = translated === key ? key : translated;
  const details = extractApiDetails(error);
  return details ? `${message}\n\n${details}` : message;
}

function showNotice(message: string, isError: boolean) {
  notice.value = message;
  noticeError.value = isError;
}
</script>

<style scoped>
.panel-access {
  display: grid;
  gap: 12px;
  align-content: start;
}

.head-button,
.row-actions button {
  width: auto;
}

.notice.warning {
  border-color: color-mix(in srgb, var(--warn-1) 45%, transparent);
}

.notice {
  white-space: pre-wrap;
}

.access-summary {
  display: grid;
  gap: 10px;
  grid-template-columns: repeat(auto-fit, minmax(260px, 1fr));
}

.summary-item {
  display: grid;
  gap: 6px;
  min-width: 0;
}

.step-row {
  grid-template-columns: minmax(190px, 1.1fr) minmax(200px, 2fr) minmax(110px, auto);
}

/* Teknik ayrıntı uzun olabilir (win-acme çıktısı): satırı taşırmasın, kırılarak sarsın. */
.step-detail {
  display: block;
  max-height: 5.5em;
  overflow: auto;
  color: var(--text-muted);
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  /* 0.72rem = 9.72px idi — olcek tabani `--fs-2xs` (10.53px); altina inen kural yok. */
  font-size: var(--fs-2xs);
  line-height: 1.45;
  overflow-wrap: anywhere;
}

.status-pill.muted {
  background: var(--surface-1);
  color: var(--text-muted);
  border: 1px dashed var(--panel-border);
}

.disabled-note {
  margin: 0;
}

@media (max-width: 860px) {
  .step-row {
    grid-template-columns: 1fr;
    gap: 6px;
  }
}

@media (max-width: 760px) {
  .form-grid {
    grid-template-columns: 1fr;
  }
}
</style>
