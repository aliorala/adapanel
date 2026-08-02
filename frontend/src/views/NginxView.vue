<template>
  <section class="panel module-shell nginx-panel">
    <div class="module-head">
      <div>
        <h3>{{ t("panel.nginx.title") }}</h3>
        <p class="meta">{{ t("panel.nginx.subtitle") }}</p>
      </div>
      <div class="row-actions">
        <button class="secondary compact" :disabled="loading || busy" @click="refresh">
          {{ t("panel.nginx.refresh") }}
        </button>
        <button
          v-if="status.isInstalled"
          class="danger compact"
          :disabled="busy || softwareStore.removingComponentId === 'nginx'"
          @click="remove">
          {{ t("panel.nginx.remove") }}
        </button>
      </div>
    </div>

    <p v-if="notice" :class="['notice', { error: noticeError }]">{{ notice }}</p>

    <!-- Durum sunucudan gelmeden "kurulu değil" ekranı gösteriliyordu; kurulu bir Nginx'i
         yöneticinin yeniden kurmaya çalışmasına yol açan yanıltıcı bir varsayılandı. -->
    <p v-if="!statusLoaded" class="empty-state" role="status" aria-live="polite">
      {{ t("panel.common.loading") }}
    </p>

    <section v-else-if="!status.isInstalled" class="nginx-empty">
      <div class="nginx-mark">NX</div>
      <div>
        <h3>{{ t("panel.nginx.notInstalled") }}</h3>
        <p class="meta">{{ t("panel.nginx.installHint") }}</p>
      </div>
      <button
        class="compact"
        :disabled="softwareStore.installingComponentId === 'nginx'"
        @click="install">
        {{ softwareStore.installingComponentId === "nginx" ? t("panel.software.installing") : t("panel.nginx.install") }}
      </button>
    </section>

    <template v-else>
      <div class="summary-strip nginx-summary">
        <article class="summary-item">
          <span>{{ t("panel.nginx.status") }}</span>
          <strong :class="['status-pill', status.isRunning ? 'ok' : 'warn']">
            {{ status.isRunning ? t("panel.nginx.running") : t("panel.nginx.stopped") }}
          </strong>
        </article>
        <article class="summary-item">
          <span>{{ t("panel.nginx.version") }}</span>
          <strong>{{ status.version || "-" }}</strong>
        </article>
        <article class="summary-item">
          <span>{{ t("panel.nginx.endpoint") }}</span>
          <strong>{{ status.listenAddress }}:{{ status.listenPort }}</strong>
        </article>
        <article class="summary-item">
          <span>{{ t("panel.nginx.startupTask") }}</span>
          <strong>{{ status.isStartupTaskRegistered ? t("panel.status.enabled") : t("panel.status.disabled") }}</strong>
        </article>
      </div>

      <div class="module-toolbar">
        <div class="row-actions">
          <button v-if="!status.isRunning" class="compact" :disabled="busy" @click="changeState('start')">
            {{ t("panel.nginx.start") }}
          </button>
          <button v-else class="secondary compact" :disabled="busy" @click="changeState('stop')">
            {{ t("panel.nginx.stop") }}
          </button>
          <button class="secondary compact" :disabled="busy" @click="changeState('reload')">
            {{ t("panel.nginx.reload") }}
          </button>
          <button class="secondary compact" :disabled="busy" @click="testConfiguration">
            {{ t("panel.nginx.test") }}
          </button>
        </div>
        <button
          v-if="status.accessUrl"
          class="secondary compact"
          :disabled="!status.isRunning"
          @click="openEndpoint">
          {{ t("panel.nginx.open") }}
        </button>
      </div>

      <form class="form-surface nginx-form" @submit.prevent="save">
        <div class="section-head">
          <div>
            <h3>{{ t("panel.nginx.configuration") }}</h3>
            <p class="meta">{{ t("panel.nginx.configurationHint") }}</p>
          </div>
          <span class="status-pill">{{ t("panel.nginx.iisUpstream") }}</span>
        </div>

        <div class="form-grid nginx-grid">
          <label class="stacked-field">
            <span class="field-label">{{ t("panel.nginx.listenAddress") }}</span>
            <input v-model.trim="form.listenAddress" required maxlength="64" />
          </label>
          <label class="stacked-field">
            <span class="field-label">{{ t("panel.sites.port") }}</span>
            <input v-model.number="form.listenPort" type="number" min="1" max="65535" required />
          </label>
          <label class="stacked-field">
            <span class="field-label">{{ t("panel.nginx.serverName") }}</span>
            <input v-model.trim="form.serverName" required maxlength="253" />
          </label>
          <label class="stacked-field upstream-field">
            <span class="field-label">{{ t("panel.nginx.upstream") }}</span>
            <input v-model.trim="form.upstreamUrl" type="url" required maxlength="2048" />
          </label>
          <label class="stacked-field">
            <span class="field-label">{{ t("panel.nginx.cacheSize") }}</span>
            <input v-model.number="form.cacheSizeMb" type="number" min="16" max="8192" :disabled="!form.cacheEnabled" />
          </label>
          <label class="check-card">
            <input v-model="form.cacheEnabled" type="checkbox" />
            <span>
              <strong>{{ t("panel.nginx.cache") }}</strong>
              <small>{{ t("panel.nginx.cacheHint") }}</small>
            </span>
          </label>
        </div>

        <div class="row-actions">
          <button class="compact" type="submit" :disabled="busy">{{ t("panel.nginx.save") }}</button>
          <span class="meta">{{ t("panel.nginx.safeDefault") }}</span>
        </div>
      </form>

      <details class="nginx-details">
        <summary>{{ t("panel.nginx.technicalDetails") }}</summary>
        <dl>
          <div><dt>{{ t("panel.software.installDir") }}</dt><dd>{{ status.installDir }}</dd></div>
          <div><dt>{{ t("panel.nginx.configPath") }}</dt><dd>{{ status.configPath }}</dd></div>
          <div><dt>{{ t("panel.nginx.lastTest") }}</dt><dd>{{ status.lastConfigTest || t("panel.nginx.testNotRun") }}</dd></div>
        </dl>
      </details>

      <p class="nginx-limit">{{ t("panel.nginx.windowsLimit") }}</p>
    </template>
  </section>
</template>

<script setup lang="ts">
import { onMounted, reactive, ref } from "vue";
import { useI18n } from "vue-i18n";
import {
  configureNginx,
  getNginxStatus,
  reloadNginx,
  startNginx,
  stopNginx,
  testNginxConfiguration,
  type NginxConfiguration,
  type NginxStatus
} from "../api/nginx";
import { openReachable } from "../utils/url";
import { useConfirm } from "../components/ConfirmDialog.vue";
import { extractApiMessage } from "../stores/auth";
import { useSoftwareStore } from "../stores/software";

const { t } = useI18n();
const { confirm } = useConfirm();
const softwareStore = useSoftwareStore();
const loading = ref(false);
const busy = ref(false);
// Durum okunana kadar isInstalled=false varsayılanı "kurulu değil" olarak görünüyordu.
const statusLoaded = ref(false);
const notice = ref("");
const noticeError = ref(false);
const status = reactive<NginxStatus>({
  isInstalled: false,
  isRunning: false,
  isStartupTaskRegistered: false,
  status: "NotInstalled",
  version: "",
  installDir: "",
  configPath: "",
  listenAddress: "127.0.0.1",
  listenPort: 8088,
  serverName: "_",
  upstreamUrl: "http://127.0.0.1:80",
  cacheEnabled: false,
  cacheSizeMb: 256,
  lastConfigTest: "",
  accessUrl: ""
});
const form = reactive<NginxConfiguration>({
  listenAddress: "127.0.0.1",
  listenPort: 8088,
  serverName: "_",
  upstreamUrl: "http://127.0.0.1:80",
  cacheEnabled: false,
  cacheSizeMb: 256
});

onMounted(async () => {
  if (!softwareStore.catalog) {
    await softwareStore.fetchCatalog().catch(() => undefined);
  }
  await refresh();
});

async function refresh() {
  loading.value = true;
  try {
    applyStatus(await getNginxStatus());
    statusLoaded.value = true;
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    loading.value = false;
  }
}

async function install() {
  busy.value = true;
  try {
    await softwareStore.installComponent("nginx");
    await refresh();
    showNotice(t("panel.nginx.installed"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    busy.value = false;
  }
}

async function remove() {
  const impact: string[] = [];
  if (status.installDir) {
    impact.push(`${t("panel.software.installDir")}: ${status.installDir}`);
  }
  impact.push(t("panel.confirm.impactNginxProxy"));

  // Yüksek riskli onay: bileşen kaldırma → "nginx" yazılmadan devam edilemez.
  await confirm({
    title: t("panel.confirm.removeComponentTitle", { name: "Nginx" }),
    message: t("panel.nginx.confirmRemove"),
    impact,
    requireText: "nginx",
    confirmLabel: t("panel.confirm.remove"),
    busyLabel: t("panel.confirm.removing"),
    action: async () => {
      busy.value = true;
      try {
        await softwareStore.removeComponent("nginx");
        await refresh();
        showNotice(t("panel.nginx.removed"), false);
      } catch (error) {
        showNotice(resolveApiMessage(error), true);
      } finally {
        busy.value = false;
      }
    },
  });
}

async function save() {
  await runAction(() => configureNginx({ ...form }), "panel.nginx.saved");
}

async function changeState(action: "start" | "stop" | "reload") {
  const operation = action === "start" ? startNginx : action === "stop" ? stopNginx : reloadNginx;
  const successKey = action === "start"
    ? "panel.nginx.started"
    : action === "stop"
      ? "panel.nginx.stoppedMessage"
      : "panel.nginx.reloaded";
  await runAction(operation, successKey);
}

async function testConfiguration() {
  await runAction(testNginxConfiguration, "panel.nginx.testPassed");
}

async function runAction(operation: () => Promise<NginxStatus>, successKey: string) {
  busy.value = true;
  try {
    applyStatus(await operation());
    showNotice(t(successKey), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    busy.value = false;
  }
}

function applyStatus(value: NginxStatus) {
  Object.assign(status, value);
  if (value.isInstalled) {
    Object.assign(form, {
      listenAddress: value.listenAddress,
      listenPort: value.listenPort,
      serverName: value.serverName,
      upstreamUrl: value.upstreamUrl,
      cacheEnabled: value.cacheEnabled,
      cacheSizeMb: value.cacheSizeMb
    });
  }
}

function openEndpoint() {
  openReachable(status.accessUrl);
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
.nginx-panel {
  overflow: hidden;
}

.nginx-empty {
  display: grid;
  grid-template-columns: auto minmax(0, 1fr) auto;
  gap: 14px;
  align-items: center;
  padding: 18px;
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-md);
  background: linear-gradient(135deg, color-mix(in srgb, var(--accent-1) 8%, transparent), var(--input-bg));
}

.nginx-empty p {
  margin: 5px 0 0;
}

.nginx-mark {
  display: grid;
  place-items: center;
  width: 44px;
  height: 44px;
  border-radius: 13px;
  background: color-mix(in srgb, var(--accent-1) 14%, transparent);
  color: var(--accent-1);
  font-weight: 800;
  letter-spacing: 0.08em;
}

.nginx-summary {
  grid-template-columns: repeat(4, minmax(0, 1fr));
}

.nginx-grid {
  grid-template-columns: 1fr 120px 1fr;
}

.upstream-field {
  grid-column: span 2;
}

.check-card {
  display: flex;
  gap: 10px;
  align-items: center;
  min-height: 62px;
  padding: 10px 12px;
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-sm);
  background: color-mix(in srgb, var(--accent-1) 5%, transparent);
}

.check-card input {
  width: auto;
}

.check-card span {
  display: grid;
  gap: 2px;
}

.check-card small,
.nginx-limit {
  color: var(--text-muted);
}

.nginx-details {
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-md);
  background: var(--input-bg);
}

.nginx-details summary {
  padding: 11px 13px;
  cursor: pointer;
  color: var(--text-soft);
  font-weight: 700;
}

.nginx-details dl {
  display: grid;
  margin: 0;
  border-top: 1px solid var(--panel-border);
}

.nginx-details dl div {
  display: grid;
  grid-template-columns: 170px minmax(0, 1fr);
  gap: 12px;
  padding: 9px 13px;
  border-bottom: 1px solid var(--panel-border);
}

.nginx-details dl div:last-child {
  border-bottom: 0;
}

.nginx-details dt {
  color: var(--text-muted);
}

.nginx-details dd {
  margin: 0;
  word-break: break-word;
  white-space: pre-wrap;
}

.nginx-limit {
  margin: 0;
  padding-left: 11px;
  border-left: 2px solid var(--warn-1);
}

@media (max-width: 900px) {
  .nginx-summary,
  .nginx-grid {
    grid-template-columns: 1fr 1fr;
  }

  .upstream-field {
    grid-column: span 2;
  }
}

@media (max-width: 620px) {
  .nginx-empty,
  .nginx-summary,
  .nginx-grid {
    grid-template-columns: 1fr;
  }

  .nginx-mark {
    display: none;
  }

  .upstream-field {
    grid-column: auto;
  }

  .nginx-details dl div {
    grid-template-columns: 1fr;
    gap: 3px;
  }
}
</style>
