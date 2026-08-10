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

    <p v-if="notice" :class="['notice', 'page-notice', { error: noticeError }]" :role="noticeError ? 'alert' : 'status'">{{ notice }}</p>

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
      <!-- BEŞ sütun. Önceden sekizdi ve dördü (tür, hedef türü, boyut, tarih) tek kelimelik
           değerler için tam sütun harcıyordu; ad, yol ve dört düğme aynı satırda sıkışıyordu.
           Birbirine ait olanlar tek hücrede: kaynağın türü kaynağın parçası, boyut hedefin
           parçası, oluşturma zamanı durumun parçası. -->
      <div class="data-row table-head backup-row">
        <span>{{ t("panel.backup.name") }}</span>
        <span>{{ t("panel.backup.source") }}</span>
        <span>{{ t("panel.backup.targetType") }}</span>
        <span>{{ t("panel.backup.statusColumn") }}</span>
        <span>{{ t("panel.software.action") }}</span>
      </div>
      <div v-for="backup in backups" :key="backup.id" class="data-row backup-row">
        <div class="cell-main">
          <strong>{{ backup.name }}</strong>
          <small>{{ backup.id }}</small>
        </div>
        <div class="cell-main">
          <strong>{{ backup.source }}</strong>
          <small>{{ backup.type }}</small>
        </div>
        <div class="cell-main">
          <strong>{{ backup.remoteUri ? t("panel.backup.remoteTarget") : t("panel.backup.localTarget") }}</strong>
          <small>{{ formatBytes(backup.size) }}</small>
        </div>
        <div class="cell-main">
          <span class="pill-row">
            <span class="status-pill ok">{{ formatStatus(backup.status) }}</span>
            <!-- Kilit rozeti: geri yüklerken parola isteneceğini kullanıcı LİSTEDE görsün. -->
            <span v-if="backup.encrypted" class="status-pill" :title="t('panel.backup.encryptedHint')">
              {{ t("panel.backup.encryptedBadge") }}
            </span>
            <!-- Yarım kalan/riskli adımlar sessizce yutulmaz; ipucunda sebepleri çevrilmiş olarak durur. -->
            <span v-if="backup.warnings?.length" class="status-pill warn" :title="warningText(backup)">
              {{ t("panel.backup.warningsBadge", { count: backup.warnings.length }) }}
            </span>
          </span>
          <small>{{ new Date(backup.createdAtUtc).toLocaleString() }}</small>
        </div>
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
          <button class="secondary compact row-button" :disabled="busy" @click="download(backup)">
            {{ t("panel.backup.download") }}
          </button>
          <button class="danger compact row-button" :disabled="busy" @click="removeBackup(backup)">
            {{ t("panel.backup.delete") }}
          </button>
        </div>
      </div>
      <p v-if="!loading && backups.length === 0" class="empty-state">{{ t("panel.backup.empty") }}</p>
    </div>

    <!-- Otomatik zamanlanmış yedek (Plesk gibi): motor Otomasyon servisidir; bu kart en çok aranan
         ayarları (sıklık + saklama) yedek ekranının içine taşır. Gelişmiş seçenekler Araçlar > Otomasyon'da. -->
    <section v-if="activeView === 'list' && isAdmin && autoForm" class="form-surface auto-backup-card">
      <div class="section-head">
        <div>
          <h3>{{ t("panel.backup.autoTitle") }}</h3>
          <p class="meta">{{ t("panel.backup.autoHint") }}</p>
        </div>
        <span :class="['status-pill', autoForm.autoBackupEnabled ? 'ok' : '']">
          {{ autoForm.autoBackupEnabled ? t("panel.status.enabled") : t("panel.status.disabled") }}
        </span>
      </div>
      <div class="form-grid">
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.backup.autoEnabled") }}</span>
          <select v-model="autoForm.autoBackupEnabled">
            <option :value="true">{{ t("panel.status.enabled") }}</option>
            <option :value="false">{{ t("panel.status.disabled") }}</option>
          </select>
        </label>
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.backup.autoFrequency") }}</span>
          <select v-model="autoFrequency">
            <option value="daily">{{ t("panel.backup.freq.daily") }}</option>
            <option value="weekly">{{ t("panel.backup.freq.weekly") }}</option>
            <option value="monthly">{{ t("panel.backup.freq.monthly") }}</option>
            <option v-if="autoFrequency === 'custom'" value="custom">
              {{ t("panel.backup.freq.custom", { hours: autoForm.backupIntervalHours }) }}
            </option>
          </select>
        </label>
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.backup.autoRetention") }}</span>
          <input v-model.number="autoForm.backupRetentionDays" type="number" min="0" />
        </label>
      </div>
      <p class="field-hint">
        {{ t("panel.backup.autoRuns", {
          last: autoStatus?.lastBackupRunUtc ? new Date(autoStatus.lastBackupRunUtc).toLocaleString() : "-",
          next: autoStatus?.nextBackupRunUtc ? new Date(autoStatus.nextBackupRunUtc).toLocaleString() : "-"
        }) }}
        {{ t("panel.backup.autoAdvancedHint") }}
      </p>
      <div class="row-actions">
        <button class="compact" :disabled="busy" @click="saveAutoBackup">
          {{ t("panel.backup.autoSave") }}
        </button>
      </div>
    </section>

    <!--
      YEDEK ŞİFRELEME (yalnız yönetici; uç de `Administrator`).

      Parola alanları GERÇEK form alanı: `window.prompt` bu panelde yasak (parolayı düz metin
      gösterir ve tarayıcı diyalogları kapattığında sessizce null döner).
    -->
    <section v-if="activeView === 'list' && isAdmin && encryption" class="form-surface encryption-card">
      <div class="section-head">
        <div>
          <h3>{{ t("panel.backup.encryptionTitle") }}</h3>
          <p class="meta">{{ t("panel.backup.encryptionHint") }}</p>
        </div>
        <span :class="['status-pill', encryptionTone]">{{ t(encryptionStateKey) }}</span>
      </div>

      <!-- Kayıt var ama ÇÖZÜLEMİYOR: zamanlanmış yedekler şifrelenemez. Sessiz kalmak,
           yedeklerin sessizce şifresiz alınması demekti. -->
      <p v-if="encryption.enabled && !encryption.passwordUsable" class="notice error" role="alert">
        {{ t("panel.backup.encryptionBroken") }}
      </p>

      <!-- ZORUNLU UYARI: parolayı BELİRLERKEN görünür, geri yükleme anında değil. -->
      <p class="notice encryption-loss" role="status">{{ t("panel.backup.encryptionLossWarning") }}</p>

      <div class="form-grid two">
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.backup.encryptionPassword") }}</span>
          <input v-model="encryptionForm.password" type="password" autocomplete="new-password" />
        </label>
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.backup.encryptionPasswordConfirm") }}</span>
          <input v-model="encryptionForm.confirm" type="password" autocomplete="new-password" />
        </label>
      </div>

      <label class="check-field">
        <input v-model="encryptionForm.acknowledged" type="checkbox" />
        <span>{{ t("panel.backup.encryptionAck") }}</span>
      </label>

      <p v-if="encryptionMismatch" class="notice error" role="alert">{{ t("panel.backup.encryptionMismatch") }}</p>

      <p class="field-hint">
        {{ t("panel.backup.encryptionAlgorithm") }}: {{ encryption.algorithm }}
        <template v-if="encryption.enabled">
          · {{ t("panel.backup.encryptionUpdatedAt") }}: {{ new Date(encryption.updatedAtUtc).toLocaleString() }}
        </template>
      </p>

      <div class="row-actions">
        <button class="compact" :disabled="busy || !canSaveEncryption" @click="saveEncryption">
          {{ t("panel.backup.encryptionSave") }}
        </button>
        <button v-if="encryption.enabled" class="danger compact" :disabled="busy" @click="disableEncryption">
          {{ t("panel.backup.encryptionDisable") }}
        </button>
      </div>
    </section>

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
        <p v-if="loading && browseEntries.length === 0" class="empty-state">{{ t("panel.common.loading") }}</p>
        <p v-else-if="browseEntries.length === 0" class="empty-state">{{ t("panel.backup.browseEmpty") }}</p>
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
          <button v-if="isAdmin" :class="{ active: backupKind === 'panel' }" @click="backupKind = 'panel'">
            {{ t("panel.backup.panelTitle") }}
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

      <div class="form-grid">
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.backup.perBackupPassword") }}</span>
          <input v-model="targetForm.encryptionPassword" type="password" autocomplete="new-password" />
          <small class="field-hint">{{ t("panel.backup.perBackupPasswordHint") }}</small>
        </label>
      </div>

      <!-- Uzak hedefe ŞİFRESİZ gitmek üzere: uyarı gönderimden ÖNCE, kullanıcı parolayı hâlâ
           yazabilecekken görünür (sonradan gelen `backup.remote_unencrypted` rozeti geç kalır). -->
      <p v-if="remoteUnencryptedRisk" class="notice encryption-loss" role="status">
        {{ t("panel.backup.remoteUnencryptedWarning") }}
      </p>

      <p class="field-hint">{{ t("panel.backup.targetHint") }}</p>
      <div class="row-actions">
        <button class="compact" :disabled="busy" @click="createSelectedBackup">
          {{ backupKind === "site" ? t("panel.backup.siteAction") : backupKind === "mail" ? t("panel.backup.mailAction") : backupKind === "panel" ? t("panel.backup.panelAction") : t("panel.backup.dbAction") }}
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
  backupPanel,
  deleteBackup,
  downloadBackup,
  getBackupEncryption,
  getBackupEntries,
  getBackups,
  restoreBackup,
  restoreBackupItems,
  setBackupEncryption,
  type BackupEncryptionSettings,
  type BackupEntry,
  type BackupRecord,
  type BackupTargetPayload
} from "../api/backup";
import { getAutomation, updateAutomation, type AutomationSettings, type AutomationStatus } from "../api/automation";
import { useConfirm } from "../components/ConfirmDialog.vue";
import { usePrompt } from "../components/PromptDialog.vue";
import { extractApiMessage, useAuthStore } from "../stores/auth";
import { useSitesStore } from "../stores/sites";

const { t } = useI18n();
const { confirm } = useConfirm();
const { prompt } = usePrompt();
const sitesStore = useSitesStore();
const authStore = useAuthStore();
const isAdmin = computed(() => authStore.session?.user?.role === "Administrator");
const backups = ref<BackupRecord[]>([]);
const activeView = ref<"list" | "create">("list");
const backupKind = ref<"site" | "database" | "mail" | "panel">("site");

const backupKindHint = computed(() => {
  if (backupKind.value === "site") return t("panel.backup.siteHint");
  if (backupKind.value === "database") return t("panel.backup.dbHint");
  if (backupKind.value === "panel") return t("panel.backup.panelHint");
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
  remoteRegion: "",
  encryptionPassword: ""
});

// Panelin varsayılan yedek şifreleme ayarı (uç yalnız yöneticiye açık).
const encryption = ref<BackupEncryptionSettings | null>(null);
const encryptionForm = reactive({ password: "", confirm: "", acknowledged: false });

const encryptionMismatch = computed(() =>
  encryptionForm.confirm.length > 0 && encryptionForm.password !== encryptionForm.confirm);

// Kaydetme şartları: iki alan dolu ve EŞİT + "anladım" işaretli. Parola kaybı geri dönüşsüz
// olduğu için onay kutusu olmadan kaydetmeyiz.
const canSaveEncryption = computed(() =>
  encryptionForm.password.length > 0 &&
  encryptionForm.password === encryptionForm.confirm &&
  encryptionForm.acknowledged);

const encryptionStateKey = computed(() => {
  if (!encryption.value?.enabled) return "panel.status.disabled";
  return encryption.value.passwordUsable ? "panel.status.enabled" : "panel.backup.encryptionUnusable";
});

const encryptionTone = computed(() => {
  if (!encryption.value?.enabled) return "";
  return encryption.value.passwordUsable ? "ok" : "danger";
});

/*
 * Uzak hedefe ŞİFRESİZ gitme riski.
 *
 * Yönetici için kesin ölçülür (kayıtlı parola durumu elimizde). Kiracıda `GET /backup/encryption`
 * kapalı olduğundan kayıtlı parolanın varlığı BİLİNMEZ; bu yüzden uyarı metni "panelde kayıtlı
 * parola yoksa" koşuluyla yazılmıştır — iki durumda da doğrudur.
 */
const remoteUnencryptedRisk = computed(() =>
  targetForm.targetType !== "local" &&
  targetForm.encryptionPassword.length === 0 &&
  !(encryption.value?.enabled && encryption.value.passwordUsable));

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

// Otomatik yedek programı (yalnız admin; motor = Otomasyon servisi, bu kart en sık kullanılan ayarların kısayolu).
const autoStatus = ref<AutomationStatus | null>(null);
const autoForm = ref<AutomationSettings | null>(null);
// Sıklık ön ayarı ↔ saat eşlemesi: günlük=24, haftalık=168, aylık=720; başka değer "custom" olarak korunur.
const autoFrequency = computed({
  get(): "daily" | "weekly" | "monthly" | "custom" {
    const hours = autoForm.value?.backupIntervalHours ?? 24;
    if (hours === 24) return "daily";
    if (hours === 168) return "weekly";
    if (hours === 720) return "monthly";
    return "custom";
  },
  set(value: "daily" | "weekly" | "monthly" | "custom") {
    if (!autoForm.value || value === "custom") return;
    autoForm.value.backupIntervalHours = value === "daily" ? 24 : value === "weekly" ? 168 : 720;
  }
});

onMounted(async () => {
  if (sitesStore.sites.length === 0) {
    await sitesStore.fetchSites().catch(() => undefined);
  }
  await refresh();
  if (isAdmin.value) {
    await loadAutoBackup();
    await loadEncryption();
  }
});

async function loadEncryption() {
  try {
    encryption.value = await getBackupEncryption();
  } catch (error) {
    // Kasa bu kurulumda yoksa (`backup.encryption_unavailable`) kart gizlenir; yedek listesi
    // etkilenmez. Sebep yine de şeritte söylenir — sessizce kaybolmaz.
    encryption.value = null;
    showNotice(resolveApiMessage(error), true);
  }
}

async function saveEncryption() {
  if (!canSaveEncryption.value) {
    return;
  }

  busy.value = true;
  try {
    encryption.value = await setBackupEncryption(encryptionForm.password);
    resetEncryptionForm();
    showNotice(t("backup.encryption_saved"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    busy.value = false;
  }
}

async function disableEncryption() {
  // Yıkıcı: bundan sonraki yedekler şifresiz alınır. MEVCUT şifreli yedekler yine parola ister —
  // onay metni bunu açıkça söylüyor.
  await confirm({
    title: t("panel.backup.encryptionDisableTitle"),
    message: t("panel.backup.encryptionDisableMessage"),
    confirmLabel: t("panel.backup.encryptionDisable"),
    action: async () => {
      busy.value = true;
      try {
        // Boş parola = kaydı sil + şifrelemeyi kapat (sunucu sözleşmesi).
        encryption.value = await setBackupEncryption("");
        resetEncryptionForm();
        showNotice(t("backup.encryption_saved"), false);
      } catch (error) {
        showNotice(resolveApiMessage(error), true);
      } finally {
        busy.value = false;
      }
    }
  });
}

function resetEncryptionForm() {
  // Parola bellekte/ekranda ASILI KALMAZ.
  encryptionForm.password = "";
  encryptionForm.confirm = "";
  encryptionForm.acknowledged = false;
}

async function loadAutoBackup() {
  try {
    autoStatus.value = await getAutomation();
    autoForm.value = { ...autoStatus.value.settings };
  } catch {
    // Otomasyon durumu okunamazsa kart gizli kalır; yedek listesi etkilenmez.
  }
}

async function saveAutoBackup() {
  if (!autoForm.value) return;
  busy.value = true;
  try {
    // backupRemotePassword boş = sunucudaki kayıtlı parola korunur (Otomasyon ekranıyla aynı sözleşme).
    autoStatus.value = await updateAutomation({ ...autoForm.value, backupRemotePassword: "" });
    autoForm.value = { ...autoStatus.value.settings };
    showNotice(t("panel.backup.autoSaved"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    busy.value = false;
  }
}

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
    // Yıkıcı değil (yalnız servis kısa süre durur) → nötr ton: kırmızı düğme ve "geri alınamaz" uyarısı yok.
    const approved = await confirm({
      title: t("panel.confirm.mailBackupTitle"),
      message: t("panel.backup.confirmMailBackup"),
      tone: "neutral",
      confirmLabel: t("panel.backup.mailAction"),
    });
    if (!approved) return;
    await runBackup(() => backupMail(buildTargetPayload()));
    return;
  }

  if (backupKind.value === "panel") {
    await runBackup(() => backupPanel(buildTargetPayload()));
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
    // Bu yedeğe özel parola tek seferliktir; formda asılı kalıp bir sonraki yedeğe sessizce
    // taşınmaz (kullanıcı hangi parolayla şifrelediğini karıştırmasın).
    targetForm.encryptionPassword = "";
    showNotice(t("panel.backup.created"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    busy.value = false;
  }
}

async function restore(backup: BackupRecord) {
  // Şifreli yedekte parola ÖNCE sorulur: onay diyaloğunu geçtikten sonra "parola lazım" demek,
  // kullanıcıyı yüksek riskli onayı ikinci kez yapmaya zorlardı.
  let password = "";
  if (backup.encrypted) {
    const entered = await askBackupPassword();
    if (entered === null) {
      return;
    }

    password = entered;
  }

  // Yüksek riskli onay: geri yükleme canlı veriyi ÜZERİNE yazar → yedek adı yazılmadan devam edilemez.
  await confirm({
    title: t("panel.confirm.restoreBackupTitle", { name: backup.name }),
    message: t("panel.backup.restoreConfirm"),
    impact: backupImpact(backup),
    requireText: backup.name,
    confirmLabel: t("panel.backup.restore"),
    busyLabel: t("panel.backup.restoring"),
    action: async () => {
      busy.value = true;
      try {
        const restored = await restoreBackup(backup.id, "", password);
        await refresh();
        reportRestoreResult(restored);
      } catch (error) {
        showNotice(resolveApiMessage(error), true);
      } finally {
        busy.value = false;
        password = "";
      }
    },
  });
}

/**
 * Şifreli yedeğin parolasını sorar. Boş bırakılabilir: sunucu o zaman panelde KAYITLI parolayı
 * dener. İptalde `null` döner (akış durur).
 */
async function askBackupPassword() {
  return prompt({
    title: t("panel.backup.restorePasswordTitle"),
    message: t("panel.backup.encryptionLossWarning"),
    label: t("panel.backup.encryptionPassword"),
    hint: t("panel.backup.restorePasswordHint"),
    password: true,
    allowEmpty: true,
    confirmLabel: t("panel.backup.restore")
  });
}

/**
 * Geri yükleme sonucunu OLDUĞU GİBİ söyler.
 *
 * NEDEN: sunucu yarım kalan adımları `warnings[]` ile döndürüyor ve durumu
 * `restored_with_errors` yapıyor. Koşulsuz "geri yüklendi" demek, kullanıcının elinde ne eski
 * hâl ne de dönmüş veritabanı varken ona "tamam" dedirtirdi. Üzerine yazılan içeriğin taşındığı
 * klasör (`preservedPath`) de söylenir: "yanlış yedeği geri yükledim" denince dönülecek tek yer orası.
 */
function reportRestoreResult(record: BackupRecord) {
  const lines: string[] = [];
  const failed = (record.warnings ?? []).length > 0;

  lines.push(failed ? t("panel.backup.restoredWithErrors") : t("panel.backup.restored"));

  if (failed) {
    lines.push(`${t("panel.backup.restoreWarningsTitle")}:`, warningText(record));
  }

  if (record.preservedPath) {
    lines.push(t("panel.backup.preservedPath", { path: record.preservedPath }));
  }

  showNotice(lines.join("\n"), failed);
}

/** Kayıt üzerindeki uyarıların çevrilmiş özeti (rozetin `title` ipucu). */
function warningText(backup: BackupRecord) {
  return (backup.warnings ?? [])
    .map((warning) => {
      const translated = t(warning.messageKey);
      const message = translated === warning.messageKey ? warning.messageKey : translated;
      return warning.target ? `${warning.target}: ${message}` : message;
    })
    .join("\n");
}

// Onay kutusundaki "etkilenecekler" listesi: ekranın zaten elinde olan yedek bilgileri.
function backupImpact(backup: BackupRecord) {
  return [
    `${t("panel.backup.name")}: ${backup.name}`,
    `${t("panel.backup.type")}: ${backup.type} (${backup.source})`,
    `${t("panel.backup.size")}: ${formatBytes(backup.size)}`,
    `${t("panel.backup.createdAt")}: ${new Date(backup.createdAtUtc).toLocaleString()}`
  ];
}

async function removeBackup(backup: BackupRecord) {
  // Normal onay: yalnız yedek dosyası silinir, canlı veri etkilenmez.
  await confirm({
    title: t("panel.confirm.deleteBackupTitle", { name: backup.name }),
    message: t("panel.backup.confirmDelete", { name: backup.name }),
    impact: backupImpact(backup),
    action: async () => {
      busy.value = true;
      try {
        await deleteBackup(backup.id);
        await refresh();
        if (browseRecord.value?.id === backup.id) {
          closeBrowser();
        }
        showNotice(t("panel.backup.deletedNotice"), false);
      } catch (error) {
        showNotice(resolveApiMessage(error), true);
      } finally {
        busy.value = false;
      }
    },
  });
}

async function download(backup: BackupRecord) {
  busy.value = true;
  try {
    await downloadBackup(backup.id, backup.name);
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
    /*
     * Gözatma ucu parola ALMAZ (bilerek: parolayı sorgu dizesine koymak onu IIS erişim
     * günlüklerine ve tarayıcı geçmişine yazmak olurdu). Şifreli yedekte gözatma yalnız panelde
     * KAYITLI parola varsa çalışır — genel "parolayı girin" metni burada yanlış yol gösterirdi.
     */
    if (extractApiMessage(error) === "backup.password_required") {
      showNotice(t("panel.backup.browseEncryptedHint"), true);
    } else {
      showNotice(resolveApiMessage(error), true);
    }
  } finally {
    busy.value = false;
  }
}

async function restoreSelectedItems() {
  const record = browseRecord.value;
  if (!record || selectedItems.value.length === 0) return;

  // Şifreli yedekte parola önce sorulur (gözatma kayıtlı parolayla çalışmış olabilir, ama
  // geri yükleme ucu isteğe ait parolayı da kabul eder).
  let password = "";
  if (record.encrypted) {
    const entered = await askBackupPassword();
    if (entered === null) {
      return;
    }

    password = entered;
  }

  const impact = [
    `${t("panel.backup.name")}: ${record.name}`,
    `${t("panel.backup.restoreSelected")}: ${selectedItems.value.length}`,
    `${t("panel.backup.itemsTarget")}: ${itemsTargetPath.value || t("panel.backup.itemsTargetOriginal")}`
  ];

  // Yüksek riskli onay: seçilen öğeler hedeftekilerin ÜZERİNE yazılır → yedek adı yazılmadan devam edilemez.
  await confirm({
    title: t("panel.confirm.restoreBackupTitle", { name: record.name }),
    message: t("panel.backup.confirmRestoreItems"),
    impact,
    requireText: record.name,
    confirmLabel: t("panel.backup.restore"),
    busyLabel: t("panel.backup.restoring"),
    action: async () => {
      busy.value = true;
      try {
        const result = await restoreBackupItems(record.id, selectedItems.value, itemsTargetPath.value, password);
        selectedItems.value = [];
        showNotice(t("panel.backup.itemsRestoredNotice", { count: result.restoredCount, target: result.targetRoot }), false);
      } catch (error) {
        showNotice(resolveApiMessage(error), true);
      } finally {
        busy.value = false;
        password = "";
      }
    },
  });
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

/* Eylem sütunu SABİT genişlikte: dört düğme her satırda aynı düzende otursun, satırdan satıra
   yeri kaymasın (oransal genişlikte uzun yedek adları düğme sütununu daraltıp diziliş bozuyordu). */
.backup-row {
  grid-template-columns: minmax(200px, 1.3fr) minmax(0, 1.2fr) minmax(0, 0.9fr) minmax(0, 1fr) 246px;
  align-items: start;
}

.backup-row .row-actions {
  align-content: start;
  gap: 6px;
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

.encryption-card {
  align-content: start;
  display: grid;
  gap: 12px;
}

/* Parola kaybı geri dönüşsüz: uyarı satırı gövde metninden ayrışsın. */
.encryption-loss {
  font-weight: 600;
}

/* Durum + kilit + uyarı rozetleri aynı satırda, dar ekranda alt satıra sarar. */
.pill-row {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
}

@media (max-width: 720px) {
  .section-head {
    flex-direction: column;
  }
}
</style>
