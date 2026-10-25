<template>
  <section class="panel module-shell automation-panel">
    <div class="module-head">
      <div>
        <h3>{{ t("panel.automation.title") }}</h3>
        <p class="meta">{{ t("panel.automation.subtitle") }}</p>
      </div>
      <button class="secondary compact" :disabled="loading || busy || testingRemote" @click="refresh">
        {{ t("panel.automation.refresh") }}
      </button>
    </div>

    <div v-if="notice" :class="['notice', 'page-notice', 'automation-notice', { error: noticeError }]" :role="noticeError ? 'alert' : 'status'">
      <p>{{ notice }}</p>
      <button type="button" class="secondary compact notice-close" :aria-label="t('panel.common.dismissNotice')" @click="notice = ''">×</button>
    </div>
    <RunningTaskBanner
      :running="backupRunning"
      :label="localBackup.running.value ? localBackup.label.value : serverBackup.label.value"
      :elapsed-text="localBackup.running.value ? localBackup.elapsedText.value : serverBackup.elapsedText.value"
      :hint="localBackup.running.value ? '' : t('panel.runningJob.resumedHint')"
      :interrupted-label="serverBackup.interruptedText.value"
      @dismiss="serverBackup.dismissInterrupted" />

    <!-- Ayarlar sunucudan gelmeden bütün otomasyon anahtarları varsayılan (kapalı) görünüyordu;
         yönetici "hiçbir otomasyon açık değil" sanıp yanlış karar veriyordu. -->
    <p v-if="!settingsLoaded" class="empty-state" role="status" aria-live="polite">
      {{ t("panel.common.loading") }}
    </p>

    <template v-else>
    <div class="form-surface">
      <div class="section-head">
        <div>
          <h3>{{ t("panel.automation.backupSection") }}</h3>
          <p class="meta">{{ t("panel.automation.backupHint") }}</p>
        </div>
        <label class="toggle-field">
          <input v-model="form.autoBackupEnabled" type="checkbox" />
          <span>{{ t("panel.automation.enabled") }}</span>
        </label>
      </div>

      <div v-if="form.autoBackupEnabled" class="form-grid">
        <!--
          ZAMANLAMA. Eskiden burada YALNIZCA "kaç saatte bir" vardı ve yedeğin SAATİ ilk elle
          tetiklenen ana kilitleniyordu (17 Ağustos 23:50'de bir kez "Şimdi yedekle" denince
          gecelik yedek 23:50'ye taşındı). Artık saat seçilebiliyor; alanlar iki ekranda ortak
          bileşenden geliyor.
        -->
        <BackupScheduleFields :plan="schedulePlan" />
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.automation.retentionDays") }}</span>
          <input v-model.number="form.backupRetentionDays" type="number" min="0" max="3650" />
          <span class="field-hint">{{ t("panel.automation.retentionHint") }}</span>
        </label>
        <label class="toggle-field">
          <input v-model="form.backupSites" type="checkbox" />
          <span>{{ t("panel.automation.backupSites") }}</span>
        </label>
        <label class="toggle-field">
          <input v-model="form.backupDatabases" type="checkbox" />
          <span>{{ t("panel.automation.backupDatabases") }}</span>
        </label>
        <label class="toggle-field">
          <input v-model="form.backupMail" type="checkbox" />
          <span>{{ t("panel.automation.backupMail") }}</span>
        </label>
        <label class="toggle-field">
          <input v-model="form.backupPanelConfig" type="checkbox" />
          <span>{{ t("panel.automation.backupPanelConfig") }}</span>
        </label>
      </div>

      <div v-if="form.autoBackupEnabled" class="section-head">
        <div>
          <h3>{{ t("panel.automation.remoteSection") }}</h3>
          <p class="meta">{{ t("panel.automation.remoteHint") }}</p>
        </div>
      </div>

      <div v-if="form.autoBackupEnabled" class="form-grid">
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.automation.remoteTarget") }}</span>
          <!--
            SEÇENEK ADLARI GERÇEĞİ SÖYLEMELİ. Eskiden liste "Yalnızca yerel disk / FTP / S3 /
            WebDAV" diyordu ve kullanıcı bunu "ya yerel YA uzak" diye okuyordu -- oysa uzak hedef
            seçildiğinde yedek ÖNCE yerel diske alınır, sonra oraya kopyalanır: ikisi birden.
            "İki yere birden nasıl yedeklerim, hangi ayarı seçmeliyim?" sorusu tam da bu yanlış
            adlandırmadan doğdu; davranış doğruydu, etiket yanlıştı.
          -->
          <select v-model="form.backupTargetType">
            <option value="local">{{ t("panel.automation.remoteLocal") }}</option>
            <option value="ftp">{{ t("panel.automation.targetFtpBoth") }}</option>
            <option value="s3">{{ t("panel.automation.targetS3Both") }}</option>
            <option value="webdav">{{ t("panel.automation.targetWebdavBoth") }}</option>
          </select>
          <small v-if="form.backupTargetType !== 'local'" class="field-hint">
            {{ t("panel.automation.dualDestinationHint") }}
          </small>
        </label>
        <label v-if="form.backupTargetType !== 'local'" class="stacked-field">
          <span class="field-label">{{ t("panel.automation.remoteUrl") }}</span>
          <input v-model.trim="form.backupRemoteUrl" :placeholder="remoteUrlPlaceholder" />
        </label>
        <label v-if="form.backupTargetType !== 'local'" class="stacked-field">
          <span class="field-label">{{ form.backupTargetType === 's3' ? t("panel.automation.remoteAccessKey") : t("panel.automation.remoteUser") }}</span>
          <input v-model.trim="form.backupRemoteUserName" autocomplete="off" />
        </label>
        <label v-if="form.backupTargetType !== 'local'" class="stacked-field">
          <span class="field-label">{{ form.backupTargetType === 's3' ? t("panel.automation.remoteSecretKey") : t("panel.automation.remotePassword") }}</span>
          <PasswordField
            v-model="remotePassword"
            autocomplete="new-password"
            :placeholder="form.backupRemotePasswordSet ? t('panel.automation.remotePasswordKept') : ''" />
        </label>
        <label v-if="form.backupTargetType === 's3'" class="stacked-field">
          <span class="field-label">{{ t("panel.automation.remoteRegion") }}</span>
          <input v-model.trim="form.backupRemoteRegion" placeholder="eu-central-1" />
        </label>
      </div>

      <div v-if="form.backupTargetType !== 'local'" class="row-actions remote-test">
        <button class="secondary compact" type="button" :disabled="testingRemote || busy" @click="testRemote">
          {{ testingRemote ? t("panel.automation.remoteTesting") : t("panel.automation.remoteTest") }}
        </button>
        <span v-if="remoteTestResult" :class="['status-pill', remoteTestResult.success ? 'ok' : 'warn']">
          {{ remoteTestResult.success ? t("panel.automation.remoteTestOk") : t(remoteTestResult.message) }}
        </span>
      </div>
      <details v-if="remoteTestResult && !remoteTestResult.success && remoteTestResult.details" class="notice-details">
        <summary>{{ t("panel.common.technicalDetails") }}</summary>
        <pre>{{ remoteTestResult.details }}</pre>
      </details>

      <!-- Saat SUNUCUYA aittir; panele başka bir saat diliminden bağlanan yönetici bunu bilmeli.
           Bilgi sunucudan gelmediyse satır hiç çizilmez (uydurma dilim gösterilmez). -->
      <p v-if="form.autoBackupEnabled && scheduleIsCalendar && serverTimeZoneLabel" class="field-hint">
        {{ t("panel.schedule.serverTimeHint", { zone: serverTimeZoneLabel, now: serverLocalTimeLabel }) }}
      </p>
      <p v-if="form.autoBackupEnabled && scheduleIsCalendar" class="field-hint">
        {{ t("panel.schedule.catchUpHint") }}
      </p>

      <p class="field-hint">
        {{ t("panel.automation.lastRun") }}:
        <strong>{{ formatDate(status?.lastBackupRunUtc) }}</strong>
        · {{ formatStatus(status?.lastBackupStatus) }}
        <template v-if="status?.nextBackupRunUtc && form.autoBackupEnabled">
          · {{ t("panel.automation.nextRun") }}: {{ formatDate(status.nextBackupRunUtc) }}
        </template>
      </p>
      <p v-if="status?.lastBackupDetails" class="cell-muted details-line">{{ automationDetails(status.lastBackupDetails, t) }}</p>
    </div>

    <div class="form-surface">
      <div class="section-head">
        <div>
          <h3>{{ t("panel.automation.sslSection") }}</h3>
          <p class="meta">{{ t("panel.automation.sslHint") }}</p>
        </div>
        <label class="toggle-field">
          <input v-model="form.autoSslRenewEnabled" type="checkbox" />
          <span>{{ t("panel.automation.enabled") }}</span>
        </label>
      </div>

      <p class="meta ssl-auto-hint">{{ t("panel.automation.sslAutoHint") }}</p>

      <p class="field-hint">
        {{ t("panel.automation.lastRun") }}:
        <strong>{{ formatDate(status?.lastSslRunUtc) }}</strong>
        · {{ formatStatus(status?.lastSslStatus) }}
        <template v-if="status?.nextSslRunUtc && form.autoSslRenewEnabled">
          · {{ t("panel.automation.nextRun") }}: {{ formatDate(status.nextSslRunUtc) }}
        </template>
      </p>
      <p v-if="status?.lastSslDetails" class="cell-muted details-line">{{ automationDetails(status.lastSslDetails, t) }}</p>
    </div>

    <div class="form-surface">
      <div class="section-head">
        <div>
          <h3>{{ t("panel.automation.logSection") }}</h3>
          <p class="meta">{{ t("panel.automation.logHint") }}</p>
        </div>
        <label class="toggle-field">
          <input v-model="form.logCleanupEnabled" type="checkbox" />
          <span>{{ t("panel.automation.enabled") }}</span>
        </label>
      </div>

      <div v-if="form.logCleanupEnabled" class="form-grid">
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.automation.logRetentionDays") }}</span>
          <input v-model.number="form.logRetentionDays" type="number" min="1" max="3650" />
        </label>
      </div>

      <p class="field-hint">
        {{ t("panel.automation.lastRun") }}:
        <strong>{{ formatDate(status?.lastLogCleanupRunUtc) }}</strong>
        · {{ formatStatus(status?.lastLogCleanupStatus) }}
      </p>
      <p v-if="status?.lastLogCleanupDetails" class="cell-muted details-line">{{ automationDetails(status.lastLogCleanupDetails, t) }}</p>
    </div>

    <!-- Yazılım güncellemeleri: kontrol varsayılan açık, KURULUM kararı yöneticinin. -->
    <div class="form-surface">
      <div class="section-head">
        <div>
          <h3>{{ t("panel.automation.updateSection") }}</h3>
          <p class="meta">{{ t("panel.automation.updateHint") }}</p>
        </div>
        <label class="toggle-field">
          <input v-model="form.updateCheckEnabled" type="checkbox" />
          <span>{{ t("panel.automation.enabled") }}</span>
        </label>
      </div>

      <label v-if="form.updateCheckEnabled" class="toggle-field">
        <input v-model="form.autoApplyComponentUpdates" type="checkbox" />
        <span>{{ t("panel.automation.autoApplyUpdates") }}</span>
      </label>
      <p v-if="form.updateCheckEnabled && form.autoApplyComponentUpdates" class="field-hint">
        {{ t("panel.automation.autoApplyWarning") }}
      </p>

      <p class="field-hint">
        {{ t("panel.automation.lastRun") }}:
        <strong>{{ formatDate(status?.lastUpdateCheckRunUtc) }}</strong>
        · {{ formatStatus(status?.lastUpdateCheckStatus) }}
        <span v-if="status?.pendingComponentUpdates">
          · {{ t("panel.automation.pendingUpdates", { count: status.pendingComponentUpdates }) }}
        </span>
      </p>
      <p v-if="status?.lastUpdateCheckDetails" class="cell-muted details-line">{{ automationDetails(status.lastUpdateCheckDetails, t) }}</p>
    </div>

    <div class="form-surface">
      <div class="section-head">
        <div>
          <h3>{{ t("panel.automation.alertSection") }}</h3>
          <p class="meta">{{ t("panel.automation.alertHint") }}</p>
        </div>
        <label class="toggle-field">
          <input v-model="form.alertsEnabled" type="checkbox" />
          <span>{{ t("panel.automation.enabled") }}</span>
        </label>
      </div>

      <div v-if="form.alertsEnabled" class="form-grid">
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.automation.diskThreshold") }}</span>
          <input v-model.number="form.diskUsageThresholdPercent" type="number" min="50" max="99" />
        </label>
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.automation.certWarningDays") }}</span>
          <input v-model.number="form.certExpiryWarningDays" type="number" min="1" max="90" />
        </label>
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.automation.memoryThreshold") }}</span>
          <input v-model.number="form.memoryUsageThresholdPercent" type="number" min="50" max="99" />
        </label>
        <label class="toggle-field">
          <input v-model="form.serviceDownAlertsEnabled" type="checkbox" />
          <span>{{ t("panel.automation.serviceDownAlerts") }}</span>
        </label>
      </div>
      <p class="field-hint">{{ t("panel.automation.alertChannelHint") }}</p>
    </div>

    <div class="row-actions">
      <button class="compact" :disabled="busy || testingRemote" @click="save">{{ t("panel.common.save") }}</button>
      <button class="secondary compact" :disabled="busy || testingRemote || backupRunning" @click="runBackupNow">
        {{ t("panel.automation.runBackupNow") }}
      </button>
      <button class="secondary compact" :disabled="busy || testingRemote" @click="runSslNow">
        {{ t("panel.automation.runSslNow") }}
      </button>
    </div>
    </template>
  </section>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from "vue";
import { useI18n } from "vue-i18n";
import PasswordField from "../components/PasswordField.vue";
import BackupScheduleFields from "../components/BackupScheduleFields.vue";
import RunningTaskBanner from "../components/RunningTaskBanner.vue";
import { getBackupRunningJobs } from "../api/backup";
import { runningJobLabel, useRunningTask, useServerRunningTask } from "../composables/useRunningTask";
import { bosProgram, type BackupSchedulePlan } from "../api/schedule";
import {
  getAutomation,
  runAutomationBackup,
  runAutomationSsl,
  updateAutomation,
  testRemoteBackup,
  type AutomationSettings,
  type AutomationStatus,
  type BackupRemoteTestResult
} from "../api/automation";
import { extractApiMessage } from "../stores/auth";
import { formatDateTime } from "../utils/datetime";
import { automationDetails } from "../utils/automationDetails";
import { getSessionEpoch } from "../api/client";

const { t, locale } = useI18n();
const session = getSessionEpoch();
let disposed = false;
let statusRequest = 0;
const isActive = () => !disposed && session === getSessionEpoch();
onBeforeUnmount(() => { disposed = true; statusRequest++; });
const status = ref<AutomationStatus | null>(null);
const loading = ref(false);
// "Henüz bilmiyorum" ile "kapalı" ekranda ayırt edilemiyordu; ayarlar gelene kadar kartları gizle.
const settingsLoaded = ref(false);
const busy = ref(false);
const localBackup = useRunningTask();
const serverBackup = useServerRunningTask({
  fetchStatus: async () => {
    if (!isActive()) {
      serverBackup.stop();
      throw new Error("Automation view is inactive");
    }
    const result = await getBackupRunningJobs();
    if (!isActive()) serverBackup.stop();
    const isAutomation = (job: { kind: string; title: string }) => job.kind === "backup" && job.title === "automation";
    return { ...result, running: result.running.filter(isAutomation), interrupted: result.interrupted.filter(isAutomation) };
  },
  label: (job) => runningJobLabel(t, job),
  interruptedLabel: (job) => t("panel.runningJob.interrupted", { job: runningJobLabel(t, job), date: formatDate(job.startedAtUtc) })
});
const backupRunning = computed(() => localBackup.running.value || serverBackup.running.value);
const notice = ref("");
const noticeError = ref(false);
let noticeRevision = 0;
let refreshErrorNotice: number | undefined;
const remotePassword = ref("");
const testingRemote = ref(false);
const remoteTestResult = ref<BackupRemoteTestResult | null>(null);
const mayStart = () => isActive() && settingsLoaded.value && !busy.value && !testingRemote.value;
/**
 * Program alanları ORTAK biçimde tutulur (bkz. api/schedule.ts): alan bileşeni iki ekranda da
 * aynı nesneyle çalışsın diye. Yükleme ve kaydetme sırasında `form` ile eşlenir.
 */
const schedulePlan = reactive<BackupSchedulePlan>(bosProgram());
const scheduleIsCalendar = computed(() => schedulePlan.mode !== "interval");
const serverTimeZoneLabel = computed(
  () => (locale.value === "en" ? status.value?.serverTimeZoneId : status.value?.serverTimeZoneDisplayName) || status.value?.serverTimeZoneId || ""
);
const serverLocalTimeLabel = computed(() => (status.value?.serverLocalTime || "").replace("T", " "));

const form = reactive<AutomationSettings>({
  autoBackupEnabled: false,
  backupScheduleMode: "interval",
  backupIntervalHours: 24,
  backupScheduleHour: 3,
  backupScheduleMinute: 0,
  backupScheduleDayOfWeek: 1,
  backupScheduleDayOfMonth: 1,
  backupRetentionDays: 14,
  backupSites: true,
  backupDatabases: false,
  backupMail: false,
  backupPanelConfig: false,
  backupDatabaseEngines: [],
  // Backend varsayilani ile AYNI (AutomationService.AutoSslRenewEnabled = true). Form zaten
  // Object.assign(form, next.settings) ile sunucudan dolduruluyor; bu deger yalnizca ilk boyama
  // aninda gorunur, ama yanlis olmasi kullaniciya bir an "kapali" gostermek demekti.
  autoSslRenewEnabled: true,
  sslRenewIntervalHours: 24,
  alertsEnabled: false,
  diskUsageThresholdPercent: 90,
  certExpiryWarningDays: 14,
  memoryUsageThresholdPercent: 90,
  serviceDownAlertsEnabled: true,
  backupTargetType: "local",
  backupRemoteUrl: "",
  backupRemoteUserName: "",
  backupRemoteRegion: "",
  backupRemotePasswordSet: false,
  logCleanupEnabled: false,
  logRetentionDays: 30,
  updateCheckEnabled: true,
  autoApplyComponentUpdates: false
});

// Requests may report their measured result without replacing a draft edited in the meantime.
let draftRevision = 0;
watch([form, schedulePlan, remotePassword], () => { draftRevision++; }, { deep: true, flush: "sync" });

let remoteRevision = 0;
watch(() => [form.backupTargetType, form.backupRemoteUrl, form.backupRemoteUserName,
  form.backupRemoteRegion, form.backupRemotePasswordSet, remotePassword.value], () => {
  remoteRevision++;
  remoteTestResult.value = null;
}, { flush: "sync" });

const remoteUrlPlaceholder = computed(() => {
  switch (form.backupTargetType) {
    case "ftp":
      return "ftp://server.example/backups/";
    case "s3":
      return "https://s3.eu-central-1.amazonaws.com/bucket-name/backups/";
    case "webdav":
      return "https://storage.example.com/backups/";
    default:
      return "";
  }
});

onMounted(refresh);

async function refresh() {
  if (!isActive() || busy.value || testingRemote.value) return;
  const request = ++statusRequest;
  const draft = draftRevision;
  const isCurrent = () => isActive() && request === statusRequest;
  loading.value = true;
  try {
    await serverBackup.start();
    if (!isCurrent()) return;
    const result = await getAutomation();
    if (!isCurrent()) return;
    applyStatus(result, draft);
    if (refreshErrorNotice === noticeRevision) showNotice("", false);
    refreshErrorNotice = undefined;
  } catch (error) {
    if (isCurrent()) {
      showNotice(resolveApiMessage(error), true);
      refreshErrorNotice = noticeRevision;
    }
  } finally {
    if (isCurrent()) loading.value = false;
  }
}

async function save() {
  await run(async () => {
    const draft = draftRevision;
    const remoteContext = remoteRevision;
    const submittedPassword = remotePassword.value;
    const result = await updateAutomation({
      ...form,
      backupDatabaseEngines: [...form.backupDatabaseEngines],
      // Ortak program biçimi burada API alan adlarına çevrilir.
      backupScheduleMode: schedulePlan.mode,
      backupIntervalHours: schedulePlan.intervalHours,
      backupScheduleHour: schedulePlan.hour,
      backupScheduleMinute: schedulePlan.minute,
      backupScheduleDayOfWeek: schedulePlan.dayOfWeek,
      backupScheduleDayOfMonth: schedulePlan.dayOfMonth,
      backupScheduleSecondHour: schedulePlan.secondHour ?? null,
      backupScheduleSecondMinute: schedulePlan.secondMinute ?? 0,
      backupRemotePassword: submittedPassword
    });
    if (!isActive()) return;
    const clearPassword = remoteRevision === remoteContext && remotePassword.value === submittedPassword;
    applyStatus(result, draft);
    if (clearPassword) remotePassword.value = "";
    showNotice(t("panel.automation.saved"), false);
  });
}

async function testRemote() {
  if (!mayStart()) return;
  const context = remoteRevision;
  const isCurrent = () => isActive() && context === remoteRevision;
  testingRemote.value = true;
  remoteTestResult.value = null;
  try {
    const result = await testRemoteBackup({
      targetType: form.backupTargetType,
      remoteUrl: form.backupRemoteUrl,
      remoteUserName: form.backupRemoteUserName,
      remotePassword: remotePassword.value,
      remoteRegion: form.backupRemoteRegion
    });
    if (isCurrent()) remoteTestResult.value = result;
  } catch (error) {
    if (isCurrent()) remoteTestResult.value = { success: false, message: extractApiMessage(error), details: "" };
  } finally {
    if (isActive()) testingRemote.value = false;
  }
}

async function runBackupNow() {
  if (!mayStart() || backupRunning.value) return;
  localBackup.begin(t("panel.automation.backupSection"));
  try {
    await run(async () => {
      const result = await runAutomationBackup();
      if (!isActive()) return;
      applyStatus(result);
      reportRunResult(result.lastBackupStatus, result.lastBackupDetails, "panel.automation.backupStarted");
    });
  } finally {
    localBackup.end();
    if (isActive()) await serverBackup.start();
  }
}

async function runSslNow() {
  await run(async () => {
    const result = await runAutomationSsl();
    if (!isActive()) return;
    applyStatus(result);
    reportRunResult(result.lastSslStatus, result.lastSslDetails, "panel.automation.sslStarted");
  });
}

function reportRunResult(outcome: string | undefined, details: string | undefined, successKey: string) {
  const measured = outcome?.trim().toLowerCase();
  const completed = measured === "completed";
  const heading = completed
    ? t(successKey)
    : measured === "failed" || measured === "completed_with_errors"
      ? t(`panel.automation.statusValue.${measured}`)
      : t("panel.automation.resultUnconfirmed");
  const detail = details?.trim() ? automationDetails(details, t) : "";
  showNotice([heading, detail].filter(Boolean).join("\n"), !completed);
}

async function run(operation: () => Promise<void>) {
  if (!mayStart()) return;
  statusRequest++;
  loading.value = false;
  busy.value = true;
  try {
    await operation();
  } catch (error) {
    if (isActive()) showNotice(resolveApiMessage(error), true);
  } finally {
    if (isActive()) busy.value = false;
  }
}

function applyStatus(next: AutomationStatus, draft?: number) {
  status.value = next;
  settingsLoaded.value = true;
  // Running backup/SSL does not save settings. Its response must not reset unsaved input.
  if (draft === undefined || draft !== draftRevision) return;
  Object.assign(form, { ...next.settings, backupDatabaseEngines: [...(next.settings.backupDatabaseEngines ?? [])] });
  // Sunucu değerleri NORMALİZE dönüyor (mod/saat/gün sınırları orada uygulanıyor); ekran onları
  // olduğu gibi alır, kendi kuralını uydurmaz.
  Object.assign(schedulePlan, {
    mode: next.settings.backupScheduleMode,
    intervalHours: next.settings.backupIntervalHours,
    hour: next.settings.backupScheduleHour,
    minute: next.settings.backupScheduleMinute,
    dayOfWeek: next.settings.backupScheduleDayOfWeek,
    dayOfMonth: next.settings.backupScheduleDayOfMonth,
    secondHour: next.settings.backupScheduleSecondHour ?? null,
    secondMinute: next.settings.backupScheduleSecondMinute ?? 0
  });
}

/** Tarih bicimi panelin geri kalaniyla AYNI (bkz. utils/datetime.ts). */
function formatDate(value: string | null | undefined) {
  return formatDateTime(value, "—");
}

function formatStatus(value: string | null | undefined) {
  if (!value) return "—";
  const key = `panel.automation.statusValue.${value}`;
  const translated = t(key);
  return translated === key ? value : translated;
}

function resolveApiMessage(error: unknown) {
  const key = extractApiMessage(error);
  const translated = t(key);
  return translated === key ? key : translated;
}

function showNotice(message: string, isError: boolean) {
  noticeRevision++;
  notice.value = message;
  noticeError.value = isError;
}
</script>

<style scoped>
.automation-panel {
  align-content: start;
  display: grid;
  gap: 12px;
}

.details-line {
  white-space: pre-line;
}

.automation-notice {
  display: grid;
  grid-template-columns: minmax(0, 1fr) auto;
  align-items: start;
  gap: var(--sp-8);
}

.automation-notice p { margin: 0; }
.notice-close { width: 28px; height: 28px; padding: 0; color: inherit; }

.row-actions button {
  width: auto;
}

@media (max-width: 720px) {
  .section-head {
    flex-direction: column;
  }
}
</style>
