<template>
  <section class="panel wide backup-view">
    <div class="bk-header">
      <div>
        <p class="section-kicker">{{ t("panel.backup.siteKicker") }}</p>
        <h2>{{ t("panel.backup.siteTitle") }}</h2>
        <p class="meta">{{ siteName || t("panel.php.selectSite") }}</p>
      </div>
      <button class="secondary compact" :disabled="loading" @click="refresh">{{ t("panel.backup.refresh") }}</button>
    </div>

    <p v-if="notice" :class="['notice', 'page-notice', { error: noticeError }]" :role="noticeError ? 'alert' : 'status'">{{ notice }}</p>

    <div v-if="siteName" class="form-surface bk-create">
      <h3>{{ t("panel.backup.createTitle") }}</h3>
      <div class="scope-grid">
        <label class="chk"><input v-model="scope.includeFiles" type="checkbox" /> {{ t("panel.backup.scopeFiles") }}</label>
        <label class="chk"><input v-model="scope.includeConfig" type="checkbox" /> {{ t("panel.backup.scopeConfig") }}</label>
        <label class="chk"><input v-model="scope.includeMail" type="checkbox" /> {{ t("panel.backup.scopeMail") }}</label>
      </div>

      <SectionLoadError
        v-if="databasesLoad.message.value"
        :message="databasesLoad.message.value"
        @retry="loadDatabases" />

      <div v-if="availableDatabases.length > 0" class="db-block">
        <span class="field-label">{{ t("panel.backup.scopeDatabases") }}</span>
        <div class="scope-grid">
          <label v-for="db in availableDatabases" :key="`${db.engine}:${db.name}`" class="chk">
            <input v-model="selectedDbKeys" type="checkbox" :value="`${db.engine}:${db.name}`" />
            {{ db.name }} <small>({{ db.engine }})</small>
          </label>
        </div>
      </div>

      <button class="compact" :disabled="busy" @click="createBackup">
        {{ busy ? t("panel.backup.creating") : t("panel.backup.createButton") }}
      </button>
    </div>

    <div class="bk-list">
      <div class="section-head">
        <h3>{{ t("panel.backup.listTitle") }}</h3>
        <span class="meta">{{ listLoaded ? siteBackups.length : "…" }}</span>
      </div>

      <div v-if="siteBackups.length > 0" class="bk-rows">
        <article v-for="record in siteBackups" :key="record.id" class="bk-row">
          <div class="bk-id">
            <strong>{{ record.name }}</strong>
            <span class="meta">{{ formatDate(record.createdAtUtc) }} · {{ formatSize(record.size) }}</span>
          </div>
          <div class="bk-actions">
            <button class="secondary compact" :disabled="downloadingId === record.id" @click="download(record)">
              {{ downloadingId === record.id ? t("panel.backup.downloading") : t("panel.backup.download") }}
            </button>
            <button class="secondary compact" :disabled="restoringId === record.id" @click="restore(record)">
              {{ restoringId === record.id ? t("panel.backup.restoring") : t("panel.backup.restore") }}
            </button>
          </div>
        </article>
      </div>
      <!-- Yedek listesi gelmeden "yedek yok" yazıyordu; var olan yedekler yokmuş gibi görünüyordu. -->
      <p v-else-if="!listLoaded" class="empty-state" role="status" aria-live="polite">
        {{ t("panel.common.loading") }}
      </p>
      <p v-else class="empty-state">{{ t("panel.backup.empty") }}</p>
    </div>
  </section>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref } from "vue";
import { useI18n } from "vue-i18n";
import { useSitesStore } from "../stores/sites";
import { backupSite, downloadBackup, getBackups, restoreBackup, type BackupRecord } from "../api/backup";
import { getDatabaseEngines, getDatabaseWorkspace } from "../api/databases";
import { useConfirm } from "../components/ConfirmDialog.vue";
import SectionLoadError from "../components/SectionLoadError.vue";
import { useNotice, useSectionError } from "../composables/useNotice";

const { t } = useI18n();
const { confirm } = useConfirm();
const sitesStore = useSitesStore();
const siteName = computed(() => sitesStore.selectedSiteName);

const loading = ref(false);
// Yüklenmemiş liste "yedek yok" olarak görünüyordu.
const listLoaded = ref(false);
const busy = ref(false);
const downloadingId = ref("");
const restoringId = ref("");
// TEK KAYNAK: bu ekran ham `extractApiMessage` basıyordu (4 nokta) — kullanıcı çevrilmemiş
// anahtar görüyordu.
const { notice, noticeError, showNotice, resolveApiMessage } = useNotice();
const backups = ref<BackupRecord[]>([]);
const availableDatabases = ref<{ engine: string; name: string }[]>([]);
// Boş liste "yedeklenecek veritabanı yok" demektir; okunamadıysa AYRI söylenir — aksi hâlde
// kullanıcı veritabanı seçmeden yedek alıp yedeğini eksik sanmadan taşırdı.
const databasesLoad = useSectionError();
const selectedDbKeys = ref<string[]>([]);

const scope = reactive({ includeFiles: true, includeConfig: true, includeMail: false });

const siteBackups = computed(() =>
  backups.value.filter((record) => record.type === "site" && record.source === siteName.value));

onMounted(async () => {
  await refresh();
  await loadDatabases();
});

async function refresh() {
  loading.value = true;
  try {
    backups.value = await getBackups();
    listLoaded.value = true;
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    loading.value = false;
  }
}

async function loadDatabases() {
  try {
    const engines = await getDatabaseEngines();
    const list: { engine: string; name: string }[] = [];
    for (const engine of engines.filter((item) => item.installed)) {
      try {
        const workspace = await getDatabaseWorkspace(engine.engine);
        for (const db of workspace.databases ?? []) {
          list.push({ engine: engine.engine, name: db.name });
        }
      } catch {
        // motor erişilemezse atla
      }
    }
    availableDatabases.value = list;
    databasesLoad.clear();
  } catch (error) {
    availableDatabases.value = [];
    databasesLoad.capture(error);
  }
}

async function createBackup() {
  if (!siteName.value) {
    return;
  }
  busy.value = true;
  try {
    const databases = selectedDbKeys.value.map((key) => {
      const [engine, ...rest] = key.split(":");
      return { engine, databaseName: rest.join(":") };
    });
    await backupSite(siteName.value, {
      includeFiles: scope.includeFiles,
      includeConfig: scope.includeConfig,
      includeMail: scope.includeMail,
      databases
    });
    showNotice(t("panel.backup.created"), false);
    await refresh();
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    busy.value = false;
  }
}

async function download(record: BackupRecord) {
  downloadingId.value = record.id;
  try {
    await downloadBackup(record.id, record.name);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    downloadingId.value = "";
  }
}

async function restore(record: BackupRecord) {
  // Canlı siteyi geri yükleme yıkıcıdır — YÜKSEK RİSKLİ onay: üzerine yazılacak site adı yazılmadan devam edilemez.
  const target = record.source || siteName.value;
  const impact = [
    `${t("panel.files.siteLabel")}: ${target}`,
    `${t("panel.backup.name")}: ${record.name}`,
    `${t("panel.backup.size")}: ${formatSize(record.size)}`,
    `${t("panel.backup.createdAt")}: ${formatDate(record.createdAtUtc)}`
  ];

  await confirm({
    title: t("panel.confirm.restoreBackupTitle", { name: record.name }),
    message: t("panel.backup.restoreConfirm"),
    impact,
    requireText: target,
    confirmLabel: t("panel.backup.restore"),
    busyLabel: t("panel.backup.restoring"),
    action: async () => {
      restoringId.value = record.id;
      try {
        await restoreBackup(record.id, "");
        showNotice(t("panel.backup.restored"), false);
      } catch (error) {
        showNotice(resolveApiMessage(error), true);
      } finally {
        restoringId.value = "";
      }
    },
  });
}

function formatSize(bytes: number): string {
  if (!bytes) {
    return "0 B";
  }
  const units = ["B", "KB", "MB", "GB"];
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit += 1;
  }
  return `${value.toFixed(value < 10 && unit > 0 ? 1 : 0)} ${units[unit]}`;
}

function formatDate(iso: string): string {
  const date = new Date(iso);
  return Number.isNaN(date.getTime()) ? iso : date.toLocaleString();
}
</script>

<style scoped>
.backup-view,
.bk-create,
.bk-list {
  display: grid;
  gap: 14px;
}

.bk-header,
.section-head,
.bk-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
}

.scope-grid {
  display: flex;
  flex-wrap: wrap;
  gap: 12px;
}

.chk {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  font-size: 0.86rem;
}

.db-block {
  display: grid;
  gap: 8px;
}

.field-label {
  color: var(--text-muted);
  font-size: 0.74rem;
  font-weight: 700;
}

.bk-rows {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.bk-row {
  padding: 10px 14px;
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-lg);
  background: var(--surface-1);
}

.bk-id {
  display: grid;
  gap: 2px;
  min-width: 0;
}

.bk-actions {
  display: flex;
  gap: 8px;
  flex-shrink: 0;
}

.meta {
  color: var(--text-muted);
}
</style>
