<template>
  <section class="subscription-card">
    <div class="section-head">
      <div>
        <h4>{{ t("panel.subscription.title") }}</h4>
        <p class="meta">{{ t("panel.subscription.hint") }}</p>
      </div>
      <!-- ASKI DURUMU EN GÖRÜNÜR YERDE: müşteri siteleri neden kapalı diye aramadan önce
           cevabı burada görmeli. -->
      <span v-if="usage" :class="['status-pill', usage.isSuspended ? 'danger' : 'ok']">
        {{ usage.isSuspended ? t("panel.subscription.suspended") : t("panel.subscription.active") }}
      </span>
    </div>

    <p v-if="error" class="notice error" role="alert">{{ error }}</p>
    <p v-else-if="loading && !usage" class="empty-state">{{ t("panel.common.loading") }}</p>

    <template v-else-if="usage">
      <p v-if="usage.isSuspended" class="notice error" role="alert">
        {{ t(`panel.subscription.reason.${usage.suspendReason || "manual"}`) }}
      </p>

      <div class="summary-strip">
        <!-- TRAFİK: ölçülemediyse sayı GÖSTERİLMEZ. Sıfır yazmak "trafik yok" sanılmasına yol
             açardı; oysa ölçemediğimiz bir şeyi ölçtük gibi sunmak olurdu. -->
        <div class="summary-item">
          <span>{{ t("panel.subscription.traffic") }}</span>
          <strong v-if="!usage.trafficMeasured" class="unmeasured">{{ t("panel.subscription.notMeasured") }}</strong>
          <strong v-else>
            {{ formatBytes(usage.trafficBytesThisMonth) }}
            <small v-if="usage.trafficLimitGb > 0"> / {{ usage.trafficLimitGb }} GB</small>
            <small v-else> / {{ t("panel.subscription.unlimited") }}</small>
          </strong>
          <div v-if="usage.trafficMeasured && usage.trafficLimitGb > 0" class="usage-bar">
            <div class="usage-fill" :class="trafficClass" :style="{ width: trafficPercent + '%' }"></div>
          </div>
        </div>

        <div class="summary-item">
          <span>{{ t("panel.subscription.sites") }}</span>
          <strong>{{ usage.siteCount }} <small>/ {{ usage.maxSites > 0 ? usage.maxSites : t("panel.subscription.unlimited") }}</small></strong>
        </div>

        <div class="summary-item">
          <span>{{ t("panel.subscription.disk") }}</span>
          <strong>
            {{ usage.diskUsageMb }} MB
            <small>/ {{ usage.maxDiskMb > 0 ? usage.maxDiskMb + " MB" : t("panel.subscription.unlimited") }}</small>
            <!-- Kesilen ölçüm ALT SINIRDIR. Burası kullanıcının kotasına ne kadar yaklaştığını
                 okuduğu yer; eksik bir sayıyı tam gibi göstermek, kotasının bittiğini fark
                 etmemesine yol açar. Sebep anahtarı varsa ipucunda o gösterilir (motorun kendi
                 anahtarı, tr/en'de çevrili); yoksa genel açıklama. -->
            <span
              v-if="!usage.diskMeasured"
              class="status-pill warn"
              :title="usage.diskUsageTruncatedReasonKey
                ? t(usage.diskUsageTruncatedReasonKey)
                : t('users.disk_usage_partial_hint')">
              {{ t("users.disk_usage_partial") }}
            </span>
          </strong>
        </div>

        <div class="summary-item">
          <span>{{ t("panel.subscription.expiry") }}</span>
          <strong :class="{ 'unmeasured': usage.isExpired }">
            {{ usage.expiresAtUtc ? formatDate(usage.expiresAtUtc) : t("panel.subscription.noExpiry") }}
          </strong>
        </div>
      </div>

      <!-- Yönetim yalnız yönetici/bayide: müşteri kendi kotasını değiştiremez. -->
      <div v-if="manageable" class="settings-rows">
        <label class="setting-row">
          <span class="field-label">{{ t("panel.subscription.plan") }}</span>
          <select v-model="selectedPlanId" class="compact-select" :disabled="busy" @change="savePlan">
            <option value="">{{ t("panel.subscription.customLimits") }}</option>
            <option v-for="plan in plans" :key="plan.id" :value="plan.id">{{ plan.name }}</option>
          </select>
          <!-- Üçüncü hücre TEK öğe kalmalı: `.setting-row` üç sütunlu bir ızgara, dördüncü bir
               çocuk eklemek satırı bozardı. Hata ile ipucu bu yüzden aynı hücrede. -->
          <span class="plan-hint-cell">
            <small v-if="plansError" class="notice error plan-load-error" role="alert">{{ plansError }}</small>
            <small class="field-hint">{{ t("panel.subscription.planHint") }}</small>
          </span>
        </label>

        <label class="setting-row">
          <span class="field-label">{{ t("panel.subscription.expiry") }}</span>
          <input v-model="expiryInput" type="date" :disabled="busy" @change="saveExpiry" />
          <small class="field-hint">{{ t("panel.subscription.expiryHint") }}</small>
        </label>

        <div class="row-actions">
          <button v-if="!usage.isSuspended" class="danger compact" :disabled="busy" @click="suspend">
            {{ t("panel.subscription.suspendAction") }}
          </button>
          <button v-else class="compact" :disabled="busy" @click="resume">
            {{ t("panel.subscription.resumeAction") }}
          </button>
        </div>
      </div>
    </template>
  </section>
</template>

<script setup lang="ts">
import { computed, onMounted, ref, watch } from "vue";
import { useI18n } from "vue-i18n";
import {
  getMySubscription,
  getSubscription,
  resumeSubscription,
  setSubscriptionExpiry,
  setSubscriptionPlan,
  suspendSubscription,
  type SubscriptionUsage
} from "../api/subscriptions";
import { getServicePlans, type ServicePlan } from "../api/servicePlans";
import { extractApiDetails, extractApiMessage } from "../stores/auth";
import { useConfirm } from "./ConfirmDialog.vue";
import { formatBytes as sharedFormatBytes } from "../utils/bytes";

const props = withDefaults(
  defineProps<{
    /** Boş ise giriş yapmış kullanıcının KENDİ aboneliği gösterilir. */
    userId?: string;
    /** Plan/süre/askı denetimleri gösterilsin mi (yönetici ve bayi). */
    manageable?: boolean;
  }>(),
  { userId: "", manageable: false }
);

const { t } = useI18n();
const { confirm } = useConfirm();

const usage = ref<SubscriptionUsage | null>(null);
const plans = ref<ServicePlan[]>([]);
/** Plan listesi ÇEKİLEMEDİ: boş liste ile karıştırılmaması için ayrı tutulur. */
const plansError = ref("");
const loading = ref(false);
const busy = ref(false);
const error = ref("");
const selectedPlanId = ref("");
const expiryInput = ref("");

const trafficPercent = computed(() => {
  if (!usage.value || usage.value.trafficLimitGb <= 0) {
    return 0;
  }
  const limit = usage.value.trafficLimitGb * 1024 * 1024 * 1024;
  return Math.min(100, Math.round((usage.value.trafficBytesThisMonth / limit) * 100));
});

const trafficClass = computed(() =>
  trafficPercent.value >= 100 ? "over" : trafficPercent.value >= 80 ? "warn" : "ok"
);

/**
 * Bayt bicimi panelin TEK kaynagindan gelir (bkz. utils/bytes.ts), ama burada "gauge"
 * hassasiyetiyle: bu sayi bir liste hucresi degil, KOTA gostergesidir. Ortak kuralin "10'un
 * ustunde ondalik gosterme" davranisi trafigi "15.5 GiB" yerine "16 GiB" yaziyordu; kotasina
 * 500 MiB kalmis bir musteri icin kaybolan tam da o bilgi. Yaninda ayni sayiyi anlatan bir de
 * doluluk cubugu var, yani sayinin isi cubugu SAYIYLA dogrulamak.
 */
function formatBytes(value: number) {
  return sharedFormatBytes(value, t, "gauge");
}

function formatDate(iso: string) {
  const tarih = new Date(iso);
  return Number.isNaN(tarih.getTime()) ? iso : tarih.toLocaleDateString();
}

function resolveApiMessage(err: unknown) {
  const anahtar = extractApiMessage(err);
  const cevrilmis = t(anahtar);
  const ayrinti = extractApiDetails(err);
  return (cevrilmis === anahtar ? anahtar : cevrilmis) + (ayrinti ? `\n\n${ayrinti}` : "");
}

async function load() {
  loading.value = true;
  error.value = "";
  try {
    usage.value = props.userId ? await getSubscription(props.userId) : await getMySubscription();
    selectedPlanId.value = usage.value.planId;
    // <input type="date"> yalnız yyyy-MM-dd kabul eder.
    expiryInput.value = usage.value.expiresAtUtc ? usage.value.expiresAtUtc.slice(0, 10) : "";

    if (props.manageable && plans.value.length === 0) {
      // HATA YUTULMUYOR. Boş liste "tanımlı plan yok" demektir; istek başarısız olduğunda da
      // aynı şeyi göstermek, yöneticiye var olan planları YOK diye sunmak olurdu — üstelik
      // plan seçemediği için müşteriyi bir plana bağlayamaz ve sebebini de göremezdi.
      try {
        plans.value = await getServicePlans();
        plansError.value = "";
      } catch (planHatasi) {
        plansError.value = resolveApiMessage(planHatasi);
      }
    }
  } catch (err) {
    error.value = resolveApiMessage(err);
  } finally {
    loading.value = false;
  }
}

async function calistir(islem: () => Promise<SubscriptionUsage>) {
  busy.value = true;
  error.value = "";
  try {
    usage.value = await islem();
    selectedPlanId.value = usage.value.planId;
    expiryInput.value = usage.value.expiresAtUtc ? usage.value.expiresAtUtc.slice(0, 10) : "";
  } catch (err) {
    error.value = resolveApiMessage(err);
    // Başarısız işlemde seçim GERİ ALINIR: aksi halde ekran, sunucuda olmayan bir durumu gösterirdi.
    if (usage.value) {
      selectedPlanId.value = usage.value.planId;
    }
  } finally {
    busy.value = false;
  }
}

const savePlan = () => calistir(() => setSubscriptionPlan(props.userId, selectedPlanId.value));
const saveExpiry = () =>
  calistir(() => setSubscriptionExpiry(props.userId, expiryInput.value ? new Date(expiryInput.value).toISOString() : null));
/**
 * Askıya alma MÜŞTERİNİN SİTELERİNİ DURDURUR (ziyaretçi siteye ulaşamaz). Onaysız yapılmaz;
 * onay metni sonucun ne olduğunu açıkça söyler. Geri alınabilir olduğu için `requireText` yok.
 */
async function suspend() {
  await confirm({
    title: t("panel.subscription.confirmSuspendTitle"),
    message: t("panel.subscription.confirmSuspendMessage"),
    confirmLabel: t("panel.subscription.suspendAction"),
    busyLabel: t("panel.confirm.working"),
    action: () => calistir(() => suspendSubscription(props.userId))
  });
}
const resume = () => calistir(() => resumeSubscription(props.userId));

watch(() => props.userId, () => void load());
onMounted(() => void load());
</script>

<style scoped>
.subscription-card {
  display: grid;
  gap: 10px;
}

.usage-bar {
  height: 6px;
  margin-top: 6px;
  border-radius: 999px;
  background: var(--ring-track);
  overflow: hidden;
}

.usage-fill {
  height: 100%;
  border-radius: 999px;
  transition: width 0.3s ease;
}

.usage-fill.ok { background: var(--ok-1); }
.usage-fill.warn { background: var(--warn-1); }
.usage-fill.over { background: var(--danger-1); }

/* Ölçülemeyen değer, ölçülmüş bir değer gibi görünmemeli. */
.unmeasured {
  color: var(--warn-text);
}

.summary-strip {
  grid-template-columns: repeat(auto-fit, minmax(160px, 1fr));
}

.plan-hint-cell {
  display: grid;
  gap: 4px;
  min-width: 0;
}

.plan-load-error {
  /* Ipucu degil HATA: kucuk gri yaziyla karismasin. */
  padding: 6px 8px;
}
</style>
