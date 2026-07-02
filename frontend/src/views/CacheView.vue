<template>
  <section class="panel module-shell">
    <div class="module-head">
      <div>
        <h3>{{ t("panel.cache.title") }}</h3>
        <p>{{ t("panel.cache.subtitle") }}</p>
      </div>
      <button class="secondary compact" :disabled="busyAction === 'refresh'" @click="refresh">
        {{ t("panel.cache.refresh") }}
      </button>
    </div>

    <p v-if="notice" :class="['notice', { error: noticeError }]">{{ notice }}</p>

    <div class="summary-strip">
      <div class="summary-item">
        <span>{{ t("panel.cache.total") }}</span>
        <strong>{{ runtimes.length }}</strong>
      </div>
      <div class="summary-item">
        <span>{{ t("panel.cache.installed") }}</span>
        <strong>{{ installedCount }}</strong>
      </div>
      <div class="summary-item">
        <span>{{ t("panel.cache.running") }}</span>
        <strong>{{ runningCount }}</strong>
      </div>
      <div class="summary-item">
        <span>{{ t("panel.cache.defaultPorts") }}</span>
        <strong>6379 / 11211</strong>
      </div>
    </div>

    <div class="data-table">
      <div class="data-row table-head cache-row">
        <span>{{ t("panel.software.component") }}</span>
        <span>{{ t("panel.sites.state") }}</span>
        <span>{{ t("panel.cache.service") }}</span>
        <span>{{ t("panel.cache.port") }}</span>
        <span>{{ t("panel.software.installDir") }}</span>
        <span>{{ t("panel.files.actions") }}</span>
      </div>
      <div v-for="item in runtimes" :key="item.componentId" class="data-row cache-row">
        <span class="cell-main">
          <strong>{{ item.displayName }}</strong>
          <small>{{ item.componentId }}</small>
          <small v-if="item.metrics" class="cache-metrics">
            {{ t("panel.cache.metrics.memory") }}: {{ item.metrics.usedMemory || "-" }} ·
            {{ t("panel.cache.metrics.keys") }}: {{ item.metrics.keys }} ·
            {{ t("panel.cache.metrics.clients") }}: {{ item.metrics.connectedClients }} ·
            {{ t("panel.cache.metrics.hitRate") }}: {{ hitRate(item.metrics) }}
          </small>
        </span>
        <span :class="['status-pill', statusClass(item)]">{{ statusLabel(item) }}</span>
        <span>{{ item.serviceName || "-" }}</span>
        <span>{{ item.defaultPort }}</span>
        <span class="cell-muted">{{ item.installDir || "-" }}</span>
        <span class="row-actions">
          <button
            v-if="!item.isInstalled"
            class="compact"
            :disabled="isBusy(item.componentId)"
            @click="install(item.componentId)">
            {{ isBusy(item.componentId) ? t("panel.cache.busy") : t("panel.cache.install") }}
          </button>
          <button
            v-else-if="!isRunning(item)"
            class="compact"
            :disabled="isBusy(item.componentId)"
            @click="toggle(item.componentId, true)">
            {{ isBusy(item.componentId) ? t("panel.cache.busy") : t("panel.cache.start") }}
          </button>
          <button
            v-else
            class="secondary compact"
            :disabled="isBusy(item.componentId)"
            @click="toggle(item.componentId, false)">
            {{ isBusy(item.componentId) ? t("panel.cache.busy") : t("panel.cache.stop") }}
          </button>
          <button
            v-if="item.isInstalled && !isRunning(item)"
            class="danger compact"
            :disabled="isBusy(item.componentId)"
            @click="remove(item.componentId)">
            {{ t("panel.cache.remove") }}
          </button>
        </span>
      </div>
      <p v-if="busyAction === 'refresh' && !status" class="empty-state">{{ t("panel.common.loading") }}</p>
    </div>
  </section>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from "vue";
import { useI18n } from "vue-i18n";
import { getCacheStatus, startMemcached, startRedis, stopMemcached, stopRedis, type CacheRuntime, type CacheStatus } from "../api/cache";
import { extractApiMessage } from "../stores/auth";
import { useSoftwareStore } from "../stores/software";

const { t } = useI18n();
const softwareStore = useSoftwareStore();
const status = ref<CacheStatus | null>(null);
const busyAction = ref("");
const notice = ref("");
const noticeError = ref(false);
const runtimes = computed(() => status.value ? [status.value.redis, status.value.memcached] : []);
const installedCount = computed(() => runtimes.value.filter((item) => item.isInstalled).length);
const runningCount = computed(() => runtimes.value.filter(isRunning).length);

onMounted(refresh);

async function refresh() {
  busyAction.value ||= "refresh";
  try {
    status.value = await getCacheStatus();
    notice.value = "";
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    if (busyAction.value === "refresh") busyAction.value = "";
  }
}

async function install(componentId: string) {
  busyAction.value = `${componentId}:install`;
  try {
    await softwareStore.installComponent(componentId);
    await refresh();
    showNotice(t("panel.cache.installedMessage"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    busyAction.value = "";
  }
}

async function remove(componentId: string) {
  if (!window.confirm(t("panel.cache.confirmRemove"))) return;
  busyAction.value = `${componentId}:remove`;
  try {
    await softwareStore.removeComponent(componentId);
    await refresh();
    showNotice(t("panel.cache.removedMessage"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    busyAction.value = "";
  }
}

async function toggle(componentId: string, shouldStart: boolean) {
  busyAction.value = `${componentId}:${shouldStart ? "start" : "stop"}`;
  try {
    if (componentId === "memcached") {
      shouldStart ? await startMemcached() : await stopMemcached();
    } else {
      shouldStart ? await startRedis() : await stopRedis();
    }
    await refresh();
    showNotice(t(shouldStart ? "panel.cache.started" : "panel.cache.stopped"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    busyAction.value = "";
  }
}

function isRunning(item: CacheRuntime) {
  return ["running", "started"].includes(item.status.toLocaleLowerCase());
}

function hitRate(metrics: { hits: number; misses: number }): string {
  const total = metrics.hits + metrics.misses;
  return total > 0 ? `${Math.round((metrics.hits / total) * 100)}%` : "—";
}

function statusLabel(item: CacheRuntime) {
  if (!item.isInstalled) return t("panel.database.notInstalled");
  return isRunning(item) ? t("panel.cache.runningState") : (item.status || t("panel.cache.stoppedState"));
}

function statusClass(item: CacheRuntime) {
  if (!item.isInstalled) return "warn";
  return isRunning(item) ? "ok" : "";
}

function isBusy(componentId: string) {
  return busyAction.value.startsWith(`${componentId}:`);
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
.cache-row {
  grid-template-columns: 1.1fr 0.8fr 1fr 0.55fr 1.5fr 1.4fr;
}
</style>
