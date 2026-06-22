<template>
  <section class="panel module-shell site-logs">
    <div class="module-head">
      <div>
        <h3>{{ t("panel.siteLogs.title") }}</h3>
        <p class="meta">{{ log && log.path ? log.path : t("panel.siteLogs.subtitle") }}</p>
      </div>
      <div class="log-actions">
        <select v-model.number="lines" :disabled="loading" @change="load">
          <option :value="120">120</option>
          <option :value="300">300</option>
          <option :value="1000">1000</option>
        </select>
        <button class="secondary compact" :disabled="loading || !siteName" @click="load">
          {{ loading ? t("panel.siteLogs.loading") : t("panel.siteLogs.refresh") }}
        </button>
      </div>
    </div>

    <p v-if="notice" :class="['notice', { error: noticeError }]">{{ notice }}</p>
    <p v-if="!siteName" class="meta">{{ t("panel.php.selectSite") }}</p>

    <template v-else>
      <pre class="log-output">{{ content }}</pre>
      <p v-if="log && log.lastModifiedUtc" class="hint">
        {{ t("panel.siteLogs.updated") }}: {{ formatDate(log.lastModifiedUtc) }}
      </p>
    </template>
  </section>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from "vue";
import { useI18n } from "vue-i18n";
import { getSiteLog, type LogTail } from "../api/logs";
import { extractApiMessage } from "../stores/auth";
import { useSitesStore } from "../stores/sites";

const { t } = useI18n();
const sitesStore = useSitesStore();
const siteName = computed(() => sitesStore.selectedSiteName);
const log = ref<LogTail | null>(null);
const lines = ref(120);
const loading = ref(false);
const notice = ref("");
const noticeError = ref(false);

const content = computed(() =>
  log.value && log.value.lines.length ? log.value.lines.join("\n") : t("panel.siteLogs.empty"));

onMounted(load);

async function load() {
  if (!siteName.value) {
    return;
  }
  loading.value = true;
  notice.value = "";
  try {
    log.value = await getSiteLog(siteName.value, lines.value);
  } catch (error) {
    const key = extractApiMessage(error);
    const translated = t(key);
    notice.value = translated === key ? key : translated;
    noticeError.value = true;
  } finally {
    loading.value = false;
  }
}

function formatDate(iso: string) {
  const date = new Date(iso);
  return Number.isNaN(date.getTime()) ? iso : date.toLocaleString();
}
</script>

<style scoped>
.site-logs {
  display: grid;
  gap: 12px;
}

.log-actions {
  display: flex;
  gap: 8px;
  align-items: center;
}

.log-actions select {
  width: auto;
  border-radius: var(--radius-sm);
  border: 1px solid var(--panel-border);
  padding: 9px 11px;
  background: var(--input-bg);
  color: var(--text-strong);
  font: inherit;
}

.log-actions button {
  width: auto;
}

.log-output {
  margin: 0;
  min-height: 280px;
  max-height: 560px;
  overflow: auto;
  padding: 14px 16px;
  border: 1px solid var(--panel-border);
  border-radius: 10px;
  background: #0b0f17;
  color: #d6e2f0;
  font-family: var(--font-mono, ui-monospace, "Cascadia Code", Consolas, monospace);
  font-size: 0.8rem;
  line-height: 1.5;
  white-space: pre-wrap;
  word-break: break-word;
}

.hint {
  color: var(--text-muted);
  font-size: 0.8rem;
}
</style>
