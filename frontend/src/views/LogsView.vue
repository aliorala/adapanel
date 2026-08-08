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
      <div class="segmented-tabs">
        <button :class="{ active: activeTab === 'panel' }" @click="activeTab = 'panel'">
          {{ t("panel.logs.panelLog") }}
        </button>
        <button :class="{ active: activeTab === 'site' }" @click="activeTab = 'site'">
          {{ t("panel.logs.siteLog") }}
        </button>
        <button :class="{ active: activeTab === 'audit' }" @click="activeTab = 'audit'">
          {{ t("panel.audit.title") }}
        </button>
      </div>
      <div class="toolbar-group">
        <select v-if="activeTab === 'site'" v-model="selectedSiteName" class="compact-control">
          <option value="">{{ t("panel.php.selectSite") }}</option>
          <option v-for="site in sitesStore.sites" :key="site.name" :value="site.name">{{ site.name }}</option>
        </select>
        <select v-if="activeTab !== 'audit'" v-model.number="lineCount" class="compact-control" @change="refreshLogs">
          <option :value="50">50</option>
          <option :value="120">120</option>
          <option :value="250">250</option>
          <option :value="500">500</option>
        </select>
        <input v-model.trim="query" class="search-control" :placeholder="t('panel.logs.search')" />
      </div>
    </div>

    <div v-if="activeTab !== 'audit'" class="summary-strip">
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

    <pre v-if="activeTab !== 'audit'" class="code-viewer">{{ filteredLines.join("\n") || t("panel.logs.empty") }}</pre>

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
import { getPanelLog, getSiteLog, type LogTail } from "../api/logs";
import { useAuthStore, extractApiMessage } from "../stores/auth";
import { useSitesStore } from "../stores/sites";

const { t } = useI18n();
const authStore = useAuthStore();
const sitesStore = useSitesStore();
const activeTab = ref<"panel" | "site" | "audit">("panel");
const selectedSiteName = ref("");
const lineCount = ref(120);
const query = ref("");
const loading = ref(false);
const notice = ref("");
const noticeError = ref(false);
const panelLog = ref<LogTail>(emptyLog());
const siteLog = ref<LogTail>(emptyLog());

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

    const [panelResult, auditResult] = await Promise.allSettled([
      getPanelLog(lineCount.value),
      authStore.fetchAuditLogs()
    ]);

    if (panelResult.status === "fulfilled") {
      panelLog.value = panelResult.value;
    } else {
      firstError ||= panelResult.reason;
    }

    if (auditResult.status === "rejected") {
      firstError ||= auditResult.reason;
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

function emptyLog(): LogTail {
  return { name: "", path: "", lines: [], lastModifiedUtc: null };
}

function formatUtc(value: string | null) {
  return value ? new Date(value).toLocaleString() : "-";
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
</style>
