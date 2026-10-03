<template>
  <section class="panel module-shell settings-editor">
    <div class="module-head">
      <div>
        <h2>{{ t("panel.errorReports.title") }}</h2>
        <p>{{ t("panel.errorReports.subtitle") }}</p>
      </div>
      <button class="secondary compact" :disabled="Boolean(busy)" @click="loadAll">
        {{ t("panel.errorReports.refresh") }}
      </button>
    </div>

    <p v-if="notice" :class="['notice', 'page-notice', { error: noticeError }]" :role="noticeError ? 'alert' : 'status'">{{ notice }}</p>

    <!-- Ayarlar/kayıtlar gelmeden "kayıt yok" veya "kapalı" göstermek yanıltıcı olurdu:
         yönetici açık bir gönderimi kapalı sanabilir ya da biriken kayıtları göremeyebilir. -->
    <div v-if="loadError" class="settings-read-error" role="alert">
      <strong>{{ t("panel.errorReports.loadFailed") }}</strong>
      <p>{{ loadError }}</p>
      <p>{{ t("panel.errorReports.loadFailedHint") }}</p>
      <button class="secondary compact" :disabled="Boolean(busy)" @click="loadAll">{{ t("panel.common.retry") }}</button>
    </div>
    <p v-else-if="!loaded" class="empty-state" role="status" aria-live="polite">
      {{ t("panel.common.loading") }}
    </p>

    <template v-if="loaded">
      <p class="settings-caption">{{ t(settingsConfirmed ? "panel.errorReports.savedSettings" : "panel.errorReports.lastKnownSettings") }}</p>
      <div class="summary-strip">
        <div class="summary-item">
          <span>{{ t("panel.errorReports.autoSendStatus") }}</span>
          <strong>{{ savedSettings?.enabled ? t("panel.errorReports.on") : t("panel.errorReports.off") }}</strong>
        </div>
        <div class="summary-item">
          <span>{{ t("panel.errorReports.collectorStatus") }}</span>
          <strong>{{ savedSettings?.collectorUrl ? t("panel.errorReports.configured") : t("panel.errorReports.notConfigured") }}</strong>
        </div>
        <div class="summary-item">
          <span>{{ t("panel.errorReports.pendingCount") }}</span>
          <strong>{{ pendingCount }}</strong>
        </div>
        <div class="summary-item">
          <span>{{ t("panel.errorReports.lastSentAt") }}</span>
          <strong>{{ formatUtc(savedSettings?.lastSentAtUtc ?? null) }}</strong>
        </div>
      </div>

      <p v-if="hasUnsavedChanges" class="settings-draft-hint" role="status">{{ t("panel.errorReports.saveBeforeSend") }}</p>
      <!-- Onay ayarları -->
      <section class="form-surface settings-section">
        <div class="section-head">
          <div>
            <h3>{{ t("panel.errorReports.consentTitle") }}</h3>
            <p class="meta">{{ t("panel.errorReports.consentHint") }}</p>
          </div>
          <span v-if="savedSettings?.consentGrantedAtUtc" class="status-pill ok">
            {{ t("panel.errorReports.consentGrantedAt") }}: {{ formatUtc(savedSettings.consentGrantedAtUtc) }}
          </span>
        </div>

        <label class="check-field">
          <input v-model="settings.enabled" type="checkbox" />
          <span>{{ t("panel.errorReports.enable") }}</span>
        </label>

        <div class="form-grid">
          <label class="stacked-field">
            <span class="field-label">{{ t("panel.errorReports.collectorUrl") }}</span>
            <input v-model.trim="settings.collectorUrl" type="url" placeholder="https://example.com/error-reports" />
            <small class="field-hint">{{ t("panel.errorReports.collectorUrlHint") }}</small>
          </label>
          <label class="stacked-field">
            <span class="field-label">{{ t("panel.errorReports.batchSize") }}</span>
            <input v-model.number="settings.batchSize" type="number" min="1" max="50" />
          </label>
          <label class="stacked-field">
            <span class="field-label">{{ t("panel.errorReports.retentionDays") }}</span>
            <input v-model.number="settings.retentionDays" type="number" min="1" max="365" />
            <small class="field-hint">{{ t("panel.errorReports.retentionHint") }}</small>
          </label>
        </div>

        <p v-if="savedSettings?.lastError" class="notice error" role="alert">
          {{ t("panel.errorReports.lastError") }}: {{ savedSettings.lastError }}
        </p>

        <div class="row-actions">
          <button class="compact" :disabled="!canSave" @click="save">
            {{ busy === "save" ? t("panel.errorReports.saving") : t("panel.errorReports.save") }}
          </button>
          <button class="secondary compact" :disabled="busy === 'send' || !canSend" @click="sendNow">
            {{ busy === "send" ? t("panel.errorReports.sending") : t("panel.errorReports.sendNow") }}
          </button>
        </div>
        <p v-if="!canSend && !hasUnsavedChanges" class="field-hint">{{ t("panel.errorReports.sendDisabledHint") }}</p>
      </section>

      <!-- "Sessiz gönderim yok" sözünün arayüzdeki karşılığı: ne gidiyor, ne GİTMİYOR.
           Bu bölüm asla gizlenmez/katlanmaz; onay ekranıyla aynı listeyi anlatır. -->
      <section class="form-surface settings-section disclosure">
        <div class="section-head">
          <div>
            <h3>{{ t("panel.errorReports.disclosureTitle") }}</h3>
            <p class="meta">{{ t("panel.errorReports.disclosureHint") }}</p>
          </div>
        </div>

        <div class="disclosure-grid">
          <div class="disclosure-card sent">
            <h4>{{ t("panel.errorReports.sentFieldsTitle") }}</h4>
            <ul>
              <li>{{ t("panel.errorReports.sentField.panelVersion") }}</li>
              <li>{{ t("panel.errorReports.sentField.osVersion") }}</li>
              <li>{{ t("panel.errorReports.sentField.exceptionType") }}</li>
              <li>{{ t("panel.errorReports.sentField.message") }}</li>
              <li>{{ t("panel.errorReports.sentField.stackTrace") }}</li>
              <li>{{ t("panel.errorReports.sentField.routePattern") }}</li>
              <li>{{ t("panel.errorReports.sentField.occurrenceCount") }}</li>
              <li>{{ t("panel.errorReports.sentField.timestamps") }}</li>
              <li>{{ t("panel.errorReports.sentField.correlationId") }}</li>
            </ul>
          </div>

          <div class="disclosure-card not-sent">
            <h4>{{ t("panel.errorReports.notSentFieldsTitle") }}</h4>
            <ul>
              <li>{{ t("panel.errorReports.notSentField.requestBodies") }}</li>
              <li>{{ t("panel.errorReports.notSentField.passwords") }}</li>
              <li>{{ t("panel.errorReports.notSentField.customerNames") }}</li>
              <li>{{ t("panel.errorReports.notSentField.emails") }}</li>
              <li>{{ t("panel.errorReports.notSentField.ipAddresses") }}</li>
              <li>{{ t("panel.errorReports.notSentField.domainNames") }}</li>
              <li>{{ t("panel.errorReports.notSentField.filePaths") }}</li>
              <li>{{ t("panel.errorReports.notSentField.connectionStrings") }}</li>
            </ul>
          </div>
        </div>

        <p class="field-hint">{{ t("panel.errorReports.disclosureFooter") }}</p>
      </section>

      <!-- Kayıt listesi -->
      <section class="settings-section">
        <div class="section-head">
          <div>
            <h3>{{ t("panel.errorReports.listTitle") }}</h3>
            <p class="meta">{{ t("panel.errorReports.listHint") }}</p>
          </div>
        </div>

        <div class="data-table reports-table">
          <div class="data-row table-head report-row">
            <span>{{ t("panel.errorReports.column.lastSeen") }}</span>
            <span>{{ t("panel.errorReports.column.type") }}</span>
            <span>{{ t("panel.errorReports.column.message") }}</span>
            <span>{{ t("panel.errorReports.column.occurrences") }}</span>
            <span>{{ t("panel.errorReports.column.sent") }}</span>
            <span>{{ t("panel.errorReports.column.detail") }}</span>
          </div>

          <template v-for="report in reports" :key="report.id">
            <div class="data-row report-row">
              <span class="cell-muted report-time" :data-label="t('panel.errorReports.column.lastSeen')">{{ formatUtc(report.lastSeenAtUtc) }}</span>
              <span class="cell-main report-type" :data-label="t('panel.errorReports.column.type')">
                <strong>{{ shortType(report.exceptionType) }}</strong>
                <small>{{ report.routePattern || "-" }}</small>
              </span>
              <span class="cell-muted report-message" :data-label="t('panel.errorReports.column.message')">{{ truncate(report.message, 90) }}</span>
              <span :data-label="t('panel.errorReports.column.occurrences')">{{ report.occurrenceCount }}</span>
              <span :data-label="t('panel.errorReports.column.sent')">
                <span :class="['status-pill', report.sentAtUtc ? 'ok' : 'warn']">{{ report.sentAtUtc ? t("panel.errorReports.sentYes") : t("panel.errorReports.sentNo") }}</span>
              </span>
              <span class="row-actions">
                <button class="secondary compact" :aria-expanded="expandedId === report.id" :aria-controls="'report-detail-' + report.id" @click="toggleDetail(report.id)">
                  {{ expandedId === report.id ? t("panel.errorReports.hideDetail") : t("panel.errorReports.showDetail") }}
                </button>
              </span>
            </div>

            <div v-if="expandedId === report.id" :id="'report-detail-' + report.id" class="data-row detail-row">
              <div class="detail-body">
                <p class="field-hint">{{ t("panel.errorReports.detailMaskedHint") }}</p>
                <dl class="detail-grid">
                  <div>
                    <dt>{{ t("panel.errorReports.sentField.panelVersion") }}</dt>
                    <dd>{{ report.panelVersion || "-" }}</dd>
                  </div>
                  <div>
                    <dt>{{ t("panel.errorReports.sentField.osVersion") }}</dt>
                    <dd>{{ report.osVersion || "-" }}</dd>
                  </div>
                  <div>
                    <dt>{{ t("panel.errorReports.sentField.exceptionType") }}</dt>
                    <dd>{{ report.exceptionType || "-" }}</dd>
                  </div>
                  <div>
                    <dt>{{ t("panel.errorReports.sentField.routePattern") }}</dt>
                    <dd>{{ report.routePattern || "-" }}</dd>
                  </div>
                  <div>
                    <dt>{{ t("panel.errorReports.sentField.correlationId") }}</dt>
                    <dd>{{ report.correlationId || "-" }}</dd>
                  </div>
                  <div>
                    <dt>{{ t("panel.errorReports.fingerprint") }}</dt>
                    <dd>{{ report.fingerprint || "-" }}</dd>
                  </div>
                  <div>
                    <dt>{{ t("panel.errorReports.firstSeen") }}</dt>
                    <dd>{{ formatUtc(report.createdAtUtc) }}</dd>
                  </div>
                  <div>
                    <dt>{{ t("panel.errorReports.column.occurrences") }}</dt>
                    <dd>{{ report.occurrenceCount }}</dd>
                  </div>
                </dl>

                <h4>{{ t("panel.errorReports.sentField.message") }}</h4>
                <pre class="code-viewer detail-code">{{ report.message || "-" }}</pre>

                <h4>{{ t("panel.errorReports.sentField.stackTrace") }}</h4>
                <pre class="code-viewer detail-code">{{ report.stackTrace || "-" }}</pre>
              </div>
            </div>
          </template>

          <p v-if="reports.length === 0" class="empty-state">{{ t("panel.errorReports.empty") }}</p>
        </div>
      </section>
    </template>
  </section>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from "vue";
import { useI18n } from "vue-i18n";
import {
  getErrorReportingSettings,
  getErrorReports,
  saveErrorReportingSettings,
  sendPendingErrorReports,
  type ErrorReport,
  type ErrorReportingSettings
} from "../api/errorReports";
import { extractApiDetails, extractApiMessage, useAuthStore } from "../stores/auth";
import { formatDateTime } from "../utils/datetime";
import { getSessionEpoch } from "../api/client";

const { t } = useI18n();

const busy = ref("");
// Veri gelmeden liste "kayıt yok", ayarlar "kapalı" görünmemeli.
const loaded = ref(false);
const settingsConfirmed = ref(false);
const loadError = ref("");
const savedSettings = ref<ErrorReportingSettings | null>(null);
const auth = useAuthStore(), mountedEpoch = getSessionEpoch(), actor = auth.session?.user?.id, role = auth.session?.user?.role;
let disposed = false, draftRevision = 0;
const current = () => !disposed && getSessionEpoch() === mountedEpoch && auth.session?.user?.id === actor && auth.session?.user?.role === role;
const notice = ref("");
const noticeError = ref(false);
const reports = ref<ErrorReport[]>([]);
const expandedId = ref<number | null>(null);

const settings = reactive<ErrorReportingSettings>({
  enabled: false,
  collectorUrl: "",
  consentGrantedAtUtc: null,
  batchSize: 20,
  retentionDays: 30,
  lastSentAtUtc: null,
  lastError: ""
});

const pendingCount = computed(() => reports.value.filter((report) => !report.sentAtUtc).length);
watch(settings, () => { draftRevision++; }, { deep: true, flush: "sync" });
const target = (value: ErrorReportingSettings) => JSON.stringify([value.enabled, value.collectorUrl.trim(), value.batchSize, value.retentionDays]);
const hasUnsavedChanges = computed(() => !!savedSettings.value && target(settings) !== target(savedSettings.value));
const canSave = computed(() => settingsConfirmed.value && !busy.value && Number.isSafeInteger(settings.batchSize) && settings.batchSize >= 1 && settings.batchSize <= 50 &&
  Number.isSafeInteger(settings.retentionDays) && settings.retentionDays >= 1 && settings.retentionDays <= 365);
// The recent fifty rows are not a complete inventory of unsent reports.
const canSend = computed(() => settingsConfirmed.value && !busy.value && !hasUnsavedChanges.value && savedSettings.value?.enabled === true && !!savedSettings.value.collectorUrl.trim());

onMounted(loadAll);
onBeforeUnmount(() => { disposed = true; });

function publishSettings(value: ErrorReportingSettings, revision: number, preserveDraft = false) {
  savedSettings.value = { ...value };
  if (!preserveDraft && draftRevision === revision) Object.assign(settings, value);
  settingsConfirmed.value = true;
  loadError.value = "";
}

async function readAll(revision: number, preserveDraft = false) {
  try {
    const [loadedSettings, loadedReports] = await Promise.all([getErrorReportingSettings(), getErrorReports(50)]);
    if (!current()) return false;
    publishSettings(loadedSettings, revision, preserveDraft);
    reports.value = loadedReports;
    if (expandedId.value !== null && !loadedReports.some(report => report.id === expandedId.value)) expandedId.value = null;
    loaded.value = true;
    return true;
  } catch (error) {
    if (current()) { settingsConfirmed.value = false; loadError.value = resolveApiMessage(error); }
    return false;
  }
}

async function loadAll() {
  if (!current() || busy.value) return;
  const revision = draftRevision, preserveDraft = hasUnsavedChanges.value;
  busy.value = "load";
  try {
    await readAll(revision, preserveDraft);
    if (current()) notice.value = "";
  } finally {
    if (current()) busy.value = "";
  }
}

async function save() {
  if (!current() || !canSave.value) return;
  const submitted = { ...settings }, revision = draftRevision;
  busy.value = "save";
  try {
    const saved = await saveErrorReportingSettings(submitted);
    if (!current()) return;
    if (target(saved) !== target(submitted)) {
      settingsConfirmed.value = false;
      showNotice(t("panel.errorReports.resultUnconfirmed"), true);
      return;
    }
    publishSettings(saved, revision);
    showNotice(t("panel.errorReports.saved"), false);
  } catch (error) {
    if (current()) { settingsConfirmed.value = false; showNotice(resolveApiMessage(error), true); }
  } finally {
    if (current()) busy.value = "";
  }
}

async function sendNow() {
  if (!current() || !canSend.value) return;
  const revision = draftRevision;
  busy.value = "send";
  try {
    const result = await sendPendingErrorReports();
    if (!current()) return;
    const unchanged = revision === draftRevision;
    await readAll(revision);
    if (!current() || !unchanged || hasUnsavedChanges.value) return;
    showNotice(
      result.sentCount > 0
        ? `${t("panel.errorReports.sentResult")}: ${result.sentCount}`
        : t("panel.errorReports.nothingToSend"),
      false);
  } catch (error) {
    if (current() && revision === draftRevision) showNotice(resolveApiMessage(error), true);
  } finally {
    if (current()) busy.value = "";
  }
}

function toggleDetail(id: number) {
  expandedId.value = expandedId.value === id ? null : id;
}

function shortType(value: string) {
  const index = value.lastIndexOf(".");
  return index >= 0 ? value.slice(index + 1) : value || "-";
}

function truncate(value: string, max: number) {
  if (!value) {
    return "-";
  }

  return value.length > max ? `${value.slice(0, max)}…` : value;
}

/** Tarih bicimi panelin geri kalaniyla AYNI (bkz. utils/datetime.ts). */
function formatUtc(value: string | null) {
  return formatDateTime(value, t("panel.common.notAvailable"));
}

function showNotice(message: string, isError: boolean) {
  notice.value = message;
  noticeError.value = isError;
}

function resolveApiMessage(error: unknown): string {
  const key = extractApiMessage(error);
  const translated = t(key);
  const message = translated === key ? key : translated;
  const details = extractApiDetails(error);
  return details ? `${message}\n\n${details}` : message;
}
</script>

<style scoped>
.row-actions button {
  width: auto;
}

.report-row {
  grid-template-columns: 160px 200px minmax(0, 1fr) 90px 120px 120px;
}

.detail-row {
  display: block;
  background: var(--surface-1);
}

.detail-body {
  display: grid;
  gap: 10px;
}

.detail-body h4 {
  margin: 4px 0 0;
  font-size: 0.9rem;
}

.detail-code {
  min-height: 0;
  max-height: 280px;
}

.detail-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(min(100%, 220px), 1fr));
  gap: 8px;
  margin: 0;
}

.detail-grid dt {
  color: var(--text-muted);
  font-size: 0.8rem;
}

.detail-grid dd {
  margin: 2px 0 0;
  word-break: break-word;
}

.disclosure-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(min(100%, 280px), 1fr));
  gap: 12px;
}

.disclosure-card {
  padding: 12px 14px;
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-md);
  background: var(--surface-0);
}

.disclosure-card h4 {
  margin: 0 0 8px;
  font-size: 0.95rem;
}

.disclosure-card ul {
  margin: 0;
  padding-left: 18px;
  display: grid;
  gap: 4px;
}

.disclosure-card li {
  line-height: 1.5;
}

.disclosure-card.sent {
  border-color: color-mix(in srgb, var(--ok-1) 30%, transparent);
  background: var(--ok-bg);
  color: var(--ok-text);
}

.disclosure-card.not-sent {
  border-color: color-mix(in srgb, var(--warn-1) 30%, transparent);
  background: var(--warn-bg);
  color: var(--warn-text);
}

@media (max-width: 900px) {
  .reports-table { overflow: visible; }
  .report-row { min-width: 0; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 14px; align-items: start; }
  .report-row.table-head { display: none; }
  .report-row > span { min-width: 0; overflow-wrap: anywhere; }
  .report-row > [data-label]::before { content: attr(data-label); display: block; margin-bottom: 5px; font-size: var(--fs-xs); font-weight: 600; color: var(--text-muted); }
  .report-time, .report-type, .report-message, .report-row .row-actions { grid-column: 1 / -1; }
  .report-row .row-actions button { width: 100%; }
  .detail-row {
    min-width: 0;
  }
}
</style>
