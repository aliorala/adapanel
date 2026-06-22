<template>
  <section class="panel module-shell server-health">
    <div class="module-head">
      <div>
        <h3>{{ t("panel.health.title") }}</h3>
        <p class="meta">{{ t("panel.health.subtitle") }}</p>
      </div>
      <button class="secondary compact" :disabled="loading" @click="load">{{ t("panel.health.refresh") }}</button>
    </div>

    <p v-if="notice" :class="['notice', { error: noticeError }]">{{ notice }}</p>

    <div v-if="services.length" class="health-list">
      <article v-for="svc in services" :key="svc.name" class="health-row">
        <div class="health-id">
          <span :class="['state-dot', dotClass(svc.status)]"></span>
          <div class="health-name">
            <strong>{{ svc.label }}</strong>
            <span class="svc-id">{{ svc.name }}</span>
          </div>
        </div>
        <span :class="['status-pill', pillClass(svc.status)]">{{ statusText(svc.status) }}</span>
        <button
          v-if="svc.status !== 'NotInstalled'"
          class="secondary compact"
          :disabled="busyName === svc.name"
          @click="restart(svc.name)">
          {{ busyName === svc.name ? t("panel.health.restarting") : t("panel.health.restart") }}
        </button>
        <span v-else class="na">—</span>
      </article>
    </div>
    <p v-else-if="loaded" class="meta">{{ t("panel.health.empty") }}</p>
  </section>
</template>

<script setup lang="ts">
import { onMounted, ref } from "vue";
import { useI18n } from "vue-i18n";
import { getServerHealth, restartService, type ServiceHealth } from "../api/health";
import { extractApiMessage } from "../stores/auth";

const { t } = useI18n();
const services = ref<ServiceHealth[]>([]);
const loading = ref(false);
const loaded = ref(false);
const busyName = ref("");
const notice = ref("");
const noticeError = ref(false);

onMounted(load);

async function load() {
  loading.value = true;
  notice.value = "";
  try {
    services.value = await getServerHealth();
    loaded.value = true;
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    loading.value = false;
  }
}

async function restart(name: string) {
  busyName.value = name;
  notice.value = "";
  try {
    const updated = await restartService(name);
    const index = services.value.findIndex((svc) => svc.name === updated.name);
    if (index >= 0) {
      services.value[index] = updated;
    }
    showNotice(t("panel.health.restartedNotice", { name: updated.label }), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    busyName.value = "";
  }
}

function statusText(status: string) {
  if (status === "Running") return t("panel.health.statusRunning");
  if (status === "Stopped") return t("panel.health.statusStopped");
  if (status === "NotInstalled") return t("panel.health.statusNotInstalled");
  return t("panel.health.statusUnknown");
}

function pillClass(status: string) {
  if (status === "Running") return "ok";
  if (status === "Stopped") return "danger";
  return "";
}

function dotClass(status: string) {
  if (status === "Running") return "dot-ok";
  if (status === "Stopped") return "dot-danger";
  return "dot-muted";
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
.server-health {
  display: grid;
  gap: 12px;
}

.health-list {
  display: grid;
  gap: 8px;
}

.health-row {
  display: flex;
  align-items: center;
  gap: 14px;
  padding: 11px 14px;
  border: 1px solid var(--panel-border);
  border-radius: 11px;
  background: var(--surface-1);
}

.health-id {
  display: flex;
  align-items: center;
  gap: 12px;
  flex: 1 1 auto;
  min-width: 0;
}

.health-name {
  display: grid;
  gap: 2px;
  min-width: 0;
}

.svc-id {
  color: var(--text-muted);
  font-size: 0.74rem;
  font-family: var(--font-mono, ui-monospace, monospace);
}

.health-row .status-pill {
  flex-shrink: 0;
}

.health-row button {
  width: auto;
  flex-shrink: 0;
}

.na {
  width: 84px;
  text-align: center;
  color: var(--text-muted);
  flex-shrink: 0;
}

.state-dot {
  width: 9px;
  height: 9px;
  border-radius: 999px;
  background: var(--text-muted);
  flex-shrink: 0;
}

.state-dot.dot-ok {
  background: var(--ok-1);
  box-shadow: 0 0 0 3px color-mix(in srgb, var(--ok-1) 22%, transparent);
}

.state-dot.dot-danger {
  background: var(--danger-1);
  box-shadow: 0 0 0 3px color-mix(in srgb, var(--danger-1) 22%, transparent);
}

.state-dot.dot-muted {
  background: var(--text-muted);
}
</style>
