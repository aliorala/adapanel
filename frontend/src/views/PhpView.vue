<template>
  <section class="panel module-shell">
    <div class="module-head">
      <div>
        <h2>{{ t("panel.php.title") }}</h2>
        <p>{{ t("panel.php.subtitle") }}</p>
      </div>
      <button class="secondary compact" :disabled="phpStore.loading" @click="refreshAll">
        {{ t("panel.php.refresh") }}
      </button>
      <button class="secondary compact" v-bind="writeGate(enablingRedis)" @click="enableRedis">
        {{ enablingRedis ? t("panel.php.enablingRedis") : t("panel.php.enableRedis") }}
      </button>
    </div>

    <p v-if="notice" :class="['notice', 'page-notice', { error: noticeError }]" :role="noticeError ? 'alert' : 'status'">{{ notice }}</p>

    <div class="module-toolbar">
      <div class="segmented-tabs" role="tablist">
        <button role="tab" :aria-selected="activeTab === 'installed'" :class="{ active: activeTab === 'installed' }" @click="activeTab = 'installed'">
          {{ t("panel.php.installed") }} ({{ phpStore.versions.length }})
        </button>
        <button role="tab" :aria-selected="activeTab === 'available'" :class="{ active: activeTab === 'available' }" @click="activeTab = 'available'">
          {{ t("panel.php.available") }} ({{ availablePhpComponents.length }})
        </button>
      </div>
      <div class="toolbar-group">
        <!-- Domain hub'dan gelince site zaten seçili (breadcrumb); tekrar seçici gösterme. -->
        <select v-if="!selectedSiteName" v-model="selectedSiteName" class="site-select">
          <option value="">{{ t("panel.php.selectSite") }}</option>
          <option v-for="site in sitesStore.sites" :key="site.name" :value="site.name">{{ site.name }}</option>
        </select>
        <input v-model.trim="query" class="search-control" :placeholder="t('panel.php.search')" />
      </div>
    </div>

    <div class="summary-strip">
      <div class="summary-item">
        <span>{{ t("panel.php.installed") }}</span>
        <strong>{{ phpStore.versions.length }}</strong>
      </div>
      <div class="summary-item">
        <span>{{ t("panel.php.fastCgiReady") }}</span>
        <strong>{{ fastCgiReadyCount }}</strong>
      </div>
      <div class="summary-item">
        <span>{{ t("panel.php.targetSite") }}</span>
        <strong>{{ selectedSiteName || "-" }}</strong>
      </div>
      <div class="summary-item">
        <span>{{ t("panel.php.lastAssignment") }}</span>
        <strong>{{ phpStore.lastAssignment?.displayName || "-" }}</strong>
      </div>
    </div>

    <div v-if="activeTab === 'installed'" class="data-table">
      <div class="data-row table-head php-installed-row">
        <span>{{ t("panel.software.component") }}</span>
        <span>{{ t("panel.php.version") }}</span>
        <span>FastCGI</span>
        <span>{{ t("panel.php.installDir") }}</span>
        <span>{{ t("panel.files.actions") }}</span>
      </div>
      <div v-for="version in filteredInstalled" :key="version.componentId" class="data-row php-installed-row">
        <span class="cell-main"><strong>{{ version.displayName }}</strong><small>{{ version.executablePath }}</small></span>
        <span>{{ version.version || "-" }}</span>
        <span :class="['status-pill', version.isFastCgiRegistered ? 'ok' : 'warn']">
          {{ version.isFastCgiRegistered ? t("panel.php.fastCgiReady") : t("panel.php.fastCgiMissing") }}
        </span>
        <span class="cell-muted">{{ version.installDir }}</span>
        <span class="row-actions">
          <button
            class="compact"
            v-bind="writeGate(!selectedSiteName || phpStore.assigningComponentId === version.componentId || version.status.toLowerCase() !== 'installed')"
            @click="assignVersion(version.componentId)">
            {{ phpStore.assigningComponentId === version.componentId ? t("panel.php.applying") : t("panel.php.assignToSite") }}
          </button>
          <button
            class="danger compact"
            v-bind="writeGate(softwareStore.removingComponentId === version.componentId)"
            @click="removeVersion(version.componentId)">
            {{ softwareStore.removingComponentId === version.componentId ? t("panel.database.removing") : t("panel.php.remove") }}
          </button>
        </span>
      </div>
      <!-- `loading` istek HATA ile bitince de false olur: eski koşul yalnız istek uçuşurken
           koruyordu, hata alındığında boş liste "kurulu PHP sürümü yok" diye okunuyordu.
           `loaded` yalnız BAŞARIDA true olur (bkz. stores/php.ts). -->
      <p v-if="!phpStore.loaded" class="empty-state" role="status" aria-live="polite">{{ t("panel.common.loading") }}</p>
      <p v-else-if="filteredInstalled.length === 0" class="empty-state">{{ t("panel.php.noInstalledVersions") }}</p>
    </div>

    <div v-else class="data-table">
      <div class="data-row table-head php-available-row">
        <span>{{ t("panel.software.component") }}</span>
        <span>{{ t("panel.php.version") }}</span>
        <span>{{ t("panel.software.type") }}</span>
        <span>{{ t("panel.php.installDir") }}</span>
        <span>{{ t("panel.files.actions") }}</span>
      </div>
      <div v-for="component in filteredAvailable" :key="component.id" class="data-row php-available-row">
        <span class="cell-main"><strong>{{ component.displayName }}</strong><small>{{ component.note || component.id }}</small></span>
        <span>{{ component.version || "-" }}</span>
        <span>{{ component.type }}</span>
        <span class="cell-muted">{{ component.installDir }}</span>
        <span class="row-actions">
          <span v-if="installedComponentIds.has(component.id)" class="status-pill ok">{{ t("panel.php.installedState") }}</span>
          <button
            v-else
            class="compact"
            v-bind="writeGate(phpStore.installingComponentId === component.id)"
            @click="installVersion(component.id)">
            {{ phpStore.installingComponentId === component.id ? t("panel.php.installing") : t("panel.php.install") }}
          </button>
        </span>
      </div>
      <!-- Kurulabilir sürümler YAZILIM KATALOĞUNDAN gelir; bekleme/hata durumu onun bayrağıyla
           ayrılır, PHP listesininkiyle değil. -->
      <p v-if="!softwareStore.loaded" class="empty-state" role="status" aria-live="polite">{{ t("panel.common.loading") }}</p>
      <p v-else-if="filteredAvailable.length === 0" class="empty-state">{{ t("panel.php.noManifestVersions") }}</p>
    </div>

    <div v-if="selectedSiteName" class="form-surface php-settings">
      <div class="section-head">
        <div>
          <h3>{{ t("panel.php.settingsTitle") }}</h3>
          <p class="meta">{{ t("panel.php.settingsHint") }}</p>
        </div>
      </div>
      <!-- Okunamadıysa FORM GÖSTERİLMEZ: aksi halde ekrandaki değerler bu siteye ait sanılır. -->
      <p v-if="settingsError" class="notice error" role="alert">{{ settingsError }}</p>
      <template v-if="settingsLoaded">
      <div class="form-grid">
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.php.memoryLimit") }}</span>
          <input v-model.trim="settings.memoryLimit" placeholder="256M" />
        </label>
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.php.maxExecutionTime") }}</span>
          <input v-model.number="settings.maxExecutionTime" type="number" min="0" max="3600" />
        </label>
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.php.uploadMaxFilesize") }}</span>
          <input v-model.trim="settings.uploadMaxFilesize" placeholder="64M" />
        </label>
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.php.postMaxSize") }}</span>
          <input v-model.trim="settings.postMaxSize" placeholder="64M" />
        </label>
        <label class="toggle-field">
          <input v-model="settings.displayErrors" type="checkbox" />
          <span>{{ t("panel.php.displayErrors") }}</span>
        </label>
        <label class="toggle-field">
          <input v-model="settings.openBasedirEnabled" type="checkbox" />
          <span>{{ t("panel.php.openBasedir") }}</span>
        </label>
      </div>
      <p class="meta">{{ t("panel.php.openBasedirHint") }}</p>
      <div class="row-actions">
        <button class="compact" v-bind="writeGate(savingSettings)" @click="saveSettings">
          {{ savingSettings ? t("panel.database.saving") : t("panel.common.save") }}
        </button>
      </div>
      </template>
    </div>
  </section>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from "vue";
import { useI18n } from "vue-i18n";
import { enableRedisExtension, getSitePhpSettings, updateSitePhpSettings } from "../api/php";
import { useConfirm } from "../components/ConfirmDialog.vue";
import { extractApiMessage } from "../stores/auth";
import { usePhpStore } from "../stores/php";
import { useSitesStore } from "../stores/sites";
import { useSoftwareStore } from "../stores/software";
import { useSubscriptionGate } from "../composables/useSubscriptionGate";

const { t } = useI18n();
// Askidaki abonelikte YAZMA dugmeleri bastan devre disi + sebebi title/aria ile soylenir
// (bkz. composables/useSubscriptionGate.ts). Okuma dugmeleri kapiya girmez.
const { writeGate } = useSubscriptionGate();
const { confirm } = useConfirm();
const phpStore = usePhpStore();
const sitesStore = useSitesStore();
const softwareStore = useSoftwareStore();
const activeTab = ref<"installed" | "available">("installed");
const query = ref("");
const notice = ref("");
const noticeError = ref(false);
const enablingRedis = ref(false);

const settings = reactive({
  memoryLimit: "256M",
  maxExecutionTime: 120,
  uploadMaxFilesize: "64M",
  postMaxSize: "64M",
  displayErrors: false,
  openBasedirEnabled: false
});
const savingSettings = ref(false);

// Ayarların O SİTE için gerçekten okunup okunmadığı. Bu bayrak olmadan form, okuma başarısız
// olduğunda ÖNCEKİ sitenin değerlerini gösteriyordu ve "Kaydet" onları yanlış siteye yazıyordu:
// A sitesi seçilir (memory_limit 512M yüklenir) → B sitesine geçilir, B'nin okuması düşer →
// ekranda hâlâ 512M yazar, hiçbir uyarı yoktur → Kaydet'e basılır → A'nın limitleri B'ye gider.
const settingsLoaded = ref(false);
const settingsError = ref("");

async function loadSettings(siteName: string) {
  settingsLoaded.value = false;
  settingsError.value = "";
  if (!siteName) {
    return;
  }
  try {
    const data = await getSitePhpSettings(siteName);
    settings.memoryLimit = data.memoryLimit;
    settings.maxExecutionTime = data.maxExecutionTime;
    settings.uploadMaxFilesize = data.uploadMaxFilesize;
    settings.postMaxSize = data.postMaxSize;
    settings.displayErrors = data.displayErrors;
    settings.openBasedirEnabled = data.openBasedirEnabled ?? false;
    settingsLoaded.value = true;
  } catch (error) {
    // ÖNCEKİ SİTENİN DEĞERLERİ EKRANDA KALMAZ. Okuyamadığımızı da söylüyoruz: sessizce
    // varsayılan göstermek, ölçülmemiş değerleri ölçülmüş gibi sunmaktı.
    settingsError.value = resolveApiMessage(error);
  }
}

async function saveSettings() {
  if (!selectedSiteName.value || !settingsLoaded.value) {
    return;
  }
  savingSettings.value = true;
  try {
    const data = await updateSitePhpSettings(selectedSiteName.value, {
      memoryLimit: settings.memoryLimit,
      maxExecutionTime: settings.maxExecutionTime,
      uploadMaxFilesize: settings.uploadMaxFilesize,
      postMaxSize: settings.postMaxSize,
      displayErrors: settings.displayErrors,
      openBasedirEnabled: settings.openBasedirEnabled
    });
    settings.memoryLimit = data.memoryLimit;
    // İzolasyon durumunu SUNUCUNUN döndürdüğü GERÇEK değerle tazele: kutucuk, kaydedilmiş sanılan
    // ama uygulanmamış bir durumu göstermesin (yanıt php.ini'den taze okunur).
    settings.openBasedirEnabled = data.openBasedirEnabled ?? settings.openBasedirEnabled;
    showNotice(t("panel.php.settingsSaved"), false);
  } catch (error) {
    // `extractApiMessage` HAM anahtar döndürür (ör. `php.settings_locked`); çevirmeden basmak
    // kullanıcıya anlamsız bir dizge gösteriyordu. Aynı dosyanın diğer 5 noktası zaten doğruydu.
    showNotice(resolveApiMessage(error), true);
  } finally {
    savingSettings.value = false;
  }
}

watch(() => sitesStore.selectedSiteName, (siteName) => loadSettings(siteName), { immediate: true });
const selectedSiteName = computed({
  get: () => sitesStore.selectedSiteName,
  set: (value: string) => sitesStore.selectSite(value)
});
const availablePhpComponents = computed(() =>
  (softwareStore.catalog?.manifest ?? []).filter((component) => component.id.startsWith("php-")));
const installedComponentIds = computed(() => new Set(
  phpStore.versions.filter((version) => version.status.toLowerCase() === "installed").map((version) => version.componentId)));
const fastCgiReadyCount = computed(() => phpStore.versions.filter((version) => version.isFastCgiRegistered).length);
const filteredInstalled = computed(() => filterByQuery(phpStore.versions, (item) =>
  [item.displayName, item.version, item.componentId, item.installDir]));
const filteredAvailable = computed(() => filterByQuery(availablePhpComponents.value, (item) =>
  [item.displayName, item.version, item.id, item.note]));

onMounted(refreshAll);

async function refreshAll() {
  try {
    await Promise.all([
      softwareStore.fetchCatalog(),
      sitesStore.fetchSites(),
      phpStore.fetchVersions()
    ]);
    notice.value = "";
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
}

async function installVersion(componentId: string) {
  try {
    const messageKey = await phpStore.installVersion(componentId);
    await softwareStore.fetchCatalog();
    activeTab.value = "installed";
    showNotice(translate(messageKey), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
}

async function assignVersion(componentId: string) {
  if (!selectedSiteName.value) {
    showNotice(t("panel.php.siteRequired"), true);
    return;
  }
  try {
    await phpStore.assignVersion(selectedSiteName.value, componentId);
    showNotice(t("php.assignment.success"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
}

async function removeVersion(componentId: string) {
  const version = phpStore.versions.find((item) => item.componentId === componentId);
  const displayName = version?.displayName || componentId;
  const impact: string[] = [];
  if (version?.installDir) {
    impact.push(`${t("panel.php.installDir")}: ${version.installDir}`);
  }
  impact.push(t("panel.confirm.impactPhpSites"));

  // Yüksek riskli onay: PHP sürümü (bileşen) kaldırma → sürüm adı yazılmadan devam edilemez.
  await confirm({
    title: t("panel.confirm.removeComponentTitle", { name: displayName }),
    message: t("panel.php.confirmRemove"),
    impact,
    requireText: displayName,
    confirmLabel: t("panel.confirm.remove"),
    busyLabel: t("panel.confirm.removing"),
    action: async () => {
      try {
        await softwareStore.removeComponent(componentId);
        await phpStore.fetchVersions();
        showNotice(t("panel.php.removed"), false);
      } catch (error) {
        showNotice(resolveApiMessage(error), true);
      }
    },
  });
}

// Sunucu geneli native phpredis uzantısını etkinleştir (idempotent; zaten varsa "already" döner).
async function enableRedis() {
  enablingRedis.value = true;
  try {
    const result = await enableRedisExtension();
    showNotice(t(result.message === "already" ? "php.redis_already" : "php.redis_enabled"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    enablingRedis.value = false;
  }
}

function filterByQuery<T>(items: T[], fields: (item: T) => Array<string | undefined>) {
  const needle = query.value.toLocaleLowerCase();
  return needle ? items.filter((item) => fields(item).some((value) => value?.toLocaleLowerCase().includes(needle))) : items;
}

function resolveApiMessage(error: unknown) {
  return translate(extractApiMessage(error));
}

function translate(key: string) {
  const translated = t(key);
  return translated === key ? key : translated;
}

function showNotice(message: string, isError: boolean) {
  notice.value = message;
  noticeError.value = isError;
}
</script>

<style scoped>
.php-installed-row {
  grid-template-columns: 1.35fr 0.55fr 0.9fr 1.45fr 1.1fr;
}

.php-available-row {
  grid-template-columns: 1.45fr 0.6fr 0.55fr 1.55fr 0.8fr;
}

.site-select {
  width: min(240px, 44vw);
}

.search-control {
  width: min(260px, 44vw);
}
</style>
