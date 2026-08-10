<template>
  <section class="form-surface settings-section license-section">
    <div class="section-head">
      <div>
        <h3>{{ t("panel.license.title") }}</h3>
        <p class="meta">{{ t("panel.license.subtitle") }}</p>
      </div>
      <!--
        Renk kararı `state`'e DEĞİL, iki bayrağa bağlı:
        yapılandırılmamış → nötr, kısıtlıyor → turuncu (kırmızı DEĞİL: panel çalışmaya devam
        ediyor), diğer hâlde yeşil.
      -->
      <!-- Rozetin kendi başına etiketi yok; `title` onu ekran okuyucuya ve fareyle bekleyene
           "Durum" diye tanıtır. -->
      <span v-if="status" :class="['status-pill', badgeTone]" :title="t('panel.license.state')">
        {{ t(stateLabelKey) }}
      </span>
    </div>

    <p v-if="notice" :class="['notice', 'page-notice', { error: noticeError }]" :role="noticeError ? 'alert' : 'status'">
      {{ notice }}
    </p>

    <SectionLoadError v-if="loadError.message.value" :message="loadError.message.value" @retry="load" />

    <p v-else-if="!status" class="empty-state" role="status" aria-live="polite">{{ t("panel.common.loading") }}</p>

    <template v-else>
      <p class="meta license-message">{{ t(status.messageKey) }}</p>

      <!-- Lisanslama KAPALI: bu bir hata değil, varsayılan kurulum. Form, düğme, alan yok. -->
      <div v-if="!status.isConfigured" class="notice license-info" role="status">
        <strong>{{ t("panel.license.notConfiguredTitle") }}</strong>
        <span>{{ t("panel.license.notConfiguredHint") }}</span>
      </div>

      <template v-else>
        <!-- "Panelim kilitlendi mi?" sorusunun cevabı: hayır. Siteler, posta, veritabanları çalışıyor. -->
        <div v-if="status.restrictsNewResources" class="notice license-warn" role="status">
          <strong>{{ t("panel.license.restrictedTitle") }}</strong>
          <span>{{ t("panel.license.restrictedHint") }}</span>
        </div>

        <div class="summary-strip">
          <div class="summary-item">
            <span>{{ t("panel.license.key") }}</span>
            <!-- Anahtarın tamamı HİÇ gösterilmez; sunucu da yalnız son dört karakteri döndürüyor. -->
            <strong>{{ status.hasKey ? `****${status.keySuffix}` : "—" }}</strong>
          </div>
          <div class="summary-item">
            <span>{{ t("panel.license.sku") }}</span>
            <strong>{{ status.sku || "—" }}</strong>
          </div>
          <div class="summary-item">
            <span>{{ t("panel.license.maxSubscriptions") }}</span>
            <strong>{{ status.maxSubscriptions === 0 ? t("panel.license.unlimited") : status.maxSubscriptions }}</strong>
          </div>
          <div class="summary-item">
            <span>{{ t("panel.license.expiresAt") }}</span>
            <strong>{{ status.expiresAtUtc ? formatDate(status.expiresAtUtc) : t("panel.license.neverExpires") }}</strong>
          </div>
          <div class="summary-item">
            <span>{{ t("panel.license.lastVerified") }}</span>
            <strong>{{ status.lastVerifiedAtUtc ? formatDate(status.lastVerifiedAtUtc) : "—" }}</strong>
          </div>
          <div v-if="showGraceEnds" class="summary-item">
            <span>{{ t("panel.license.graceEnds") }}</span>
            <strong>{{ status.offlineGraceEndsAtUtc ? formatDate(status.offlineGraceEndsAtUtc) : "—" }}</strong>
          </div>
        </div>

        <!-- Teşhis metni: çeviri anahtarı değil, sunucudan gelen ham hata. Yalnız doluysa görünür. -->
        <p v-if="status.lastError" class="notice error" role="alert">
          {{ t("panel.license.lastError") }}: {{ status.lastError }}
        </p>

        <div class="form-surface license-activate">
          <div class="section-head">
            <div>
              <h4>{{ status.hasKey ? t("panel.license.changeKey") : t("panel.license.activateTitle") }}</h4>
            </div>
          </div>
          <label class="stacked-field">
            <span class="field-label">{{ t("panel.license.key") }}</span>
            <input
              v-model.trim="licenseKey"
              :placeholder="t('panel.license.keyPlaceholder')"
              spellcheck="false"
              autocapitalize="off"
              autocomplete="off" />
          </label>
          <div class="row-actions">
            <button class="compact" :disabled="busy || licenseKey.length === 0" @click="activate">
              {{ busy ? t("panel.common.saving") : t("panel.license.activate") }}
            </button>
            <button class="secondary compact" :disabled="busy" @click="refreshStatus">
              {{ t("panel.license.refresh") }}
            </button>
            <button v-if="status.hasKey" class="danger compact" :disabled="busy" @click="remove">
              {{ t("panel.license.remove") }}
            </button>
          </div>
        </div>

        <div class="form-surface license-fingerprint">
          <div class="section-head">
            <div>
              <h4>{{ t("panel.license.fingerprint") }}</h4>
            </div>
            <button class="secondary compact" @click="copyFingerprint">{{ t("panel.license.copyFingerprint") }}</button>
          </div>
          <!-- 64 karakter: dar ekranda sayfayı yatay kaydırmasın diye KENDİ İÇİNDE kayar. -->
          <div class="fingerprint-value"><code>{{ status.fingerprint || "—" }}</code></div>
          <small class="field-hint">{{ t("panel.license.fingerprintHint") }}</small>
          <!--
            `fingerprintSource` normal kullanıcıya gösterilmez; yalnız kayıt defteri okunamamış
            (parmak izi yalnız veri klasörüne bağlı) durumda destek için uyarı çıkar.
          -->
          <small v-if="status.fingerprintSource === 'installid-only'" class="field-hint license-source-warn">
            {{ t("panel.license.fingerprintSourceWarning") }}
          </small>
        </div>
      </template>
    </template>
  </section>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from "vue";
import { useI18n } from "vue-i18n";

import { activateLicense, getLicense, refreshLicense, removeLicense, type LicenseStatus } from "../api/license";
import SectionLoadError from "../components/SectionLoadError.vue";
import { useConfirm } from "../components/ConfirmDialog.vue";
import { useNotice, useSectionError } from "../composables/useNotice";
import { copyToClipboard } from "../utils/clipboard";

const { t } = useI18n();
const { confirm } = useConfirm();
const { notice, noticeError, showNotice, showError } = useNotice();
const loadError = useSectionError();

const status = ref<LicenseStatus | null>(null);
const licenseKey = ref("");
const busy = ref(false);

const STATE_LABEL_KEYS: Record<LicenseStatus["state"], string> = {
  NotConfigured: "panel.license.stateNotConfigured",
  Active: "panel.license.stateActive",
  Grace: "panel.license.stateGrace",
  GraceExpired: "panel.license.stateGraceExpired",
  Expired: "panel.license.stateExpired",
  Invalid: "panel.license.stateInvalid",
  Unlicensed: "panel.license.stateUnlicensed"
};

const stateLabelKey = computed(() =>
  STATE_LABEL_KEYS[status.value?.state ?? "NotConfigured"] ?? "panel.license.stateNotConfigured");

const badgeTone = computed(() => {
  if (!status.value?.isConfigured) {
    return "";
  }

  // Kısıtlıyken TURUNCU: panel ölmedi, yalnız yeni kaynak açılmıyor.
  return status.value.restrictsNewResources ? "warn" : "ok";
});

// Tolerans bitişi yalnız tolerans/kısıt durumlarında anlamlı; diğerlerinde geçmiş bir tarih
// göstermek "lisansım bitiyor mu" paniğine yol açardı.
const showGraceEnds = computed(() =>
  status.value?.state === "Grace" || status.value?.state === "GraceExpired");

/*
 * YOKLAMA YOK.
 *
 * `GET /license` doğrulama aralığı dolduysa arka planda lisans sunucusuna ağ isteği tetikler.
 * Ekranı zamanlayıcıyla yoklamak o sunucuya gereksiz yük bindirir: sekme açılınca bir kez,
 * kullanıcı "Durumu yenile" derse bir kez daha.
 */
onMounted(load);

async function load() {
  try {
    status.value = await getLicense();
    loadError.clear();
  } catch (error) {
    status.value = null;
    loadError.capture(error);
  }
}

async function activate() {
  if (!licenseKey.value) {
    return;
  }

  busy.value = true;
  try {
    status.value = await activateLicense(licenseKey.value);
    // Anahtar bir SIRDIR: kaydedildikten sonra alanda asılı kalmaz.
    licenseKey.value = "";
    showNotice(t("license.activated"), false);
  } catch (error) {
    showError(error);
  } finally {
    busy.value = false;
  }
}

async function refreshStatus() {
  busy.value = true;
  try {
    // Ağ hatasında bile 200 döner; sonuç `state`'e yansır.
    status.value = await refreshLicense();
    loadError.clear();
    showNotice(t("license.refreshed"), false);
  } catch (error) {
    showError(error);
  } finally {
    busy.value = false;
  }
}

async function remove() {
  // Yıkıcı değil: siteler/posta/veritabanları etkilenmez, yalnız yeni kaynak kapısı kapanır.
  await confirm({
    title: t("panel.license.remove"),
    message: t("panel.license.removeConfirm"),
    tone: "neutral",
    confirmLabel: t("panel.license.remove"),
    action: async () => {
      busy.value = true;
      try {
        status.value = await removeLicense();
        showNotice(t("license.deactivated"), false);
      } catch (error) {
        showError(error);
      } finally {
        busy.value = false;
      }
    }
  });
}

async function copyFingerprint() {
  const value = status.value?.fingerprint ?? "";
  if (!value) {
    return;
  }

  // Kopyalanmadıysa "kopyalandı" DEMEYİZ: `copyToClipboard` gerçekten kopyalayıp kopyalamadığını döndürür.
  const copied = await copyToClipboard(value);
  showNotice(copied ? t("panel.common.copied") : t("panel.common.copyFailed"), !copied);
}

function formatDate(value: string) {
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? value : date.toLocaleString();
}
</script>

<style scoped>
.license-section {
  align-content: start;
}

.section-head {
  display: flex;
  justify-content: space-between;
  align-items: flex-start;
  gap: 10px;
  flex-wrap: wrap;
}

.section-head h4 {
  margin: 0;
}

.license-message {
  margin: 0;
}

/* Bilgi/uyarı kutuları: başlık + açıklama tek blok. `.notice` zaten temanın kutu stilini verir. */
.license-info,
.license-warn {
  display: grid;
  gap: 4px;
}

.license-warn {
  border-color: var(--warn-border, var(--panel-border));
}

.license-activate,
.license-fingerprint {
  display: grid;
  gap: 10px;
}

/* 64 karakterlik özet dar ekranda SAYFAYI değil kendi kutusunu kaydırır. */
.fingerprint-value {
  overflow-x: auto;
  padding: 8px 10px;
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-lg);
  background: var(--surface-1);
}

.fingerprint-value code {
  font-family: ui-monospace, "Cascadia Mono", "Consolas", monospace;
  font-size: 0.8rem;
  white-space: nowrap;
}

.license-source-warn {
  color: var(--text-muted);
}

.row-actions button {
  width: auto;
}
</style>
