<template>
  <section class="panel module-shell">
    <div class="module-head">
      <div>
        <h2>{{ t("panel.php.title") }}</h2>
        <p>{{ t("panel.php.subtitle") }}</p>
      </div>
      <button class="secondary compact" :disabled="actionsBusy" @click="refreshAll">
        {{ t("panel.php.refresh") }}
      </button>
      <button v-if="isAdministrator" class="secondary compact" v-bind="writeGate(!canMutate())" @click="enableRedis">
        {{ enablingRedis ? t("panel.php.enablingRedis") : t("panel.php.enableRedis") }}
      </button>
    </div>

    <p v-if="notice" :class="['notice', 'page-notice', { error: noticeError, 'warn-banner': noticeWarning }]" :role="noticeError ? 'alert' : 'status'">{{ notice }}</p>

    <section v-if="(!versionsMeasured || (activeTab === 'available' && !catalogMeasured)) && !refreshing && !phpStore.loading && !softwareStore.loading" class="php-read-state" role="status">
      <div>
        <strong>{{ t("panel.common.loadFailed") }}</strong>
        <p>{{ t(phpStore.versions.length || (activeTab === 'available' && availablePhpComponents.length) ? "panel.php.lastKnown" : "panel.php.readRetryHint") }}</p>
      </div>
      <button class="secondary compact" :disabled="actionsBusy" @click="refreshAll">{{ t("panel.common.retry") }}</button>
    </section>

    <div class="module-toolbar">
      <div class="segmented-tabs" role="tablist">
        <button role="tab" :aria-selected="activeTab === 'installed'" :class="{ active: activeTab === 'installed' }" @click="activeTab = 'installed'">
          {{ t("panel.php.installed") }} ({{ versionsMeasured ? phpStore.versions.length : "—" }})
        </button>
        <button v-if="isAdministrator" role="tab" :aria-selected="activeTab === 'available'" :class="{ active: activeTab === 'available' }" @click="activeTab = 'available'">
          {{ t("panel.php.available") }} ({{ catalogMeasured ? availablePhpComponents.length : "—" }})
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
        <strong>{{ versionsMeasured ? phpStore.versions.length : "—" }}</strong>
      </div>
      <div class="summary-item">
        <span>{{ t("panel.php.fastCgiReady") }}</span>
        <strong>{{ versionsMeasured ? fastCgiReadyCount : "—" }}</strong>
      </div>
      <div class="summary-item">
        <span>{{ t("panel.php.targetSite") }}</span>
        <strong>{{ selectedSiteName || "-" }}</strong>
      </div>
      <div class="summary-item">
        <span>{{ t("panel.php.lastAssignment") }}</span>
        <strong>{{ selectedSiteAssignment?.displayName || "-" }}</strong>
      </div>
    </div>

    <div v-if="activeTab === 'installed' || !isAdministrator" class="data-table">
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
        <span :class="['status-pill', versionsMeasured && version.isFastCgiRegistered ? 'ok' : 'warn']">
          {{ !versionsMeasured ? t("panel.health.statusUnknown") : version.isFastCgiRegistered ? t("panel.php.fastCgiReady") : t("panel.php.fastCgiMissing") }}
        </span>
        <span class="cell-muted">{{ version.installDir }}</span>
        <span class="row-actions">
          <button
            class="compact"
            v-bind="writeGate(!canMutate() || !selectedSiteName || version.status.toLowerCase() !== 'installed')"
            @click="assignVersion(version.componentId)">
            {{ phpStore.assigningComponentId === version.componentId ? t("panel.php.applying") : t("panel.php.assignToSite") }}
          </button>
          <button v-if="isAdministrator"
            class="danger compact"
            v-bind="writeGate(!canMutate())"
            @click="removeVersion(version.componentId)">
            {{ softwareStore.removingComponentId === version.componentId ? t("panel.database.removing") : t("panel.php.remove") }}
          </button>
        </span>
      </div>
      <p v-if="phpStore.loading" class="empty-state" role="status" aria-live="polite">{{ t("panel.common.loading") }}</p>
      <p v-else-if="!versionsMeasured" class="empty-state" role="status">{{ t("panel.health.statusUnknown") }}</p>
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
          <span v-if="!versionsMeasured" class="status-pill warn">{{ t("panel.health.statusUnknown") }}</span>
          <span v-else-if="installedComponentIds.has(component.id)" class="status-pill ok">{{ t("panel.php.installedState") }}</span>
          <button
            v-else
            class="compact"
            v-bind="writeGate(!canMutate() || !catalogMeasured)"
            @click="installVersion(component.id)">
            {{ phpStore.installingComponentId === component.id ? t("panel.php.installing") : t("panel.php.install") }}
          </button>
        </span>
      </div>
      <p v-if="softwareStore.loading" class="empty-state" role="status" aria-live="polite">{{ t("panel.common.loading") }}</p>
      <p v-else-if="!catalogMeasured" class="empty-state" role="status">{{ t("panel.health.statusUnknown") }}</p>
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
      <p v-else-if="!settingsLoaded" class="empty-state" role="status">{{ t("panel.common.loading") }}</p>
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
        <button class="compact" v-bind="writeGate(savingSettings || !actorCurrent())" @click="saveSettings">
          {{ savingSettings ? t("panel.database.saving") : t("panel.common.save") }}
        </button>
      </div>
      </template>
    </div>
  </section>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from "vue";
import { useI18n } from "vue-i18n";
import { enableRedisExtension, getSitePhpSettings, updateSitePhpSettings } from "../api/php";
import { useConfirm } from "../components/ConfirmDialog.vue";
import { extractApiMessage, useAuthStore } from "../stores/auth";
import { usePhpStore, PhpInstallationRefreshError } from "../stores/php";
import { useSitesStore } from "../stores/sites";
import { useSoftwareStore } from "../stores/software";
import { useSubscriptionGate } from "../composables/useSubscriptionGate";
import { getSessionEpoch } from "../api/client";

const { t } = useI18n();
// Askidaki abonelikte YAZMA dugmeleri bastan devre disi + sebebi title/aria ile soylenir
// (bkz. composables/useSubscriptionGate.ts). Okuma dugmeleri kapiya girmez.
const { writeGate, locked } = useSubscriptionGate();
const { confirm } = useConfirm();
const phpStore = usePhpStore();
const sitesStore = useSitesStore();
const softwareStore = useSoftwareStore();
const auth = useAuthStore();
const activeTab = ref<"installed" | "available">("installed");
const query = ref("");
const notice = ref("");
const noticeError = ref(false);
const noticeWarning = ref(false);
const enablingRedis = ref(false);
const refreshing = ref(false);
const actionBusy = ref(false);
const verificationRequired = ref(false);
const sessionEpoch = getSessionEpoch();
let viewRevision = 0;
const isAdministrator = computed(() => auth.session?.user?.role === "Administrator");
const actorCurrent = () => !disposed && sessionEpoch === getSessionEpoch() && auth.session?.isAuthenticated === true &&
  ["Administrator", "Reseller"].includes(auth.session?.user?.role ?? "");
const contextCurrent = (revision: number) => actorCurrent() && revision === viewRevision;
const versionsMeasured = computed(() => phpStore.loaded && !verificationRequired.value);
const catalogMeasured = computed(() => softwareStore.loaded && !verificationRequired.value);
const actionsBusy = computed(() => refreshing.value || actionBusy.value || phpStore.loading ||
  (isAdministrator.value && softwareStore.loading) || !!phpStore.installingComponentId || !!phpStore.assigningComponentId ||
  !!softwareStore.installingComponentId || !!softwareStore.removingComponentId || softwareStore.installingDefaults || softwareStore.checkingUpdates);
function canMutate() { return actorCurrent() && versionsMeasured.value && !actionsBusy.value && !locked.value; }

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
let settingsRequest = 0;
let disposed = false;
onBeforeUnmount(() => { disposed = true; viewRevision++; });
watch(() => [auth.session, auth.session?.user?.id, auth.session?.user?.role, auth.session?.isAuthenticated], () => {
  viewRevision++; settingsRequest++; verificationRequired.value = true; settingsLoaded.value = false;
  settingsError.value = t("panel.common.loadFailed"); notice.value = "";
  if (!isAdministrator.value) activeTab.value = "installed";
}, { flush: "sync" });

function settingsContext(site = sitesStore.selectedSiteName) {
  const request = settingsRequest, epoch = getSessionEpoch();
  return { site, current: () => !disposed && sitesStore.selectedSiteName === site && settingsRequest === request && getSessionEpoch() === epoch };
}

async function loadSettings(siteName: string) {
  ++settingsRequest;
  const context = settingsContext(siteName);
  settingsLoaded.value = false;
  settingsError.value = "";
  if (!siteName) {
    return;
  }
  try {
    const data = await getSitePhpSettings(siteName);
    if (!context.current()) return;
    settings.memoryLimit = data.memoryLimit;
    settings.maxExecutionTime = data.maxExecutionTime;
    settings.uploadMaxFilesize = data.uploadMaxFilesize;
    settings.postMaxSize = data.postMaxSize;
    settings.displayErrors = data.displayErrors;
    settings.openBasedirEnabled = data.openBasedirEnabled ?? false;
    settingsLoaded.value = true;
  } catch (error) {
    if (!context.current()) return;
    // ÖNCEKİ SİTENİN DEĞERLERİ EKRANDA KALMAZ. Okuyamadığımızı da söylüyoruz: sessizce
    // varsayılan göstermek, ölçülmemiş değerleri ölçülmüş gibi sunmaktı.
    settingsError.value = resolveApiMessage(error);
  }
}

async function saveSettings() {
  if (!actorCurrent() || locked.value || savingSettings.value || !selectedSiteName.value || !settingsLoaded.value) {
    return;
  }
  savingSettings.value = true;
  const context = settingsContext();
  try {
    const data = await updateSitePhpSettings(context.site, {
      memoryLimit: settings.memoryLimit,
      maxExecutionTime: settings.maxExecutionTime,
      uploadMaxFilesize: settings.uploadMaxFilesize,
      postMaxSize: settings.postMaxSize,
      displayErrors: settings.displayErrors,
      openBasedirEnabled: settings.openBasedirEnabled
    });
    if (!context.current()) return;
    settings.memoryLimit = data.memoryLimit;
    settings.maxExecutionTime = data.maxExecutionTime;
    settings.uploadMaxFilesize = data.uploadMaxFilesize;
    settings.postMaxSize = data.postMaxSize;
    settings.displayErrors = data.displayErrors;
    // İzolasyon durumunu SUNUCUNUN döndürdüğü GERÇEK değerle tazele: kutucuk, kaydedilmiş sanılan
    // ama uygulanmamış bir durumu göstermesin (yanıt php.ini'den taze okunur).
    settings.openBasedirEnabled = data.openBasedirEnabled ?? settings.openBasedirEnabled;
    showNotice(t("panel.php.settingsSaved"), false);
  } catch (error) {
    // `extractApiMessage` HAM anahtar döndürür (ör. `php.settings_locked`); çevirmeden basmak
    // kullanıcıya anlamsız bir dizge gösteriyordu. Aynı dosyanın diğer 5 noktası zaten doğruydu.
    if (context.current()) showNotice(resolveApiMessage(error), true);
  } finally {
    savingSettings.value = false;
  }
}

watch(() => sitesStore.selectedSiteName, (siteName) => {
  viewRevision++;
  notice.value = "";
  void loadSettings(siteName);
}, { immediate: true });
const selectedSiteName = computed({
  get: () => sitesStore.selectedSiteName,
  set: (value: string) => sitesStore.selectSite(value)
});
const selectedSiteAssignment = computed(() => phpStore.lastAssignment?.siteName.trim().toLowerCase() === selectedSiteName.value.trim().toLowerCase()
  ? phpStore.lastAssignment : null);
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
  if (!actorCurrent() || actionsBusy.value) return;
  const revision = ++viewRevision;
  refreshing.value = true;
  try {
    const reads: Promise<unknown>[] = [sitesStore.fetchSites(), phpStore.fetchVersions()];
    if (isAdministrator.value) reads.push(softwareStore.fetchCatalog());
    if (settingsError.value && selectedSiteName.value) reads.push(loadSettings(selectedSiteName.value));
    const results = await Promise.allSettled(reads);
    if (!contextCurrent(revision)) return;
    const failed = results.find((result): result is PromiseRejectedResult => result.status === "rejected");
    if (phpStore.loaded && (!isAdministrator.value || softwareStore.loaded)) verificationRequired.value = false;
    showNotice(failed ? resolveApiMessage(failed.reason) : "", !!failed);
  } finally {
    refreshing.value = false;
  }
}

async function installVersion(componentId: string) {
  if (!canMutate() || !isAdministrator.value || !catalogMeasured.value || installedComponentIds.value.has(componentId) ||
    !availablePhpComponents.value.some(component => component.id === componentId)) return;
  const revision = ++viewRevision;
  actionBusy.value = true;
  let completedMessage = "";
  try {
    completedMessage = await phpStore.installVersion(componentId);
    if (!contextCurrent(revision)) return;
    await softwareStore.fetchCatalog();
    if (!contextCurrent(revision)) return;
    if (!phpStore.loaded || !softwareStore.loaded) throw new PhpInstallationRefreshError(completedMessage, new Error("Catalog unconfirmed"));
    activeTab.value = "installed";
    showNotice(translate(completedMessage), false);
  } catch (error) {
    if (!contextCurrent(revision)) return;
    if (error instanceof PhpInstallationRefreshError) completedMessage = error.installedMessageKey;
    const clarification = error instanceof PhpInstallationRefreshError && extractApiMessage(error.refreshError) === "panel.php.installationUnconfirmed"
      ? "panel.php.installationUnconfirmed" : "panel.software.refreshAfterInstallFailed";
    showNotice(completedMessage ? `${translate(completedMessage)}\n\n${t(clarification)}` : resolveApiMessage(error), true);
  } finally {
    actionBusy.value = false;
  }
}

async function assignVersion(componentId: string) {
  if (!canMutate() || !phpStore.versions.some(version => version.componentId === componentId && version.status.toLowerCase() === "installed")) return;
  if (!selectedSiteName.value) {
    showNotice(t("panel.php.siteRequired"), true);
    return;
  }
  const revision = ++viewRevision, site = selectedSiteName.value;
  actionBusy.value = true;
  try {
    const assignment = await phpStore.assignVersion(site, componentId);
    if (contextCurrent(revision) && selectedSiteName.value === site) {
      const warnings = [assignment.handlerOrderNoticeKey ?? "aspnet.handler_order_unverified",
        assignment.isFastCgiRegistered ? "" : "panel.php.fastCgiMissing"].filter(Boolean);
      showNotice(warnings.length ? warnings.map(translate).join("\n\n") : t("php.assignment.success"), false, warnings.length > 0);
    }
  } catch (error) {
    if (contextCurrent(revision)) showNotice(resolveApiMessage(error), true);
  } finally {
    actionBusy.value = false;
  }
}

async function removeVersion(componentId: string) {
  if (!canMutate() || !isAdministrator.value) return;
  const version = phpStore.versions.find((item) => item.componentId === componentId);
  if (!version) return;
  const revision = viewRevision;
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
      if (!contextCurrent(revision) || !canMutate() || !isAdministrator.value ||
        !phpStore.versions.some(item => item.componentId === componentId && item.executablePath === version.executablePath)) return;
      actionBusy.value = true;
      const operation = ++viewRevision;
      phpStore.invalidateVersions();
      try {
        try { await softwareStore.removeComponent(componentId); }
        finally {
          // A scan started while removal was running can also predate its outcome.
          if (sessionEpoch === getSessionEpoch()) phpStore.invalidateVersions();
        }
        if (!contextCurrent(operation)) return;
        await phpStore.fetchVersions();
        if (contextCurrent(operation)) showNotice(t("panel.php.removed"), false);
      } catch (error) {
        if (contextCurrent(operation)) showNotice(resolveApiMessage(error), true);
      } finally {
        actionBusy.value = false;
      }
    },
  });
}

// Sunucu geneli native phpredis uzantısını etkinleştir (idempotent; zaten varsa "already" döner).
async function enableRedis() {
  if (!canMutate() || !isAdministrator.value) return;
  const revision = ++viewRevision;
  actionBusy.value = true;
  enablingRedis.value = true;
  try {
    const result = await enableRedisExtension();
    if (contextCurrent(revision)) showNotice(t(result.message === "already" ? "php.redis_already" : "php.redis_enabled"), false);
  } catch (error) {
    if (contextCurrent(revision)) showNotice(resolveApiMessage(error), true);
  } finally {
    enablingRedis.value = false;
    actionBusy.value = false;
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

function showNotice(message: string, isError: boolean, isWarning = false) {
  notice.value = message;
  noticeError.value = isError;
  noticeWarning.value = isWarning;
}
</script>

<style scoped>
.page-notice { white-space: pre-line; overflow-wrap: anywhere; }
.php-read-state {
  display: flex;
  align-items: center;
  justify-content: space-between;
  flex-wrap: wrap;
  gap: 12px;
  padding: 14px 16px;
  border: 1px solid var(--panel-border);
  border-left: 3px solid var(--warn-1);
  border-radius: 10px;
  background: var(--surface-1);
}
.php-read-state > div { flex: 1 1 220px; min-width: 0; }
.php-read-state p { margin: 5px 0 0; color: var(--text-muted); overflow-wrap: anywhere; }
.php-read-state button { flex-shrink: 0; }
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

@media (max-width: 640px) {
  .summary-strip { grid-template-columns: repeat(2, minmax(0, 1fr)); }
  .summary-item { min-width: 0; }
  .summary-item strong { overflow-wrap: anywhere; }
}
</style>
