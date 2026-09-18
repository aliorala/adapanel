<template>
  <section class="panel software-panel">
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
          <p class="meta">{{ t("panel.software.pathHint") }}</p>
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
            <p v-if="item.installed?.lastError" class="error-text">
              {{ t("panel.software.lastError") }}: {{ translate(item.installed.lastError) }}
            </p>
          </div>
          <!-- data-label: DAR EKRANDA hücrenin başlığı. Tablo 1120px altında tek sütuna
               indiğinde başlık satırı kayboluyor ve "8.3.6", "runtime", "C:\..." anonim
               satırlara dönüşüyordu; etiket artık hücrenin İÇİNDE, aynı çeviri anahtarından. -->
          <span class="version-cell" :data-label="t('panel.software.version')">
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
          <!-- TÜR SÜTUNU İÇ DEĞER GÖSTERİYORDU: "phpFile", "phar", "targz" — bunlar kullanıcının
               kelimeleri değil, `components.json`'un `type` alanının kendisi. Çeviri yoksa ham
               değere düşülür (bilgi kaybolmaz), ama tanınan altı tür artık Türkçe okunur. -->
          <span :data-label="t('panel.software.type')">{{ componentTypeLabel(item.type) }}</span>
          <span class="path-cell" :data-label="t('panel.software.installDir')">{{ displayInstallDir(item) }}</span>
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
              class="compact"
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

          <!-- AÇIKLAMA SÜTUNDAN ÇIKTI, SATIRIN ALTINA GEÇTİ.
               ÖLÇÜLDÜ: açıklama, ızgaranın 1.2fr'lik İLK sütununda yaşıyordu (~280px). ClamAV'ın
               190 karakterlik notu orada 6-7 satıra sarıyor, satır yüksekliği 60px'ten 190px'e
               çıkıyordu; yanındaki beş hücre `align-items: center` ile o yüksekliğin ortasında
               yüzüyordu. 25 satır boyunca ne satır ritmi ne de sütun hizası kalıyordu.
               Not artık ızgaranın TAMAMINI kaplayan kendi bandında (`grid-column: 1 / -1`):
               metin ~3 kat geniş bir ölçüde okunur, hücreler tek satırlık ortak yükseklikte
               kalır. HİÇBİR ŞEY GİZLENMEDİ — aynı metin, daha geniş yerde. -->
          <p v-if="item.note" class="component-note">{{ item.note }}</p>
        </article>
      </div>

      <!-- Katalog gelmeden "bileşen yok" yazıyordu: `catalog` null iken türetilen liste boş
           kalıyor, ekran KESİN bir cümle kuruyordu. Artık liste ancak BAŞARIYLA okunduktan
           sonra "yok" der (bkz. stores/software.ts `loaded`). -->
      <p v-else-if="!softwareStore.loaded" class="empty-state" role="status" aria-live="polite">
        {{ t("panel.common.loading") }}
      </p>

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

      <!-- Aynı yalan kurulu bileşen şeridinde de vardı. -->
      <p v-else-if="!softwareStore.loaded" class="empty-state" role="status" aria-live="polite">
        {{ t("panel.common.loading") }}
      </p>

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
 * "KURULUM DİZİNİ" SÜTUNU AYNI TABLODA İKİ DİL KONUŞUYORDU.
 *
 * ÖLÇÜLDÜ: kurulu bileşenler sunucudan ÇÖZÜLMÜŞ yolla geliyor (`C:\ProgramData\AdaPanel\...`),
 * kurulmamışlar ise manifestteki HAM yolla (`%ProgramData%\AdaPanel\...`). Aynı sütunda alt alta
 * iki farklı biçim duruyor ve kullanıcı bunları iki AYRI konum sanıyordu.
 *
 * Değişkenin karşılığı TAHMİN EDİLMİYOR, ÖLÇÜLÜYOR: aynı bileşenin hem ham hem çözülmüş yolu
 * elimizde olduğunda ikisinin ortak kuyruğu atılır, geriye değişkenin gerçek karşılığı kalır
 * (`%ProgramData%` -> `C:\ProgramData`). Tek bir çift bile bulunamazsa satır HAM hâliyle kalır;
 * "C:\" yazıp uydurmaktansa değişkeni göstermek doğrudur (bkz. `panel.software.pathHint`).
 */
const pathVariableMap = computed(() => {
  const map = new Map<string, string>();
  for (const row of manifestRows.value) {
    const gercek = row.installed?.installDir ?? "";
    const eslesme = /^(%[^%]+%)(.+)$/.exec(row.installDir ?? "");
    if (!eslesme || !gercek) {
      continue;
    }

    const [, degisken, kuyruk] = eslesme;
    if (!gercek.toLowerCase().endsWith(kuyruk.toLowerCase())) {
      continue;
    }

    const cozulmus = gercek.slice(0, gercek.length - kuyruk.length);
    if (cozulmus) {
      map.set(degisken.toLowerCase(), cozulmus);
    }
  }

  return map;
});

function displayInstallDir(item: ManifestRow): string {
  if (item.installed?.installDir) {
    return item.installed.installDir;
  }

  const ham = item.installDir ?? "";
  const eslesme = /^(%[^%]+%)(.*)$/.exec(ham);
  if (!eslesme) {
    return ham;
  }

  const cozulmus = pathVariableMap.value.get(eslesme[1].toLowerCase());
  return cozulmus ? `${cozulmus}${eslesme[2]}` : ham;
}

/** Bileşen türü kullanıcının dilinde; tanınmayan tür HAM hâliyle kalır (bilgi silinmez). */
function componentTypeLabel(type: string): string {
  const key = `panel.software.kind.${(type ?? "").toLowerCase()}`;
  const translated = t(key);
  return translated === key ? (type || "-") : translated;
}

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
const RESTARTS_ON_UPDATE = ["mysql-8", "mariadb-11", "postgresql-16", "stalwart-mail", "redis", "memcached", "docker"];

async function updateComponent(item: { id: string; installed?: { version?: string; availableVersion?: string } | null; displayName?: string }) {
  const risky = RESTARTS_ON_UPDATE.includes(item.id.toLowerCase());
  if (risky) {
    await confirm({
      title: t("panel.software.updateConfirmTitle"),
      message: t("panel.software.updateConfirmMessage"),
      confirmLabel: t("panel.software.update"),
      busyLabel: t("panel.software.installing"),
      tone: "neutral",
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
  gap: var(--sp-12);
}

/* `.section-head` BU İKİ LİSTEDEN ÇIKARILDI: ortak tanım base.css'te ve aynı üç bildirimi
   (flex / space-between / flex-start) `--sp-12` boşlukla zaten veriyor. Kopyanın tek EKSİĞİ
   `flex-wrap: wrap` idi; ortak tanıma geçince onu da kazanır, yani dar panelde uzun bir başlık
   yanındaki düğmeyi artık ezmez. */
.software-header,
.software-actions,
.software-toolbar,
.row-actions {
  display: flex;
  gap: var(--sp-12);
}

.software-header,
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
  gap: var(--sp-8);
  padding: var(--sp-4);
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-md);
  background: var(--input-bg);
}

.workspace-tab {
  display: flex;
  gap: var(--sp-10);
  align-items: center;
  min-height: 48px;
  padding: var(--sp-6) var(--sp-10);
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
  gap: var(--sp-2);
}

.workspace-tab strong {
  color: var(--text-strong);
  font-size: var(--fs-sm);
}

.workspace-tab small {
  color: var(--text-muted);
  font-size: var(--fs-2xs);
}

.workspace-mark {
  display: grid;
  flex: 0 0 34px;
  width: 34px;
  height: 34px;
  place-items: center;
  border-radius: var(--radius-md);
  /* Dolgu üzerine yazılan metin: `--accent-1` değil onun ölçülmüş metin karşılığı. */
  color: var(--accent-text);
  font-size: var(--fs-2xs);
  font-weight: 900;
  background: color-mix(in srgb, var(--accent-1) 11%, transparent);
}

.software-toolbar,
.list-card,
.installed-strip {
  padding: var(--sp-12);
  border-radius: var(--radius-md);
  border: 1px solid var(--panel-border);
  background: var(--surface-0);
}

.category-tabs {
  display: flex;
  flex-wrap: wrap;
  gap: var(--sp-4);
}

/*
  KATEGORİ SEKMELERİ ARTIK DÜĞME GİBİ DAVRANMIYOR.
  İki ölçülmüş sorun vardı. (1) Seçili sekme `linear-gradient(135deg, --accent-1, --accent-2)`
  ile boyanıyor ve üzerine `--on-accent` (beyaz) yazılıyordu; base.css'in ölçümüne göre beyaz
  metin `--accent-1` üzerinde 2.80:1 — yani gradyanın açık ucunda WCAG AA'nın (4.5) altında.
  Panelin birincil düğmesi tam bu yüzden `--accent-fill-*` belirteçlerine taşınmıştı; burada
  eski, ölçülmemiş gradyan hâlâ yaşıyordu. (2) Görsel HİYERARŞİ yanlıştı: bunlar bir SÜZGEÇ,
  bir eylem değil. Dolu vurgulu bir kutu olarak çizilince ekrandaki en güçlü çağrı hâline
  geliyor ve başlıktaki gerçek birincil düğme ("Varsayılanları kur") ile yarışıyordu.
  Panelin kendi seçili-sekme dili kullanıldı (base.css `.segmented-tabs button.active`):
  %14 vurgu tonu + `--accent-text` yazı + altta 2px vurgu çizgisi. ÖLÇÜLDÜ: koyu temada
  2.80 -> 7.36:1, açık temada 4.51 -> 4.91:1. Düğme gölgesi ve hover'daki 1px zıplama da
  kapatıldı — bir süzgeç zıplamaz.
*/
.category-tab {
  display: inline-flex;
  width: auto;
  align-items: center;
  gap: var(--sp-8);
  background: var(--surface-2);
  color: var(--text-soft);
  font-weight: 550;
  box-shadow: none;
}

.category-tab:not(:disabled):hover {
  background: var(--surface-3);
  transform: none;
  filter: none;
  box-shadow: none;
}

.category-tab.active {
  background: color-mix(in srgb, var(--accent-1) 14%, transparent);
  color: var(--accent-text);
  box-shadow: inset 0 -2px var(--accent-1);
}

/* Sayaç eskiden `opacity: 0.74` ile soldurulmuştu. Opaklık metni ZEMİNE doğru soldurur, yani
   kontrastı düşürür (base.css'te devre dışı düğme için aynı hata ölçülmüştü). Sayı zaten bir
   bilgi; rengiyle geri çekilir, saydamlıkla değil. */
.category-tab span {
  color: var(--text-muted);
  font-variant-numeric: tabular-nums;
}

.category-tab.active span {
  color: inherit;
}

.software-search {
  display: grid;
  min-width: min(100%, 340px);
  gap: var(--sp-6);
}

.list-card,
.installed-strip,
.installed-list,
.component-name {
  display: grid;
  gap: var(--sp-12);
}

.software-table {
  display: grid;
  gap: 0;
  overflow: hidden;
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-sm);
}

/* Satır ölçüleri base.css'teki `.data-row` ile EŞİTLENDİ (`--sp-10 --sp-12`): panelde iki
   farklı tablo yoğunluğu olmasın, bir ekrandan diğerine geçerken satırlar zıplamasın. */
/* `align-items: center` -> `start`: açıklama bandı satıra ikinci bir sıra eklediğinde
   ortalama, beş hücreyi bandın da dahil olduğu yüksekliğin ORTASINA kaydırırdı — düzeltilen
   tırtıklılığın ta kendisi. Üstte hizalanınca her satırın ilk sırası aynı taban çizgisinde
   başlar; açıklama uzunluğu artık komşu hücrelerin yerini değiştirmez. */
.software-row {
  display: grid;
  grid-template-columns: minmax(190px, 1.2fr) 0.55fr 0.7fr minmax(190px, 1.2fr) 0.7fr auto;
  gap: var(--sp-6) var(--sp-12);
  align-items: start;
  padding: var(--sp-10) var(--sp-12);
  border-bottom: 1px solid var(--panel-border);
  background: var(--surface-0);
}

/* Açıklama bandı: ızgaranın TAMAMI. `1 / -1` sütun sayısına bağlı değildir, dar ekranda tablo
   tek sütuna indiğinde de doğru çalışır. */
.component-note {
  grid-column: 1 / -1;
  margin: 0;
  max-width: 96ch;
  color: var(--text-muted);
  font-size: var(--fs-xs);
  line-height: 1.5;
}

.software-row:last-child {
  border-bottom: 0;
}

.software-row:not(.table-head):hover {
  background: color-mix(in srgb, var(--accent-1) 4%, var(--surface-0));
}

.table-head {
  color: var(--text-soft);
  font-size: var(--fs-xs);
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
  font-size: var(--fs-xs);
}

.component-name .detected-label,
.detection-source {
  color: var(--accent-text);
  font-size: var(--fs-2xs);
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
  gap: var(--sp-6);
}

/*
  VURGU DOLGUSU ÜZERİNDEKİ BEYAZ YAZI — ölçülmüş kontrast hatası, üç yerde.
  base.css'in ölçümü: beyaz metin `--accent-1` (#4b9cff) üzerinde 2.80:1, AA eşiği 4.5.
  Panelin birincil düğmesi bu yüzden `--accent-fill-top` (= `--accent-2`, 4.51:1) belirtecine
  taşınmıştı; ama bu dosyadaki üç öge — güncelleme rozeti, başlıktaki güncelleme sayacı ve
  "Güncelle" düğmesi — hâlâ düz `--accent-1` dolgusu kullanıyordu. Üçü de tam olarak
  "bir şey yapman gerekiyor" diyen ögeler, yani okunmasının en çok önem taşıdığı yerler.
  `.update-action` kuralı TÜMÜYLE SİLİNDİ (sınıf da şablondan kaldırıldı): düğme zaten
  `class="compact"` ile birincil; kural onun ölçülmüş gradyanını ölçülmemiş düz renkle
  eziyordu. Silmek, doğru davranışa geri dönmektir.
*/
.update-badge {
  padding: var(--sp-2) var(--sp-6);
  border-radius: 999px;
  font-size: var(--fs-2xs);
  font-weight: 600;
  color: var(--on-accent);
  background: var(--accent-fill-top);
  white-space: nowrap;
}

.unknown-badge {
  padding: var(--sp-2) var(--sp-6);
  border-radius: 999px;
  font-size: var(--fs-2xs);
  color: var(--text-muted);
  border: 1px dashed var(--panel-border);
  white-space: nowrap;
}

.update-count {
  display: inline-grid;
  place-items: center;
  min-width: 17px;
  height: 17px;
  margin-right: var(--sp-4);
  padding: 0 var(--sp-4);
  border-radius: 999px;
  font-size: var(--fs-2xs);
  font-weight: 700;
  color: var(--on-accent);
  background: var(--accent-fill-top);
}

/*
  DURUM ROZETİNİN KENDİ GÖRÜNÜMÜ SİLİNDİ — base.css'teki `.status-pill` KULLANILIYOR.
  Buradaki eski kural aynı sınıf adını panelin geri kalanından FARKLI çiziyordu: başka dolgu
  (`--sp-4 9px` yerine `--sp-2 --sp-10`), başka yazı boyu, kenarlık yok ve en önemlisi başka
  RENK — "Kurulu" burada MAVİ, panelin her yerinde YEŞİL. Sonuç: bu tabloda renk hiçbir şey
  ayırt etmiyordu; kurulu / kurulabilir / başarısız üç durumun ikisi mavi-gri tonlarındaydı ve
  göz listeyi tarayarak "neler kurulu" sorusunu cevaplayamıyordu.
  Kural kaldırılınca base.css devreye giriyor: `.ok` yeşil, `.danger` kırmızı, modifiye
  edilmemiş rozet (yani `.muted`) nötr gri. `.muted` için AYRI KURAL GEREKMİYOR — base'in
  sade `.status-pill` tanımı zaten tam olarak o nötr görünümdür; ikinci bir tanım yazmak aynı
  şeyi iki yerde tutmak olurdu.
*/

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
  gap: var(--sp-10);
  align-items: center;
  padding: var(--sp-10) var(--sp-12);
  border-bottom: 1px solid var(--panel-border);
  background: var(--surface-0);
}

.installed-chip:last-child {
  border-bottom: 0;
}

.installed-chip label {
  display: grid;
  grid-template-columns: auto minmax(120px, 1fr);
  gap: var(--sp-8);
  align-items: center;
}

.installed-chip input {
  min-height: 30px;
  padding-block: var(--sp-4);
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
  padding: var(--sp-10) var(--sp-12);
  cursor: pointer;
  font-weight: 650;
}

/* Yazı tipi listesi ELDE yazılıydı ve base.css'in `--font-mono` listesinden farklıydı
   ("Cascadia Mono" / "Liberation Mono" eksikti): aynı panelde iki farklı konsol yazı tipi
   çizilebiliyordu. Belirteç zaten tam bunun için tanımlanmış. */
.diagnostic-panel pre {
  max-height: 260px;
  margin: 0;
  padding: 0 var(--sp-12) var(--sp-12);
  overflow: auto;
  color: var(--text-soft);
  font: var(--fs-2xs)/1.5 var(--font-mono);
  white-space: pre-wrap;
  word-break: break-word;
}

.install-progress {
  display: flex;
  gap: var(--sp-10);
  align-items: flex-start;
  padding: var(--sp-10) var(--sp-12);
  border: 1px solid color-mix(in srgb, var(--accent-1) 24%, transparent);
  border-radius: var(--radius-sm);
  background: color-mix(in srgb, var(--accent-1) 7%, transparent);
}

.install-progress-text {
  display: grid;
  gap: var(--sp-4);
}

.live-steps {
  list-style: none;
  margin: var(--sp-4) 0 0;
  padding: 0;
  display: grid;
  gap: var(--sp-2);
  max-height: 200px;
  overflow-y: auto;
}

.live-step {
  display: flex;
  align-items: center;
  gap: var(--sp-6);
  font-size: var(--fs-sm);
  color: var(--text-muted);
}

.live-step.done {
  color: var(--accent-text);
}

.live-step.failed {
  color: var(--danger-text);
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
  gap: var(--sp-2);
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

/*
  DAR EKRANDA TABLO ARTIK BAŞLIKSIZ KALMIYOR.
  ÖNCESİ ÖLÇÜLDÜ: 1120px altında altı sütunlu satır tek sütuna iniyor VE başlık satırı
  `display: none` ile kaldırılıyordu. Geriye anonim satırlar kalıyordu —
  "8.3.6" / "runtime" / "C:\AdaPanel\php\8.3" — hangisinin sürüm, hangisinin tür, hangisinin
  klasör olduğunu söyleyen hiçbir şey yoktu. Bu, panelin başka yerinde bilinçle reddedilmiş bir
  çözüm (bkz. DashboardOverviewView'daki disk/ağ satırı yorumu): bilgi kaldırılmaz.
  ÇÖZÜM: başlık satırı yerine her hücre KENDİ etiketini taşır (`data-label`, şablonda başlık
  satırıyla AYNI çeviri anahtarından besleniyor). Böylece 900px'te tablo etiketli bir kart
  yığınına dönüşür; hiçbir sütun kaybolmaz. Durum rozetine etiket konmadı çünkü rozetin
  metni ("Kurulu" / "Başarısız") zaten kendi etiketi.
  `.path-cell`in kırpması da bu genişlikte kapatıldı: tek sütunda yer var, yolu üç nokta ile
  kesmenin gerekçesi kalmıyor.
*/
@media (max-width: 1120px) {
  .software-row {
    grid-template-columns: 1fr;
    gap: var(--sp-6);
  }

  .table-head {
    display: none;
  }

  .software-row > [data-label] {
    display: block;
  }

  .software-row > [data-label]::before {
    content: attr(data-label);
    display: block;
    color: var(--text-muted);
    font-size: var(--fs-2xs);
    font-weight: 600;
  }

  .path-cell {
    overflow: visible;
    white-space: normal;
    overflow-wrap: anywhere;
  }

  .installed-chip {
    grid-template-columns: 1fr;
    gap: var(--sp-6);
  }
}

@media (max-width: 820px) {
  /* `.section-head` buradan da çıkarıldı (kopyanın parçasıydı): ortak tanımın `flex-wrap`
     davranışı dar ekranda zaten alt satıra indirir. */
  .software-header,
  .software-actions,
  .software-toolbar {
    flex-direction: column;
    align-items: flex-start;
  }

  .workspace-tabs {
    grid-template-columns: 1fr;
  }
}
</style>
