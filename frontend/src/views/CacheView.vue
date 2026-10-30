<template>
  <section class="panel module-shell">
    <div class="module-head">
      <div>
        <h3>{{ t("panel.cache.title") }}</h3>
        <p>{{ t("panel.cache.subtitle") }}</p>
      </div>
      <button class="secondary compact" :disabled="!!busyAction" @click="refresh">
        {{ t("panel.cache.refresh") }}
      </button>
    </div>

    <p v-if="notice" :class="['notice', 'page-notice', { error: noticeError }]" :role="noticeError ? 'alert' : 'status'">{{ notice }}</p>

    <div class="summary-strip">
      <div class="summary-item">
        <span>{{ t("panel.cache.total") }}</span>
        <strong>{{ status ? runtimes.length : "—" }}</strong>
      </div>
      <div class="summary-item">
        <span>{{ t("panel.cache.installed") }}</span>
        <strong>{{ installedCount ?? "—" }}</strong>
      </div>
      <div class="summary-item">
        <span>{{ t("panel.cache.running") }}</span>
        <strong>{{ runningCount ?? "—" }}</strong>
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
            {{ t(item.componentId === "redis" ? "panel.cache.usage.connections" : "panel.cache.metrics.clients") }}: {{ item.componentId === "redis" ? item.metrics.clients?.length ?? "—" : item.metrics.connectedClients }} ·
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
            v-bind="writeGate(!!busyAction)"
            @click="install(item.componentId)">
            {{ isBusy(item.componentId) ? t("panel.cache.busy") : t("panel.cache.install") }}
          </button>
          <button
            v-else-if="isStopped(item)"
            class="compact"
            v-bind="writeGate(!!busyAction)"
            @click="toggle(item.componentId, true)">
            {{ isBusy(item.componentId) ? t("panel.cache.busy") : t("panel.cache.start") }}
          </button>
          <button
            v-else-if="isRunning(item)"
            class="secondary compact"
            v-bind="writeGate(!!busyAction)"
            @click="toggle(item.componentId, false)">
            {{ isBusy(item.componentId) ? t("panel.cache.busy") : t("panel.cache.stop") }}
          </button>
          <button
            v-if="item.isInstalled && isStopped(item)"
            class="danger compact"
            v-bind="writeGate(!!busyAction)"
            @click="remove(item.componentId)">
            {{ t("panel.cache.remove") }}
          </button>
        </span>
      </div>
      <p v-if="!status" class="empty-state" role="status">{{ t(statusError ? "panel.common.loadFailed" : "panel.common.loading") }}</p>
    </div>
    <article class="redis-usage" aria-labelledby="redis-usage-title">
      <h4 id="redis-usage-title">{{ t("panel.cache.usage.title") }}</h4>
      <p>{{ t("panel.cache.usage.application") }}</p>
      <p v-if="!redisMetrics" role="status">{{ t(!status ? "panel.cache.usage.unavailable" : !status.redis.isInstalled ? "panel.cache.usage.notInstalled" : isStopped(status.redis) ? "panel.cache.usage.stopped" : "panel.cache.usage.unavailable") }}</p>
      <template v-else>
        <dl class="redis-summary">
          <div><dt>{{ t("panel.cache.usage.measuredAt") }}</dt><dd>{{ measurementTime(redisMetrics.measuredAtUtc) }}</dd></div>
          <div><dt>{{ t("panel.cache.metrics.memory") }}</dt><dd>{{ redisMetrics.usedMemory || "—" }}</dd></div>
          <div><dt>{{ t("panel.cache.metrics.keys") }}</dt><dd>{{ redisMetrics.keys }}</dd></div>
          <div><dt>{{ t("panel.cache.usage.hits") }}</dt><dd>{{ redisMetrics.hits }}</dd></div>
          <div><dt>{{ t("panel.cache.usage.misses") }}</dt><dd>{{ redisMetrics.misses }}</dd></div>
        </dl>
        <p>{{ t("panel.cache.usage.cumulative") }}</p>
        <p class="redis-evidence" role="status">{{ t(readDelta === null ? "panel.cache.usage.firstMeasurement" : readDelta > 0 ? "panel.cache.usage.readActivity" : "panel.cache.usage.noReads", { count: readDelta }) }}</p>
        <p v-if="redisMetrics.keys > 0">{{ t("panel.cache.usage.storedData") }}</p>
        <h5>{{ t("panel.cache.usage.databases") }}</h5>
        <p>{{ t("panel.cache.usage.databaseHint") }}</p>
        <p v-if="!redisMetrics.databases">{{ t("panel.cache.usage.unavailable") }}</p>
        <p v-else-if="!redisMetrics.databases.length">{{ t("panel.cache.usage.noDatabases") }}</p>
        <ul v-else class="redis-list">
          <li v-for="database in redisMetrics.databases" :key="database.index">
            <strong>db{{ database.index }}</strong> · {{ t("panel.cache.metrics.keys") }}: {{ database.keys }} ·
            {{ t("panel.cache.usage.expires") }}: {{ database.expires ?? "—" }} ·
            {{ t("panel.cache.usage.ttl") }}: {{ database.averageTtlMilliseconds ?? "—" }}
          </li>
        </ul>
        <h5>{{ t("panel.cache.usage.connections") }}</h5>
        <p>{{ t("panel.cache.usage.clientHint") }}</p>
        <p v-if="clientsState !== 'measured'">{{ t(clientsState === "unknown" ? "panel.cache.usage.clientsUnknown" : "panel.cache.usage.clientsEmpty") }}</p>
        <ul v-else class="redis-list">
          <li v-for="(client, index) in redisMetrics.clients" :key="index">
            <strong>{{ client.name || t("panel.cache.usage.unnamed") }}</strong> · db{{ client.databaseIndex }} ·
            {{ t("panel.cache.usage.idle", { seconds: client.idleSeconds }) }}
          </li>
        </ul>
      </template>
    </article>
  </section>
</template>

<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from "vue";
import { useI18n } from "vue-i18n";
import { getCacheStatus, startMemcached, startRedis, stopMemcached, stopRedis, type CacheMetrics, type CacheRuntime, type CacheStatus } from "../api/cache";
import { redisReadDelta, redisClientsState } from "../utils/redisUsage";
import { useConfirm } from "../components/ConfirmDialog.vue";
import { extractApiMessage } from "../stores/auth";
import { useSoftwareStore } from "../stores/software";
import { useSubscriptionGate } from "../composables/useSubscriptionGate";
import { getSessionEpoch } from "../api/client";

const { t, locale } = useI18n();
// Askidaki abonelikte YAZMA dugmeleri bastan devre disi + sebebi title/aria ile soylenir
// (bkz. composables/useSubscriptionGate.ts). Okuma dugmeleri kapiya girmez.
const { writeGate, locked } = useSubscriptionGate();
const { confirm } = useConfirm();
const softwareStore = useSoftwareStore();
const status = ref<CacheStatus | null>(null);
const statusError = ref(false);
let previousRedisMetrics: CacheMetrics | null = null;
const readDelta = ref<number | null>(null);
const redisMetrics = computed(() => status.value?.redis.isInstalled && isRunning(status.value.redis) ? status.value.redis.metrics ?? null : null);
const clientsState = computed(() => redisClientsState(redisMetrics.value?.clients));
function resetUsage() { previousRedisMetrics = null; readDelta.value = null; }
function measurementTime(value?: string) {
  if (!value) return "—";
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? "—" : date.toLocaleString(locale?.value);
}
const sessionEpoch = getSessionEpoch();
let disposed = false, statusVersion = 0;
const isActive = () => !disposed && sessionEpoch === getSessionEpoch();
const busyAction = ref("");
const notice = ref("");
const noticeError = ref(false);
const runtimes = computed(() => status.value ? [status.value.redis, status.value.memcached] : []);
const installedCount = computed(() => status.value ? runtimes.value.filter((item) => item.isInstalled).length : null);
const runningCount = computed(() => !status.value || runtimes.value.some(item => item.isInstalled && !isRunning(item) && !isStopped(item))
  ? null : runtimes.value.filter(isRunning).length);

onMounted(refresh);
onUnmounted(() => { disposed = true; ++statusVersion; resetUsage(); });

async function refresh() {
  if (!isActive()) { resetUsage(); return; }
  if (busyAction.value) return;
  busyAction.value = "refresh";
  try {
    await readStatus();
    if (isActive()) showNotice("", false);
  } catch (error) {
    if (isActive()) {
      resetUsage();
      status.value = null;
      statusError.value = true;
      showNotice(resolveApiMessage(error), true);
    }
  } finally {
    if (isActive()) busyAction.value = "";
  }
}

async function readStatus() {
  const version = ++statusVersion;
  statusError.value = false;
  const result = await getCacheStatus();
  if (!isActive()) { resetUsage(); return null; }
  if (version !== statusVersion) return null;
  status.value = result;
  const metrics = redisMetrics.value;
  if (metrics) {
    readDelta.value = redisReadDelta(previousRedisMetrics, metrics);
    previousRedisMetrics = { ...metrics };
  } else resetUsage();
  return result;
}

function findRuntime(componentId: string) {
  return ["redis", "memcached"].includes(componentId)
    ? runtimes.value.find(item => item.componentId === componentId) : undefined;
}

function canMutate() {
  return isActive() && status.value !== null && !busyAction.value && !locked.value;
}

async function mutate(componentId: string, action: string, worker: () => Promise<unknown>, successKey: string, verified: (item: CacheRuntime) => boolean) {
  if (!canMutate()) return;
  ++statusVersion;
  if (componentId === "redis") resetUsage();
  busyAction.value = `${componentId}:${action}`;
  let requestCompleted = false;
  try {
    await worker();
    requestCompleted = true;
    if (!isActive()) return;
    await readStatus();
    if (!isActive()) return;
    const runtime = findRuntime(componentId);
    const confirmed = !!runtime && verified(runtime);
    showNotice(t(confirmed ? successKey : "panel.cache.resultUnconfirmed"), !confirmed);
  } catch (error) {
    if (!isActive()) return;
    resetUsage();
    status.value = null;
    statusError.value = true;
    const message = resolveApiMessage(error);
    showNotice(requestCompleted ? `${t("panel.cache.resultUnconfirmed")}\n\n${message}` : message, true);
  } finally {
    if (isActive()) busyAction.value = "";
  }
}

async function install(componentId: string) {
  const runtime = findRuntime(componentId);
  if (!runtime || runtime.isInstalled) return;
  await mutate(componentId, "install", () => softwareStore.installComponent(componentId), "panel.cache.installedMessage", item => item.isInstalled);
}

async function remove(componentId: string) {
  const runtime = findRuntime(componentId);
  if (!canMutate() || !runtime?.isInstalled || !isStopped(runtime)) return;
  const version = statusVersion;
  const displayName = runtime.displayName || componentId;
  const impact: string[] = [];
  if (runtime?.serviceName) {
    impact.push(`${t("panel.cache.service")}: ${runtime.serviceName}`);
  }
  if (runtime?.installDir) {
    impact.push(`${t("panel.software.installDir")}: ${runtime.installDir}`);
  }

  // Yüksek riskli onay: bileşen kaldırma → bileşen adı yazılmadan devam edilemez.
  await confirm({
    title: t("panel.confirm.removeComponentTitle", { name: displayName }),
    message: t("panel.cache.confirmRemove"),
    impact,
    requireText: displayName,
    confirmLabel: t("panel.confirm.remove"),
    busyLabel: t("panel.confirm.removing"),
    action: async () => {
      if (!canMutate() || version !== statusVersion) return;
      await mutate(componentId, "remove", () => softwareStore.removeComponent(componentId), "panel.cache.removedMessage", item => !item.isInstalled);
    },
  });
}

async function toggle(componentId: string, shouldStart: boolean) {
  const runtime = findRuntime(componentId);
  if (!runtime?.isInstalled || (shouldStart ? !isStopped(runtime) : !isRunning(runtime))) return;
  const worker = componentId === "memcached"
    ? (shouldStart ? startMemcached : stopMemcached)
    : (shouldStart ? startRedis : stopRedis);
  await mutate(componentId, shouldStart ? "start" : "stop", worker,
    shouldStart ? "panel.cache.started" : "panel.cache.stopped",
    item => shouldStart ? item.isInstalled && isRunning(item) : !item.isInstalled || isStopped(item));
}

function isRunning(item: CacheRuntime) {
  return ["running", "started"].includes(item.status.trim().toLowerCase());
}

function isStopped(item: CacheRuntime) {
  return item.status.trim().toLowerCase() === "stopped";
}

function hitRate(metrics: { hits: number; misses: number }): string {
  const total = metrics.hits + metrics.misses;
  return total > 0 ? `${Math.round((metrics.hits / total) * 100)}%` : "—";
}

function statusLabel(item: CacheRuntime) {
  if (!item.isInstalled) return t("panel.database.notInstalled");
  if (!item.status.trim() || item.status.trim().toLowerCase() === "unknown") return t("panel.health.statusUnknown");
  return isRunning(item) ? t("panel.cache.runningState")
    : isStopped(item) ? t("panel.cache.stoppedState") : item.status;
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
.redis-usage { margin-top: 1.5rem; padding: 1rem; border: 1px solid var(--border-color, #64748b55); border-radius: 12px; overflow-wrap: anywhere; }
.redis-usage h4, .redis-usage h5 { margin: 0 0 .6rem; }
.redis-usage h5 { margin-top: 1.3rem; }
.redis-usage p { line-height: 1.6; }
.redis-summary { display: grid; grid-template-columns: repeat(auto-fit, minmax(150px, 1fr)); gap: 1rem; }
.redis-summary dt { font-size: .85rem; opacity: .8; }
.redis-summary dd { margin: .35rem 0 0; font-weight: 600; }
.redis-list { padding-left: 1.2rem; display: grid; gap: .6rem; }
.redis-evidence { font-weight: 600; }
</style>
