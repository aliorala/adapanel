<template>
  <section class="panel module-shell automation-panel">
    <div class="module-head">
      <div>
        <h3>{{ t("panel.automation.title") }}</h3>
        <p class="meta">{{ t("panel.automation.subtitle") }}</p>
      </div>
      <button class="secondary compact" :disabled="loading" @click="refresh">
        {{ t("panel.automation.refresh") }}
      </button>
    </div>

    <p v-if="notice" :class="['notice', { error: noticeError }]">{{ notice }}</p>

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
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.automation.frequency") }}</span>
          <select v-model.number="form.backupIntervalHours">
            <option :value="24">{{ t("panel.automation.freqDaily") }}</option>
            <option :value="168">{{ t("panel.automation.freqWeekly") }}</option>
            <option :value="720">{{ t("panel.automation.freqMonthly") }}</option>
          </select>
        </label>
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.automation.retentionDays") }}</span>
          <input v-model.number="form.backupRetentionDays" type="number" min="0" max="3650" />
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

      <div v-if="form.autoBackupEnabled" class="section-head remote-head">
        <div>
          <h3>{{ t("panel.automation.remoteSection") }}</h3>
          <p class="meta">{{ t("panel.automation.remoteHint") }}</p>
        </div>
      </div>

      <div v-if="form.autoBackupEnabled" class="form-grid">
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.automation.remoteTarget") }}</span>
          <select v-model="form.backupTargetType">
            <option value="local">{{ t("panel.automation.remoteLocal") }}</option>
            <option value="ftp">FTP</option>
            <option value="s3">S3 (AWS / MinIO / R2 / Wasabi)</option>
            <option value="webdav">WebDAV</option>
          </select>
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
          <input
            v-model="remotePassword"
            type="password"
            autocomplete="new-password"
            :placeholder="form.backupRemotePasswordSet ? t('panel.automation.remotePasswordKept') : ''" />
        </label>
        <label v-if="form.backupTargetType === 's3'" class="stacked-field">
          <span class="field-label">{{ t("panel.automation.remoteRegion") }}</span>
          <input v-model.trim="form.backupRemoteRegion" placeholder="eu-central-1" />
        </label>
      </div>

      <div v-if="form.backupTargetType !== 'local'" class="row-actions remote-test">
        <button class="secondary compact" type="button" :disabled="testingRemote" @click="testRemote">
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

      <p class="field-hint">
        {{ t("panel.automation.lastRun") }}:
        <strong>{{ formatDate(status?.lastBackupRunUtc) }}</strong>
        · {{ formatStatus(status?.lastBackupStatus) }}
        <template v-if="status?.nextBackupRunUtc && form.autoBackupEnabled">
          · {{ t("panel.automation.nextRun") }}: {{ formatDate(status.nextBackupRunUtc) }}
        </template>
      </p>
      <p v-if="status?.lastBackupDetails" class="cell-muted details-line">{{ status.lastBackupDetails }}</p>
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
      <p v-if="status?.lastSslDetails" class="cell-muted details-line">{{ status.lastSslDetails }}</p>
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
      <p v-if="status?.lastLogCleanupDetails" class="cell-muted details-line">{{ status.lastLogCleanupDetails }}</p>
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
      <p v-if="status?.lastUpdateCheckDetails" class="cell-muted details-line">{{ status.lastUpdateCheckDetails }}</p>
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
      <button class="compact" :disabled="busy" @click="save">{{ t("panel.common.save") }}</button>
      <button class="secondary compact" :disabled="busy" @click="runBackupNow">
        {{ t("panel.automation.runBackupNow") }}
      </button>
      <button class="secondary compact" :disabled="busy" @click="runSslNow">
        {{ t("panel.automation.runSslNow") }}
      </button>
    </div>
  </section>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref } from "vue";
import { useI18n } from "vue-i18n";
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

const { t } = useI18n();
const status = ref<AutomationStatus | null>(null);
const loading = ref(false);
const busy = ref(false);
const notice = ref("");
const noticeError = ref(false);
const remotePassword = ref("");
const testingRemote = ref(false);
const remoteTestResult = ref<BackupRemoteTestResult | null>(null);
const form = reactive<AutomationSettings>({
  autoBackupEnabled: false,
  backupIntervalHours: 24,
  backupRetentionDays: 14,
  backupSites: true,
  backupDatabases: false,
  backupMail: false,
  backupPanelConfig: false,
  backupDatabaseEngines: [],
  autoSslRenewEnabled: false,
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

const remoteUrlPlaceholder = computed(() => {
  switch (form.backupTargetType) {
    case "ftp":
      return "ftp://sunucu/yedekler/";
    case "s3":
      return "https://s3.eu-central-1.amazonaws.com/bucket-adi/yedekler/";
    case "webdav":
      return "https://depo.example.com/yedekler/";
    default:
      return "";
  }
});

onMounted(refresh);

async function refresh() {
  loading.value = true;
  try {
    applyStatus(await getAutomation());
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    loading.value = false;
  }
}

async function save() {
  await run(async () => {
    applyStatus(await updateAutomation({ ...form, backupRemotePassword: remotePassword.value }));
    remotePassword.value = "";
    showNotice(t("panel.automation.saved"), false);
  });
}

async function testRemote() {
  testingRemote.value = true;
  remoteTestResult.value = null;
  try {
    remoteTestResult.value = await testRemoteBackup({
      targetType: form.backupTargetType,
      remoteUrl: form.backupRemoteUrl,
      remoteUserName: form.backupRemoteUserName,
      remotePassword: remotePassword.value,
      remoteRegion: form.backupRemoteRegion
    });
  } catch (error) {
    remoteTestResult.value = { success: false, message: extractApiMessage(error), details: "" };
  } finally {
    testingRemote.value = false;
  }
}

async function runBackupNow() {
  await run(async () => {
    applyStatus(await runAutomationBackup());
    showNotice(t("panel.automation.backupStarted"), false);
  });
}

async function runSslNow() {
  await run(async () => {
    applyStatus(await runAutomationSsl());
    showNotice(t("panel.automation.sslStarted"), false);
  });
}

async function run(operation: () => Promise<void>) {
  busy.value = true;
  try {
    await operation();
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    busy.value = false;
  }
}

function applyStatus(next: AutomationStatus) {
  status.value = next;
  Object.assign(form, next.settings);
}

function formatDate(value: string | null | undefined) {
  return value ? new Date(value).toLocaleString() : "—";
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

.section-head {
  display: flex;
  justify-content: space-between;
  gap: 10px;
  align-items: flex-start;
  flex-wrap: wrap;
}

.toggle-field {
  display: flex;
  align-items: center;
  gap: 8px;
  color: var(--text-soft);
}

.toggle-field input {
  width: auto;
}

.details-line {
  white-space: pre-line;
}

.row-actions button {
  width: auto;
}

@media (max-width: 720px) {
  .section-head {
    flex-direction: column;
  }
}
</style>
