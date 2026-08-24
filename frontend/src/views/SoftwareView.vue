<template>
  <section class="panel wide software-panel">
    <div class="software-header">
      <div>
        <h2>{{ t("panel.software.title") }}</h2>
        <p class="meta">{{ t("panel.software.subtitle") }}</p>
      </div>

      <div v-if="activeWorkspace === 'components'" class="software-actions">
        <button class="secondary compact" :disabled="softwareStore.loading" @click="refreshCatalog">
          {{ softwareStore.loading ? t("panel.software.scanning") : t("panel.software.scanSystem") }}
        </button>
        <button class="secondary compact" :disabled="softwareStore.checkingUpdates" @click="checkForUpdates">
          <span v-if="softwareStore.updatableCount > 0 && !softwareStore.checkingUpdates" class="update-count">
            {{ softwareStore.updatableCount }}
          </span>
          {{ softwareStore.checkingUpdates ? t("panel.software.checkingUpdates") : t("panel.software.checkUpdates") }}
        </button>
        <button class="compact" :disabled="softwareStore.installingDefaults" @click="installDefaultStack">
          {{ softwareStore.installingDefaults ? t("panel.software.installing") : t("panel.software.installDefaults") }}
        </button>
      </div>
    </div>

    <p v-if="notice" :class="['notice', 'page-notice', { error: noticeError }]" :role="noticeError ? 'alert' : 'status'">
      {{ notice }}
    </p>
    <details v-if="noticeDetails" class="diagnostic-panel">
      <summary>{{ t("panel.software.diagnostics") }}</summary>
      <pre>{{ noticeDetails }}</pre>
    </details>

    <!-- SAYFA YENİLENDİĞİNDE "KURULUM SÜRÜYOR" KAYBOLMUYOR.
         Aşağıdaki adım listesi `IOperationProgressService`'ten gelir ve o servis yalnız
         BELLEKTEDİR: panel yeniden başlarsa (ya da sayfa yenilenirse) adımlar yok olur ve ekran,
         PostgreSQL'in 365 MB'lık indirmesinin yarıda kaldığını değil HİÇ BAŞLAMADIĞINI gösterir.
         Bu şerit kaydı diskten okur; "kesildi" durumunu da söyleyebilir. -->
    <RunningTaskBanner
      :running="serverSoftware.running.value"
      :label="serverSoftware.label.value"
      :elapsed-text="serverSoftware.elapsedText.value"
      :hint="t('panel.runningJob.resumedHint')"
      :interrupted-label="serverSoftware.interruptedText.value"
      @dismiss="serverSoftware.dismissInterrupted" />

    <div
      v-if="softwareStore.installingComponentId || softwareStore.installingDefaults"
      class="install-progress"
      role="status">
      <span class="install-spinner" aria-hidden="true"></span>
      <span class="install-progress-text">
        <strong>{{ liveProgressLabel }}</strong>
        <small>{{ t("panel.software.installationActiveHint") }}</small>
        <ul v-if="(liveProgress?.steps?.length ?? 0) > 0" class="live-steps">
          <li v-for="(step, index) in liveProgress?.steps ?? []" :key="index" :class="['live-step', step.status]">
            <span class="live-step-icon">{{ step.status === 'done' ? '✓' : step.status === 'failed' ? '✕' : '•' }}</span>
            {{ step.name }}
          </li>
        </ul>
      </span>
    </div>

    <nav class="workspace-tabs" :aria-label="t('panel.software.workspaceLabel')">
      <button
        type="button"
        :class="['workspace-tab', { active: activeWorkspace === 'applications' }]"
        @click="activeWorkspace = 'applications'">
        <span class="workspace-mark">AP</span>
        <span>
          <strong>{{ t("panel.software.workspaceApplications") }}</strong>
          <small>{{ t("panel.software.workspaceApplicationsHint") }}</small>
        </span>
      </button>
      <button
        type="button"
        :class="['workspace-tab', { active: activeWorkspace === 'components' }]"
        @click="activeWorkspace = 'components'">
        <span class="workspace-mark">SV</span>
        <span>
          <strong>{{ t("panel.software.workspaceComponents") }}</strong>
          <small>{{ t("panel.software.workspaceComponentsHint") }}</small>
        </span>
      </button>
    </nav>

    <AppStoreView v-if="activeWorkspace === 'applications'" />

    <template v-else>
    <section class="software-toolbar">
      <div class="category-tabs" role="tablist">
        <!-- Seçili sekme GÖRSEL olarak yalnız renkle belliydi; `aria-selected` olmadan ekran
             okuyucu bütün kategori düğmelerini aynı okuyordu. -->
        <button
          v-for="category in categoryTabs"
          :key="category.key"
          type="button"
          role="tab"
          :aria-selected="activeCategory === category.key"
          :class="['category-tab', { active: activeCategory === category.key }]"
          @click="activeCategory = category.key">
          {{ category.label }}
          <span>{{ category.count }}</span>
        </button>
      </div>

      <label class="software-search">
        <span>{{ t("panel.software.search") }}</span>
        <input v-model="search" :placeholder="t('panel.software.searchPlaceholder')" />
      </label>
    </section>

    <section class="list-card">
      <div class="section-head">
        <div>
          <h3>{{ t("panel.software.available") }}</h3>
          <p class="meta">{{ t("panel.software.tableHint") }}</p>
        </div>
        <span class="meta">{{ visibleManifestRows.length }} / {{ manifestRows.length }}</span>
      </div>

      <div v-if="visibleManifestRows.length > 0" class="software-table">
        <div class="software-row table-head">
          <span>{{ t("panel.software.name") }}</span>
          <span>{{ t("panel.software.version") }}</span>
          <span>{{ t("panel.software.type") }}</span>
          <span>{{ t("panel.software.installDir") }}</span>
          <span>{{ t("panel.software.status") }}</span>
          <span>{{ t("panel.software.action") }}</span>
        </div>

        <article v-for="item in visibleManifestRows" :key="item.id" class="software-row">
          <div class="component-name">
            <strong>{{ item.displayName }}</strong>
            <span>{{ componentKindLabel(item) }}</span>
            <span v-if="item.installed?.isSystemDetected" class="detected-label">
              {{ t("panel.software.systemDetected") }} · {{ formatDetectionSource(item.installed.detectionSource) }}
            </span>
            <p v-if="item.note" class="meta">{{ item.note }}</p>
            <p v-if="item.installed?.lastError" class="error-text">
              {{ t("panel.software.lastError") }}: {{ translate(item.installed.lastError) }}
            </p>
          </div>
          <span class="version-cell">
            {{ item.installed?.version || item.version || "-" }}
            <span
              v-if="item.installed?.updateAvailable"
              class="update-badge"
              :title="t('panel.software.updateTo', { version: item.installed.availableVersion })">
              {{ t("panel.software.newVersion") }} {{ item.installed.availableVersion }}
            </span>
            <!-- Sürümü canlı sorgulanamayan bileşen: "güncel" DEĞİL, "bilinmiyor" demektir. -->
            <span
              v-else-if="item.installed && !item.installed.versionCheckSupported && !item.installed.isSystemDetected"
              class="unknown-badge"
              :title="t('panel.software.versionCheckUnsupportedHint')">
              {{ t("panel.software.versionCheckUnsupported") }}
            </span>
          </span>
          <span>{{ item.type }}</span>
          <span class="path-cell">{{ item.installed?.installDir || item.installDir }}</span>
          <span :class="['status-pill', statusClass(item.installed?.status)]">
            {{
              item.installed?.isSystemDetected
                ? t("panel.software.systemDetected")
                : item.installed
                  ? formatStatus(item.installed.status)
                  : t("panel.software.state.available")
            }}
          </span>
          <div class="row-actions">
            <button
              v-if="item.installed?.updateAvailable"
              class="compact update-action"
              :disabled="softwareStore.installingComponentId === item.id || softwareStore.removingComponentId === item.id"
              @click="updateComponent(item)">
              {{ softwareStore.installingComponentId === item.id ? t("panel.software.installing") : t("panel.software.update") }}
            </button>
            <button
              v-if="showInstallButton(item)"
              class="compact"
              :disabled="softwareStore.installingComponentId === item.id || softwareStore.removingComponentId === item.id"
              @click="installComponent(item.id)">
              {{
                softwareStore.installingComponentId === item.id
                  ? t("panel.software.installing")
                  : isFailed(item.installed)
                    ? t("panel.software.retryInstall")
                    : t("panel.software.install")
              }}
            </button>
            <button
              v-if="item.installed"
              class="secondary compact"
              :disabled="softwareStore.removingComponentId === item.id || softwareStore.installingComponentId === item.id"
              @click="removeComponent(item.id)">
              {{ softwareStore.removingComponentId === item.id ? t("panel.software.removing") : t("panel.software.remove") }}
            </button>
          </div>
        </article>
      </div>

      <p v-else class="empty-state">
        {{ t("panel.software.noItems") }}
      </p>
    </section>

    <section class="installed-strip">
      <div>
        <strong>{{ t("panel.software.installed") }}</strong>
        <p class="meta">{{ t("panel.software.installedHint") }}</p>
      </div>

      <div v-if="installedRows.length > 0" class="installed-list">
        <article v-for="item in installedRows" :key="item.id" class="installed-chip">
          <strong>{{ item.displayName }}</strong>
          <span>
            {{ item.version || "-" }} ·
            {{ item.isSystemDetected ? t("panel.software.systemDetected") : formatStatus(item.status) }}
          </span>
          <span v-if="item.isSystemDetected" class="detection-source">
            {{ t("panel.software.detectionSource") }}: {{ formatDetectionSource(item.detectionSource) }}
          </span>
          <label v-if="item.generatedPassword">
            <span>{{ t("panel.software.generatedPassword") }}</span>
            <input :value="item.generatedPassword" readonly />
          </label>
          <button
            class="secondary compact chip-remove"
            :disabled="softwareStore.removingComponentId === item.componentId"
            @click="removeComponent(item.componentId)">
            {{ softwareStore.removingComponentId === item.componentId ? t("panel.software.removing") : t("panel.software.remove") }}
          </button>
        </article>
      </div>

      <p v-else class="empty-state">
        {{ t("panel.software.noInstalled") }}
      </p>
    </section>

    </template>
  </section>
</template>

<script setup lang="ts">
import { computed, onMounted, ref, watch } from "vue";
import { useI18n } from "vue-i18n";
import AppStoreView from "./AppStoreView.vue";
import { getSoftwareRunningJobs } from "../api/software";
import { useConfirm } from "../components/ConfirmDialog.vue";
import RunningTaskBanner from "../components/RunningTaskBanner.vue";
import { runningJobLabel, useServerRunningTask } from "../composables/useRunningTask";
import { extractApiDetails, extractApiMessage } from "../stores/auth";
import { useSoftwareStore, type ComponentManifestItem, type InstalledComponent } from "../stores/software";

type CategoryKey = "all" | "installed" | "runtime" | "database" | "services" | "tools";
type WorkspaceKey = "applications" | "components";
type ManifestRow = ComponentManifestItem & { installed: InstalledComponent | null };

const { t } = useI18n();
const { confirm } = useConfirm();
const softwareStore = useSoftwareStore();

const notice = ref("");
const noticeDetails = ref("");
const noticeError = ref(false);
const search = ref("");
const activeCategory = ref<CategoryKey>("all");
const activeWorkspace = ref<WorkspaceKey>("applications");

const manifestRows = computed<ManifestRow[]>(() => {
  const installedMap = new Map((softwareStore.catalog?.installed ?? []).map((item) => [item.componentId, item]));

  return (softwareStore.catalog?.manifest ?? []).map((item) => ({
    ...item,
    installed: installedMap.get(item.id) ?? null
  }));
});

const installedRows = computed(() => softwareStore.catalog?.installed ?? []);

/*
 * KURULUM SAYFA YENİLEMEYE DAYANIYOR.
 *
 * ÖNCESİ: kurulum durumu yalnız `IOperationProgressService`'ten okunuyordu ve o servis BELLEKTE
 * tutuluyor. Kullanıcı 365 MB'lık bir PostgreSQL kurulumu sürerken sayfayı yenilediğinde panel
 * "kurulum yok" diyordu; indirme sunucuda devam ederken ekran boş görünüyor, kullanıcı kurulumu
 * ikinci kez tetiklemeye çalışıyordu.
 *
 * ŞİMDİ: `/software/running` kaydı DİSKTEN okuyor ve sahibini ölçüyor — panel kurulum sürerken
 * yeniden başlamışsa bunu "yarıda kaldı" olarak söyleyebiliyor. İki uç birbirinin yerini almaz:
 * adım listesi "hangi adımda"yı, bu şerit "sürüyor mu ve ne zamandır"ı cevaplar.
 *
 * YÜZDE YOK: indirilen paketin boyutu her kaynaktan gelmiyor, adımların süresi de birbirine
 * benzemiyor. Adım adları GERÇEK; onların üstüne uydurma bir yüzde koymak ölçülmüş bilgiyi
 * kirletmek olurdu.
 */
const serverSoftware = useServerRunningTask({
  fetchStatus: getSoftwareRunningJobs,
  label: (job) => runningJobLabel(t, job),
  interruptedLabel: (job) =>
    t("panel.runningJob.interrupted", {
      job: runningJobLabel(t, job),
      date: new Date(job.startedAtUtc).toLocaleString()
    })
});

// Sunucuda süren kurulum varsa şerit ekran açılır açılmaz dolar (yenilenmiş sayfa dahil).
onMounted(serverSoftware.start);

/*
 * KURULUM BİTİNCE KATALOG TAZELENİR.
 *
 * Bu sekme kurulumu kendisi başlatmadıysa (sayfa yenilenmişti) sonucu isteyecek açık bir isteği
 * yok; katalog tazelenmezse yeni kurulan bileşen listede "kurulu değil" görünmeye devam eder.
 */
watch(
  () => serverSoftware.job.value,
  async (yeni, eski) => {
    if (eski && !yeni && !softwareStore.installingComponentId && !softwareStore.installingDefaults) {
      await softwareStore.fetchCatalog().catch(() => undefined);
    }
  });

const liveProgress = computed(() => softwareStore.liveProgress);
const liveProgressLabel = computed(() => {
  const running = liveProgress.value?.steps.find((step) => step.status === "running");
  return running
    ? t("panel.software.installingNamed", { name: running.name })
    : t("panel.software.installationActive");
});

const visibleManifestRows = computed(() => {
  const query = search.value.trim().toLowerCase();

  return manifestRows.value.filter((item) => {
    const categoryMatches = activeCategory.value === "all" ||
      (activeCategory.value === "installed" ? !!item.installed : componentCategory(item) === activeCategory.value);
    const searchMatches = !query ||
      [item.id, item.displayName, item.version, item.type, item.installDir, item.serviceName, item.note]
        .some((value) => (value ?? "").toLowerCase().includes(query));

    return categoryMatches && searchMatches;
  });
});

const categoryTabs = computed(() => {
  const count = (key: CategoryKey) => key === "all"
    ? manifestRows.value.length
    : key === "installed"
      ? manifestRows.value.filter((item) => item.installed).length
      : manifestRows.value.filter((item) => componentCategory(item) === key).length;

  return [
    { key: "all" as const, label: t("panel.software.category.all"), count: count("all") },
    { key: "installed" as const, label: t("panel.software.category.installed"), count: count("installed") },
    { key: "runtime" as const, label: t("panel.software.category.runtime"), count: count("runtime") },
    { key: "database" as const, label: t("panel.software.category.database"), count: count("database") },
    { key: "services" as const, label: t("panel.software.category.services"), count: count("services") },
    { key: "tools" as const, label: t("panel.software.category.tools"), count: count("tools") }
  ];
});

async function refreshCatalog() {
  try {
    await softwareStore.fetchCatalog();
    showNotice(t("panel.software.refreshed"), false);
  } catch (error) {
    showApiError(error);
  }
}

async function checkForUpdates() {
  try {
    await softwareStore.checkForUpdates();
    const count = softwareStore.updatableCount;
    showNotice(
      count > 0
        ? t("panel.software.updatesFound", { count })
        : t("panel.software.updatesNone"),
      false);
  } catch (error) {
    showApiError(error);
  }
}

// İşlem sürerken /software/operations/progress'i yoklayıp canlı adımları gösterir;
// iş bitince yoklamayı durdurur. Böylece kurulum donmuş gibi görünmez.
async function pollProgressWhile<T>(work: Promise<T>): Promise<T> {
  let active = true;
  const loop = (async () => {
    while (active) {
      try {
        await softwareStore.fetchOperationProgress();
      } catch {
        // geçici hata; bir sonraki turda yeniden dene
      }
      if (!active) break;
      await new Promise((resolve) => setTimeout(resolve, 1500));
    }
  })();

  try {
    return await work;
  } finally {
    active = false;
    await loop.catch(() => undefined);
    softwareStore.liveProgress = null;
  }
}

async function installComponent(componentId: string) {
  try {
    const result = await pollProgressWhile(softwareStore.installComponent(componentId));
    showNotice(translate(result.message), false);
  } catch (error) {
    showApiError(error);
  }
}

// Servisi olan bileşenler (veritabanı motorları, mail, önbellek) yükseltme sırasında KISA SÜRE DURUR.
// Bunu sessizce yapmak yanlış olur; onay isteriz. Servissiz araçlar (phpMyAdmin, win-acme...) doğrudan.
const RESTARTS_ON_UPDATE = ["mysql", "mariadb", "postgres", "stalwart", "redis", "memcached", "docker"];

async function updateComponent(item: { id: string; installed?: { version?: string; availableVersion?: string } | null; displayName?: string }) {
  const risky = RESTARTS_ON_UPDATE.some((prefix) => item.id.toLowerCase().startsWith(prefix));
  if (risky) {
    await confirm({
      title: t("panel.software.updateConfirmTitle"),
      message: t("panel.software.updateConfirmMessage"),
      impact: [
        `${item.displayName ?? item.id}: ${item.installed?.version ?? "?"} → ${item.installed?.availableVersion ?? "?"}`,
        t("panel.software.updateConfirmService")
      ],
      action: async () => {
        await runUpdate(item.id);
      }
    });
    return;
  }

  await runUpdate(item.id);
}

async function runUpdate(componentId: string) {
  try {
    // force=true: sunucu "zaten kurulu" deyip atlamasın — yükseltmenin tek yolu bu.
    const result = await pollProgressWhile(softwareStore.installComponent(componentId, true));
    showNotice(translate(result.message), false);
  } catch (error) {
    showApiError(error);
  }
}

async function installDefaultStack() {
  try {
    const results = await pollProgressWhile(softwareStore.installDefaultStack());
    const failed = results.filter((result) => result.status.toLowerCase() === "failed");
    if (failed.length > 0) {
      showNotice(
        t("panel.software.defaultsPartial", { failed: failed.length, total: results.length }),
        true,
        failed.map((result) => `${result.displayName}: ${translate(result.message)}`).join("\n\n")
      );
      return;
    }

    showNotice(t("software.defaults.completed"), false);
  } catch (error) {
    showApiError(error);
  }
}

async function removeComponent(componentId: string) {
  const installed = installedRows.value.find((item) => item.componentId === componentId);
  const displayName = installed?.displayName || componentId;
  const message = installed?.isSystemDetected
    ? t(
        componentId === "vc-redist-x64"
          ? "panel.software.confirmSharedDetectedRemove"
          : "panel.software.confirmDetectedRemove",
        { name: displayName }
      )
    : t("panel.software.confirmRemove");

  const impact: string[] = [];
  if (installed?.version) {
    impact.push(`${displayName} ${installed.version}`);
  }
  if (installed?.serviceName) {
    impact.push(`${t("panel.confirm.impactService")}: ${installed.serviceName}`);
  }
  if (installed?.installDir) {
    impact.push(`${t("panel.confirm.impactFiles")}: ${installed.installDir}`);
  }

  // Yüksek riskli onay: bileşen kaldırma (sistem geneli) → bileşen adı yazılmadan devam edilemez.
  await confirm({
    title: t("panel.confirm.removeComponentTitle", { name: displayName }),
    message,
    impact,
    requireText: displayName,
    confirmLabel: t("panel.confirm.remove"),
    busyLabel: t("panel.confirm.removing"),
    action: async () => {
      try {
        await softwareStore.removeComponent(componentId);
        showNotice(t("software.remove.completed"), false);
      } catch (error) {
        showApiError(error);
      }
    },
  });
}

function componentCategory(item: ComponentManifestItem): CategoryKey {
  const id = item.id.toLowerCase();
  if (id.includes("mysql") || id.includes("mariadb") || id.includes("postgres")) {
    return "database";
  }

  if (id.includes("redis") || id.includes("memcached") || id.includes("nginx")) {
    return "services";
  }

  if (id.includes("php") || id.includes("node") || id.includes("python")) {
    return "runtime";
  }

  return "tools";
}

function componentKindLabel(item: ComponentManifestItem) {
  if (item.default) {
    return t("panel.software.recommended");
  }

  return t(`panel.software.category.${componentCategory(item)}`);
}

function isFailed(installed: InstalledComponent | null): boolean {
  return !!installed && (installed.status ?? "").toLowerCase() === "failed";
}

// Kurulu (status "Installed") bileşende "Kur" butonu gösterilmez; yalnızca kaldırma kalır.
// Başarısız kurulumda yeniden deneme için buton görünür kalır.
function showInstallButton(item: ManifestRow): boolean {
  if (!item.installed) {
    return true;
  }

  return (item.installed.status ?? "").toLowerCase() !== "installed";
}

function statusClass(status?: string) {
  const normalized = (status ?? "available").toLowerCase();
  if (normalized === "installed") {
    return "ok";
  }

  if (normalized === "failed") {
    return "danger";
  }

  return "muted";
}

function showNotice(message: string, isError: boolean, details = "") {
  notice.value = message;
  noticeDetails.value = details;
  noticeError.value = isError;
}

function showApiError(error: unknown) {
  showNotice(
    translate(extractApiMessage(error)),
    true,
    extractApiDetails(error)
  );
}

function translate(key: string): string {
  const translated = t(key);
  return translated === key ? key : translated;
}

function formatStatus(status: string): string {
  return translate(`panel.software.state.${status.toLowerCase()}`);
}

function formatDetectionSource(source: string): string {
  if (source.startsWith("Windows Service (") && source.endsWith(")")) {
    return t("panel.software.source.windows_service", { name: source.slice(17, -1) });
  }

  const sourceKey = source
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, "_")
    .replace(/^_+|_+$/g, "");
  const key = `panel.software.source.${sourceKey}`;
  const translated = t(key);
  return translated === key ? source : translated;
}
</script>

<style scoped>
.software-panel {
  gap: 12px;
}

.software-header,
.software-actions,
.section-head,
.software-toolbar,
.row-actions {
  display: flex;
  gap: 12px;
}

.software-header,
.section-head,
.software-toolbar {
  justify-content: space-between;
  align-items: flex-start;
}

.software-actions,
.row-actions {
  align-items: center;
  flex-wrap: wrap;
}

.workspace-tabs {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 8px;
  padding: 5px;
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-md);
  background: var(--input-bg);
}

.workspace-tab {
  display: flex;
  gap: 11px;
  align-items: center;
  min-height: 48px;
  padding: 7px 10px;
  border: 1px solid transparent;
  border-radius: var(--radius-sm);
  color: var(--text-soft);
  background: transparent;
  text-align: left;
}

.workspace-tab:hover {
  border-color: color-mix(in srgb, var(--accent-1) 18%, transparent);
  background: var(--surface-1);
}

.workspace-tab.active {
  border-color: color-mix(in srgb, var(--accent-1) 28%, transparent);
  background: color-mix(in srgb, var(--accent-1) 9%, var(--surface-2));
}

.workspace-tab > span:last-child {
  display: grid;
  gap: 3px;
}

.workspace-tab strong {
  color: var(--text-strong);
  font-size: 0.86rem;
}

.workspace-tab small {
  color: var(--text-muted);
  font-size: 0.72rem;
}

.workspace-mark {
  display: grid;
  flex: 0 0 34px;
  width: 34px;
  height: 34px;
  place-items: center;
  border-radius: 9px;
  color: var(--accent-1);
  font-size: 0.68rem;
  font-weight: 900;
  background: color-mix(in srgb, var(--accent-1) 11%, transparent);
}

.software-toolbar,
.list-card,
.installed-strip {
  padding: 11px;
  border-radius: var(--radius-md);
  border: 1px solid var(--panel-border);
  background: var(--surface-0);
}

.category-tabs {
  display: flex;
  flex-wrap: wrap;
  gap: 5px;
}

.category-tab {
  display: inline-flex;
  width: auto;
  align-items: center;
  gap: 8px;
  background: var(--surface-2);
  color: var(--text-soft);
  font-weight: 550;
}

.category-tab.active {
  background: linear-gradient(135deg, var(--accent-1), var(--accent-2));
  color: var(--on-accent);
}

.category-tab span {
  opacity: 0.74;
}

.software-search {
  display: grid;
  min-width: min(100%, 340px);
  gap: 6px;
}

.list-card,
.installed-strip,
.installed-list,
.component-name {
  display: grid;
  gap: 12px;
}

.software-table {
  display: grid;
  gap: 0;
  overflow: hidden;
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-sm);
}

.software-row {
  display: grid;
  grid-template-columns: minmax(190px, 1.2fr) 0.55fr 0.55fr minmax(190px, 1.2fr) 0.7fr auto;
  gap: 12px;
  align-items: center;
  padding: 9px 10px;
  border-bottom: 1px solid var(--panel-border);
  background: var(--surface-0);
}

.software-row:last-child {
  border-bottom: 0;
}

.software-row:not(.table-head):hover {
  background: color-mix(in srgb, var(--accent-1) 4%, var(--surface-0));
}

.table-head {
  color: var(--text-soft);
  font-size: 0.8rem;
  font-weight: 600;
  background: var(--surface-2);
}

.chip-remove {
  width: auto;
}

.installed-chip strong {
  color: var(--text-strong);
}

.component-name span,
.installed-chip span {
  color: var(--text-muted);
  font-size: 0.82rem;
}

.component-name .detected-label,
.detection-source {
  color: var(--accent-text);
  font-size: 0.76rem;
}

.path-cell {
  overflow: hidden;
  color: var(--text-soft);
  text-overflow: ellipsis;
  white-space: nowrap;
}

.version-cell {
  display: inline-flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 6px;
}

.update-badge {
  padding: 2px 7px;
  border-radius: 999px;
  font-size: 0.72rem;
  font-weight: 600;
  color: #fff;
  background: var(--accent-1, #2e7d32);
  white-space: nowrap;
}

.unknown-badge {
  padding: 2px 7px;
  border-radius: 999px;
  font-size: 0.72rem;
  color: var(--text-muted);
  border: 1px dashed var(--panel-border);
  white-space: nowrap;
}

.update-action {
  background: var(--accent-1, #2e7d32);
  border-color: var(--accent-1, #2e7d32);
}

.update-count {
  display: inline-grid;
  place-items: center;
  min-width: 17px;
  height: 17px;
  margin-right: 5px;
  padding: 0 4px;
  border-radius: 999px;
  font-size: 0.72rem;
  font-weight: 700;
  color: #fff;
  background: var(--accent-1, #2e7d32);
}

.status-pill {
  width: fit-content;
  padding: 4px 9px;
  border-radius: 999px;
  font-size: 0.78rem;
  font-weight: 700;
}

.status-pill.ok {
  color: var(--accent-1);
  background: color-mix(in srgb, var(--accent-1) 12%, transparent);
}

.status-pill.muted {
  color: var(--text-muted);
  background: var(--surface-2);
}

.status-pill.danger {
  color: var(--danger-text);
  background: var(--danger-bg);
}

.installed-list {
  grid-template-columns: 1fr;
  gap: 0;
  overflow: hidden;
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-sm);
}

.installed-chip {
  display: grid;
  grid-template-columns: minmax(180px, 1fr) minmax(130px, 0.55fr) minmax(180px, 0.8fr) minmax(220px, 1fr) auto;
  gap: 10px;
  align-items: center;
  padding: 8px 10px;
  border-bottom: 1px solid var(--panel-border);
  background: var(--surface-0);
}

.installed-chip:last-child {
  border-bottom: 0;
}

.installed-chip label {
  display: grid;
  grid-template-columns: auto minmax(120px, 1fr);
  gap: 8px;
  align-items: center;
}

.installed-chip input {
  min-height: 30px;
  padding-block: 5px;
}

.meta,
.empty-state {
  margin: 0;
  color: var(--text-muted);
  line-height: 1.5;
}

.error-text {
  margin: 0;
  color: var(--danger-text);
}

.diagnostic-panel {
  border: 1px solid color-mix(in srgb, var(--danger-1) 28%, transparent);
  border-radius: var(--radius-sm);
  background: var(--danger-bg);
  color: var(--danger-text);
}

.diagnostic-panel summary {
  padding: 9px 11px;
  cursor: pointer;
  font-weight: 650;
}

.diagnostic-panel pre {
  max-height: 260px;
  margin: 0;
  padding: 0 11px 11px;
  overflow: auto;
  color: var(--text-soft);
  font: 0.76rem/1.5 "Cascadia Code", "Consolas", monospace;
  white-space: pre-wrap;
  word-break: break-word;
}

.install-progress {
  display: flex;
  gap: 10px;
  align-items: flex-start;
  padding: 10px 12px;
  border: 1px solid color-mix(in srgb, var(--accent-1) 24%, transparent);
  border-radius: var(--radius-sm);
  background: color-mix(in srgb, var(--accent-1) 7%, transparent);
}

.install-progress-text {
  display: grid;
  gap: 4px;
}

.live-steps {
  list-style: none;
  margin: 4px 0 0;
  padding: 0;
  display: grid;
  gap: 3px;
  max-height: 200px;
  overflow-y: auto;
}

.live-step {
  display: flex;
  align-items: center;
  gap: 7px;
  font-size: 0.84rem;
  color: var(--text-muted);
}

.live-step.done {
  color: var(--accent-text, #2e7d32);
}

.live-step.failed {
  color: var(--danger-text, #c0392b);
}

.live-step.running {
  color: var(--text-strong);
}

.live-step-icon {
  width: 12px;
  text-align: center;
}

.install-progress > span:last-child {
  display: grid;
  gap: 2px;
}

.install-progress small {
  color: var(--text-muted);
}

.install-spinner {
  width: 17px;
  height: 17px;
  border: 2px solid color-mix(in srgb, var(--accent-1) 22%, transparent);
  border-top-color: var(--accent-1);
  border-radius: 50%;
  animation: software-spin 0.8s linear infinite;
}

@keyframes software-spin {
  to {
    transform: rotate(360deg);
  }
}

@media (max-width: 1120px) {
  .software-row {
    grid-template-columns: 1fr;
  }

  .table-head {
    display: none;
  }

  .installed-chip {
    grid-template-columns: 1fr;
  }
}

@media (max-width: 820px) {
  .software-header,
  .software-actions,
  .software-toolbar,
  .section-head {
    flex-direction: column;
    align-items: flex-start;
  }

  .workspace-tabs {
    grid-template-columns: 1fr;
  }
}
</style>
