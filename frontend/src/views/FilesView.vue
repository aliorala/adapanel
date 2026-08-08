<template>
  <section class="panel module-shell files-panel">
    <div class="module-head">
      <div>
        <h2>{{ t("panel.files.title") }}</h2>
        <p class="meta">{{ t("panel.files.subtitle") }}</p>
      </div>
      <button class="secondary compact" @click="() => refresh()">
        {{ t("panel.files.refresh") }}
      </button>
    </div>

    <p v-if="notice" :class="['notice', 'page-notice', { error: noticeError }]" :role="noticeError ? 'alert' : 'status'">
      {{ notice }}
    </p>

    <div class="module-toolbar">
      <div class="segmented-tabs">
        <button :class="{ active: activeTab === 'files' }" @click="activeTab = 'files'">
          {{ t("panel.files.tab.files") }}
        </button>
        <button :class="{ active: activeTab === 'backups' }" @click="activeTab = 'backups'">
          {{ t("panel.files.tab.backups") }}
        </button>
      </div>
      <div class="site-picker">
        <!-- Domain hub'dan gelince site zaten seçili (breadcrumb); tekrar seçici gösterme. -->
        <template v-if="!selectedSiteName">
          <span>{{ t("panel.files.siteLabel") }}</span>
          <select v-model="selectedSiteName">
            <option value="">{{ t("panel.files.selectSite") }}</option>
            <option v-for="site in sitesStore.sites" :key="site.name" :value="site.name">
              {{ site.name }}
            </option>
          </select>
        </template>
        <span class="status-pill">{{ workspace?.entries.length ?? 0 }} {{ t("panel.files.items") }}</span>
      </div>
    </div>

    <section v-if="activeTab === 'files' && !selectedSiteName" class="form-surface empty-state">
      <strong>{{ t("panel.files.siteRequired") }}</strong>
      <p class="meta">{{ t("panel.files.siteRequiredHint") }}</p>
    </section>

    <section v-else-if="activeTab === 'files'" class="form-surface workspace-card">
      <div class="action-row">
        <strong>{{ workspace?.path || "/" }}</strong>
        <div class="action-row">
          <label class="upload-button">
            <input type="file" @change="uploadSelectedFile" />
            <span>{{ t("panel.files.upload") }}</span>
          </label>
          <button class="secondary compact" :disabled="!canGoUp" @click="openPath(workspace?.parentPath || siteRootPath)">
            {{ t("panel.files.up") }}
          </button>
          <button class="secondary compact" @click="createFolder">
            {{ t("panel.files.createFolder") }}
          </button>
        </div>
      </div>

      <form v-if="workspace?.isDirectory" class="search-row" @submit.prevent="runSearch">
        <input v-model="searchQuery" minlength="2" :placeholder="t('panel.files.searchHint')" />
        <button type="submit" class="secondary compact">{{ t("panel.files.search") }}</button>
      </form>

      <div v-if="searchResults.length" class="search-results">
        <button v-for="entry in searchResults" :key="entry.path" class="search-result" @click="openPath(entry.path)">
          <strong>{{ entry.name }}</strong>
          <small>{{ entry.path }}</small>
        </button>
      </div>

      <div v-if="workspace?.isDirectory" class="data-table">
        <div class="data-row table-head files-row">
          <span>{{ t("panel.files.name") }}</span>
          <span>{{ t("panel.files.kind") }}</span>
          <span>{{ t("panel.files.actions") }}</span>
        </div>

        <div v-for="entry in workspace?.entries ?? []" :key="entry.path" class="data-row files-row">
          <div class="cell-main">
            <button class="link-button" @click="openPath(entry.path)">{{ entry.name }}</button>
            <small>{{ entry.path }}</small>
          </div>
          <span>{{ entry.isDirectory ? t("panel.files.folder") : entry.extension || t("panel.files.file") }}</span>
          <div class="entry-actions">
            <a v-if="!entry.isDirectory" class="secondary compact action-link" :href="getDownloadUrl(entry.path)">
              {{ t("panel.files.download") }}
            </a>
            <button class="secondary compact" @click="compress(entry.path)">{{ t("panel.files.compress") }}</button>
            <button v-if="entry.extension.toLowerCase() === '.zip'" class="secondary compact" @click="extract(entry.path)">
              {{ t("panel.files.extract") }}
            </button>
            <button class="danger compact" @click="removePath(entry.path)">{{ t("panel.sites.delete") }}</button>
          </div>
        </div>
      </div>

      <label v-else>
        <span>{{ t("panel.files.editor") }}</span>
        <textarea v-model="fileContent" rows="14"></textarea>
      </label>

      <div v-if="workspace && !workspace.isDirectory" class="action-row">
        <button @click="saveCurrentFile">
          {{ t("panel.files.save") }}
        </button>
        <a class="secondary compact action-link" :href="getDownloadUrl(workspace.path)">
          {{ t("panel.files.download") }}
        </a>
      </div>

      <form v-if="workspace" class="permissions-row" @submit.prevent="savePermissions">
        <label>
          <span>{{ t("panel.security.principal") }}</span>
          <input v-model="permissionPrincipal" placeholder="IIS_IUSRS" />
        </label>
        <label>
          <span>{{ t("panel.security.rights") }}</span>
          <select v-model="permissionRights">
            <option value="R">R</option>
            <option value="M">M</option>
            <option value="F">F</option>
          </select>
        </label>
        <button type="submit" class="secondary compact">{{ t("panel.files.permissions") }}</button>
      </form>
    </section>

    <section v-else class="form-surface workspace-card">
      <div class="section-head">
        <div>
          <h3>{{ t("panel.backup.title") }}</h3>
          <p class="meta">{{ t("panel.backup.subtitle") }}</p>
        </div>
        <button class="secondary compact" @click="refreshBackups">
          {{ t("panel.backup.refresh") }}
        </button>
      </div>

      <div class="form-grid">
        <label>
          <span>{{ t("panel.php.targetSite") }}</span>
          <select v-model="backupSiteName">
            <option value="">{{ t("panel.php.selectSite") }}</option>
            <option v-for="site in sitesStore.sites" :key="site.name" :value="site.name">
              {{ site.name }}
            </option>
          </select>
        </label>
        <label>
          <span>{{ t("panel.database.engineLabel") }}</span>
          <select v-model="backupEngine">
            <option value="mariadb">MariaDB</option>
            <option value="mysql">MySQL</option>
            <option value="postgresql">PostgreSQL</option>
          </select>
        </label>
      </div>

      <div class="action-row">
        <button :disabled="!backupSiteName" @click="createSiteBackup">
          {{ t("panel.backup.siteAction") }}
        </button>
        <button class="secondary compact" @click="createDatabaseBackup">
          {{ t("panel.backup.dbAction") }}
        </button>
      </div>

      <div v-if="backups.length" class="data-table">
        <div class="data-row table-head backup-row">
          <span>{{ t("panel.files.name") }}</span>
          <span>{{ t("panel.backup.type") }}</span>
          <span>{{ t("panel.sites.state") }}</span>
          <span></span>
        </div>

        <div v-for="item in backups" :key="item.id" class="data-row backup-row">
          <div class="cell-main">
            <strong>{{ item.name }}</strong>
            <small>{{ item.source }}</small>
          </div>
          <span>{{ item.type }}</span>
          <span>{{ item.status }}</span>
          <button class="secondary compact" @click="restore(item.id)">
            {{ t("panel.backup.restore") }}
          </button>
        </div>
      </div>
    </section>
  </section>
</template>

<script setup lang="ts">
import { computed, onMounted, ref, watch } from "vue";
import { useI18n } from "vue-i18n";
import { backupDatabase, backupSite, getBackups, restoreBackup, type BackupRecord } from "../api/backup";
import {
  compressPath,
  createDirectory,
  deletePath,
  extractPath,
  getDownloadUrl,
  getFileWorkspace,
  saveFile,
  searchFiles,
  updatePermissions,
  uploadFile,
  type FileEntry,
  type FileWorkspace
} from "../api/files";
import { extractApiMessage } from "../stores/auth";
import { useSitesStore } from "../stores/sites";
import { usePrompt } from "../components/PromptDialog.vue";

const { t } = useI18n();
const { prompt } = usePrompt();
const sitesStore = useSitesStore();

const activeTab = ref<"files" | "backups">("files");
const workspace = ref<FileWorkspace | null>(null);
const fileContent = ref("");
const backups = ref<BackupRecord[]>([]);
const backupSiteName = ref("");
const backupEngine = ref("mariadb");
const notice = ref("");
const noticeError = ref(false);
const searchQuery = ref("");
const searchResults = ref<FileEntry[]>([]);
const permissionPrincipal = ref("IIS_IUSRS");
const permissionRights = ref("M");
const selectedSiteName = computed({
  get: () => sitesStore.selectedSiteName,
  set: (value: string) => sitesStore.selectSite(value)
});
const siteRootPath = computed(() => selectedSiteName.value ? `sites/${selectedSiteName.value}` : "");
const canGoUp = computed(() =>
  Boolean(workspace.value?.parentPath) &&
  Boolean(siteRootPath.value) &&
  workspace.value!.path !== siteRootPath.value &&
  workspace.value!.parentPath.startsWith(siteRootPath.value)
);

onMounted(async () => {
  try {
    if (!sitesStore.sites.length) {
      await sitesStore.fetchSites();
    }
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }

  await Promise.all([
    selectedSiteName.value ? refresh(siteRootPath.value) : Promise.resolve(),
    refreshBackups()
  ]);
});

watch(selectedSiteName, async (siteName, previousSiteName) => {
  if (siteName === previousSiteName) {
    return;
  }

  workspace.value = null;
  searchResults.value = [];
  if (siteName) {
    await refresh(`sites/${siteName}`);
  }
});

async function refresh(path = workspace.value?.path || siteRootPath.value) {
  if (!selectedSiteName.value || !path.startsWith(siteRootPath.value)) {
    workspace.value = null;
    return;
  }

  try {
    workspace.value = await getFileWorkspace(path);
    fileContent.value = workspace.value.content || "";
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
}

async function openPath(path: string) {
  if (!siteRootPath.value || !path.startsWith(siteRootPath.value)) {
    return;
  }

  searchResults.value = [];
  await refresh(path);
}

async function uploadSelectedFile(event: Event) {
  const input = event.target as HTMLInputElement;
  const file = input.files?.[0];
  if (!file || !workspace.value?.isDirectory) {
    return;
  }

  try {
    await uploadFile(workspace.value.path, file);
    await refresh();
    showNotice(t("panel.files.uploaded"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    input.value = "";
  }
}

async function runSearch() {
  try {
    searchResults.value = await searchFiles(workspace.value?.path || "", searchQuery.value);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
}

async function compress(path: string) {
  try {
    workspace.value = await compressPath(path);
    showNotice(t("panel.files.compressed"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
}

async function extract(path: string) {
  try {
    workspace.value = await extractPath(path);
    showNotice(t("panel.files.extracted"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
}

async function savePermissions() {
  if (!workspace.value) {
    return;
  }

  try {
    await updatePermissions(workspace.value.path, permissionPrincipal.value, permissionRights.value);
    showNotice(t("panel.files.permissionsSaved"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
}

/*
 * Aşağıdaki dört işlemin hiçbirinde hata yakalama YOKTU: sunucu reddettiğinde (izin yok, disk
 * dolu, yol kilitli) kullanıcı EKRANDA HİÇBİR ŞEY görmüyordu.
 *
 * En pahalısı "kaydet"ti: kullanıcı dosyayı düzenler, Kaydet'e basar, istek başarısız olur,
 * hiçbir uyarı çıkmaz — kaydedildiğini sanıp sekmeyi kapatır ve emeği kaybolur. Sessiz
 * başarısızlık burada doğrudan VERİ KAYBIDIR.
 */
async function createFolder() {
  const folderName = await prompt({
    title: t("panel.files.folderPrompt"),
    label: t("panel.files.name"),
    defaultValue: "new-folder"
  });
  if (!folderName) {
    return;
  }

  const basePath = workspace.value?.path ? `${workspace.value.path}/${folderName}` : `${siteRootPath.value}/${folderName}`;
  try {
    await createDirectory(basePath);
    await refresh(workspace.value?.path || "");
    showNotice(t("panel.files.folderCreated"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
}

async function removePath(path: string) {
  try {
    await deletePath(path);
    await refresh(workspace.value?.path || "");
    showNotice(t("panel.files.deleted"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
}

async function saveCurrentFile() {
  if (!workspace.value) {
    return;
  }

  try {
    await saveFile(workspace.value.path, fileContent.value);
    // "Kaydedildi" ancak istek GERÇEKTEN başarılı dönünce yazılır.
    showNotice(t("panel.files.saved"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
}

async function refreshBackups() {
  try {
    backups.value = await getBackups();
  } catch (error) {
    // Yedek listesi okunamadıysa ESKİ liste bırakılmaz: bayat bir listeyi güncel sanmak,
    // olmayan bir yedeğe güvenmek demektir.
    backups.value = [];
    showNotice(resolveApiMessage(error), true);
  }
}

async function createSiteBackup() {
  try {
    await backupSite(backupSiteName.value);
    await refreshBackups();
    showNotice(t("panel.backup.created"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
}

async function createDatabaseBackup() {
  try {
    await backupDatabase(backupEngine.value);
    await refreshBackups();
    showNotice(t("panel.backup.created"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
}

async function restore(backupId: string) {
  try {
    await restoreBackup(backupId, "");
    await refreshBackups();
    showNotice(t("panel.backup.restored"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
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
.files-panel,
.workspace-card {
  display: grid;
  gap: 12px;
}

.site-picker {
  display: flex;
  align-items: center;
  justify-content: flex-end;
  gap: 8px;
  flex-wrap: wrap;
}

.site-picker select {
  width: min(260px, 48vw);
}

.empty-state {
  min-height: 180px;
  place-content: center;
  text-align: center;
}

.section-head,
.action-row {
  display: flex;
  justify-content: space-between;
  gap: 12px;
  align-items: flex-start;
  flex-wrap: wrap;
}

.files-row,
.backup-row,
.form-grid {
  display: grid;
  grid-template-columns: repeat(4, minmax(0, 1fr));
  gap: 12px;
}

.files-row {
  grid-template-columns: minmax(220px, 1fr) 110px minmax(330px, auto);
}

.entry-actions,
.search-row,
.permissions-row {
  display: flex;
  gap: 8px;
  align-items: end;
}

.entry-actions {
  justify-content: flex-end;
  flex-wrap: wrap;
}

.entry-actions button,
.entry-actions a,
.search-row button,
.permissions-row button {
  width: auto;
}

.action-link,
.upload-button {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  padding: 8px 10px;
  border-radius: var(--radius-sm);
  background: var(--surface-2);
  color: var(--text-strong);
  font-weight: 700;
  text-decoration: none;
  cursor: pointer;
}

.upload-button input {
  display: none;
}

.search-row input {
  flex: 1;
}

.search-results {
  display: grid;
  gap: 6px;
}

.search-result {
  display: grid;
  gap: 3px;
  background: var(--surface-2);
  color: var(--text-strong);
  text-align: left;
}

.search-result small {
  color: var(--text-muted);
}

.permissions-row label {
  display: grid;
  gap: 5px;
  flex: 1;
}

.link-button {
  background: transparent;
  border: 0;
  color: var(--accent-1);
  text-align: left;
  padding: 0;
}

.meta {
  margin: 0;
  color: var(--text-muted);
}

@media (max-width: 900px) {
  .files-row,
  .backup-row,
  .form-grid {
    grid-template-columns: 1fr;
  }
}
</style>
