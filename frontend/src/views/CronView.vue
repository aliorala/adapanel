<template>
  <section class="panel module-shell">
    <div class="module-head">
      <div>
        <h2>{{ t("panel.tasks.title") }}</h2>
        <p>{{ t("panel.tasks.subtitle") }}</p>
      </div>
      <button class="secondary compact" :disabled="loading" @click="refresh">
        {{ t("panel.tasks.refresh") }}
      </button>
    </div>

    <p v-if="notice" :class="['notice', { error: noticeError }]">{{ notice }}</p>

    <div class="module-toolbar">
      <div class="toolbar-group">
        <button @click="formOpen = !formOpen">
          {{ formOpen ? t("panel.tasks.closeForm") : t("panel.tasks.add") }}
        </button>
        <span class="status-pill ok">{{ tasks.length }} {{ t("panel.tasks.taskCount") }}</span>
      </div>
      <input v-model.trim="query" class="search-control" :placeholder="t('panel.tasks.search')" />
    </div>

    <section v-if="formOpen" class="form-surface">
      <div class="form-grid">
        <label><span class="field-label">{{ t("panel.tasks.name") }}</span><input v-model.trim="form.name" /></label>
        <label><span class="field-label">{{ t("panel.tasks.command") }}</span><input v-model.trim="form.command" /></label>
        <label><span class="field-label">{{ t("panel.tasks.arguments") }}</span><input v-model="form.arguments" /></label>
        <label><span class="field-label">{{ t("panel.tasks.workingDirectory") }}</span><input v-model.trim="form.workingDirectory" /></label>
        <label>
          <span class="field-label">{{ t("panel.tasks.scheduleType") }}</span>
          <select v-model="form.scheduleType">
            <option value="hourly">{{ t("panel.tasks.hourly") }}</option>
            <option value="daily">{{ t("panel.tasks.daily") }}</option>
            <option value="minute">{{ t("panel.tasks.minutely") }}</option>
          </select>
        </label>
        <label><span class="field-label">{{ t("panel.tasks.interval") }}</span><input v-model.number="form.interval" type="number" min="1" /></label>
        <label v-if="form.scheduleType === 'daily'">
          <span class="field-label">{{ t("panel.tasks.timeOfDay") }}</span>
          <input v-model="form.timeOfDay" type="time" />
        </label>
      </div>
      <div class="row-actions">
        <button :disabled="saving || !form.name || !form.command" @click="saveTask">
          {{ saving ? t("panel.tasks.saving") : t("panel.tasks.save") }}
        </button>
        <button class="secondary compact" @click="resetForm">{{ t("panel.tasks.reset") }}</button>
      </div>
    </section>

    <div class="data-table">
      <div class="data-row table-head cron-row">
        <span>{{ t("panel.tasks.name") }}</span>
        <span>{{ t("panel.tasks.command") }}</span>
        <span>{{ t("panel.tasks.scheduleType") }}</span>
        <span>{{ t("panel.sites.state") }}</span>
        <span>{{ t("panel.tasks.nextRun") }}</span>
        <span>{{ t("panel.tasks.lastRun") }}</span>
        <span>{{ t("panel.files.actions") }}</span>
      </div>
      <div v-for="item in filteredTasks" :key="item.name" class="data-row cron-row">
        <span class="cell-main"><strong>{{ item.name }}</strong><small>{{ item.workingDirectory || "-" }}</small></span>
        <span class="cell-main"><strong>{{ item.command }}</strong><small>{{ item.arguments || "-" }}</small></span>
        <span>{{ scheduleLabel(item.scheduleType) }}</span>
        <span :class="['status-pill', taskStateClass(item.state)]">{{ item.state || "-" }}</span>
        <span>{{ formatUtc(item.nextRunTimeUtc) }}</span>
        <span>{{ formatUtc(item.lastRunTimeUtc) }}</span>
        <span class="row-actions">
          <button class="danger compact" :disabled="deletingName === item.name" @click="removeTask(item.name)">
            {{ deletingName === item.name ? t("panel.database.removing") : t("panel.sites.delete") }}
          </button>
        </span>
      </div>
      <p v-if="loading && filteredTasks.length === 0" class="empty-state">{{ t("panel.common.loading") }}</p>
      <p v-else-if="filteredTasks.length === 0" class="empty-state">{{ t("panel.tasks.empty") }}</p>
    </div>
  </section>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref } from "vue";
import { useI18n } from "vue-i18n";
import { createScheduledTask, deleteScheduledTask, getScheduledTasks, type ScheduledTask } from "../api/cron";
import { extractApiMessage } from "../stores/auth";

const { t } = useI18n();
const tasks = ref<ScheduledTask[]>([]);
const query = ref("");
const formOpen = ref(false);
const loading = ref(false);
const saving = ref(false);
const deletingName = ref("");
const notice = ref("");
const noticeError = ref(false);
const form = reactive(defaultForm());
const filteredTasks = computed(() => {
  const needle = query.value.toLocaleLowerCase();
  return needle
    ? tasks.value.filter((item) => [item.name, item.command, item.arguments, item.state]
        .some((value) => value?.toLocaleLowerCase().includes(needle)))
    : tasks.value;
});

onMounted(refresh);

async function refresh() {
  loading.value = true;
  try {
    tasks.value = await getScheduledTasks();
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    loading.value = false;
  }
}

async function saveTask() {
  saving.value = true;
  try {
    await createScheduledTask({ ...form });
    await refresh();
    resetForm();
    formOpen.value = false;
    showNotice(t("panel.tasks.saved"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    saving.value = false;
  }
}

async function removeTask(name: string) {
  if (!window.confirm(t("panel.tasks.confirmDelete"))) return;
  deletingName.value = name;
  try {
    await deleteScheduledTask(name);
    await refresh();
    showNotice(t("panel.tasks.deleted"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    deletingName.value = "";
  }
}

function defaultForm() {
  return { name: "", command: "powershell.exe", arguments: "", workingDirectory: "", scheduleType: "hourly", interval: 1, timeOfDay: "00:00" };
}

function resetForm() {
  Object.assign(form, defaultForm());
}

function scheduleLabel(value: string) {
  const key = value === "minute" ? "panel.tasks.minutely" : value === "daily" ? "panel.tasks.daily" : "panel.tasks.hourly";
  return t(key);
}

function taskStateClass(value: string) {
  const state = value.toLocaleLowerCase();
  return state.includes("ready") || state.includes("running") ? "ok" : state.includes("disabled") ? "warn" : "";
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
.cron-row {
  grid-template-columns: 1.1fr 1.8fr 0.7fr 0.75fr 1fr 1fr 0.6fr;
}

.search-control {
  width: min(300px, 50vw);
}
</style>
