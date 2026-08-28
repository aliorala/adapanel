<template>
  <section class="panel module-shell">
    <div class="module-head">
      <div>
        <h2>{{ t("panel.logs.title") }}</h2>
        <p>{{ t("panel.logs.subtitle") }}</p>
      </div>
      <button class="secondary compact" :disabled="loading" @click="refresh">
        {{ loading ? t("panel.logs.loading") : t("panel.logs.refresh") }}
      </button>
    </div>

    <p v-if="notice" :class="['notice', 'page-notice', { error: noticeError }]" :role="noticeError ? 'alert' : 'status'">{{ notice }}</p>

    <div class="module-toolbar">
      <div class="segmented-tabs" role="tablist">
        <button role="tab" :aria-selected="activeTab === 'panel'" :class="{ active: activeTab === 'panel' }" @click="activeTab = 'panel'">
          {{ t("panel.logs.panelLog") }}
        </button>
        <button role="tab" :aria-selected="activeTab === 'site'" :class="{ active: activeTab === 'site' }" @click="activeTab = 'site'">
          {{ t("panel.logs.siteLog") }}
        </button>
        <button role="tab" :aria-selected="activeTab === 'audit'" :class="{ active: activeTab === 'audit' }" @click="activeTab = 'audit'">
          {{ t("panel.audit.title") }}
        </button>
        <button role="tab" :aria-selected="activeTab === 'rotation'" :class="{ active: activeTab === 'rotation' }" @click="activeTab = 'rotation'">
          {{ t("panel.logs.rotationTab") }}
        </button>
      </div>
      <div class="toolbar-group">
        <select v-if="activeTab === 'site'" v-model="selectedSiteName" class="compact-control">
          <option value="">{{ t("panel.php.selectSite") }}</option>
          <option v-for="site in sitesStore.sites" :key="site.name" :value="site.name">{{ site.name }}</option>
        </select>
        <select v-if="isTailTab" v-model.number="lineCount" class="compact-control" @change="refreshLogs">
          <option :value="50">50</option>
          <option :value="120">120</option>
          <option :value="250">250</option>
          <option :value="500">500</option>
        </select>
        <input v-if="activeTab !== 'rotation'" v-model.trim="query" class="search-control" :placeholder="t('panel.logs.search')" />
      </div>
    </div>

    <div v-if="isTailTab" class="summary-strip">
      <div class="summary-item">
        <span>{{ t("panel.logs.source") }}</span>
        <strong>{{ activeLog.name || "-" }}</strong>
      </div>
      <div class="summary-item">
        <span>{{ t("panel.logs.path") }}</span>
        <strong class="path-value">{{ activeLog.path || "-" }}</strong>
      </div>
      <div class="summary-item">
        <span>{{ t("panel.logs.updated") }}</span>
        <strong>{{ formatUtc(activeLog.lastModifiedUtc) }}</strong>
      </div>
      <div class="summary-item">
        <span>{{ t("panel.logs.visibleLines") }}</span>
        <strong>{{ filteredLines.length }}</strong>
      </div>
    </div>

    <pre v-if="isTailTab" class="code-viewer">{{ filteredLines.join("\n") || t("panel.logs.empty") }}</pre>

    <template v-else-if="activeTab === 'rotation'">
      <p class="hint">{{ t("panel.logs.rotationHint") }}</p>

      <div class="summary-strip">
        <div class="summary-item">
          <span>{{ t("panel.logs.rotationLastRun") }}</span>
          <strong>{{ formatUtc(rotation.lastRunUtc) }}</strong>
        </div>
        <div class="summary-item">
          <span>{{ t("panel.logs.rotationNextRun") }}</span>
          <strong>{{ rotation.settings.enabled ? formatUtc(rotation.nextRunUtc) : t("panel.logs.rotationOff") }}</strong>
        </div>
        <div class="summary-item">
          <span>{{ t("panel.logs.rotationFreed") }}</span>
          <strong>{{ formatBytes(rotation.totalFreedBytes) }}</strong>
        </div>
        <div class="summary-item">
          <span>{{ t("panel.logs.rotationArchiveTotal") }}</span>
          <strong>{{ formatBytes(totalArchiveBytes) }}</strong>
        </div>
      </div>

      <form class="form-grid rotation-form" @submit.prevent="saveRotation">
        <label class="check-row">
          <input v-model="rotation.settings.enabled" type="checkbox" />
          <span>{{ t("panel.logs.rotationEnabled") }}</span>
        </label>
        <label class="check-row">
          <input v-model="rotation.settings.compress" type="checkbox" />
          <span>{{ t("panel.logs.rotationCompress") }}</span>
        </label>
        <label>
          {{ t("panel.logs.rotationSiteAge") }}
          <input v-model.number="rotation.settings.siteLogMaxAgeDays" type="number" min="0" max="3650" />
          <small>{{ t("panel.logs.rotationSiteAgeHint") }}</small>
        </label>
        <label>
          {{ t("panel.logs.rotationPanelAge") }}
          <input v-model.number="rotation.settings.panelLogMaxAgeDays" type="number" min="0" max="3650" />
          <small>{{ t("panel.logs.rotationZeroOff") }}</small>
        </label>
        <label>
          {{ t("panel.logs.rotationMaxFile") }}
          <input v-model.number="rotation.settings.maxFileSizeMB" type="number" min="0" max="1048576" />
          <small>{{ t("panel.logs.rotationMaxFileHint") }}</small>
        </label>
        <label>
          {{ t("panel.logs.rotationKeep") }}
          <input v-model.number="rotation.settings.siteKeepCount" type="number" min="0" max="3650" />
          <small>{{ t("panel.logs.rotationZeroUnlimited") }}</small>
        </label>
        <label>
          {{ t("panel.logs.rotationPanelKeep") }}
          <input v-model.number="rotation.settings.panelKeepCount" type="number" min="0" max="3650" />
          <small>{{ t("panel.logs.rotationPanelKeepHint") }}</small>
        </label>
        <label>
          {{ t("panel.logs.rotationMaxTotal") }}
          <input v-model.number="rotation.settings.maxTotalSizeMB" type="number" min="0" max="1048576" />
          <small>{{ t("panel.logs.rotationZeroOff") }}</small>
        </label>
        <label>
          {{ t("panel.logs.rotationInterval") }}
          <input v-model.number="rotation.settings.intervalHours" type="number" min="1" max="720" />
        </label>
        <div class="rotation-actions">
          <button type="submit" :disabled="rotationBusy">{{ t("panel.common.save") }}</button>
          <button
            type="button"
            class="secondary"
            :disabled="rotationBusy || !rotation.settings.enabled"
            @click="runRotation"
          >
            {{ t("panel.logs.rotationRunNow") }}
          </button>
        </div>
      </form>

      <div class="data-table">
        <div class="data-row table-head rotation-row">
          <span>{{ t("panel.logs.rotationTarget") }}</span>
          <span>{{ t("panel.logs.rotationLast") }}</span>
          <span>{{ t("panel.logs.rotationRotated") }}</span>
          <span>{{ t("panel.logs.rotationSkipped") }}</span>
          <span>{{ t("panel.logs.rotationSize") }}</span>
          <span>{{ t("panel.logs.rotationSaved") }}</span>
        </div>
        <div v-for="target in rotation.targets" :key="target.key" class="data-row rotation-row">
          <span class="cell-main">
            <strong>{{ target.name }}</strong>
            <small class="path-value">{{ target.directory }}</small>
            <small v-if="target.status === 'error'" class="danger-text">{{ describeError(target) }}</small>
          </span>
          <span>{{ target.directoryExists ? formatUtc(target.lastRotationUtc) : t("panel.logs.rotationNoDirectory") }}</span>
          <span>{{ target.rotatedFiles }} / {{ target.deletedArchives }}</span>
          <span>{{ target.skippedLockedFiles + target.skippedUnsafeFiles }}</span>
          <span>{{ formatBytes(target.currentLogBytes) }} + {{ formatBytes(target.currentArchiveBytes) }}</span>
          <span>{{ formatBytes(target.freedBytes) }}</span>
        </div>
        <p v-if="rotation.targets.length === 0" class="empty-state">{{ t("panel.logs.rotationEmpty") }}</p>
      </div>
    </template>

    <div v-else class="data-table">
      <div class="data-row table-head audit-row">
        <span>{{ t("panel.logs.action") }}</span>
        <span>{{ t("panel.logs.resource") }}</span>
        <span>{{ t("panel.logs.user") }}</span>
        <span>{{ t("panel.logs.ip") }}</span>
        <span>{{ t("panel.logs.date") }}</span>
      </div>
      <div v-for="item in filteredAudit" :key="item.id" class="data-row audit-row">
        <span class="cell-main"><strong>{{ item.action }}</strong><small>{{ item.details || "-" }}</small></span>
        <span>{{ item.resource || "-" }}</span>
        <span>{{ item.userName || "-" }}</span>
        <span>{{ item.ipAddress || "-" }}</span>
        <span>{{ formatUtc(item.createdAtUtc) }}</span>
      </div>
      <p v-if="loading && filteredAudit.length === 0" class="empty-state">{{ t("panel.common.loading") }}</p>
      <p v-else-if="filteredAudit.length === 0" class="empty-state">{{ t("panel.logs.emptyAudit") }}</p>
    </div>
  </section>
</template>

<script setup lang="ts">
import { computed, onMounted, ref, watch } from "vue";
import { useI18n } from "vue-i18n";
import {
  getLogRotation,
  getPanelLog,
  getSiteLog,
  runLogRotation,
  updateLogRotation,
  type LogRotationStatus,
  type LogRotationTarget,
  type LogTail
} from "../api/logs";
import { useAuthStore, extractApiMessage } from "../stores/auth";
import { useConfirm } from "../components/ConfirmDialog.vue";
import { useSitesStore } from "../stores/sites";
import { formatBytes as sharedFormatBytes } from "../utils/bytes";
import { formatDateTime } from "../utils/datetime";

const { t } = useI18n();
const { confirm } = useConfirm();
const authStore = useAuthStore();
const sitesStore = useSitesStore();
const activeTab = ref<"panel" | "site" | "audit" | "rotation">("panel");
const selectedSiteName = ref("");
const lineCount = ref(120);
const query = ref("");
const loading = ref(false);
const notice = ref("");
const noticeError = ref(false);
const panelLog = ref<LogTail>(emptyLog());
const siteLog = ref<LogTail>(emptyLog());
const rotation = ref<LogRotationStatus>(emptyRotation());
const rotationBusy = ref(false);

// Kuyruk gösteren sekmeler: satır sayısı seçici ve kod görünümü yalnız bunlarda anlamlı.
const isTailTab = computed(() => activeTab.value === "panel" || activeTab.value === "site");
const totalArchiveBytes = computed(() =>
  rotation.value.targets.reduce((toplam, target) => toplam + target.currentArchiveBytes, 0));
const activeLog = computed(() => activeTab.value === "site" ? siteLog.value : panelLog.value);
const filteredLines = computed(() => {
  const needle = query.value.toLocaleLowerCase();
  return needle
    ? activeLog.value.lines.filter((line) => line.toLocaleLowerCase().includes(needle))
    : activeLog.value.lines;
});
const filteredAudit = computed(() => {
  const needle = query.value.toLocaleLowerCase();
  return needle
    ? authStore.auditLogs.filter((item) =>
        [item.action, item.resource, item.userName, item.ipAddress, item.details]
          .some((value) => value?.toLocaleLowerCase().includes(needle)))
    : authStore.auditLogs;
});

onMounted(refresh);
watch(selectedSiteName, refreshSiteLog);

async function refresh() {
  loading.value = true;
  let firstError: unknown = null;

  try {
    if (sitesStore.sites.length === 0) {
      try {
        await sitesStore.fetchSites();
      } catch (error) {
        firstError = error;
      }
    }

    selectedSiteName.value ||= sitesStore.selectedSiteName;

    const [panelResult, auditResult, rotationResult] = await Promise.allSettled([
      getPanelLog(lineCount.value),
      authStore.fetchAuditLogs(),
      getLogRotation()
    ]);

    if (panelResult.status === "fulfilled") {
      panelLog.value = panelResult.value;
    } else {
      firstError ||= panelResult.reason;
    }

    if (auditResult.status === "rejected") {
      firstError ||= auditResult.reason;
    }

    if (rotationResult.status === "fulfilled") {
      rotation.value = rotationResult.value;
    } else {
      firstError ||= rotationResult.reason;
    }

    await refreshSiteLog();
    if (firstError) {
      showNotice(resolveApiMessage(firstError), true);
    } else {
      notice.value = "";
    }
  } finally {
    loading.value = false;
  }
}

async function refreshLogs() {
  try {
    panelLog.value = await getPanelLog(lineCount.value);
    await refreshSiteLog();
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
}

async function refreshSiteLog() {
  if (!selectedSiteName.value) {
    siteLog.value = emptyLog();
    return;
  }
  try {
    siteLog.value = await getSiteLog(selectedSiteName.value, lineCount.value);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
}

async function saveRotation() {
  rotationBusy.value = true;
  try {
    rotation.value = await updateLogRotation(rotation.value.settings);
    showNotice(t("panel.logs.rotationSaved"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    rotationBusy.value = false;
  }
}

/*
 * Bu düğme DOSYA SİLEBİLİR (saklama sınırları açıksa) — onaysız çalıştırmıyoruz.
 *
 * `window.confirm` KALDIRILDI: tarayıcı diyaloğu engellenmiş bir sekmede (kullanıcı "bu site
 * daha fazla diyalog göstermesin" dediyse ya da kurumsal politika engelliyorsa) `false` döner ve
 * düğme SESSİZCE HİÇBİR ŞEY YAPMAZ. Kullanıcı düğmenin bozuk olduğunu sanır. Panelin kendi
 * onay diyaloğu (57 ekran onu kullanıyor) her zaman çizilir ve neyin kaybedileceğini yazar.
 */
async function runRotation() {
  const approved = await confirm({
    title: t("panel.logs.rotationRunTitle"),
    message: t("panel.logs.rotationRunConfirm"),
    confirmLabel: t("panel.logs.rotationRunConfirmLabel")
  });

  if (!approved) {
    return;
  }

  rotationBusy.value = true;
  try {
    rotation.value = await runLogRotation();
    showNotice(t("panel.logs.rotationCompleted"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    rotationBusy.value = false;
  }
}

/** Bilinen hata kodu çevrilir; bilinmeyen ham gösterilir (uydurma çeviri yok). */
function describeError(target: LogRotationTarget) {
  return target.error === "directory_is_reparse_point"
    ? t("panel.logs.rotationErrorReparsePoint")
    : target.error;
}

/** Bayt bicimi panelin geri kalaniyla AYNI (bkz. utils/bytes.ts); ekran kendi bicimini uydurmaz. */
function formatBytes(value: number) {
  return sharedFormatBytes(value, t);
}

function emptyLog(): LogTail {
  return { name: "", path: "", lines: [], lastModifiedUtc: null };
}

function emptyRotation(): LogRotationStatus {
  return {
    settings: {
      enabled: true,
      compress: true,
      siteLogMaxAgeDays: 30,
      panelLogMaxAgeDays: 2,
      maxFileSizeMB: 0,
      siteKeepCount: 0,
      panelKeepCount: 14,
      maxTotalSizeMB: 0,
      intervalHours: 24
    },
    lastRunUtc: null,
    nextRunUtc: null,
    targets: [],
    totalFreedBytes: 0
  };
}

/** Tarih bicimi panelin geri kalaniyla AYNI (bkz. utils/datetime.ts). */
function formatUtc(value: string | null) {
  return formatDateTime(value, "-");
}

function showNotice(message: string, isError: boolean) {
  notice.value = message;
  noticeError.value = isError;
}

function resolveApiMessage(error: unknown) {
  const key = extractApiMessage(error);
  const translated = t(key);
  return translated === key ? key : translated;
}
</script>

<style scoped>
.audit-row {
  grid-template-columns: minmax(210px, 1.7fr) 1fr 0.8fr 0.8fr 1fr;
}

.compact-control {
  width: auto;
  min-width: 120px;
}

.search-control {
  width: min(280px, 48vw);
}

.path-value {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.rotation-row {
  grid-template-columns: minmax(220px, 1.8fr) 1fr 0.7fr 0.6fr 1fr 0.8fr;
}

.rotation-form {
  margin-bottom: 14px;
}

.rotation-form small {
  display: block;
  margin-top: 4px;
  color: var(--text-muted);
  font-size: 0.74rem;
}

.check-row {
  display: flex;
  gap: 8px;
  align-items: center;
}

.check-row input {
  width: auto;
}

.rotation-actions {
  display: flex;
  gap: 8px;
  align-items: end;
}

.rotation-actions button {
  width: auto;
}

.danger-text {
  color: var(--danger-text, var(--danger-1));
}
</style>
