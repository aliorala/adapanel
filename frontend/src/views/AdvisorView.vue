<template>
  <section class="panel advisor-panel">
    <div class="section-head">
      <div>
        <h2>{{ t("panel.advisor.title") }}</h2>
        <p class="meta">{{ t("panel.advisor.subtitle") }}</p>
      </div>
      <button class="secondary compact" :disabled="loading" @click="refresh">
        {{ loading ? t("panel.common.loading") : t("panel.advisor.refresh") }}
      </button>
    </div>

    <p v-if="notice" class="notice error">{{ notice }}</p>

    <div v-if="report" class="advisor-summary">
      <span class="sev-pill critical">{{ report.criticalCount }} {{ t("panel.advisor.critical") }}</span>
      <span class="sev-pill warning">{{ report.warningCount }} {{ t("panel.advisor.warning") }}</span>
      <span class="sev-pill info">{{ report.infoCount }} {{ t("panel.advisor.info") }}</span>
      <span class="meta checked-at">{{ t("panel.advisor.checkedAt") }}: {{ formatUtc(report.checkedAtUtc) }}</span>
    </div>

    <div v-if="report && report.items.length === 0" class="advisor-clear">
      <span class="clear-badge">✓</span>
      <div>
        <strong>{{ t("panel.advisor.allClearTitle") }}</strong>
        <p class="meta">{{ t("panel.advisor.allClearHint") }}</p>
      </div>
    </div>

    <ul v-else-if="report" class="advisor-list">
      <li v-for="item in report.items" :key="item.id" :class="['advisor-item', item.severity]">
        <span class="sev-dot" :class="item.severity" aria-hidden="true"></span>
        <div class="advisor-body">
          <strong>{{ t(item.titleKey) }}</strong>
          <p v-if="item.detailKey || item.detail" class="advisor-detail">
            <span v-if="item.detailKey">{{ t(item.detailKey) }} </span>
            <code v-if="item.detail">{{ item.detail }}</code>
          </p>
        </div>
        <button v-if="item.actionSection" class="secondary compact" @click="go(item.actionSection)">
          {{ t("panel.advisor.fix") }}
        </button>
      </li>
    </ul>
  </section>
</template>

<script setup lang="ts">
import { onMounted, ref } from "vue";
import { useI18n } from "vue-i18n";
import { getAdvisorReport, type AdvisorReport } from "../api/advisor";
import { extractApiMessage } from "../stores/auth";

const { t } = useI18n();
const emit = defineEmits<{ (event: "navigate", sectionId: string): void }>();

const report = ref<AdvisorReport | null>(null);
const loading = ref(false);
const notice = ref("");

onMounted(refresh);

async function refresh() {
  loading.value = true;
  notice.value = "";
  try {
    report.value = await getAdvisorReport();
  } catch (error) {
    const key = extractApiMessage(error);
    const translated = t(key);
    notice.value = translated === key ? key : translated;
  } finally {
    loading.value = false;
  }
}

function go(sectionId: string) {
  emit("navigate", sectionId);
}

function formatUtc(value: string) {
  return value ? new Date(value).toLocaleString() : "-";
}
</script>

<style scoped>
.advisor-panel {
  display: grid;
  gap: 16px;
}

.section-head {
  display: flex;
  justify-content: space-between;
  align-items: flex-start;
  gap: 12px;
}

.advisor-summary {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 10px;
}

.sev-pill {
  padding: 4px 12px;
  border-radius: 999px;
  font-size: 0.82rem;
  font-weight: 700;
  border: 1px solid var(--panel-border);
}

.sev-pill.critical {
  color: #b91c1c;
  background: color-mix(in srgb, #ef4444 12%, transparent);
  border-color: color-mix(in srgb, #ef4444 40%, transparent);
}

.sev-pill.warning {
  color: #b45309;
  background: color-mix(in srgb, #f59e0b 12%, transparent);
  border-color: color-mix(in srgb, #f59e0b 40%, transparent);
}

.sev-pill.info {
  color: var(--text-soft);
  background: var(--surface-1);
}

.checked-at {
  margin-left: auto;
}

.advisor-clear {
  display: flex;
  align-items: center;
  gap: 14px;
  padding: 18px;
  border: 1px solid color-mix(in srgb, #22c55e 40%, var(--panel-border));
  border-radius: var(--radius-md);
  background: color-mix(in srgb, #22c55e 8%, var(--surface-1));
}

.clear-badge {
  display: grid;
  place-items: center;
  width: 40px;
  height: 40px;
  border-radius: 50%;
  background: color-mix(in srgb, #22c55e 20%, transparent);
  color: #15803d;
  font-size: 1.3rem;
  font-weight: 800;
  flex-shrink: 0;
}

.advisor-clear p {
  margin: 2px 0 0;
}

.advisor-list {
  list-style: none;
  margin: 0;
  padding: 0;
  display: grid;
  gap: 10px;
}

.advisor-item {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 14px 16px;
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-md);
  background: var(--surface-1);
}

.advisor-item.critical {
  border-color: color-mix(in srgb, #ef4444 45%, var(--panel-border));
}

.advisor-item.warning {
  border-color: color-mix(in srgb, #f59e0b 40%, var(--panel-border));
}

.sev-dot {
  width: 10px;
  height: 10px;
  border-radius: 50%;
  flex-shrink: 0;
}

.sev-dot.critical {
  background: #ef4444;
}

.sev-dot.warning {
  background: #f59e0b;
}

.sev-dot.info {
  background: var(--text-muted);
}

.advisor-body {
  flex: 1;
  min-width: 0;
  display: grid;
  gap: 2px;
}

.advisor-detail {
  margin: 0;
  color: var(--text-soft);
  font-size: 0.85rem;
}

.advisor-detail code {
  font-family: ui-monospace, "Cascadia Code", Consolas, monospace;
}

@media (max-width: 720px) {
  .advisor-item {
    flex-wrap: wrap;
  }
}
</style>
