<template>
  <section class="panel module-shell">
    <div class="module-head">
      <div>
        <h3>{{ t("panel.database.toolSettings.title") }}</h3>
        <p class="meta">{{ t("panel.database.toolSettings.subtitle") }}</p>
      </div>
      <button class="secondary compact" :disabled="loading" @click="load">{{ t("panel.database.toolSettings.refresh") }}</button>
    </div>

    <p v-if="notice" :class="['notice', { error: noticeError }]">{{ notice }}</p>

    <div v-if="loaded" class="form-surface">
      <div class="form-grid">
        <label>
          <span class="field-label">{{ t("panel.database.toolSettings.uploadMax") }}</span>
          <input v-model="form.uploadMaxFilesize" placeholder="512M" />
        </label>
        <label>
          <span class="field-label">{{ t("panel.database.toolSettings.postMax") }}</span>
          <input v-model="form.postMaxSize" placeholder="512M" />
        </label>
        <label>
          <span class="field-label">{{ t("panel.database.toolSettings.memory") }}</span>
          <input v-model="form.memoryLimit" placeholder="512M" />
        </label>
        <label>
          <span class="field-label">{{ t("panel.database.toolSettings.maxExec") }}</span>
          <input v-model.number="form.maxExecutionTime" type="number" min="0" max="86400" />
        </label>
      </div>
      <div class="row-actions">
        <button :disabled="saving" @click="save">
          {{ saving ? t("panel.database.toolSettings.saving") : t("panel.database.toolSettings.save") }}
        </button>
      </div>
      <p class="hint">{{ t("panel.database.toolSettings.help") }}</p>

      <div class="row-actions">
        <button class="secondary" :disabled="redisBusy" @click="enableRedis">
          {{ redisBusy ? t("panel.database.toolSettings.enablingRedis") : t("panel.database.toolSettings.enableRedis") }}
        </button>
      </div>
      <p class="hint">{{ t("panel.database.toolSettings.redisHint") }}</p>
    </div>
  </section>
</template>

<script setup lang="ts">
import { onMounted, reactive, ref } from "vue";
import { useI18n } from "vue-i18n";
import { enableRedisExtension, getGlobalPhpSettings, updateGlobalPhpSettings } from "../api/php";
import { extractApiMessage } from "../stores/auth";

const { t } = useI18n();
const loading = ref(false);
const saving = ref(false);
const redisBusy = ref(false);
const loaded = ref(false);
const notice = ref("");
const noticeError = ref(false);
const form = reactive({
  uploadMaxFilesize: "",
  postMaxSize: "",
  memoryLimit: "",
  maxExecutionTime: 300,
  displayErrors: false
});

onMounted(load);

async function load() {
  loading.value = true;
  try {
    const settings = await getGlobalPhpSettings();
    form.uploadMaxFilesize = settings.uploadMaxFilesize || "";
    form.postMaxSize = settings.postMaxSize || "";
    form.memoryLimit = settings.memoryLimit || "";
    // 0 = süresiz; "|| 300" 0'ı falsy sayıp eziyordu → ?? ile sadece null/undefined'da varsayılan kullan.
    form.maxExecutionTime = settings.maxExecutionTime ?? 300;
    form.displayErrors = settings.displayErrors;
    loaded.value = true;
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    loading.value = false;
  }
}

async function save() {
  saving.value = true;
  try {
    await updateGlobalPhpSettings({
      uploadMaxFilesize: form.uploadMaxFilesize,
      postMaxSize: form.postMaxSize,
      memoryLimit: form.memoryLimit,
      maxExecutionTime: form.maxExecutionTime,
      displayErrors: form.displayErrors
    });
    showNotice(t("panel.database.toolSettings.saved"), false);
    await load();
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    saving.value = false;
  }
}

async function enableRedis() {
  redisBusy.value = true;
  try {
    const result = await enableRedisExtension();
    showNotice(result.message === "already"
      ? t("panel.database.toolSettings.redisAlready")
      : t("panel.database.toolSettings.redisEnabled"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    redisBusy.value = false;
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
.form-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(220px, 1fr));
  gap: 12px;
}
label { display: grid; gap: 6px; }
.row-actions { margin-top: 14px; }
.row-actions button { width: auto; }
.hint { color: var(--text-muted, #94a3b8); font-size: 13px; margin-top: 10px; }
</style>
