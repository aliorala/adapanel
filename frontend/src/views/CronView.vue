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
        <label v-if="isAdmin">
          <span class="field-label">{{ t("panel.tasks.taskType") }}</span>
          <select v-model="form.taskType">
            <option value="command">{{ t("panel.tasks.typeCommand") }}</option>
            <option value="php">{{ t("panel.tasks.typePhp") }}</option>
            <option value="url">{{ t("panel.tasks.typeUrl") }}</option>
          </select>
        </label>
        <label v-if="form.taskType === 'url'" class="span-2">
          <span class="field-label">{{ t("panel.tasks.url") }}</span>
          <input v-model.trim="form.url" placeholder="https://ornek.com/cron/calistir?key=..." spellcheck="false" />
          <small class="field-hint">{{ t("panel.tasks.urlHint") }}</small>
        </label>
        <label v-if="form.taskType !== 'url'">
          <span class="field-label">{{ form.taskType === 'php' ? t("panel.tasks.scriptPath") : t("panel.tasks.command") }}</span>
          <input v-model.trim="form.command" :placeholder="form.taskType === 'php' ? 'C:\\inetpub\\wwwroot\\site\\httpdocs\\cron.php' : 'powershell.exe'" />
          <small v-if="form.taskType === 'php'" class="field-hint">{{ t("panel.tasks.phpHint") }}</small>
        </label>
        <label v-if="form.taskType !== 'url'"><span class="field-label">{{ t("panel.tasks.arguments") }}</span><input v-model="form.arguments" /></label>
        <label v-if="form.taskType === 'command'"><span class="field-label">{{ t("panel.tasks.workingDirectory") }}</span><input v-model.trim="form.workingDirectory" /></label>
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
        <button :disabled="saving || !form.name || (form.taskType === 'url' ? !form.url : !form.command)" @click="saveTask">
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
        <span>{{ t("panel.tasks.lastResult") }}</span>
        <span>{{ t("panel.tasks.nextRun") }}</span>
        <span>{{ t("panel.tasks.lastRun") }}</span>
        <span>{{ t("panel.files.actions") }}</span>
      </div>
      <div v-for="item in filteredTasks" :key="item.name" class="data-row cron-row">
        <span class="cell-main">
          <strong>{{ item.name }}</strong>
          <small>{{ typeLabel(item.taskType) }}</small>
        </span>
        <span class="cell-main">
          <strong>{{ item.taskType === "url" ? item.url || item.arguments : item.command }}</strong>
          <small>{{ item.taskType === "url" ? "-" : item.arguments || "-" }}</small>
        </span>
        <span>{{ scheduleLabel(item.scheduleType) }}</span>
        <span :class="['status-pill', taskStateClass(item.state)]">{{ item.state || "-" }}</span>
        <span :class="['status-pill', resultClass(item)]">{{ resultLabel(item) }}</span>
        <span>{{ formatUtc(item.nextRunTimeUtc) }}</span>
        <span>{{ formatUtc(item.lastRunTimeUtc) }}</span>
        <span class="row-actions">
          <button class="secondary compact" :disabled="runningName === item.name" @click="runTask(item.name)">
            {{ runningName === item.name ? t("panel.tasks.running") : t("panel.tasks.runNow") }}
          </button>
          <button class="secondary compact" :disabled="togglingName === item.name" @click="toggleTask(item)">
            {{ isDisabled(item) ? t("panel.tasks.enableTask") : t("panel.tasks.disableTask") }}
          </button>
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
import { createScheduledTask, deleteScheduledTask, getScheduledTasks, runScheduledTask, setScheduledTaskEnabled, type ScheduledTask } from "../api/cron";
import { useConfirm } from "../components/ConfirmDialog.vue";
import { extractApiDetails, extractApiMessage, useAuthStore } from "../stores/auth";

const { t } = useI18n();
const { confirm } = useConfirm();
const authStore = useAuthStore();
// Müşteri/bayi yalnızca URL görevi kurar (backend de zorlar); tip seçici yalnız admin'de görünür.
const isAdmin = computed(() => authStore.session?.user?.role === "Administrator");
const tasks = ref<ScheduledTask[]>([]);
const query = ref("");
const formOpen = ref(false);
const loading = ref(false);
const saving = ref(false);
const deletingName = ref("");
const runningName = ref("");
const togglingName = ref("");
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

onMounted(() => {
  if (!isAdmin.value) {
    form.taskType = "url";
  }
  void refresh();
});

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
  const task = tasks.value.find((item) => item.name === name);
  const impact = [`${t("panel.tasks.name")}: ${name}`];
  if (task?.command) {
    impact.push(`${t("panel.tasks.command")}: ${task.command}`);
  }

  // Normal onay: tek bir zamanlanmış görev silinir, veri/dosya kaybı yok → ad yazdırmaya gerek yok.
  await confirm({
    title: t("panel.confirm.deleteTaskTitle", { name }),
    message: t("panel.tasks.confirmDelete"),
    impact,
    action: async () => {
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
    },
  });
}

function defaultForm() {
  return {
    name: "",
    taskType: "command",
    command: "powershell.exe",
    arguments: "",
    workingDirectory: "",
    url: "",
    phpVersion: "",
    scheduleType: "hourly",
    interval: 1,
    timeOfDay: "00:00"
  };
}

function resetForm() {
  Object.assign(form, defaultForm());
}

// Görevi hemen tetikle; birkaç saniye sonra listeyi yenile ki "son çalışma" sütunu sonucu göstersin.
async function runTask(name: string) {
  runningName.value = name;
  try {
    await runScheduledTask(name);
    showNotice(t("panel.tasks.runStarted"), false);
    window.setTimeout(() => void refresh(), 4000);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    runningName.value = "";
  }
}

function isDisabled(item: ScheduledTask) {
  return item.state.toLocaleLowerCase().includes("disabled");
}

async function toggleTask(item: ScheduledTask) {
  togglingName.value = item.name;
  try {
    await setScheduledTaskEnabled(item.name, isDisabled(item));
    await refresh();
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    togglingName.value = "";
  }
}

// Son çalışma sonucu: 0 = başarılı; 267009/267011/267045 = koşuyor/hiç çalışmadı/kuyrukta (nötr); diğerleri hata kodu.
function resultLabel(item: ScheduledTask) {
  const code = item.lastTaskResult;
  if (code === null || code === undefined || code === 267011) return "-";
  if (code === 0) return t("panel.tasks.resultOk");
  if (code === 267009 || code === 267045) return t("panel.tasks.running");
  return t("panel.tasks.resultFail", { code: "0x" + code.toString(16).toUpperCase() });
}

function resultClass(item: ScheduledTask) {
  const code = item.lastTaskResult;
  if (code === 0) return "ok";
  if (code === null || code === undefined || code === 267011 || code === 267009 || code === 267045) return "";
  return "warn";
}

function typeLabel(value: string) {
  const key = value === "url" ? "panel.tasks.typeUrl" : value === "php" ? "panel.tasks.typePhp" : "panel.tasks.typeCommand";
  return t(key);
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
  const message = translated === key ? key : translated;
  // Gerçek teknik nedeni (backend Details) da göster: "kaydedilemedi" tek başına teşhis edilemiyor.
  const details = extractApiDetails(error);
  return details ? `${message} — ${details}` : message;
}
</script>

<style scoped>
.cron-row {
  grid-template-columns: 1.1fr 1.6fr 0.65fr 0.7fr 0.8fr 0.95fr 0.95fr 1.25fr;
}

.span-2 {
  grid-column: span 2;
}

.field-hint {
  color: var(--text-muted);
  font-size: 0.78rem;
}

.search-control {
  width: min(300px, 50vw);
}
</style>
