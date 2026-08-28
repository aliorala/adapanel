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
      <div class="segmented-tabs" role="tablist">
        <button role="tab" :aria-selected="activeTab === 'files'" :class="{ active: activeTab === 'files' }" @click="activeTab = 'files'">
          {{ t("panel.files.tab.files") }}
        </button>
        <button role="tab" :aria-selected="activeTab === 'backups'" :class="{ active: activeTab === 'backups' }" @click="activeTab = 'backups'">
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
          <!-- Oluşturma düğmeleri yalnız KLASÖR görünümünde anlamlı: bir dosya açıkken
               `workspace.path` o dosyanın yoludur ve altına klasör/dosya yaratmak sessizce
               yanlış yere yazmak olurdu. -->
          <button class="secondary compact" v-bind="writeGate(!workspace?.isDirectory)" @click="createFolder">
            {{ t("panel.files.createFolder") }}
          </button>
          <button class="secondary compact" v-bind="writeGate(!workspace?.isDirectory)" @click="createNewFile">
            {{ t("panel.files.createFile") }}
          </button>
        </div>
      </div>

      <form v-if="workspace?.isDirectory" class="search-row" @submit.prevent="runSearch">
        <input v-model="searchQuery" minlength="2" :placeholder="t('panel.files.searchHint')" />
        <button type="submit" class="secondary compact">{{ t("panel.files.search") }}</button>
      </form>

      <template v-if="searchRan">
        <div v-if="searchResults.length" class="search-results">
          <button
            v-for="entry in searchPager.items.value"
            :key="entry.path"
            class="search-result"
            @click="openPath(entry.path)">
            <strong>{{ entry.name }}</strong>
            <small>{{ entry.path }}</small>
          </button>
          <TablePager :state="searchPager" />
        </div>
        <p v-else class="empty-state">{{ t("panel.files.searchEmpty") }}</p>
      </template>

      <!-- "Yükleniyor", "klasör boş" ve "okunamadı" AYRI durumlar: üçü de aynı boş tabloya
           düşüyordu ve kullanıcı listenin gelmediğini mi yoksa klasörün gerçekten boş mu
           olduğunu ayırt edemiyordu. -->
      <p v-if="loading" class="empty-state" role="status" aria-live="polite">{{ t("panel.common.loading") }}</p>

      <SectionLoadError v-else-if="workspaceLoad.message.value" :message="workspaceLoad.message.value" @retry="() => refresh()" />

      <template v-else-if="workspace?.isDirectory">
        <div v-if="selection.count.value > 0" class="bulk-bar" role="status">
          <span class="status-pill">{{ t("panel.bulk.selectedCount", { count: selection.count.value }) }}</span>
          <button class="secondary compact" v-bind="writeGate(bulk.busy.value || transferBusy)" @click="startTransfer('move')">
            {{ t("panel.files.move") }}
          </button>
          <button class="secondary compact" v-bind="writeGate(bulk.busy.value || transferBusy)" @click="startTransfer('copy')">
            {{ t("panel.files.copy") }}
          </button>
          <button class="danger compact" v-bind="writeGate(bulk.busy.value || transferBusy)" @click="removeSelected">
            {{ t("panel.bulk.deleteSelected", { count: selection.count.value }) }}
          </button>
          <button class="secondary compact" :disabled="bulk.busy.value || transferBusy" @click="selection.clear">
            {{ t("panel.bulk.clearSelection") }}
          </button>
        </div>

        <!-- HEDEF KLASÖR SORULUR, TAHMİN EDİLMEZ. Yol elle yazılabilir (site kökünün altındaki
             her klasör hedef olabilir) ama listeden seçmek yazım hatasını tümüyle ortadan
             kaldırır: yanlış yazılmış bir hedef, arka uçta "hedef klasör yok" ile reddedilir. -->
        <form v-if="transferMode && selection.count.value > 0" class="transfer-form" @submit.prevent="runTransfer">
          <label>
            <span>{{ t("panel.files.destination") }}</span>
            <input v-model="transferTarget" :placeholder="siteRootPath" />
            <small class="field-hint">{{ t("panel.files.destinationHint") }}</small>
          </label>
          <label>
            <span>{{ t("panel.files.destinationPick") }}</span>
            <select v-model="transferTarget">
              <option :value="siteRootPath">{{ t("panel.files.destinationRoot") }}</option>
              <option v-if="parentTargetPath" :value="parentTargetPath">{{ t("panel.files.destinationParent") }}</option>
              <option v-for="folder in folderTargets" :key="folder.path" :value="folder.path">{{ folder.name }}</option>
            </select>
          </label>
          <label class="overwrite-field">
            <input type="checkbox" v-model="transferOverwrite" />
            <span>{{ t("panel.files.overwrite") }}</span>
          </label>
          <div class="entry-actions">
            <button type="submit" v-bind="writeGate(transferBusy || !transferTarget)">
              {{ transferMode === "move" ? t("panel.files.move") : t("panel.files.copy") }}
            </button>
            <button type="button" class="secondary compact" :disabled="transferBusy" @click="transferMode = null">
              {{ t("panel.common.cancel") }}
            </button>
          </div>
        </form>

        <div class="data-table">
          <div class="data-row table-head files-row">
            <span class="select-cell">
              <input
                type="checkbox"
                :checked="selection.allSelected(visiblePaths)"
                :aria-label="t('panel.bulk.selectAll')"
                @change="selection.toggleAll(visiblePaths)" />
            </span>
            <span>{{ t("panel.files.name") }}</span>
            <span>{{ t("panel.files.kind") }}</span>
            <span>{{ t("panel.files.actions") }}</span>
          </div>

          <div v-for="entry in filePager.items.value" :key="entry.path" class="data-row files-row">
            <span class="select-cell">
              <input
                type="checkbox"
                :checked="selection.isSelected(entry.path)"
                :aria-label="t('panel.bulk.selectRow', { name: entry.name })"
                @change="selection.toggle(entry.path)" />
            </span>
            <div class="cell-main">
              <button class="link-button" @click="openPath(entry.path)">{{ entry.name }}</button>
              <small>{{ entry.path }}</small>
            </div>
            <span>{{ entry.isDirectory ? t("panel.files.folder") : entry.extension || t("panel.files.file") }}</span>
            <div class="entry-actions">
              <a v-if="!entry.isDirectory" class="secondary compact action-link" :href="getDownloadUrl(entry.path)">
                {{ t("panel.files.download") }}
              </a>
              <button class="secondary compact" v-bind="writeGate()" @click="renamePath(entry)">{{ t("panel.files.rename") }}</button>
              <button class="secondary compact" v-bind="writeGate()" @click="compress(entry.path)">{{ t("panel.files.compress") }}</button>
              <button v-if="entry.extension.toLowerCase() === '.zip'" class="secondary compact" v-bind="writeGate()" @click="extract(entry.path)">
                {{ t("panel.files.extract") }}
              </button>
              <button class="danger compact" v-bind="writeGate()" @click="removePath(entry)">{{ t("panel.sites.delete") }}</button>
            </div>
          </div>

          <p v-if="entries.length === 0" class="empty-state">{{ t("panel.files.emptyFolder") }}</p>
        </div>

        <TablePager :state="filePager" :disabled="bulk.busy.value" />
      </template>

      <label v-else-if="workspace">
        <span>{{ t("panel.files.editor") }}</span>
        <textarea v-model="fileContent" rows="14"></textarea>
      </label>

      <div v-if="workspace && !workspace.isDirectory" class="action-row">
        <button v-bind="writeGate()" @click="saveCurrentFile">
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
        <button type="submit" class="secondary compact" v-bind="writeGate()">{{ t("panel.files.permissions") }}</button>
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
        <button v-bind="writeGate(!backupSiteName)" @click="createSiteBackup">
          {{ t("panel.backup.siteAction") }}
        </button>
        <button class="secondary compact" v-bind="writeGate()" @click="createDatabaseBackup">
          {{ t("panel.backup.dbAction") }}
        </button>
      </div>

      <div v-if="gorunenYedekler.length" class="data-table">
        <div class="data-row table-head backup-row">
          <span>{{ t("panel.files.name") }}</span>
          <span>{{ t("panel.backup.type") }}</span>
          <span>{{ t("panel.sites.state") }}</span>
          <span></span>
        </div>

        <div v-for="item in gorunenYedekler" :key="item.id" class="data-row backup-row">
          <div class="cell-main">
            <strong>{{ item.name }}</strong>
            <small>{{ item.source }}</small>
          </div>
          <span>{{ item.type }}</span>
          <span>{{ item.status }}</span>
          <button class="secondary compact" v-bind="writeGate()" @click="restore(item.id)">
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
  copyEntries,
  createDirectory,
  createFile,
  deletePath,
  extractPath,
  getDownloadUrl,
  getFileWorkspace,
  moveEntries,
  renameEntry,
  saveFile,
  searchFiles,
  updatePermissions,
  uploadFile,
  type FileEntry,
  type FileWorkspace
} from "../api/files";
import { useSitesStore } from "../stores/sites";
import { useConfirm } from "../components/ConfirmDialog.vue";
import { usePrompt } from "../components/PromptDialog.vue";
import SectionLoadError from "../components/SectionLoadError.vue";
import TablePager from "../components/TablePager.vue";
import { useNotice, useSectionError } from "../composables/useNotice";
import { useBulkAction, useBulkSelection } from "../composables/useBulkSelection";
import { usePagination } from "../composables/usePagination";
import { useSubscriptionGate } from "../composables/useSubscriptionGate";

const { t } = useI18n();
// Askidaki abonelikte YAZMA dugmeleri bastan devre disi + sebebi title/aria ile soylenir
// (bkz. composables/useSubscriptionGate.ts). Okuma dugmeleri kapiya girmez.
const { writeGate } = useSubscriptionGate();
const { prompt } = usePrompt();
const { confirm } = useConfirm();
const sitesStore = useSitesStore();

const activeTab = ref<"files" | "backups">("files");
const workspace = ref<FileWorkspace | null>(null);
// Dosya listesi sunucudan gelene kadar tablo BOŞ görünüyordu: 2000 dosyalı bir klasör "boş
// klasör" sanılabiliyordu. Yükleniyor / boş / okunamadı artık üç ayrı durum.
const loading = ref(false);
const workspaceLoad = useSectionError();
const fileContent = ref("");
const backups = ref<BackupRecord[]>([]);

/**
 * SEÇİLİ SİTENİN yedekleri.
 *
 * Bu ekran bir alan adının içindedir (Web Siteleri → site → Dosyalar → Yedekler), ama liste
 * sunucudaki BÜTÜN yedekleri basıyordu: kullanıcı bir sitenin içindeyken başka sitelerin,
 * veritabanlarının ve panelin yedeklerini görüyordu. Bağlamda olmayan satırlar yalnızca
 * gürültü değil, yanlış satıra basma riskidir (geri yükleme yıkıcıdır).
 *
 * Süzgeç `SiteBackupView` ile AYNI kuraldır: tür "site" VE kaynak seçili site. Site
 * seçilmemişse (sunucu geneli Dosyalar ekranı) liste OLDUĞU GİBİ kalır — orada bağlam yoktur
 * ve süzmek her şeyi gizlerdi.
 */
const gorunenYedekler = computed(() =>
  selectedSiteName.value
    ? backups.value.filter((record) => record.type === "site" && record.source === selectedSiteName.value)
    : backups.value);

const backupSiteName = ref("");
const backupEngine = ref("mariadb");
const { notice, noticeError, showNotice, resolveApiMessage } = useNotice();
const searchQuery = ref("");
const searchResults = ref<FileEntry[]>([]);
const searchRan = ref(false);

// Klasör tek seferde DOM'a basılmaz (bkz. composables/usePagination.ts).
const entries = computed(() => workspace.value?.entries ?? []);
const filePager = usePagination(entries);
const searchPager = usePagination(searchResults);
const selection = useBulkSelection();
const bulk = useBulkAction();
const visiblePaths = computed(() => filePager.items.value.map((entry) => entry.path));
const permissionPrincipal = ref("IIS_IUSRS");
const permissionRights = ref("M");

// TAŞI / KOPYALA hedef formu. `window.prompt` yerine ekranın kendi alanları: hedef listeden
// seçilebilir, üzerine yazma BİLİNÇLİ bir kutudur (varsayılan kapalı) ve iptal edilebilir.
const transferMode = ref<"move" | "copy" | null>(null);
const transferTarget = ref("");
const transferOverwrite = ref(false);
const transferBusy = ref(false);
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

/** Hedef listesindeki "üst klasör" seçeneği; site kökünün dışına çıkan bir üst yol gösterilmez. */
const parentTargetPath = computed(() => (canGoUp.value ? workspace.value!.parentPath : ""));

/**
 * Hedef olarak seçilebilecek alt klasörler. Taşınan/kopyalanan ögelerin KENDİLERİ listede yer
 * almaz: bir klasörü kendi içine taşımak arka uçta `files.move_into_itself` ile reddedilir,
 * kullanıcıya en baştan sunulmaması daha dürüst.
 */
const folderTargets = computed(() =>
  entries.value.filter((entry) => entry.isDirectory && !selection.isSelected(entry.path)));

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
  searchRan.value = false;
  selection.clear();
  transferMode.value = null;
  if (siteName) {
    await refresh(`sites/${siteName}`);
  }
});

async function refresh(path = workspace.value?.path || siteRootPath.value) {
  if (!selectedSiteName.value || !path.startsWith(siteRootPath.value)) {
    workspace.value = null;
    workspaceLoad.clear();
    return;
  }

  loading.value = true;
  try {
    workspace.value = await getFileWorkspace(path);
    fileContent.value = workspace.value.content || "";
    workspaceLoad.clear();
    // Artık listede olmayan yollar seçili kalmasın (silme/klasör değişimi sonrası).
    selection.keepOnly(workspace.value.entries.map((entry) => entry.path));
  } catch (error) {
    // Hata hem şeritte hem listenin yerinde görünür: boş bir tablo "klasör boş" sanılmasın.
    workspaceLoad.capture(error);
    showNotice(resolveApiMessage(error), true);
  } finally {
    loading.value = false;
  }
}

async function openPath(path: string) {
  if (!siteRootPath.value || !path.startsWith(siteRootPath.value)) {
    return;
  }

  searchResults.value = [];
  searchRan.value = false;
  selection.clear();
  // Klasör değişince açık hedef formu ANLAMSIZ kalır: seçim gitti, hedef başka klasörün yolu.
  transferMode.value = null;
  filePager.reset();
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
    searchPager.reset();
    // "Sonuç yok" ile "henüz aranmadı" ayrı: boş sonuç listesi sessizce kaybolmasın.
    searchRan.value = true;
  } catch (error) {
    searchResults.value = [];
    searchRan.value = false;
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

/**
 * BOŞ DOSYA OLUŞTUR — panelin en çok istenen eksiğiydi: siteye `.htaccess`, `robots.txt` ya da
 * `web.config` koymanın tek yolu dosyayı dışarıda yazıp YÜKLEMEKTİ.
 *
 * ÜZERİNE YAZMA KORUMASI: arka uçtaki `PUT /files` var olan dosyayı sormadan ezer, "yalnız yoksa
 * yaz" diye bir uç yok. Bu yüzden ad ÖNCE listede aranır. Kontrol istemcide olduğu için iki
 * eşzamanlı oturum arasında yarış açığı kalır; kapatılması arka uçta ayrı bir uç gerektirir ve
 * burada "kapandı" denmiyor.
 */
async function createNewFile() {
  const fileName = await prompt({
    title: t("panel.files.filePrompt"),
    label: t("panel.files.name"),
    placeholder: ".htaccess"
  });
  if (!fileName) {
    return;
  }

  const name = fileName.trim();
  // Ad bir YOL olamaz: "alt/klasor/dosya" ya da ".." arka uçta sanal alan denetimine takılır;
  // kullanıcı bunu isteği göndermeden önce öğrensin.
  if (/[\\/:*?"<>|]/.test(name)) {
    showNotice(t("files.invalid_name"), true);
    return;
  }

  if (entries.value.some((entry) => entry.name.toLowerCase() === name.toLowerCase())) {
    showNotice(t("panel.files.fileExists"), true);
    return;
  }

  const basePath = workspace.value?.path ? `${workspace.value.path}/${name}` : `${siteRootPath.value}/${name}`;
  try {
    await createFile(basePath);
    // Dosya oluşturulup DÜZENLEYİCİDE açılır: "oluştur" sonrası kullanıcı ayrıca aramak
    // zorunda kalmasın.
    await openPath(basePath);
    showNotice(t("panel.files.fileCreated"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
}

/**
 * YENİDEN ADLANDIR — hedefi ögenin kendi klasörü olan tek ögelik taşıma (bkz. api/files.ts).
 *
 * Sonuç öge başına gelir: istek 200 dönse bile öge taşınmamış olabilir (hedefte aynı ad var,
 * dosya kullanımda...). Bu yüzden "yeniden adlandırıldı" yalnız `moved` doğruyken yazılır.
 */
async function renamePath(entry: FileEntry) {
  const newName = await prompt({
    title: t("panel.files.renamePrompt"),
    label: t("panel.files.name"),
    defaultValue: entry.name
  });
  if (!newName || newName.trim() === entry.name) {
    return;
  }

  try {
    const result = await renameEntry(entry.path, newName.trim(), false);
    await refresh(workspace.value?.path || "");
    const failure = result.items.find((item) => !item.moved);
    if (failure) {
      showNotice(t("panel.bulk.failedItem", { name: entry.name, reason: hataMetni(failure.errorKey) }), true);
      return;
    }

    showNotice(t("panel.files.renamed"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
}

/** Hedef formunu açar; hedef varsayılan olarak BULUNULAN klasördür, kullanıcı değiştirir. */
function startTransfer(mode: "move" | "copy") {
  if (selection.count.value === 0) {
    return;
  }

  transferMode.value = mode;
  transferTarget.value = workspace.value?.path || siteRootPath.value;
  transferOverwrite.value = false;
}

/**
 * TAŞI / KOPYALA — KISMİ BAŞARISIZLIK YALANLANMAZ.
 *
 * Uçlar tek istekte çoklu öge alır ve öge başına sonuç döner. "Tamamlandı" demek, taşınamayan iki
 * dosyayı sessizce kaybetmek olurdu; özet kaçının olduğunu, kaçının olmadığını ve HANGİSİNİN NEDEN
 * olmadığını (`errorKey` → kullanıcının dili) satır satır yazar.
 */
async function runTransfer() {
  const mode = transferMode.value;
  const targets = entries.value.filter((entry) => selection.isSelected(entry.path));
  if (!mode || targets.length === 0) {
    return;
  }

  const destination = transferTarget.value.trim().replace(/\\/g, "/").replace(/\/+$/, "");
  // Site kökünün dışı arka uçta zaten reddedilir; burada da söylenir ki kullanıcı hatayı
  // istek gitmeden görsün. Anahtar arka ucun kullandığıyla AYNI: iki yerde iki farklı cümle olmaz.
  if (!destination || !siteRootPath.value || !destination.startsWith(siteRootPath.value)) {
    showNotice(t("files.path_outside_root"), true);
    return;
  }

  const paths = targets.map((entry) => entry.path);
  transferBusy.value = true;
  try {
    const failures: { name: string; reason: string }[] = [];
    // UYARI ≠ HATA: kopyalamada bir alt öge bağlantı (junction/symlink) olduğu için atlanmışsa
    // öge "kopyalandı" sayılır ama kopya EKSİKTİR (bkz. CopyFileResultDto.ErrorKey). Bu satırı
    // yutmak, kullanıcının eksik kopyayı tam sanmasına yol açardı.
    const warnings: { name: string; reason: string }[] = [];
    let done = 0;

    if (mode === "move") {
      const result = await moveEntries(paths, destination, transferOverwrite.value);
      done = result.movedCount;
      for (const item of result.items.filter((satir) => !satir.moved)) {
        failures.push({ name: item.sourcePath, reason: hataMetni(item.errorKey) });
      }
    } else {
      const result = await copyEntries(paths, destination, transferOverwrite.value);
      done = result.copiedCount;
      for (const item of result.items) {
        if (!item.copied) {
          failures.push({ name: item.sourcePath, reason: hataMetni(item.errorKey) });
        } else if (item.errorKey) {
          warnings.push({ name: item.sourcePath, reason: hataMetni(item.errorKey) });
        }
      }
    }

    transferMode.value = null;
    selection.clear();
    await refresh(workspace.value?.path || "");
    showNotice(
      transferOzeti(mode, targets.length, done, failures, warnings),
      failures.length > 0 || warnings.length > 0);
  } catch (error) {
    // İstek TÜMÜYLE reddedildi (hedef klasör yok, sanal alan dışı...): form açık kalır ki
    // kullanıcı hedefi düzeltip yeniden deneyebilsin.
    showNotice(resolveApiMessage(error), true);
  } finally {
    transferBusy.value = false;
  }
}

/** Sunucunun döndürdüğü hata ANAHTARINI kullanıcının diline çevirir; karşılığı yoksa anahtarın
 *  kendisi görünür — sessizce boş bırakmaktan iyidir (bkz. composables/useNotice.ts). */
function hataMetni(errorKey: string) {
  if (!errorKey) {
    return t("panel.files.reasonUnknown");
  }

  const translated = t(errorKey);
  return translated === errorKey ? errorKey : translated;
}

/**
 * Taşıma/kopyalama özeti — ÜÇ DURUM AYRI: tamamı oldu / bir kısmı olmadı / hepsi oldu ama kopya
 * eksik. Üçünü tek "tamamlandı" cümlesine indirmek, kullanıcıya olmayan bir sonucu bildirmektir.
 */
function transferOzeti(
  mode: "move" | "copy",
  total: number,
  done: number,
  failures: { name: string; reason: string }[],
  warnings: { name: string; reason: string }[]
) {
  const lines = [...failures, ...warnings].map((satir) =>
    t("panel.bulk.failedItem", { name: satir.name, reason: satir.reason }));

  if (failures.length === 0 && warnings.length === 0) {
    return mode === "move"
      ? t("panel.files.movedAll", { total })
      : t("panel.files.copiedAll", { total });
  }

  if (failures.length === 0) {
    return [t("panel.files.copiedIncomplete", { total }), ...lines].join("\n");
  }

  const head = mode === "move"
    ? t("panel.files.movedPartial", { total, done, failed: failures.length })
    : t("panel.files.copiedPartial", { total, done, failed: failures.length });
  return [head, ...lines].join("\n");
}

/**
 * Silme ONAYSIZ yapılmaz.
 *
 * Arkasında `Directory.Delete(recursive: true)` var: klasörde tek tıkla BÜTÜN ağaç gider ve
 * geri dönüşü yoktur. Bu yüzden klasörlerde `requireText` ile klasör adının yazılması istenir;
 * dosyada normal onay yeter. Onay metni NEYİN silineceğini (tam yol) açıkça söyler.
 */
async function removePath(entry: FileEntry) {
  const isFolder = entry.isDirectory;
  await confirm({
    title: isFolder ? t("panel.files.confirmDeleteFolderTitle") : t("panel.files.confirmDeleteFileTitle"),
    message: isFolder
      ? t("panel.files.confirmDeleteFolderMessage", { name: entry.name })
      : t("panel.files.confirmDeleteFileMessage", { name: entry.name }),
    impact: [entry.path],
    // Klasör = geri dönüşsüz özyinelemeli silme: adın birebir yazılması istenir.
    requireText: isFolder ? entry.name : undefined,
    action: async () => {
      try {
        await deletePath(entry.path);
        await refresh(workspace.value?.path || "");
        showNotice(t("panel.files.deleted"), false);
      } catch (error) {
        showNotice(resolveApiMessage(error), true);
      }
    }
  });
}

/**
 * TOPLU SİLME — ONAY ZORUNLU, KISMİ BAŞARISIZLIK SESSİZ KALMAZ.
 *
 * Onay kaç öğenin gideceğini ve HANGİLERİNİ (tam yol listesi) yazar. Seçimde klasör varsa
 * arkasında özyinelemeli silme vardır; bu yüzden öğe SAYISININ birebir yazılması istenir —
 * "8 öğe" görüp refleksle onaylamak mümkün olmasın.
 *
 * Silme tek uçlu (`DELETE /files`), toplu ucu YOK: öğeler tek tek denenir. Biri başarısız
 * olursa döngü durmaz; özet "kaçı silindi, kaçı silinemedi ve neden" der.
 */
async function removeSelected() {
  const targets = entries.value.filter((entry) => selection.isSelected(entry.path));
  if (targets.length === 0) {
    return;
  }

  const folderCount = targets.filter((entry) => entry.isDirectory).length;
  const impact = bulk.impactList(targets.map((entry) => entry.path));
  if (folderCount > 0) {
    impact.unshift(t("panel.files.bulkFolderWarn", { count: folderCount }));
  }

  const onay = await confirm({
    title: t("panel.bulk.confirmDeleteTitle", { count: targets.length }),
    message: t("panel.bulk.confirmDeleteMessage", { count: targets.length }),
    impact,
    requireText: String(targets.length)
  });
  if (!onay) {
    return;
  }

  const outcome = await bulk.runBulk(targets, (entry) => entry.path, (entry) => deletePath(entry.path));
  await refresh(workspace.value?.path || "");
  showNotice(bulk.deleteSummary(outcome), outcome.failures.length > 0);
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
  /* Satır eylemleri "Yeniden adlandır" ile bir düğme daha aldı: dar sütun düğmeleri alt alta
     kaydırıyordu. */
  grid-template-columns: 30px minmax(220px, 1fr) 110px minmax(430px, auto);
}

.select-cell {
  display: flex;
  align-items: center;
}

.select-cell input {
  width: 16px;
  height: 16px;
  margin: 0;
}

.bulk-bar {
  display: flex;
  align-items: center;
  gap: 10px;
  flex-wrap: wrap;
  padding: 8px 10px;
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-sm);
  background: var(--surface-2);
}

.bulk-bar button {
  width: auto;
}

.transfer-form {
  display: grid;
  grid-template-columns: minmax(220px, 1fr) minmax(180px, 1fr) auto auto;
  gap: 12px;
  align-items: end;
  padding: 10px;
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-sm);
  background: var(--surface-2);
}

.transfer-form label {
  display: grid;
  gap: 5px;
}

.overwrite-field {
  display: flex;
  align-items: center;
  gap: 8px;
  white-space: nowrap;
}

.overwrite-field input {
  width: 16px;
  height: 16px;
  margin: 0;
}

@media (max-width: 900px) {
  .transfer-form {
    grid-template-columns: 1fr;
  }
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
