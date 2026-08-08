<template>
  <section class="panel asp-panel">
    <div class="asp-header">
      <div>
        <h2>{{ t("panel.asp.title") }}</h2>
        <p class="meta">{{ t("panel.asp.subtitle") }}</p>
      </div>

      <button
        class="secondary compact"
        :disabled="!selectedSiteName || aspStore.loadingSiteName === selectedSiteName"
        @click="refreshConfiguration">
        {{ t("panel.asp.refresh") }}
      </button>
    </div>

    <p v-if="notice" :class="['notice', 'page-notice', { error: noticeError }]" :role="noticeError ? 'alert' : 'status'">
      {{ notice }}
    </p>

    <div class="module-toolbar">
      <div class="segmented-tabs">
        <button :class="{ active: activeSection === 'classic' }" @click="activeSection = 'classic'">{{ t("panel.asp.classicAsp") }}</button>
        <button :class="{ active: activeSection === 'dotnet' }" @click="activeSection = 'dotnet'">{{ t("panel.asp.dotNetTitle") }}</button>
        <button :class="{ active: activeSection === 'mixed' }" @click="activeSection = 'mixed'">{{ t("panel.asp.mixedHandlers") }}</button>
      </div>
      <label class="site-select">
        <select v-model="selectedSiteName">
          <option value="">{{ t("panel.php.selectSite") }}</option>
          <option v-for="site in sitesStore.sites" :key="site.name" :value="site.name">
            {{ site.name }}
          </option>
        </select>
      </label>
    </div>

    <template v-if="selectedSiteName && currentConfiguration">
      <div class="summary-strip">
        <article class="summary-item">
          <span class="tile-label">{{ t("panel.asp.classicAsp") }}</span>
          <strong>{{ currentConfiguration.classicAspEnabled ? t("panel.status.enabled") : t("panel.status.disabled") }}</strong>
        </article>

        <article class="summary-item">
          <span class="tile-label">{{ t("panel.asp.dotNetRuntime") }}</span>
          <strong>{{ currentConfiguration.managedRuntimeVersion || t("panel.asp.noManagedCode") }}</strong>
        </article>

        <article class="summary-item">
          <span class="tile-label">{{ t("panel.asp.pipelineMode") }}</span>
          <strong>{{ currentConfiguration.managedPipelineMode }}</strong>
        </article>

        <article class="summary-item">
          <span class="tile-label">{{ t("panel.asp.mixedMode") }}</span>
          <strong>{{ currentConfiguration.isMixedHandlersEnabled ? t("panel.status.enabled") : t("panel.status.disabled") }}</strong>
        </article>
      </div>

      <div class="asp-columns">
        <section v-if="activeSection === 'classic'" class="card-grid">
          <div class="section-head">
            <h3>{{ t("panel.asp.classicAsp") }}</h3>
            <span :class="['pill', currentConfiguration.classicAspEnabled ? 'ok' : 'muted']">
              {{ currentConfiguration.classicAspEnabled ? t("panel.status.enabled") : t("panel.status.disabled") }}
            </span>
          </div>

          <label class="toggle-row">
            <input v-model="classicAspForm.enabled" type="checkbox" />
            <span>{{ t("panel.asp.enableClassicAsp") }}</span>
          </label>

          <button
            :disabled="aspStore.classicBusySiteName === selectedSiteName"
            @click="saveClassicAsp">
            {{ aspStore.classicBusySiteName === selectedSiteName ? t("panel.asp.saving") : t("panel.asp.saveClassicAsp") }}
          </button>
        </section>

        <section v-if="activeSection === 'dotnet'" class="card-grid">
          <div class="section-head">
            <h3>{{ t("panel.asp.dotNetTitle") }}</h3>
            <span class="meta">{{ currentConfiguration.applicationPoolName }}</span>
          </div>

          <label>
            <span>{{ t("panel.asp.dotNetRuntime") }}</span>
            <select v-model="dotNetForm.managedRuntimeVersion">
              <option value="v4.0">.NET CLR v4.0</option>
              <option value="v2.0">.NET CLR v2.0</option>
              <option value="">{{ t("panel.asp.noManagedCode") }}</option>
            </select>
          </label>

          <label>
            <span>{{ t("panel.asp.pipelineMode") }}</span>
            <select v-model="dotNetForm.managedPipelineMode">
              <option value="Integrated">{{ t("panel.asp.integrated") }}</option>
              <option value="Classic">{{ t("panel.asp.classic") }}</option>
            </select>
          </label>

          <label class="toggle-row">
            <input v-model="dotNetForm.enable32BitAppOnWin64" type="checkbox" />
            <span>{{ t("panel.asp.enable32Bit") }}</span>
          </label>

          <button
            :disabled="aspStore.dotNetBusySiteName === selectedSiteName"
            @click="saveDotNet">
            {{ aspStore.dotNetBusySiteName === selectedSiteName ? t("panel.asp.saving") : t("panel.asp.saveDotNet") }}
          </button>
        </section>
      </div>

      <section v-if="activeSection === 'mixed'" class="card-grid">
        <div class="section-head">
          <div>
            <h3>{{ t("panel.asp.mixedHandlers") }}</h3>
            <p class="meta">{{ t("panel.asp.mixedHandlersSubtitle") }}</p>
          </div>
          <span :class="['pill', currentConfiguration.hasPhpHandler ? 'ok' : 'warn']">
            {{ currentConfiguration.hasPhpHandler ? currentConfiguration.phpHandlerName : t("panel.asp.phpHandlerMissing") }}
          </span>
        </div>

        <div class="mixed-grid">
          <label>
            <span>{{ t("panel.asp.phpVersion") }}</span>
            <select v-model="mixedForm.phpComponentId">
              <option value="">{{ t("panel.asp.selectPhpVersion") }}</option>
              <option v-for="version in installedPhpVersions" :key="version.componentId" :value="version.componentId">
                {{ version.displayName }}
              </option>
            </select>
          </label>

          <label>
            <span>{{ t("panel.asp.dotNetRuntime") }}</span>
            <select v-model="mixedForm.managedRuntimeVersion">
              <option value="v4.0">.NET CLR v4.0</option>
              <option value="v2.0">.NET CLR v2.0</option>
            </select>
          </label>

          <label>
            <span>{{ t("panel.asp.pipelineMode") }}</span>
            <select v-model="mixedForm.managedPipelineMode">
              <option value="Integrated">{{ t("panel.asp.integrated") }}</option>
              <option value="Classic">{{ t("panel.asp.classic") }}</option>
            </select>
          </label>

          <label class="toggle-row">
            <input v-model="mixedForm.enableClassicAsp" type="checkbox" />
            <span>{{ t("panel.asp.enableClassicAsp") }}</span>
          </label>

          <label class="toggle-row">
            <input v-model="mixedForm.enable32BitAppOnWin64" type="checkbox" />
            <span>{{ t("panel.asp.enable32Bit") }}</span>
          </label>
        </div>

        <button
          :disabled="!mixedForm.phpComponentId || aspStore.mixedBusySiteName === selectedSiteName"
          @click="enableMixedMode">
          {{ aspStore.mixedBusySiteName === selectedSiteName ? t("panel.asp.applyingMixed") : t("panel.asp.enableMixedMode") }}
        </button>
      </section>
    </template>

    <!-- Site seçiliyken yapılandırma gelene kadar "site seçin" ipucu görünüyordu;
         yüklenme durumu ile "seçim yok" ekranda ayırt edilemiyordu. -->
    <p v-else-if="selectedSiteName" class="empty-state" role="status" aria-live="polite">
      {{ t("panel.common.loading") }}
    </p>

    <p v-else class="meta">
      {{ t("panel.asp.selectSiteHint") }}
    </p>
  </section>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from "vue";
import { useI18n } from "vue-i18n";
import { extractApiMessage } from "../stores/auth";
import { useAspStore } from "../stores/asp";
import { usePhpStore } from "../stores/php";
import { useSitesStore } from "../stores/sites";

const { t } = useI18n();
const aspStore = useAspStore();
const phpStore = usePhpStore();
const sitesStore = useSitesStore();

const notice = ref("");
const noticeError = ref(false);
const activeSection = ref<"classic" | "dotnet" | "mixed">("mixed");

const selectedSiteName = computed({
  get: () => sitesStore.selectedSiteName,
  set: (value: string) => sitesStore.selectSite(value)
});

const currentConfiguration = computed(() =>
  selectedSiteName.value ? aspStore.getConfiguration(selectedSiteName.value) : null);

const installedPhpVersions = computed(() =>
  phpStore.versions.filter((version) => version.status.toLowerCase() === "installed"));

const classicAspForm = reactive({
  enabled: false
});

const dotNetForm = reactive({
  managedRuntimeVersion: "v4.0",
  managedPipelineMode: "Integrated",
  enable32BitAppOnWin64: false
});

const mixedForm = reactive({
  enableClassicAsp: true,
  managedRuntimeVersion: "v4.0",
  managedPipelineMode: "Integrated",
  enable32BitAppOnWin64: false,
  phpComponentId: ""
});

onMounted(async () => {
  try {
    if (sitesStore.sites.length === 0 && !sitesStore.loading) {
      await sitesStore.fetchSites();
    }

    if (phpStore.versions.length === 0 && !phpStore.loading) {
      await phpStore.fetchVersions();
    }
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
});

watch(selectedSiteName, async (siteName) => {
  if (!siteName) {
    return;
  }

  try {
    await aspStore.fetchConfiguration(siteName);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
}, { immediate: true });

watch(currentConfiguration, (configuration) => {
  if (!configuration) {
    return;
  }

  classicAspForm.enabled = configuration.classicAspEnabled;
  dotNetForm.managedRuntimeVersion = configuration.managedRuntimeVersion;
  dotNetForm.managedPipelineMode = configuration.managedPipelineMode || "Integrated";
  dotNetForm.enable32BitAppOnWin64 = configuration.enable32BitAppOnWin64;
  mixedForm.enableClassicAsp = configuration.classicAspEnabled;
  mixedForm.managedRuntimeVersion = configuration.managedRuntimeVersion || "v4.0";
  mixedForm.managedPipelineMode = configuration.managedPipelineMode || "Integrated";
  mixedForm.enable32BitAppOnWin64 = configuration.enable32BitAppOnWin64;
  mixedForm.phpComponentId = configuration.phpComponentId || mixedForm.phpComponentId;
}, { immediate: true });

watch(installedPhpVersions, (versions) => {
  if (!mixedForm.phpComponentId) {
    mixedForm.phpComponentId = currentConfiguration.value?.phpComponentId || versions[0]?.componentId || "";
  }
}, { immediate: true });

async function refreshConfiguration() {
  if (!selectedSiteName.value) {
    showNotice(t("panel.asp.selectSiteHint"), true);
    return;
  }

  try {
    await aspStore.fetchConfiguration(selectedSiteName.value);
    showNotice(t("panel.asp.refreshed"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
}

async function saveClassicAsp() {
  if (!selectedSiteName.value) {
    showNotice(t("panel.asp.selectSiteHint"), true);
    return;
  }

  try {
    await aspStore.setClassicAsp(selectedSiteName.value, { enabled: classicAspForm.enabled });
    showNotice(t("aspnet.classic_asp_updated"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
}

async function saveDotNet() {
  if (!selectedSiteName.value) {
    showNotice(t("panel.asp.selectSiteHint"), true);
    return;
  }

  try {
    await aspStore.setDotNetRuntime(selectedSiteName.value, { ...dotNetForm });
    showNotice(t("aspnet.dotnet_updated"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
}

async function enableMixedMode() {
  if (!selectedSiteName.value) {
    showNotice(t("panel.asp.selectSiteHint"), true);
    return;
  }

  try {
    await aspStore.enableMixedMode(selectedSiteName.value, { ...mixedForm });
    showNotice(t("aspnet.mixed_handlers_updated"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
}

function resolveApiMessage(error: unknown): string {
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
.asp-panel,
.status-grid,
.asp-columns,
.card-grid,
.mixed-grid {
  display: grid;
  gap: 12px;
}

.asp-header,
.section-head {
  display: flex;
  gap: 12px;
  justify-content: space-between;
  align-items: flex-start;
}

.status-grid,
.asp-columns {
  grid-template-columns: repeat(4, minmax(0, 1fr));
}

.status-tile,
.card-grid {
  display: grid;
  gap: 12px;
  padding: 14px;
  border-radius: var(--radius-md);
  border: 1px solid var(--panel-border);
  background: linear-gradient(180deg, var(--surface-2), var(--surface-2));
}

.tile-label {
  color: var(--text-muted);
  font-size: 0.84rem;
  text-transform: uppercase;
  letter-spacing: 0.08em;
}

.mixed-grid {
  grid-template-columns: repeat(3, minmax(0, 1fr));
}

.site-select {
  width: min(260px, 52vw);
}

.pill {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  min-height: 34px;
  padding: 4px 12px;
  border-radius: 999px;
  font-size: 0.82rem;
  font-weight: 700;
}

.pill.ok {
  background: color-mix(in srgb, var(--accent-1) 14%, transparent);
  color: var(--accent-1);
}

.pill.warn,
.pill.muted {
  background: color-mix(in srgb, var(--warn-1) 12%, transparent);
  color: var(--warn-text);
}

.toggle-row {
  grid-template-columns: auto 1fr;
  align-items: center;
}

.toggle-row input {
  width: 18px;
  height: 18px;
  margin: 0;
}

.toggle-row span {
  margin-left: 10px;
}

/* Onay kutusu satirlari DISARIDA: bunlar iki satirlik bir izgara degil, yan yana duran
   kutu+etikettir. Kapsamli `label` kurali element duzeyinde oldugu icin global
   `.check-field` kuralini eziyor, onay kutusu hucreyi kaplayip etiketi alt satira dusuruyordu.
   `:where()` KULLANILIYOR, duz `:not()` DEGIL: `:not(.x)` argumaninin ozgullugunu kurala EKLER
   ((0,1,1) -> (0,2,1)) ve boylece bilesenlerin kendi `.checkbox-field[data-v]` (0,2,0) kurallarini
   ezer -- SiteAliases'in onay kutulari tam olarak boyle bozulmustu. `:where()` her zaman SIFIR
   ozgullukte oldugu icin dislama yapar ama kurali guclendirmez. */
label:not(:where(.check-field, .checkbox-field, .checkbox-row)) {
  display: grid;
  gap: 8px;
}

@media (max-width: 960px) {
  .status-grid,
  .asp-columns,
  .mixed-grid {
    grid-template-columns: 1fr;
  }
}

@media (max-width: 760px) {
  .asp-header,
  .section-head {
    flex-direction: column;
    align-items: flex-start;
  }
}
</style>
