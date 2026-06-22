<template>
  <section class="panel apps-panel">
    <div class="apps-header">
      <div>
        <h2>{{ t("panel.apps.title") }}</h2>
        <p class="meta">{{ t("panel.apps.subtitle") }}</p>
      </div>

      <button
        class="secondary compact"
        :disabled="!selectedSiteName || appsStore.loadingSiteName === selectedSiteName"
        @click="refreshConfiguration">
        {{ t("panel.apps.refresh") }}
      </button>
    </div>

    <p v-if="notice" :class="['notice', { error: noticeError }]">
      {{ notice }}
    </p>

    <div class="module-toolbar">
      <div class="segmented-tabs">
        <button :class="{ active: activeRuntime === 'node' }" @click="activeRuntime = 'node'">Node.js</button>
        <button :class="{ active: activeRuntime === 'python' }" @click="activeRuntime = 'python'">Python</button>
        <button :class="{ active: activeRuntime === 'current' }" @click="activeRuntime = 'current'">{{ t("panel.apps.activeConfig") }}</button>
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

    <template v-if="selectedSiteName">
      <div class="summary-strip">
        <article class="summary-item">
          <span class="tile-label">{{ t("panel.apps.currentRuntime") }}</span>
          <strong>{{ runtimeLabel }}</strong>
        </article>

        <article class="summary-item">
          <span class="tile-label">{{ t("panel.apps.httpPlatform") }}</span>
          <strong>{{ currentConfiguration?.hasHttpPlatformHandler ? t("panel.status.enabled") : t("panel.status.disabled") }}</strong>
        </article>

        <article class="summary-item">
          <span class="tile-label">{{ t("panel.apps.managedRuntime") }}</span>
          <strong>{{ currentConfiguration?.managedRuntimeDisabled ? t("panel.apps.noManagedCode") : t("panel.apps.managedCodePresent") }}</strong>
        </article>

        <article class="summary-item">
          <span class="tile-label">{{ t("panel.apps.workingDirectory") }}</span>
          <strong>{{ currentConfiguration?.workingDirectory || "." }}</strong>
        </article>
      </div>

      <div class="apps-columns">
        <section v-if="activeRuntime === 'node'" class="runtime-card">
          <div class="section-head">
            <div>
              <h3>{{ t("panel.apps.nodeTitle") }}</h3>
              <p class="meta">{{ t("panel.apps.nodeHint") }}</p>
            </div>
            <button
              class="secondary compact"
              :disabled="!nodeComponent || softwareStore.installingComponentId === nodeComponent.id || nodeInstalled"
              @click="installRuntime(nodeComponent?.id)">
              {{ nodeInstalled ? t("panel.php.installedState") : (softwareStore.installingComponentId === nodeComponent?.id ? t("panel.php.installing") : t("panel.php.install")) }}
            </button>
          </div>

          <label>
            <span>{{ t("panel.apps.startupFile") }}</span>
            <input v-model="nodeForm.startupFile" />
          </label>

          <label>
            <span>{{ t("panel.apps.workingDirectory") }}</span>
            <input v-model="nodeForm.workingDirectory" />
          </label>

          <label>
            <span>{{ t("panel.apps.arguments") }}</span>
            <input v-model="nodeForm.arguments" :placeholder="t('panel.apps.nodeArgumentsPlaceholder')" />
          </label>

          <label>
            <span>{{ t("panel.apps.environmentVariables") }}</span>
            <textarea v-model="nodeEnvironmentText" rows="4" :placeholder="environmentPlaceholder"></textarea>
          </label>

          <button
            :disabled="!nodeInstalled || !selectedSiteName || appsStore.nodeBusySiteName === selectedSiteName"
            @click="saveNodeRuntime">
            {{ appsStore.nodeBusySiteName === selectedSiteName ? t("panel.apps.saving") : t("panel.apps.applyNode") }}
          </button>
        </section>

        <section v-if="activeRuntime === 'python'" class="runtime-card">
          <div class="section-head">
            <div>
              <h3>{{ t("panel.apps.pythonTitle") }}</h3>
              <p class="meta">{{ t("panel.apps.pythonHint") }}</p>
            </div>
            <button
              class="secondary compact"
              :disabled="!pythonComponent || softwareStore.installingComponentId === pythonComponent.id || pythonInstalled"
              @click="installRuntime(pythonComponent?.id)">
              {{ pythonInstalled ? t("panel.php.installedState") : (softwareStore.installingComponentId === pythonComponent?.id ? t("panel.php.installing") : t("panel.php.install")) }}
            </button>
          </div>

          <label>
            <span>{{ t("panel.apps.startupFile") }}</span>
            <input v-model="pythonForm.startupFile" />
          </label>

          <label>
            <span>{{ t("panel.apps.workingDirectory") }}</span>
            <input v-model="pythonForm.workingDirectory" />
          </label>

          <label>
            <span>{{ t("panel.apps.arguments") }}</span>
            <input v-model="pythonForm.arguments" :placeholder="t('panel.apps.pythonArgumentsPlaceholder')" />
          </label>

          <label>
            <span>{{ t("panel.apps.environmentVariables") }}</span>
            <textarea v-model="pythonEnvironmentText" rows="4" :placeholder="environmentPlaceholder"></textarea>
          </label>

          <button
            :disabled="!pythonInstalled || !selectedSiteName || appsStore.pythonBusySiteName === selectedSiteName"
            @click="savePythonRuntime">
            {{ appsStore.pythonBusySiteName === selectedSiteName ? t("panel.apps.saving") : t("panel.apps.applyPython") }}
          </button>
        </section>
      </div>

      <section v-if="activeRuntime === 'current' && currentConfiguration?.isEnabled" class="runtime-card">
        <div class="section-head">
          <div>
            <h3>{{ t("panel.apps.activeConfig") }}</h3>
            <p class="meta">{{ currentConfiguration.processPath }}</p>
          </div>
          <span :class="['pill', currentConfiguration.runtimeType === 'none' ? 'muted' : 'ok']">
            {{ runtimeLabel }}
          </span>
        </div>

        <dl class="detail-grid">
          <div>
            <dt>{{ t("panel.apps.startupFile") }}</dt>
            <dd>{{ currentConfiguration.startupFile || "-" }}</dd>
          </div>
          <div>
            <dt>{{ t("panel.apps.arguments") }}</dt>
            <dd>{{ currentConfiguration.arguments || "-" }}</dd>
          </div>
          <div>
            <dt>{{ t("panel.apps.webConfig") }}</dt>
            <dd>{{ currentConfiguration.webConfigPath || "-" }}</dd>
          </div>
          <div>
            <dt>{{ t("panel.apps.logsDirectory") }}</dt>
            <dd>{{ currentConfiguration.logsDirectory || "-" }}</dd>
          </div>
        </dl>
      </section>
    </template>

    <p v-else class="meta">
      {{ t("panel.apps.selectSiteHint") }}
    </p>
  </section>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from "vue";
import { useI18n } from "vue-i18n";
import { extractApiMessage } from "../stores/auth";
import { useAppsStore } from "../stores/apps";
import { useSitesStore } from "../stores/sites";
import { useSoftwareStore } from "../stores/software";

const { t } = useI18n();
const appsStore = useAppsStore();
const sitesStore = useSitesStore();
const softwareStore = useSoftwareStore();

const notice = ref("");
const noticeError = ref(false);
const activeRuntime = ref<"node" | "python" | "current">("node");
const nodeEnvironmentText = ref("PORT=%HTTP_PLATFORM_PORT%");
const pythonEnvironmentText = ref("PORT=%HTTP_PLATFORM_PORT%\nPYTHONUNBUFFERED=1");

const selectedSiteName = computed({
  get: () => sitesStore.selectedSiteName,
  set: (value: string) => sitesStore.selectSite(value)
});

const currentConfiguration = computed(() =>
  selectedSiteName.value ? appsStore.getConfiguration(selectedSiteName.value) : null);

const nodeComponent = computed(() =>
  softwareStore.catalog?.manifest.find((component) => component.id === "nodejs-lts") ?? null);

const pythonComponent = computed(() =>
  softwareStore.catalog?.manifest.find((component) => component.id === "python-3") ?? null);

const installedComponentIds = computed(() => new Set(
  (softwareStore.catalog?.installed ?? [])
    .filter((component) => component.status.toLowerCase() === "installed")
    .map((component) => component.componentId)));

const nodeInstalled = computed(() => installedComponentIds.value.has("nodejs-lts"));
const pythonInstalled = computed(() => installedComponentIds.value.has("python-3"));

const runtimeLabel = computed(() => {
  const runtimeType = currentConfiguration.value?.runtimeType ?? "none";
  const key = `panel.apps.runtime.${runtimeType}`;
  const translated = t(key);
  return translated === key ? runtimeType : translated;
});

const environmentPlaceholder = computed(() => "PORT=%HTTP_PLATFORM_PORT%");

const nodeForm = reactive({
  startupFile: "server.js",
  workingDirectory: ".",
  arguments: "",
  componentId: "nodejs-lts"
});

const pythonForm = reactive({
  startupFile: "app.py",
  workingDirectory: ".",
  arguments: "",
  componentId: "python-3"
});

onMounted(async () => {
  try {
    if (!softwareStore.catalog && !softwareStore.loading) {
      await softwareStore.fetchCatalog();
    }

    if (sitesStore.sites.length === 0 && !sitesStore.loading) {
      await sitesStore.fetchSites();
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
    await appsStore.fetchConfiguration(siteName);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
}, { immediate: true });

watch(currentConfiguration, (configuration) => {
  if (!configuration) {
    return;
  }

  if (configuration.runtimeType === "node") {
    nodeForm.startupFile = configuration.startupFile || nodeForm.startupFile;
    nodeForm.workingDirectory = configuration.workingDirectory || nodeForm.workingDirectory;
    nodeForm.arguments = configuration.arguments || "";
    nodeEnvironmentText.value = configuration.environmentVariables.join("\n") || nodeEnvironmentText.value;
  }

  if (configuration.runtimeType === "python") {
    pythonForm.startupFile = configuration.startupFile || pythonForm.startupFile;
    pythonForm.workingDirectory = configuration.workingDirectory || pythonForm.workingDirectory;
    pythonForm.arguments = configuration.arguments || "";
    pythonEnvironmentText.value = configuration.environmentVariables.join("\n") || pythonEnvironmentText.value;
  }
}, { immediate: true });

async function refreshConfiguration() {
  if (!selectedSiteName.value) {
    showNotice(t("panel.apps.selectSiteHint"), true);
    return;
  }

  try {
    await appsStore.fetchConfiguration(selectedSiteName.value);
    showNotice(t("panel.apps.refreshed"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
}

async function installRuntime(componentId?: string) {
  if (!componentId) {
    return;
  }

  try {
    await softwareStore.installComponent(componentId);
    showNotice(t("software.install.completed"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
}

async function saveNodeRuntime() {
  if (!selectedSiteName.value) {
    showNotice(t("panel.apps.selectSiteHint"), true);
    return;
  }

  try {
    await appsStore.configureNode(selectedSiteName.value, {
      ...nodeForm,
      environmentVariables: parseEnvironmentVariables(nodeEnvironmentText.value)
    });
    showNotice(t("appruntime.node_updated"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
}

async function savePythonRuntime() {
  if (!selectedSiteName.value) {
    showNotice(t("panel.apps.selectSiteHint"), true);
    return;
  }

  try {
    await appsStore.configurePython(selectedSiteName.value, {
      ...pythonForm,
      environmentVariables: parseEnvironmentVariables(pythonEnvironmentText.value)
    });
    showNotice(t("appruntime.python_updated"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
}

function parseEnvironmentVariables(value: string): string[] {
  return value
    .split(/\r?\n/g)
    .map((item) => item.trim())
    .filter(Boolean);
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
.apps-panel,
.status-grid,
.apps-columns,
.runtime-card,
.detail-grid {
  display: grid;
  gap: 12px;
}

.apps-header,
.section-head {
  display: flex;
  gap: 12px;
  justify-content: space-between;
  align-items: flex-start;
}

.status-grid,
.apps-columns,
.detail-grid {
  grid-template-columns: repeat(4, minmax(0, 1fr));
}

.apps-columns,
.detail-grid {
  grid-template-columns: repeat(2, minmax(0, 1fr));
}

.status-tile,
.runtime-card {
  display: grid;
  gap: 12px;
  padding: 14px;
  border-radius: var(--radius-md);
  border: 1px solid var(--panel-border);
  background: linear-gradient(180deg, var(--surface-2), var(--surface-2));
}

.tile-label,
.detail-grid dt,
.meta {
  color: var(--text-muted);
}

.detail-grid {
  margin: 0;
}

.detail-grid div {
  display: grid;
  gap: 4px;
}

.detail-grid dd,
.meta {
  margin: 0;
  word-break: break-word;
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

.pill.muted {
  background: color-mix(in srgb, var(--warn-1) 12%, transparent);
  color: var(--warn-text);
}

.site-select {
  width: min(260px, 52vw);
}

label {
  display: grid;
  gap: 8px;
}

input,
textarea,
select,
button {
  width: 100%;
  border-radius: var(--radius-sm);
  font: inherit;
}

input,
textarea,
select {
  border: 1px solid var(--panel-border);
  padding: 12px 14px;
  background: var(--input-bg);
  color: var(--text-strong);
}

textarea {
  resize: vertical;
}

@media (max-width: 980px) {
  .status-grid,
  .apps-columns,
  .detail-grid {
    grid-template-columns: 1fr;
  }
}

@media (max-width: 760px) {
  .apps-header,
  .section-head {
    flex-direction: column;
    align-items: flex-start;
  }
}
</style>
