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

    <div class="form-surface remote-db">
      <div class="remote-head">
        <div>
          <h4>{{ t("panel.database.remote.title") }}</h4>
          <p class="meta">{{ t("panel.database.remote.subtitle") }}</p>
        </div>
        <span :class="['status-pill', remote.enabled ? 'ok' : '']">
          {{ remote.enabled ? t("panel.database.remote.on") : t("panel.database.remote.off") }}
        </span>
      </div>
      <div class="form-grid">
        <label>
          <span class="field-label">{{ t("panel.database.remote.ip") }}</span>
          <input v-model.trim="remote.allowedIp" :placeholder="t('panel.database.remote.ipPlaceholder')" />
        </label>
      </div>
      <p v-if="remote.enabled" class="hint conn">
        {{ t("panel.database.remote.connInfo") }}
        <code>{{ remote.serverHost || "sunucu-ip" }}:{{ remote.port }}</code>
      </p>
      <div class="row-actions">
        <button v-if="!remote.enabled" :disabled="remoteBusy" @click="setRemote(true)">
          {{ remoteBusy ? t("panel.database.remote.working") : t("panel.database.remote.enable") }}
        </button>
        <button v-else class="secondary" :disabled="remoteBusy" @click="setRemote(false)">
          {{ remoteBusy ? t("panel.database.remote.working") : t("panel.database.remote.disable") }}
        </button>
      </div>
      <p class="hint">{{ t("panel.database.remote.help") }}</p>
    </div>
  </section>
</template>

<script setup lang="ts">
import { onMounted, reactive, ref } from "vue";
import { useI18n } from "vue-i18n";
import { enableRedisExtension, getGlobalPhpSettings, updateGlobalPhpSettings } from "../api/php";
import { getRemoteDbAccess, setRemoteDbAccess } from "../api/databases";
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
const remote = reactive({ enabled: false, allowedIp: "", serverHost: "", port: 3306 });
const remoteBusy = ref(false);

onMounted(() => {
  void load();
  void loadRemote();
});

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

async function loadRemote() {
  try {
    const data = await getRemoteDbAccess();
    remote.enabled = data.enabled;
    remote.allowedIp = data.allowedIp;
    remote.serverHost = data.serverHost;
    remote.port = data.port;
  } catch {
    // sessiz geç; kullanıcı yine de aç/kapat yapabilir
  }
}

async function setRemote(enabled: boolean) {
  remoteBusy.value = true;
  try {
    const data = await setRemoteDbAccess(enabled, remote.allowedIp);
    remote.enabled = data.enabled;
    remote.allowedIp = data.allowedIp;
    remote.serverHost = data.serverHost;
    remote.port = data.port;
    showNotice(enabled ? t("panel.database.remote.enabledMsg") : t("panel.database.remote.disabledMsg"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    remoteBusy.value = false;
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
.remote-db { margin-top: 14px; }
.remote-head { display: flex; justify-content: space-between; align-items: flex-start; gap: 12px; margin-bottom: 12px; }
.remote-head h4 { margin: 0; }
.conn code { font-weight: 700; }
</style>
