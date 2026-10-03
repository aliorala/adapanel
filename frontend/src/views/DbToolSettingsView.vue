<template>
  <section class="panel module-shell">
    <div class="module-head">
      <div>
        <h3>{{ t("panel.database.toolSettings.title") }}</h3>
        <p class="meta">{{ t("panel.database.toolSettings.subtitle") }}</p>
      </div>
      <button class="secondary compact" :disabled="loading || saving || redisBusy || remoteLoading || remoteBusy" @click="refreshAll">{{ t("panel.database.toolSettings.refresh") }}</button>
    </div>

    <p v-if="notice" :class="['notice', 'page-notice', { error: noticeError }]" :role="noticeError ? 'alert' : 'status'">{{ notice }}</p>
    <p v-if="loading" class="empty-state" role="status">{{ t("panel.common.loading") }}</p>

    <div v-if="loaded" class="form-surface">
      <div class="form-grid">
        <label>
          <span class="field-label">{{ t("panel.database.toolSettings.uploadMax") }}</span>
          <input v-model="form.uploadMaxFilesize" :disabled="saving" placeholder="512M" />
        </label>
        <label>
          <span class="field-label">{{ t("panel.database.toolSettings.postMax") }}</span>
          <input v-model="form.postMaxSize" :disabled="saving" placeholder="512M" />
        </label>
        <label>
          <span class="field-label">{{ t("panel.database.toolSettings.memory") }}</span>
          <input v-model="form.memoryLimit" :disabled="saving" placeholder="512M" />
        </label>
        <label>
          <span class="field-label">{{ t("panel.database.toolSettings.maxExec") }}</span>
          <input v-model.number="form.maxExecutionTime" :disabled="saving" type="number" min="0" max="3600" />
        </label>
      </div>
      <div class="row-actions">
        <button :disabled="saving || loading || redisBusy" @click="save">
          {{ saving ? t("panel.database.toolSettings.saving") : t("panel.database.toolSettings.save") }}
        </button>
      </div>
      <p class="hint">{{ t("panel.database.toolSettings.help") }}</p>

      <div class="row-actions redis-actions">
        <span :class="['status-pill', redisEnabled ? 'ok' : '']">
          {{ redisEnabled ? t("panel.database.toolSettings.redisOn") : t("panel.database.toolSettings.redisOff") }}
        </span>
        <button v-if="!redisEnabled" class="secondary" :disabled="redisBusy || saving || loading" @click="enableRedis">
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
        <!-- Okunamadıysa DURUM ROZETİ GÖSTERİLMEZ: "Kapalı" demek, ölçemediğimizi ölçtük gibi
             sunmaktı ve kullanıcı açık olan bir erişimi kapalı sanıyordu. -->
        <span v-if="remoteLoaded" :class="['status-pill', remote.enabled ? 'ok' : '']">
          {{ remote.enabled ? t("panel.database.remote.on") : t("panel.database.remote.off") }}
        </span>
      </div>
      <p v-if="remoteError" class="notice error" role="alert">{{ remoteError }}</p>
      <p v-if="remoteLoading" class="empty-state" role="status">{{ t("panel.common.loading") }}</p>
      <template v-if="remoteLoaded">
      <div class="form-grid">
        <label>
          <span class="field-label">{{ t("panel.database.remote.ip") }}</span>
          <input v-model.trim="remote.allowedIp" :disabled="remoteBusy" :placeholder="t('panel.database.remote.ipPlaceholder')" />
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
      </template>
      <p class="hint">{{ t("panel.database.remote.help") }}</p>
    </div>
  </section>
</template>

<script setup lang="ts">
import { onBeforeUnmount, onMounted, reactive, ref } from "vue";
import { useI18n } from "vue-i18n";
import { enableRedisExtension, getGlobalPhpSettings, updateGlobalPhpSettings, type SitePhpSettings } from "../api/php";
import { getSessionEpoch } from "../api/client";
import { getRemoteDbAccess, setRemoteDbAccess } from "../api/databases";
import { extractApiMessage } from "../stores/auth";

const { t } = useI18n();
const loading = ref(false);
const saving = ref(false);
const redisBusy = ref(false);
const redisEnabled = ref(false);
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
const remoteLoading = ref(false);
const remoteLoaded = ref(false);
let disposed = false;
const session = getSessionEpoch();
const isCurrent = () => !disposed && getSessionEpoch() === session;
onBeforeUnmount(() => { disposed = true; });

onMounted(refreshAll);

async function refreshAll() {
  await Promise.all([load(), loadRemote()]);
}

function applySettings(settings: SitePhpSettings) {
  form.uploadMaxFilesize = settings.uploadMaxFilesize || "";
  form.postMaxSize = settings.postMaxSize || "";
  form.memoryLimit = settings.memoryLimit || "";
  form.maxExecutionTime = settings.maxExecutionTime ?? 300;
  form.displayErrors = settings.displayErrors;
  redisEnabled.value = settings.redisEnabled ?? false;
  loaded.value = true;
}

async function load() {
  if (!isCurrent() || loading.value || saving.value || redisBusy.value) return;
  loading.value = true;
  loaded.value = false;
  notice.value = "";
  try {
    const settings = await getGlobalPhpSettings();
    if (isCurrent()) applySettings(settings);
  } catch (error) {
    if (isCurrent()) showNotice(resolveApiMessage(error), true);
  } finally {
    loading.value = false;
  }
}

async function save() {
  if (!isCurrent() || !loaded.value || loading.value || saving.value || redisBusy.value) return;
  saving.value = true;
  try {
    const settings = await updateGlobalPhpSettings({
      uploadMaxFilesize: form.uploadMaxFilesize,
      postMaxSize: form.postMaxSize,
      memoryLimit: form.memoryLimit,
      maxExecutionTime: form.maxExecutionTime,
      displayErrors: form.displayErrors
    });
    if (!isCurrent()) return;
    applySettings(settings);
    showNotice(t("panel.database.toolSettings.saved"), false);
  } catch (error) {
    if (isCurrent()) showNotice(resolveApiMessage(error), true);
  } finally {
    saving.value = false;
  }
}

async function enableRedis() {
  if (!isCurrent() || !loaded.value || redisBusy.value || loading.value || saving.value) return;
  redisBusy.value = true;
  try {
    const result = await enableRedisExtension();
    if (!isCurrent()) return;
    redisEnabled.value = result.enabled;
    showNotice(result.message === "already"
      ? t("panel.database.toolSettings.redisAlready")
      : t("panel.database.toolSettings.redisEnabled"), false);
  } catch (error) {
    if (isCurrent()) showNotice(resolveApiMessage(error), true);
  } finally {
    redisBusy.value = false;
  }
}

const remoteError = ref("");

async function loadRemote() {
  if (!isCurrent() || remoteLoading.value || remoteBusy.value) return;
  remoteLoading.value = true;
  remoteLoaded.value = false;
  remoteError.value = "";
  try {
    const data = await getRemoteDbAccess();
    if (!isCurrent()) return;
    remote.enabled = data.enabled;
    remote.allowedIp = data.allowedIp;
    remote.serverHost = data.serverHost;
    remote.port = data.port;
    remoteLoaded.value = true;
  } catch (error) {
    // ÖLÇEMEDİK ≠ KAPALI. Sessizce geçilince `remote` başlangıç değerlerinde kalıyor
    // (enabled: false) ve rozet "Kapalı" diyordu — uzak erişim sunucuda AÇIK olsa bile.
    // Kullanıcı güvenlik durumunu yanlış okuyor, "Etkinleştir"e basıp izin listesini boş IP ile
    // ezebiliyordu.
    if (isCurrent()) remoteError.value = resolveApiMessage(error);
  } finally {
    remoteLoading.value = false;
  }
}

async function setRemote(enabled: boolean) {
  if (!isCurrent() || !remoteLoaded.value || remoteLoading.value || remoteBusy.value) return;
  remoteBusy.value = true;
  try {
    const data = await setRemoteDbAccess(enabled, remote.allowedIp);
    if (!isCurrent()) return;
    remote.enabled = data.enabled;
    remote.allowedIp = data.allowedIp;
    remote.serverHost = data.serverHost;
    remote.port = data.port;
    showNotice(enabled ? t("panel.database.remote.enabledMsg") : t("panel.database.remote.disabledMsg"), false);
  } catch (error) {
    if (isCurrent()) showNotice(resolveApiMessage(error), true);
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
/* Onay kutusu satirlari DISARIDA: bunlar iki satirlik bir izgara degil, yan yana duran
   kutu+etikettir. Kapsamli `label` kurali element duzeyinde oldugu icin global
   `.check-field` kuralini eziyor, onay kutusu hucreyi kaplayip etiketi alt satira dusuruyordu.
   `:where()` KULLANILIYOR, duz `:not()` DEGIL: `:not(.x)` argumaninin ozgullugunu kurala EKLER
   ((0,1,1) -> (0,2,1)) ve boylece bilesenlerin kendi `.checkbox-field[data-v]` (0,2,0) kurallarini
   ezer -- SiteAliases'in onay kutulari tam olarak boyle bozulmustu. `:where()` her zaman SIFIR
   ozgullukte oldugu icin dislama yapar ama kurali guclendirmez. */
label:not(:where(.check-field, .checkbox-field, .checkbox-row)) { display: grid; gap: 6px; }
.row-actions { margin-top: 14px; }
.row-actions button { width: auto; }
/* base.css `.hint` rengi ve boyutu verir; buradaki TEK ek ust bosluk. */
.hint { margin-top: var(--sp-10); }
.remote-db { margin-top: 14px; }
.remote-head { display: flex; justify-content: space-between; align-items: flex-start; gap: 12px; margin-bottom: 12px; }
.remote-head h4 { margin: 0; }
.conn code { font-weight: 700; }
</style>
