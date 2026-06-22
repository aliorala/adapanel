<template>
  <section class="panel wide stats-view">
    <div class="st-header">
      <div>
        <p class="section-kicker">{{ t("panel.navigation.monitor") }}</p>
        <h2>{{ t("panel.hosting.stats") }}</h2>
        <p class="meta">{{ siteName || t("panel.php.selectSite") }}</p>
      </div>
      <select v-model.number="statsDays" class="compact-select" :disabled="loading" @change="load">
        <option :value="1">{{ t("panel.hosting.statsToday") }}</option>
        <option :value="7">{{ t("panel.hosting.statsWeek") }}</option>
        <option :value="30">{{ t("panel.hosting.statsMonth") }}</option>
      </select>
    </div>

    <p v-if="notice" class="notice error">{{ notice }}</p>
    <p v-if="!siteName" class="empty-state">{{ t("panel.hosting.selectSiteHint") }}</p>
    <p v-else-if="stats && !stats.hasData" class="empty-state">{{ t("panel.hosting.statsEmpty") }}</p>

    <template v-if="stats && stats.hasData">
      <div class="summary-strip">
        <div class="summary-item">
          <span>{{ t("panel.hosting.statsRequests") }}</span>
          <strong>{{ stats.totalRequests.toLocaleString() }}</strong>
        </div>
        <div class="summary-item">
          <span>{{ t("panel.hosting.statsVisitors") }}</span>
          <strong>{{ stats.uniqueVisitors.toLocaleString() }}</strong>
        </div>
        <div class="summary-item">
          <span>{{ t("panel.hosting.statsBandwidth") }}</span>
          <strong>{{ formatBytes(stats.totalBytes) }}</strong>
        </div>
        <div class="summary-item">
          <span>2xx / 3xx / 4xx / 5xx</span>
          <strong>{{ stats.status2xx }} / {{ stats.status3xx }} / {{ stats.status4xx }} / {{ stats.status5xx }}</strong>
        </div>
      </div>

      <div class="stats-columns">
        <div>
          <h5>{{ t("panel.hosting.statsTopPages") }}</h5>
          <div v-for="page in stats.topPages" :key="page.key" class="stat-bar">
            <span class="stat-label" :title="page.key">{{ page.key }}</span>
            <span class="stat-count">{{ page.count.toLocaleString() }}</span>
          </div>
        </div>
        <div>
          <h5>{{ t("panel.hosting.statsTopReferrers") }}</h5>
          <p v-if="stats.topReferrers.length === 0" class="meta">{{ t("panel.hosting.statsNoReferrers") }}</p>
          <div v-for="ref in stats.topReferrers" :key="ref.key" class="stat-bar">
            <span class="stat-label" :title="ref.key">{{ ref.key }}</span>
            <span class="stat-count">{{ ref.count.toLocaleString() }}</span>
          </div>
        </div>
      </div>

      <div>
        <h5>{{ t("panel.hosting.statsDaily") }}</h5>
        <div v-for="day in stats.dailyHits" :key="day.key" class="stat-bar">
          <span class="stat-label">{{ day.key }}</span>
          <span class="stat-track"><span class="stat-fill" :style="{ width: dailyWidth(day.count) }"></span></span>
          <span class="stat-count">{{ day.count.toLocaleString() }}</span>
        </div>
      </div>
    </template>
  </section>
</template>

<script setup lang="ts">
import { computed, onMounted, ref, watch } from "vue";
import { useI18n } from "vue-i18n";
import { useSitesStore } from "../stores/sites";
import { getSiteStatistics, type WebStatistics } from "../api/statistics";
import { extractApiMessage } from "../stores/auth";

const { t } = useI18n();
const sitesStore = useSitesStore();
const siteName = computed(() => sitesStore.selectedSiteName);

const stats = ref<WebStatistics | null>(null);
const statsDays = ref(7);
const loading = ref(false);
const notice = ref("");

onMounted(load);
watch(siteName, load);

async function load() {
  if (!siteName.value) {
    stats.value = null;
    return;
  }
  loading.value = true;
  notice.value = "";
  try {
    stats.value = await getSiteStatistics(siteName.value, statsDays.value);
  } catch (error) {
    notice.value = extractApiMessage(error);
    stats.value = null;
  } finally {
    loading.value = false;
  }
}

function formatBytes(bytes: number): string {
  if (!bytes) {
    return "0 B";
  }
  const units = ["B", "KB", "MB", "GB", "TB"];
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit += 1;
  }
  return `${value.toFixed(value < 10 && unit > 0 ? 1 : 0)} ${units[unit]}`;
}

function dailyWidth(count: number): string {
  const max = Math.max(1, ...(stats.value?.dailyHits.map((day) => day.count) ?? [1]));
  return `${Math.round((count / max) * 100)}%`;
}
</script>

<style scoped>
.stats-view {
  display: grid;
  gap: 16px;
}

.st-header {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
}

.summary-strip {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(150px, 1fr));
  gap: 10px;
}

.summary-item {
  display: grid;
  gap: 4px;
  padding: 12px 14px;
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-lg);
  background: var(--surface-1);
}

.summary-item span {
  color: var(--text-muted);
  font-size: 0.72rem;
}

.summary-item strong {
  font-size: 1.05rem;
}

.stats-columns {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(260px, 1fr));
  gap: 18px;
}

.stat-bar {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 5px 0;
  border-bottom: 1px solid var(--panel-border);
}

.stat-label {
  flex: 1 1 auto;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: 0.82rem;
}

.stat-count {
  flex-shrink: 0;
  color: var(--text-muted);
  font-variant-numeric: tabular-nums;
}

.stat-track {
  flex: 0 0 120px;
  height: 8px;
  border-radius: 999px;
  background: var(--surface-2);
  overflow: hidden;
}

.stat-fill {
  display: block;
  height: 100%;
  background: linear-gradient(90deg, var(--accent-1), var(--accent-2, var(--accent-1)));
}

h5 {
  margin: 0 0 8px;
}

.meta {
  color: var(--text-muted);
}
</style>
