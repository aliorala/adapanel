<template>
  <section class="panel apps-panel">
    <div class="module-head">
      <div>
        <h2>{{ t("panel.apps.title") }}</h2>
        <!-- Tek cümlelik, jargonsuz açıklama: sayfa ne yapar?
             `.meta` EKLENDI: alti ekranin besinde sayfa alt basligi bu sinifi tasiyor, burada
             tasimiyordu. Renk zaten ayni ciziliyordu (base.css `.module-head p` da muted verir),
             gorunur fark satir yuksekliginde: 1.45 -> 1.5, yani alt basliklar artik ayni ritimde. -->
        <p class="meta">{{ t("panel.apps.intro") }}</p>
      </div>

      <button
        class="secondary compact"
        :disabled="!selectedSiteName || appsStore.loadingSiteName === selectedSiteName"
        @click="refreshConfiguration">
        {{ t("panel.apps.refresh") }}
      </button>
    </div>

    <p v-if="notice" :class="['notice', 'page-notice', { error: noticeError }]" :role="noticeError ? 'alert' : 'status'">
      {{ notice }}
    </p>

    <div class="module-toolbar">
      <label class="stacked-field site-select" for="apps-site-select">
        <span class="field-label">{{ t("panel.php.selectSite") }}</span>
        <select id="apps-site-select" v-model="selectedSiteName">
          <option value="">{{ t("panel.php.selectSite") }}</option>
          <option v-for="site in sitesStore.sites" :key="site.name" :value="site.name">
            {{ site.name }}
          </option>
        </select>
      </label>
    </div>

    <!-- Yapılandırma gelmeden durum kartını göstermiyoruz: yüklenmemişken hostedByApp false
         olduğu için Node/Python ile barındırılan bir site "düz site" görünüyor ve kullanıcı
         barındırmanın kapalı olduğunu sanıyordu. -->
    <p v-if="selectedSiteName && !configurationLoaded" class="empty-state" role="status" aria-live="polite">
      {{ t("panel.common.loading") }}
    </p>

    <template v-if="selectedSiteName && configurationLoaded">
      <!-- 1) DURUM: kullanıcının ilk gördüğü şey, sitenin şu an neyle hizmet verdiği. -->
      <article :class="['runtime-status', hostedByApp ? 'app' : 'plain']">
        <div class="runtime-status-main">
          <span :class="['status-pill', hostedByApp ? 'ok' : 'warn']">
            {{ hostedByApp ? hostedRuntimeLabel : t("panel.apps.statusPlainPill") }}
          </span>
          <strong>
            {{ hostedByApp
              ? t("panel.apps.statusApp", { site: selectedSiteName, runtime: hostedRuntimeLabel })
              : t("panel.apps.statusPlain", { site: selectedSiteName }) }}
          </strong>
          <p class="field-hint">
            {{ hostedByApp
              ? t("panel.apps.statusAppDetail", { file: currentConfiguration?.startupFile || "-" })
              : t("panel.apps.statusPlainDetail") }}
          </p>
        </div>

        <a v-if="siteUrl" class="site-link" :href="siteUrl" target="_blank" rel="noopener">
          {{ t("panel.apps.openSite") }}
        </a>
      </article>

      <!-- 2) AKIŞ: düz form yığını yerine üç adım. -->
      <ol class="step-list">
        <li :class="['step', { done: stepRuntimeDone }]">
          <span class="step-index" aria-hidden="true">{{ stepRuntimeDone ? "✓" : "1" }}</span>
          <div class="step-body">
            <h3>{{ t("panel.apps.stepRuntimeTitle") }}</h3>
            <p class="field-hint">{{ t("panel.apps.stepRuntimeHint") }}</p>

            <div class="segmented-tabs runtime-choice" role="tablist">
              <button role="tab" :aria-selected="activeRuntime === 'node'" type="button" :class="{ active: activeRuntime === 'node' }" @click="activeRuntime = 'node'">
                Node.js
              </button>
              <button role="tab" :aria-selected="activeRuntime === 'python'" type="button" :class="{ active: activeRuntime === 'python' }" @click="activeRuntime = 'python'">
                Python
              </button>
            </div>

            <div class="row-actions">
              <span :class="['status-pill', runtimeInstalled ? 'ok' : 'warn']">
                {{ runtimeInstalled ? t("panel.php.installedState") : t("panel.apps.notInstalled") }}
              </span>
              <button
                v-if="!runtimeInstalled"
                class="secondary compact"
                v-bind="writeGate(!activeComponent || softwareStore.installingComponentId === activeComponent.id)"
                @click="installRuntime(activeComponent?.id)">
                {{ softwareStore.installingComponentId === activeComponent?.id
                  ? t("panel.php.installing")
                  : t("panel.php.install") }}
              </button>
              <span v-else class="field-hint">{{ t("panel.apps.runtimeReadyHint") }}</span>
            </div>
          </div>
        </li>

        <li :class="['step', { done: stepAppDone }]">
          <span class="step-index" aria-hidden="true">{{ stepAppDone ? "✓" : "2" }}</span>
          <div class="step-body">
            <h3>{{ t("panel.apps.stepAppTitle") }}</h3>
            <p class="field-hint">{{ t("panel.apps.stepAppHint") }}</p>

            <!-- EN KRİTİK BİLGİ: sabit port çalışmaz. Uzun paragraf değil, kopyalanabilir kod.
                 Kutu artik base.css'in paylasilan uyari seridi (`notice warn-banner`) uzerine
                 kuruluyor: kapsamli surum kendi kenar/dolgu/yaricap uclusunu yaziyordu ve
                 panelde uyari kutusunun ALTINCI gorunumuydu. Yerel sinifa yalniz ic bosluk
                 kaldi. -->
            <div class="notice warn-banner port-callout">
              <strong>{{ t("panel.apps.portTitle") }}</strong>
              <p class="field-hint">{{ t("panel.apps.portBody") }}</p>
              <CodeSample
                :code="portSample"
                :copy-label="t('panel.apps.copy')"
                :copied-label="t('panel.apps.copied')" />
              <p class="field-hint danger-hint">{{ t("panel.apps.portWarning") }}</p>
            </div>

            <div class="form-grid two">
              <label class="stacked-field" for="apps-startup-file">
                <span class="field-label">
                  {{ t("panel.apps.startupFile") }}
                  <FieldHint :label="t('panel.apps.startupFile')" :text="t('panel.apps.startupFileHelp')" />
                </span>
                <input id="apps-startup-file" v-model.trim="startupFile" :placeholder="startupFilePlaceholder" />
                <small class="field-hint">{{ t("panel.apps.startupFileExample", { example: startupFilePlaceholder }) }}</small>
              </label>

              <label class="stacked-field" for="apps-working-directory">
                <span class="field-label">
                  {{ t("panel.apps.workingDirectory") }}
                  <FieldHint :label="t('panel.apps.workingDirectory')" :text="t('panel.apps.workingDirectoryHelp')" />
                </span>
                <input id="apps-working-directory" v-model.trim="workingDirectory" placeholder="." />
                <small class="field-hint">{{ t("panel.apps.workingDirectoryExample") }}</small>
              </label>
            </div>

            <label class="stacked-field" for="apps-arguments">
              <span class="field-label">
                {{ t("panel.apps.arguments") }}
                <FieldHint :label="t('panel.apps.arguments')" :text="t('panel.apps.argumentsHelp')" />
              </span>
              <input id="apps-arguments" v-model="argumentsValue" :placeholder="argumentsPlaceholder" />
              <small class="field-hint">{{ t("panel.apps.optionalField") }}</small>
            </label>

            <label class="stacked-field" for="apps-environment">
              <span class="field-label">
                {{ t("panel.apps.environmentVariables") }}
                <FieldHint :label="t('panel.apps.environmentVariables')" :text="t('panel.apps.environmentVariablesHelp')" />
              </span>
              <textarea
                id="apps-environment"
                v-model="environmentText"
                rows="3"
                :placeholder="environmentPlaceholder"></textarea>
              <small class="field-hint">{{ t("panel.apps.autoEnvNote") }}</small>
              <div class="auto-env">
                <code v-for="variable in automaticEnvironmentVariables" :key="variable">{{ variable }}</code>
              </div>
            </label>
          </div>
        </li>

        <li :class="['step', { done: stepApplyDone }]">
          <span class="step-index" aria-hidden="true">{{ stepApplyDone ? "✓" : "3" }}</span>
          <div class="step-body">
            <h3>{{ t("panel.apps.stepApplyTitle") }}</h3>
            <p class="field-hint">{{ t("panel.apps.stepApplyHint", { site: selectedSiteName }) }}</p>

            <div class="row-actions">
              <button v-bind="writeGate(!canApply)" @click="applyRuntime">
                {{ busy
                  ? t("panel.apps.saving")
                  : (activeRuntime === "node" ? t("panel.apps.applyNode") : t("panel.apps.applyPython")) }}
              </button>
              <span v-if="!runtimeInstalled" class="field-hint">{{ t("panel.apps.applyBlockedRuntime") }}</span>
              <span v-else-if="!startupFile" class="field-hint">{{ t("panel.apps.applyBlockedStartupFile") }}</span>
            </div>
          </div>
        </li>
      </ol>

      <!-- 3) DÖNGÜYÜ KAPAT: uygulandıktan sonra sonucu nereden göreceği. -->
      <section v-if="currentConfiguration?.isEnabled" class="form-surface result-card">
        <div class="module-head">
          <div>
            <h3>{{ t("panel.apps.resultTitle") }}</h3>
            <p>{{ t("panel.apps.resultHint") }}</p>
          </div>
          <a v-if="siteUrl" class="site-link" :href="siteUrl" target="_blank" rel="noopener">
            {{ t("panel.apps.openSite") }}
          </a>
        </div>

        <div class="stacked-field">
          <span class="field-label">{{ t("panel.apps.logsDirectory") }}</span>
          <CodeSample
            v-if="currentConfiguration.logsDirectory"
            :code="currentConfiguration.logsDirectory"
            :copy-label="t('panel.apps.copy')"
            :copied-label="t('panel.apps.copied')" />
          <small class="field-hint">{{ t("panel.apps.logsHint") }}</small>
        </div>

        <details class="tech-details">
          <summary>{{ t("panel.apps.activeConfig") }}</summary>
          <dl class="detail-grid">
            <div>
              <dt>{{ t("panel.apps.currentRuntime") }}</dt>
              <dd>{{ hostedRuntimeLabel }}</dd>
            </div>
            <div>
              <dt>{{ t("panel.apps.startupFile") }}</dt>
              <dd>{{ currentConfiguration.startupFile || "-" }}</dd>
            </div>
            <div>
              <dt>{{ t("panel.apps.workingDirectory") }}</dt>
              <dd>{{ currentConfiguration.workingDirectory || "." }}</dd>
            </div>
            <div>
              <dt>{{ t("panel.apps.arguments") }}</dt>
              <dd>{{ currentConfiguration.arguments || "-" }}</dd>
            </div>
            <div>
              <dt>{{ t("panel.apps.environmentVariables") }}</dt>
              <dd>{{ currentConfiguration.environmentVariables.join(", ") || "-" }}</dd>
            </div>
            <div>
              <dt>{{ t("panel.apps.webConfig") }}</dt>
              <dd>{{ currentConfiguration.webConfigPath || "-" }}</dd>
            </div>
            <div>
              <dt>{{ t("panel.apps.httpPlatform") }}</dt>
              <dd>{{ currentConfiguration.hasHttpPlatformHandler ? t("panel.status.enabled") : t("panel.status.disabled") }}</dd>
            </div>
            <div>
              <dt>{{ t("panel.apps.managedRuntime") }}</dt>
              <dd>{{ currentConfiguration.managedRuntimeDisabled ? t("panel.apps.noManagedCode") : t("panel.apps.managedCodePresent") }}</dd>
            </div>
          </dl>
        </details>
      </section>
    </template>

    <p v-else class="empty-state">
      {{ t("panel.apps.selectSiteHint") }}
    </p>
  </section>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from "vue";
import { useI18n } from "vue-i18n";
import CodeSample from "../components/CodeSample.vue";
import FieldHint from "../components/FieldHint.vue";
import { extractApiMessage } from "../stores/auth";
import { useAppsStore } from "../stores/apps";
import { useSitesStore } from "../stores/sites";
import { useSoftwareStore } from "../stores/software";
import { useSubscriptionGate } from "../composables/useSubscriptionGate";

const { t } = useI18n();
// Askidaki abonelikte YAZMA dugmeleri bastan devre disi + sebebi title/aria ile soylenir
// (bkz. composables/useSubscriptionGate.ts). Okuma dugmeleri kapiya girmez.
const { writeGate } = useSubscriptionGate();
const appsStore = useAppsStore();
const sitesStore = useSitesStore();
const softwareStore = useSoftwareStore();

const notice = ref("");
const noticeError = ref(false);
const activeRuntime = ref<"node" | "python">("node");

// Panel bu değişkenleri web.config'e HER ZAMAN kendisi ekler (AppRuntimeService.BuildEnvironmentVariables).
// Kullanıcının kutusuna yazmasına gerek yok; kutuda göstermek yerine "otomatik" olarak listeliyoruz.
const NODE_AUTO_ENVIRONMENT = ["PORT=%HTTP_PLATFORM_PORT%"];
const PYTHON_AUTO_ENVIRONMENT = ["PORT=%HTTP_PLATFORM_PORT%", "PYTHONUNBUFFERED=1"];

const NODE_PORT_SAMPLE = [
  "// server.js",
  "const port = process.env.PORT || process.env.HTTP_PLATFORM_PORT;",
  "app.listen(port);"
].join("\n");

const PYTHON_PORT_SAMPLE = [
  "# app.py",
  "import os",
  "port = int(os.environ.get(\"PORT\") or os.environ.get(\"HTTP_PLATFORM_PORT\"))",
  "app.run(port=port)"
].join("\n");

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

const nodeEnvironmentText = ref("");
const pythonEnvironmentText = ref("");

const selectedSiteName = computed({
  get: () => sitesStore.selectedSiteName,
  set: (value: string) => sitesStore.selectSite(value)
});

const currentConfiguration = computed(() =>
  selectedSiteName.value ? appsStore.getConfiguration(selectedSiteName.value) : null);

// Site şu an gerçekten bir uygulamayla mı hizmet veriyor? (web.config'de handler var ve process tanımlı)
const hostedByApp = computed(() => Boolean(currentConfiguration.value?.isEnabled));
// Yapılandırma sunucudan geldi mi? "Gelmedi" ile "barındırma kapalı" ekranda ayrışsın diye gerekli.
const configurationLoaded = computed(() => currentConfiguration.value !== undefined && currentConfiguration.value !== null);

const hostedRuntimeLabel = computed(() => runtimeLabel(currentConfiguration.value?.runtimeType ?? "none"));

const activeForm = computed(() => (activeRuntime.value === "node" ? nodeForm : pythonForm));

const startupFile = computed({
  get: () => activeForm.value.startupFile,
  set: (value: string) => (activeForm.value.startupFile = value)
});

const workingDirectory = computed({
  get: () => activeForm.value.workingDirectory,
  set: (value: string) => (activeForm.value.workingDirectory = value)
});

const argumentsValue = computed({
  get: () => activeForm.value.arguments,
  set: (value: string) => (activeForm.value.arguments = value)
});

const environmentText = computed({
  get: () => (activeRuntime.value === "node" ? nodeEnvironmentText.value : pythonEnvironmentText.value),
  set: (value: string) => {
    if (activeRuntime.value === "node") nodeEnvironmentText.value = value;
    else pythonEnvironmentText.value = value;
  }
});

const automaticEnvironmentVariables = computed(() =>
  (activeRuntime.value === "node" ? NODE_AUTO_ENVIRONMENT : PYTHON_AUTO_ENVIRONMENT));

const portSample = computed(() => (activeRuntime.value === "node" ? NODE_PORT_SAMPLE : PYTHON_PORT_SAMPLE));

const startupFilePlaceholder = computed(() => (activeRuntime.value === "node" ? "server.js" : "app.py"));

const argumentsPlaceholder = computed(() => (activeRuntime.value === "node"
  ? t("panel.apps.nodeArgumentsPlaceholder")
  : t("panel.apps.pythonArgumentsPlaceholder")));

const environmentPlaceholder = computed(() => (activeRuntime.value === "node"
  ? "NODE_ENV=production"
  : "DJANGO_SETTINGS_MODULE=app.settings"));

const activeComponent = computed(() => {
  const componentId = activeRuntime.value === "node" ? "nodejs-lts" : "python-3";
  return softwareStore.catalog?.manifest.find((component) => component.id === componentId) ?? null;
});

const installedComponentIds = computed(() => new Set(
  (softwareStore.catalog?.installed ?? [])
    .filter((component) => component.status.toLowerCase() === "installed")
    .map((component) => component.componentId)));

const runtimeInstalled = computed(() =>
  installedComponentIds.value.has(activeRuntime.value === "node" ? "nodejs-lts" : "python-3"));

const busy = computed(() => (activeRuntime.value === "node"
  ? appsStore.nodeBusySiteName === selectedSiteName.value
  : appsStore.pythonBusySiteName === selectedSiteName.value));

const stepRuntimeDone = computed(() => runtimeInstalled.value);
const stepAppDone = computed(() => Boolean(startupFile.value.trim()));
const stepApplyDone = computed(() =>
  hostedByApp.value && currentConfiguration.value?.runtimeType === activeRuntime.value);

const canApply = computed(() =>
  Boolean(selectedSiteName.value) && runtimeInstalled.value && stepAppDone.value && !busy.value);

// Sonucu görebilmesi için sitenin adresi: nip.io önizleme bağlamalarını atla, asıl domaini göster.
const siteUrl = computed(() => {
  const site = sitesStore.sites.find((item) => item.name === selectedSiteName.value);
  if (!site) return "";

  const binding = site.bindings.find((item) => item.hostName && !item.hostName.toLowerCase().endsWith(".nip.io"))
    ?? site.bindings[0];
  if (!binding) return "";

  const protocol = (binding.protocol || "http").toLowerCase();
  const host = binding.hostName || window.location.hostname;
  const standardPort = (protocol === "http" && binding.port === 80) || (protocol === "https" && binding.port === 443);
  return `${protocol}://${host}${standardPort ? "" : `:${binding.port}`}`;
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
    activeRuntime.value = "node";
    nodeForm.startupFile = configuration.startupFile || nodeForm.startupFile;
    nodeForm.workingDirectory = configuration.workingDirectory || nodeForm.workingDirectory;
    nodeForm.arguments = configuration.arguments || "";
    nodeEnvironmentText.value = userEnvironmentVariables(configuration.environmentVariables, NODE_AUTO_ENVIRONMENT);
  }

  if (configuration.runtimeType === "python") {
    activeRuntime.value = "python";
    pythonForm.startupFile = configuration.startupFile || pythonForm.startupFile;
    pythonForm.workingDirectory = configuration.workingDirectory || pythonForm.workingDirectory;
    pythonForm.arguments = configuration.arguments || "";
    pythonEnvironmentText.value = userEnvironmentVariables(configuration.environmentVariables, PYTHON_AUTO_ENVIRONMENT);
  }
}, { immediate: true });

// Panelin otomatik eklediği satırları kutuda gösterme: kullanıcı yalnız kendi değişkenlerini görsün.
function userEnvironmentVariables(variables: string[], automatic: string[]): string {
  return variables.filter((variable) => !automatic.includes(variable)).join("\n");
}

function runtimeLabel(runtimeType: string): string {
  const key = `panel.apps.runtime.${runtimeType}`;
  const translated = t(key);
  return translated === key ? runtimeType : translated;
}

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

async function applyRuntime() {
  if (!selectedSiteName.value) {
    showNotice(t("panel.apps.selectSiteHint"), true);
    return;
  }

  const payload = {
    ...activeForm.value,
    environmentVariables: parseEnvironmentVariables(environmentText.value)
  };

  try {
    if (activeRuntime.value === "node") {
      await appsStore.configureNode(selectedSiteName.value, payload);
      showNotice(t("appruntime.node_updated"), false);
    } else {
      await appsStore.configurePython(selectedSiteName.value, payload);
      showNotice(t("appruntime.python_updated"), false);
    }
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
.apps-panel {
  display: grid;
  gap: var(--sp-12);
}

.site-select {
  width: min(320px, 100%);
}

/* Durum şeridi ve adım kartı: alti ekranda ortak IC KART recetesi
   (1px kenar + `--radius-md` + `--surface-1` + `--sp-12` dolgu; olcum ServerHealthView'de).
   Zemin `--surface-0` idi ve `.panel` de `--surface-0`: kart, uzerinde durdugu sayfayla AYNI
   renkteydi, yani kutuyu yalniz kenar tasiyordu -- acik temada o kenar 1.35:1 (base.css). */
.runtime-status,
.step {
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-md);
  background: var(--surface-1);
  padding: var(--sp-12);
}

.runtime-status {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  justify-content: space-between;
  gap: var(--sp-12);
}

.runtime-status.app {
  border-color: color-mix(in srgb, var(--ok-1) 38%, transparent);
}

.runtime-status-main {
  display: grid;
  gap: var(--sp-4);
  min-width: 0;
}

/* 1.02rem SILINDI: yazi olceginde yok ve govdeden (1rem) farki 0.27px -- hicbir ekranda
   ayirt edilemez, yani hiyerarsi kurmuyordu. `--fs-lg` govde boyunda kalir, ayrimi
   kalinlik ve konum yapar. */
.runtime-status-main strong {
  color: var(--text-strong);
  font-size: var(--fs-lg);
}

/* Dugme GIBI duran bir baglanti: olculeri gercek dugmeyle AYNI olmali, yoksa yan yana
   durduklari satirda birbirinden 1-2px kayik gorunurler. base.css `button`:
   `min-height: 34px`, `padding: var(--sp-6) var(--sp-12)`, `--radius-sm`, `font-weight: 600`. */
.site-link {
  display: inline-flex;
  align-items: center;
  flex: 0 0 auto;
  min-height: 34px;
  padding: var(--sp-6) var(--sp-12);
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-sm);
  background: var(--surface-3);
  color: var(--text-strong);
  font-weight: 600;
  text-decoration: none;
  white-space: nowrap;
}

.site-link:hover {
  border-color: color-mix(in srgb, var(--accent-1) 55%, transparent);
  color: var(--accent-text);
}

/* Adım listesi: numara + gövde; tamamlanan adım yeşil onay işaretine döner. */
.step-list {
  display: grid;
  gap: var(--sp-10);
  margin: 0;
  padding: 0;
  list-style: none;
}

.step {
  display: flex;
  align-items: flex-start;
  gap: var(--sp-12);
}

.step-index {
  display: inline-flex;
  flex: 0 0 auto;
  align-items: center;
  justify-content: center;
  width: 26px;
  height: 26px;
  border: 1px solid var(--panel-border);
  border-radius: 50%;
  background: var(--surface-2);
  color: var(--text-soft);
  font-size: var(--fs-xs);
  font-weight: 700;
}

.step.done {
  border-color: color-mix(in srgb, var(--ok-1) 32%, transparent);
}

.step.done .step-index {
  border-color: color-mix(in srgb, var(--ok-1) 45%, transparent);
  background: var(--ok-bg);
  color: var(--ok-text);
}

.step-body {
  display: grid;
  gap: var(--sp-10);
  min-width: 0;
  flex: 1 1 auto;
}

.step-body h3 {
  margin: 0;
}

.runtime-choice {
  width: fit-content;
  max-width: 100%;
}

/* Port uyarısı: bu ekranın 1 numaralı tökezleme noktası, göz seviyesinde dursun.
   Kenar/zemin/yaricap artik `.notice.warn-banner`dan geliyor; burada yalniz ic aralik var
   (paylasilan serit 4px veriyor, bu kutuda baslik + cumle + kod ornegi + uyari alt alta
   duruyor ve 4px onlari birbirine yapistiriyordu). */
.port-callout {
  gap: var(--sp-8);
}

.port-callout strong {
  color: var(--text-strong);
}

.danger-hint {
  color: var(--danger-text);
}

.auto-env {
  display: flex;
  flex-wrap: wrap;
  gap: var(--sp-6);
}

.auto-env code {
  padding: var(--sp-2) var(--sp-8);
  border: 1px solid var(--panel-border);
  border-radius: 999px;
  background: var(--surface-1);
  color: var(--text-soft);
  /* Yedek listesi TEK KAYNAGA baglandi: `--font-mono` (base.css). Ayni panelde uc ayri
     tek-aralikli yedek listesi vardi; belirtec tam bunun icin tanimlandi. */
  font-family: var(--font-mono);
  font-size: var(--fs-2xs);
  word-break: break-all;
}

.result-card .module-head h3 {
  margin: 0;
}

.tech-details summary {
  cursor: pointer;
  color: var(--text-soft);
  font-weight: 600;
}

/* `.detail-grid` alti bilesende kullanilan PAYLASILAN bir addir ve her biri kendi
   tanimini yazar. Bu ekranla ProxyView'inki yan yana konuldugunda uc yerde ayriliyordu
   (aralik 10 karsi 12, etiket 0.82rem karsi tanimsiz, satir arasi bosluk 3 karsi 4).
   Ikisi ayni degerlere getirildi: ayni ad, ayni gorunum. */
.detail-grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: var(--sp-12);
  margin: var(--sp-10) 0 0;
}

.detail-grid div {
  display: grid;
  gap: var(--sp-4);
  min-width: 0;
}

.detail-grid dt {
  color: var(--text-muted);
  font-size: var(--fs-xs);
}

.detail-grid dd {
  margin: 0;
  color: var(--text-soft);
  word-break: break-word;
}

@media (max-width: 760px) {
  .form-grid.two,
  .detail-grid {
    grid-template-columns: 1fr;
  }

  .site-link {
    width: 100%;
    justify-content: center;
  }
}
</style>
