<template>
  <section class="panel module-shell backup-panel">
    <div class="module-head">
      <div>
        <h3>{{ t("panel.backup.title") }}</h3>
        <p class="meta">{{ t("panel.backup.subtitle") }}</p>
      </div>
      <button class="secondary compact" :disabled="loading" @click="refresh">
        {{ t("panel.backup.refresh") }}
      </button>
    </div>

    <p v-if="notice" :class="['notice', { error: noticeError }]">{{ notice }}</p>

    <div class="module-toolbar">
      <div class="segmented-tabs">
        <button :class="{ active: activeView === 'list' }" @click="activeView = 'list'">
          {{ t("panel.backup.list") }}
        </button>
        <button :class="{ active: activeView === 'create' }" @click="activeView = 'create'">
          {{ t("panel.backup.new") }}
        </button>
      </div>
      <span class="status-pill">{{ backups.length }} {{ t("panel.backup.records") }}</span>
    </div>

    <div v-if="activeView === 'list'" class="data-table backup-table">
      <div class="data-row table-head backup-row">
        <span>{{ t("panel.backup.name") }}</span>
        <span>{{ t("panel.backup.type") }}</span>
        <span>{{ t("panel.backup.source") }}</span>
        <span>{{ t("panel.backup.targetType") }}</span>
        <span>{{ t("panel.backup.size") }}</span>
        <span>{{ t("panel.backup.statusColumn") }}</span>
        <span>{{ t("panel.backup.createdAt") }}</span>
        <span>{{ t("panel.software.action") }}</span>
      </div>
      <div v-for="backup in backups" :key="backup.id" class="data-row backup-row">
        <div class="cell-main">
          <strong>{{ backup.name }}</strong>
          <small>{{ backup.id }}</small>
        </div>
        <span>{{ backup.type }}</span>
        <span>{{ backup.source }}</span>
        <span>{{ backup.remoteUri ? t("panel.backup.remoteTarget") : t("panel.backup.localTarget") }}</span>
        <span>{{ formatBytes(backup.size) }}</span>
        <span class="status-pill ok">{{ formatStatus(backup.status) }}</span>
        <span class="cell-muted">{{ new Date(backup.createdAtUtc).toLocaleString() }}</span>
        <div class="row-actions">
          <button class="secondary compact row-button" :disabled="busy" @click="restore(backup)">
            {{ t("panel.backup.restore") }}
          </button>
          <button
            v-if="backup.format === 'zip'"
            class="secondary compact row-button"
            :disabled="busy"
            @click="openBrowser(backup)">
            {{ t("panel.backup.browse") }}
          </button>
        </div>
      </div>
      <p v-if="!loading && backups.length === 0" class="empty-state">{{ t("panel.backup.empty") }}</p>
    </div>

    <section v-if="activeView === 'list' && browseRecord" class="form-surface backup-browser">
      <div class="section-head">
        <div>
          <h3>{{ t("panel.backup.browseTitle") }}: {{ browseRecord.name }}</h3>
          <p class="meta">{{ t("panel.backup.browseHint") }}</p>
        </div>
        <button class="secondary compact" @click="closeBrowser">{{ t("panel.common.cancel") }}</button>
      </div>

      <div class="breadcrumb">
        <button class="secondary compact" :disabled="busy" @click="navigateTo('')">/</button>
        <button
          v-for="crumb in breadcrumbs"
          :key="crumb.prefix"
          class="secondary compact"
          :disabled="busy"
          @click="navigateTo(crumb.prefix)">
          {{ crumb.name }}
        </button>
      </div>

      <div class="data-table">
        <div class="data-row table-head entry-row">
          <span></span>
          <span>{{ t("panel.backup.name") }}</span>
          <span>{{ t("panel.backup.size") }}</span>
        </div>
        <div v-for="entry in browseEntries" :key="entry.path" class="data-row entry-row">
          <input v-model="selectedItems" type="checkbox" :value="entry.path" />
          <button
            v-if="entry.isDirectory"
            class="secondary compact entry-link"
            :disabled="busy"
            @click="navigateTo(entry.path)">
            📁 {{ entry.name }}
          </button>
          <strong v-else class="entry-file">{{ entry.name }}</strong>
          <span class="cell-muted">{{ entry.isDirectory ? "—" : formatBytes(entry.size) }}</span>
        </div>
        <p v-if="browseEntries.length === 0" class="empty-state">{{ t("panel.backup.browseEmpty") }}</p>
      </div>
      <p v-if="browseTruncated" class="field-hint">{{ t("panel.backup.browseTruncated") }}</p>

      <div class="form-grid two">
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.backup.itemsTarget") }}</span>
          <input v-model.trim="itemsTargetPath" :placeholder="t('panel.backup.itemsTargetOriginal')" />
        </label>
      </div>
      <div class="row-actions">
        <button class="compact" :disabled="busy || selectedItems.length === 0" @click="restoreSelectedItems">
          {{ t("panel.backup.restoreSelected") }} ({{ selectedItems.length }})
        </button>
      </div>
    </section>

    <section v-else class="form-surface backup-create">
      <div class="section-head">
        <div>
          <h3>{{ t("panel.backup.new") }}</h3>
          <p class="meta">{{ backupKindHint }}</p>
        </div>
        <div class="segmented-tabs">
          <button :class="{ active: backupKind === 'site' }" @click="backupKind = 'site'">
            {{ t("panel.backup.siteTitle") }}
          </button>
          <button :class="{ active: backupKind === 'database' }" @click="backupKind = 'database'">
            {{ t("panel.backup.dbTitle") }}
          </button>
          <button :class="{ active: backupKind === 'mail' }" @click="backupKind = 'mail'">
            {{ t("panel.backup.mailTitle") }}
          </button>
        </div>
      </div>

      <div class="form-grid">
        <label v-if="backupKind === 'site'" class="stacked-field">
          <span class="field-label">{{ t("panel.php.targetSite") }}</span>
          <select v-model="siteName" required>
            <option value="">{{ t("panel.php.selectSite") }}</option>
            <option v-for="site in sitesStore.sites" :key="site.name" :value="site.name">{{ site.name }}</option>
          </select>
        </label>
        <template v-else-if="backupKind === 'database'">
          <label class="stacked-field">
            <span class="field-label">{{ t("panel.database.engineLabel") }}</span>
            <select v-model="databaseEngine">
              <option value="mariadb">MariaDB</option>
              <option value="mysql">MySQL</option>
              <option value="postgresql">PostgreSQL</option>
            </select>
          </label>
          <label class="stacked-field">
            <span class="field-label">{{ t("panel.database.databaseName") }}</span>
            <input v-model="databaseName" :placeholder="t('panel.backup.allDatabases')" />
          </label>
        </template>
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.backup.targetType") }}</span>
          <select v-model="targetForm.targetType">
            <option value="local">{{ t("panel.backup.target.local") }}</option>
            <option value="ftp">{{ t("panel.backup.target.ftp") }}</option>
            <option value="s3">{{ t("panel.backup.target.s3") }}</option>
            <option value="webdav">{{ t("panel.backup.target.webdav") }}</option>
          </select>
        </label>
      </div>

      <div v-if="targetForm.targetType !== 'local'" class="form-grid">
        <label class="stacked-field remote-url">
          <span class="field-label">{{ t("panel.backup.remoteUrl") }}</span>
          <input v-model="targetForm.remoteUrl" :placeholder="remoteUrlPlaceholder" />
        </label>
        <label class="stacked-field">
          <span class="field-label">{{ targetForm.targetType === 's3' ? t("panel.backup.remoteAccessKey") : t("panel.backup.remoteUser") }}</span>
          <input v-model="targetForm.remoteUserName" autocomplete="off" />
        </label>
        <label class="stacked-field">
          <span class="field-label">{{ targetForm.targetType === 's3' ? t("panel.backup.remoteSecretKey") : t("panel.backup.remotePassword") }}</span>
          <input v-model="targetForm.remotePassword" type="password" autocomplete="new-password" />
        </label>
        <label v-if="targetForm.targetType === 's3'" class="stacked-field">
          <span class="field-label">{{ t("panel.backup.remoteRegion") }}</span>
          <input v-model="targetForm.remoteRegion" placeholder="eu-central-1" />
        </label>
      </div>

      <p class="field-hint">{{ t("panel.backup.targetHint") }}</p>
      <div class="row-actions">
        <button class="compact" :disabled="busy" @click="createSelectedBackup">
          {{ backupKind === "site" ? t("panel.backup.siteAction") : backupKind === "mail" ? t("panel.backup.mailAction") : t("panel.backup.dbAction") }}
        </button>
        <button class="secondary compact" @click="activeView = 'list'">{{ t("panel.common.cancel") }}</button>
      </div>
    </section>
  </section>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from "vue";
import { useI18n } from "vue-i18n";
import {
  backupDatabase,
  backupMail,
  backupSite,
  getBackupEntries,
  getBackups,
  restoreBackup,
  restoreBackupItems,
  type BackupEntry,
  type BackupRecord,
  type BackupTargetPayload
} from "../api/backup";
import { extractApiMessage } from "../stores/auth";
import { useSitesStore } from "../stores/sites";

const { t } = useI18n();
const sitesStore = useSitesStore();
const backups = ref<BackupRecord[]>([]);
const activeView = ref<"list" | "create">("list");
const backupKind = ref<"site" | "database" | "mail">("site");

const backupKindHint = computed(() => {
  if (backupKind.value === "site") return t("panel.backup.siteHint");
  if (backupKind.value === "database") return t("panel.backup.dbHint");
  return t("panel.backup.mailHint");
});
const siteName = ref(sitesStore.selectedSiteName);
const databaseEngine = ref("mariadb");
const databaseName = ref("");
const loading = ref(false);
const busy = ref(false);
const notice = ref("");
const noticeError = ref(false);
const targetForm = reactive<BackupTargetPayload>({
  targetType: "local",
  remoteUrl: "",
  remoteUserName: "",
  remotePassword: "",
  remoteRegion: ""
});

// Granüler geri yükleme (zip içeriğine gözatma).
const browseRecord = ref<BackupRecord | null>(null);
const browsePrefix = ref("");
const browseEntries = ref<BackupEntry[]>([]);
const browseTruncated = ref(false);
const selectedItems = ref<string[]>([]);
const itemsTargetPath = ref("");

const breadcrumbs = computed(() => {
  const parts = browsePrefix.value.split("/").filter(Boolean);
  return parts.map((name, index) => ({
    name,
    prefix: parts.slice(0, index + 1).join("/") + "/"
  }));
});

const remoteUrlPlaceholder = computed(() => {
  if (targetForm.targetType === "ftp") {
    return "ftp://backup.example.com/adapanel/";
  }

  if (targetForm.targetType === "webdav") {
    return "https://dav.example.com/backups/";
  }

  return "https://s3.eu-central-1.amazonaws.com/bucket-adi/yedekler/";
});

watch(() => sitesStore.selectedSiteName, (value) => {
  if (value) {
    siteName.value = value;
  }
});

onMounted(async () => {
  if (sitesStore.sites.length === 0) {
    await sitesStore.fetchSites().catch(() => undefined);
  }
  await refresh();
});

async function refresh() {
  loading.value = true;
  try {
    backups.value = await getBackups();
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    loading.value = false;
  }
}

async function createSelectedBackup() {
  if (backupKind.value === "site") {
    await createSiteBackup();
    return;
  }

  if (backupKind.value === "mail") {
    if (!window.confirm(t("panel.backup.confirmMailBackup"))) return;
    await runBackup(() => backupMail(buildTargetPayload()));
    return;
  }

  await createDatabaseBackup();
}

async function createSiteBackup() {
  if (!siteName.value) {
    showNotice(t("panel.php.siteRequired"), true);
    return;
  }

  await runBackup(() => backupSite(siteName.value, undefined, buildTargetPayload()));
}

async function createDatabaseBackup() {
  await runBackup(() => backupDatabase(databaseEngine.value, databaseName.value, buildTargetPayload()));
}

async function runBackup(operation: () => Promise<BackupRecord>) {
  busy.value = true;
  try {
    await operation();
    await refresh();
    activeView.value = "list";
    showNotice(t("panel.backup.created"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    busy.value = false;
  }
}

async function restore(backup: BackupRecord) {
  busy.value = true;
  try {
    await restoreBackup(backup.id, "");
    await refresh();
    showNotice(t("panel.backup.restored"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    busy.value = false;
  }
}

function buildTargetPayload() {
  return { ...targetForm };
}

async function openBrowser(backup: BackupRecord) {
  browseRecord.value = backup;
  selectedItems.value = [];
  itemsTargetPath.value = "";
  await navigateTo("");
}

function closeBrowser() {
  browseRecord.value = null;
  browseEntries.value = [];
  selectedItems.value = [];
}

async function navigateTo(prefix: string) {
  if (!browseRecord.value) return;
  busy.value = true;
  try {
    const result = await getBackupEntries(browseRecord.value.id, prefix);
    browsePrefix.value = result.prefix;
    browseEntries.value = result.entries;
    browseTruncated.value = result.truncated;
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    busy.value = false;
  }
}

async function restoreSelectedItems() {
  if (!browseRecord.value || selectedItems.value.length === 0) return;
  if (!window.confirm(t("panel.backup.confirmRestoreItems"))) return;
  busy.value = true;
  try {
    const result = await restoreBackupItems(browseRecord.value.id, selectedItems.value, itemsTargetPath.value);
    selectedItems.value = [];
    showNotice(t("panel.backup.itemsRestoredNotice", { count: result.restoredCount, target: result.targetRoot }), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    busy.value = false;
  }
}

function formatBytes(value: number) {
  if (value < 1024) return `${value} B`;
  if (value < 1024 ** 2) return `${(value / 1024).toFixed(1)} KiB`;
  if (value < 1024 ** 3) return `${(value / 1024 ** 2).toFixed(1)} MiB`;
  return `${(value / 1024 ** 3).toFixed(1)} GiB`;
}

function formatStatus(status: string) {
  const key = `panel.backup.status.${status}`;
  const translated = t(key);
  return translated === key ? status : translated;
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
.backup-panel,
.backup-create {
  align-content: start;
}

.section-head {
  display: flex;
  justify-content: space-between;
  gap: 10px;
  align-items: flex-start;
  flex-wrap: wrap;
}

.backup-row {
  grid-template-columns: minmax(210px, 1.4fr) 0.5fr 0.75fr 0.65fr 0.55fr 0.8fr 1fr auto;
}

.entry-row {
  grid-template-columns: auto minmax(240px, 1fr) 120px;
  min-width: 480px;
}

.entry-row input[type="checkbox"] {
  width: auto;
}

.entry-link {
  justify-self: start;
  text-align: left;
}

.entry-file {
  overflow-wrap: anywhere;
}

.breadcrumb {
  display: flex;
  gap: 4px;
  flex-wrap: wrap;
}

.breadcrumb button {
  width: auto;
}

.backup-browser {
  align-content: start;
}

.row-button,
.row-actions button {
  width: auto;
}

.remote-url {
  grid-column: span 1;
}

@media (max-width: 720px) {
  .section-head {
    flex-direction: column;
  }
}
</style>
