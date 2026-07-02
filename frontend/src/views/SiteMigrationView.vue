<template>
  <section class="panel module-shell site-migration">
    <div class="module-head">
      <div>
        <h3>{{ t("panel.siteImport.title") }}</h3>
        <p class="meta">{{ t("panel.siteImport.subtitle") }}</p>
      </div>
    </div>

    <p v-if="notice" :class="['notice', { error: noticeError }]">{{ notice }}</p>
    <p v-if="!siteName" class="meta">{{ t("panel.php.selectSite") }}</p>

    <div v-else class="form-surface">
      <label class="stacked-field">
        <span class="field-label">{{ t("panel.migration.url") }}</span>
        <input v-model.trim="archiveUrl" placeholder="https://eski-sunucu/site.zip" :disabled="busy" />
        <small class="field-hint">{{ t("panel.migration.urlHint") }}</small>
      </label>

      <label class="check-field">
        <input v-model="cleanFirst" type="checkbox" :disabled="busy" />
        <span>{{ t("panel.migration.cleanFirst") }}</span>
      </label>

      <div class="row-actions">
        <button :disabled="busy || !archiveUrl.trim()" @click="run">
          {{ busy ? t("panel.siteImport.running") : t("panel.migration.run") }}
        </button>
      </div>

      <p v-if="busy" class="hint">{{ t("panel.migration.runningHint") }}</p>
      <p class="hint">{{ t("panel.migration.help") }}</p>
    </div>

    <div v-if="siteName" class="form-surface ftp-pull">
      <h4>{{ t("panel.migration.ftp.title") }}</h4>
      <p class="field-hint">{{ t("panel.migration.ftp.subtitle") }}</p>

      <div class="ftp-grid">
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.migration.ftp.protocol") }}</span>
          <select v-model="ftp.protocol" :disabled="ftpBusy">
            <option value="sftp">SFTP — {{ t("panel.migration.ftp.recommended") }}</option>
            <option value="ftps">FTPS</option>
            <option value="ftp">FTP</option>
          </select>
        </label>
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.migration.ftp.host") }}</span>
          <input v-model.trim="ftp.host" placeholder="eski-sunucu.com" :disabled="ftpBusy" />
        </label>
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.migration.ftp.port") }}</span>
          <input v-model.number="ftp.port" type="number" :placeholder="defaultPortHint" :disabled="ftpBusy" />
        </label>
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.migration.ftp.user") }}</span>
          <input v-model.trim="ftp.userName" autocomplete="off" :disabled="ftpBusy" />
        </label>
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.migration.ftp.password") }}</span>
          <input v-model="ftp.password" type="password" autocomplete="new-password" :disabled="ftpBusy" />
        </label>
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.migration.ftp.remotePath") }}</span>
          <input v-model.trim="ftp.remotePath" placeholder="public_html" :disabled="ftpBusy" />
          <small class="field-hint">{{ t("panel.migration.ftp.remotePathHint") }}</small>
        </label>
      </div>

      <div class="row-actions">
        <button :disabled="ftpBusy || !ftp.host.trim() || !ftp.userName.trim()" @click="runFtp">
          {{ ftpBusy ? t("panel.migration.ftp.running") : t("panel.migration.ftp.run") }}
        </button>
      </div>

      <p v-if="ftpBusy" class="hint">{{ t("panel.migration.ftp.runningHint") }}</p>
      <p class="hint">{{ t("panel.migration.ftp.help") }}</p>
    </div>
  </section>
</template>

<script setup lang="ts">
import { computed, reactive, ref } from "vue";
import { useI18n } from "vue-i18n";
import { importSiteArchive, migrateSiteFiles } from "../api/siteHosting";
import { extractApiDetails, extractApiMessage } from "../stores/auth";
import { useSitesStore } from "../stores/sites";

const { t } = useI18n();
const sitesStore = useSitesStore();
const siteName = computed(() => sitesStore.selectedSiteName);
const archiveUrl = ref("");
const cleanFirst = ref(false);
const busy = ref(false);
const notice = ref("");
const noticeError = ref(false);

async function run() {
  if (!siteName.value || !archiveUrl.value.trim()) {
    return;
  }
  busy.value = true;
  notice.value = "";
  try {
    const result = await importSiteArchive(siteName.value, archiveUrl.value.trim(), cleanFirst.value);
    showNotice(t("panel.migration.done", { count: result.filesCopied }), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    busy.value = false;
  }
}

const ftp = reactive({ protocol: "sftp", host: "", port: 0, userName: "", password: "", remotePath: "public_html" });
const ftpBusy = ref(false);
const defaultPortHint = computed(() => (ftp.protocol === "sftp" ? "22" : "21"));

async function runFtp() {
  if (!siteName.value || !ftp.host.trim() || !ftp.userName.trim()) {
    return;
  }
  ftpBusy.value = true;
  notice.value = "";
  try {
    const result = await migrateSiteFiles(siteName.value, {
      protocol: ftp.protocol,
      host: ftp.host.trim(),
      port: ftp.port || 0,
      userName: ftp.userName.trim(),
      password: ftp.password,
      remotePath: ftp.remotePath.trim()
    });
    showNotice(t("panel.migration.ftp.done", { count: result.filesDownloaded }), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    ftpBusy.value = false;
  }
}

function resolveApiMessage(error: unknown) {
  const key = extractApiMessage(error);
  const translated = t(key);
  const message = translated === key ? key : translated;
  const details = extractApiDetails(error);
  return details ? `${message}\n\n${details}` : message;
}

function showNotice(message: string, isError: boolean) {
  notice.value = message;
  noticeError.value = isError;
}
</script>

<style scoped>
.site-migration {
  display: grid;
  gap: 12px;
}

.field-hint {
  color: var(--text-muted);
  font-size: 0.74rem;
}

.row-actions button {
  width: auto;
}

.hint {
  color: var(--text-muted);
  font-size: 0.8rem;
  white-space: pre-line;
}
</style>
