<template>
  <section class="panel module-shell docker-panel">
    <div class="module-head">
      <div>
        <h2>{{ t("panel.docker.title") }}</h2>
        <p class="meta">{{ t("panel.docker.subtitle") }}</p>
      </div>
      <button class="secondary compact" :disabled="loading" @click="refresh">{{ t("panel.docker.refresh") }}</button>
    </div>

    <p v-if="notice" :class="['notice', { error: noticeError }]">{{ notice }}</p>

    <div v-if="loading && !workspace" class="empty-state">{{ t("panel.common.loading") }}</div>
    <div v-else-if="workspace && !workspace.installed" class="empty-state">{{ t("panel.docker.notInstalled") }}</div>
    <div v-else-if="workspace && !workspace.daemonRunning" class="empty-state">{{ t("panel.docker.daemonDown") }}</div>

    <template v-else-if="workspace">
      <div class="summary-strip">
        <span class="status-pill ok">Docker {{ workspace.version }}</span>
        <span class="status-pill">{{ workspace.containers.length }} {{ t("panel.docker.containers") }}</span>
        <span class="status-pill">{{ workspace.images.length }} {{ t("panel.docker.images") }}</span>
      </div>

      <div class="module-toolbar">
        <div class="segmented-tabs">
          <button :class="{ active: tab === 'containers' }" @click="tab = 'containers'">{{ t("panel.docker.containers") }}</button>
          <button :class="{ active: tab === 'images' }" @click="tab = 'images'">{{ t("panel.docker.images") }}</button>
          <button :class="{ active: tab === 'run' }" @click="tab = 'run'">{{ t("panel.docker.run") }}</button>
        </div>
      </div>

      <div v-if="tab === 'containers'" class="data-table">
        <div class="data-row table-head docker-row">
          <span>{{ t("panel.docker.name") }}</span>
          <span>{{ t("panel.docker.image") }}</span>
          <span>{{ t("panel.docker.state") }}</span>
          <span>{{ t("panel.docker.ports") }}</span>
          <span>{{ t("panel.software.action") }}</span>
        </div>
        <div v-for="c in workspace.containers" :key="c.id" class="data-row docker-row">
          <div class="cell-main"><strong>{{ c.name }}</strong><small>{{ c.id.slice(0, 12) }}</small></div>
          <span class="cell-muted">{{ c.image }}</span>
          <span :class="['status-pill', c.state === 'running' ? 'ok' : 'warn']">{{ c.status }}</span>
          <span class="cell-muted">{{ c.ports || "—" }}</span>
          <div class="row-actions">
            <button v-if="c.state !== 'running'" class="secondary compact row-button" :disabled="busy" @click="action(c.id, 'start')">{{ t("panel.docker.start") }}</button>
            <button v-else class="secondary compact row-button" :disabled="busy" @click="action(c.id, 'stop')">{{ t("panel.docker.stop") }}</button>
            <button class="secondary compact row-button" :disabled="busy" @click="action(c.id, 'restart')">{{ t("panel.docker.restart") }}</button>
            <button class="secondary compact row-button" :disabled="busy" @click="showLogs(c.id)">{{ t("panel.docker.logs") }}</button>
            <button class="secondary compact row-button danger" :disabled="busy" @click="removeContainer(c.id)">{{ t("panel.docker.remove") }}</button>
          </div>
        </div>
        <p v-if="workspace.containers.length === 0" class="empty-state">{{ t("panel.docker.noContainers") }}</p>
        <pre v-if="logs" class="docker-logs">{{ logs }}</pre>
      </div>

      <div v-else-if="tab === 'images'" class="data-table">
        <div class="form-surface">
          <label class="inline-field">
            <span class="field-label">{{ t("panel.docker.pull") }}</span>
            <input v-model.trim="pullImage" placeholder="nginx:latest" />
            <button class="compact" :disabled="busy || !pullImage" @click="pull">{{ t("panel.docker.pullAction") }}</button>
          </label>
        </div>
        <div class="data-row table-head image-row">
          <span>{{ t("panel.docker.repository") }}</span>
          <span>{{ t("panel.docker.tag") }}</span>
          <span>{{ t("panel.docker.size") }}</span>
          <span>{{ t("panel.software.action") }}</span>
        </div>
        <div v-for="img in workspace.images" :key="img.id" class="data-row image-row">
          <strong>{{ img.repository }}</strong>
          <span>{{ img.tag }}</span>
          <span class="cell-muted">{{ img.size }}</span>
          <button class="secondary compact row-button danger" :disabled="busy" @click="removeImage(img.id)">{{ t("panel.docker.remove") }}</button>
        </div>
        <p v-if="workspace.images.length === 0" class="empty-state">{{ t("panel.docker.noImages") }}</p>
      </div>

      <section v-else class="form-surface">
        <div class="form-grid">
          <label class="stacked-field">
            <span class="field-label">{{ t("panel.docker.image") }}</span>
            <input v-model.trim="runForm.image" placeholder="nginx:latest" />
          </label>
          <label class="stacked-field">
            <span class="field-label">{{ t("panel.docker.name") }}</span>
            <input v-model.trim="runForm.name" placeholder="my-web" />
          </label>
          <label class="stacked-field">
            <span class="field-label">{{ t("panel.docker.portsLabel") }}</span>
            <input v-model.trim="runForm.ports" placeholder="8080:80, 8443:443" />
          </label>
          <label class="stacked-field">
            <span class="field-label">{{ t("panel.docker.envLabel") }}</span>
            <textarea v-model="runForm.environment" rows="3" placeholder="KEY=VALUE"></textarea>
          </label>
          <label class="toggle-field">
            <input v-model="runForm.autoRestart" type="checkbox" />
            <span>{{ t("panel.docker.autoRestart") }}</span>
          </label>
        </div>
        <div class="row-actions">
          <button class="compact" :disabled="busy || !runForm.image" @click="run">{{ t("panel.docker.runAction") }}</button>
        </div>
      </section>
    </template>
  </section>
</template>

<script setup lang="ts">
import { onMounted, reactive, ref } from "vue";
import { useI18n } from "vue-i18n";
import {
  dockerContainerAction,
  getDocker,
  getDockerLogs,
  pullDockerImage,
  removeDockerContainer,
  removeDockerImage,
  runDockerContainer,
  type DockerWorkspace
} from "../api/docker";
import { extractApiMessage } from "../stores/auth";

const { t } = useI18n();
const workspace = ref<DockerWorkspace | null>(null);
const tab = ref<"containers" | "images" | "run">("containers");
const loading = ref(false);
const busy = ref(false);
const notice = ref("");
const noticeError = ref(false);
const logs = ref("");
const pullImage = ref("");
const runForm = reactive({ image: "", name: "", ports: "", environment: "", autoRestart: true });

onMounted(refresh);

async function refresh() {
  loading.value = true;
  try {
    workspace.value = await getDocker();
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    loading.value = false;
  }
}

async function action(id: string, kind: "start" | "stop" | "restart") {
  await run2(async () => {
    workspace.value = await dockerContainerAction(id, kind);
  });
}

async function removeContainer(id: string) {
  if (!window.confirm(t("panel.docker.confirmRemove"))) return;
  await run2(async () => {
    workspace.value = await removeDockerContainer(id);
  });
}

async function showLogs(id: string) {
  await run2(async () => {
    const result = await getDockerLogs(id, 200);
    logs.value = result.output || t("panel.docker.noLogs");
  });
}

async function pull() {
  await run2(async () => {
    workspace.value = await pullDockerImage(pullImage.value);
    pullImage.value = "";
    showNotice(t("panel.docker.pulled"), false);
  });
}

async function removeImage(id: string) {
  if (!window.confirm(t("panel.docker.confirmRemove"))) return;
  await run2(async () => {
    workspace.value = await removeDockerImage(id);
  });
}

async function run() {
  await run2(async () => {
    workspace.value = await runDockerContainer({ ...runForm });
    runForm.image = "";
    runForm.name = "";
    runForm.ports = "";
    runForm.environment = "";
    tab.value = "containers";
    showNotice(t("panel.docker.started"), false);
  });
}

async function run2(operation: () => Promise<void>) {
  busy.value = true;
  try {
    await operation();
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    busy.value = false;
  }
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
.docker-panel {
  display: grid;
  gap: 12px;
  align-content: start;
}

.summary-strip {
  display: flex;
  gap: 8px;
  flex-wrap: wrap;
}

.docker-row {
  grid-template-columns: minmax(160px, 1.3fr) minmax(140px, 1.2fr) 0.9fr 0.9fr auto;
}

.image-row {
  grid-template-columns: minmax(200px, 1.6fr) 0.7fr 0.7fr auto;
}

.row-actions {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
}

.row-actions button {
  width: auto;
}

.inline-field {
  display: flex;
  gap: 8px;
  align-items: center;
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

.danger {
  color: var(--danger-text);
}

.docker-logs {
  max-height: 320px;
  overflow: auto;
  margin: 8px 0 0;
  padding: 12px;
  border-radius: var(--radius-sm);
  background: #0a0e16;
  color: var(--accent-text);
  white-space: pre-wrap;
}
</style>
