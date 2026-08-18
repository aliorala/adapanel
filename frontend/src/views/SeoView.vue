<template>
  <section class="panel wide seo-view">
    <div class="section-head">
      <div>
        <h2>{{ t("panel.seo.title") }}</h2>
        <p class="meta">{{ t("panel.seo.subtitle") }}</p>
      </div>
      <div class="seo-controls">
        <select v-model="siteName" class="compact-select" :disabled="busy">
          <option v-for="site in sitesStore.sites" :key="site.name" :value="site.name">{{ site.name }}</option>
        </select>
        <select v-model.number="maxPages" class="compact-select" :disabled="busy">
          <option :value="25">25 {{ t("panel.seo.pagesUnit") }}</option>
          <option :value="50">50 {{ t("panel.seo.pagesUnit") }}</option>
          <option :value="100">100 {{ t("panel.seo.pagesUnit") }}</option>
          <option :value="200">200 {{ t("panel.seo.pagesUnit") }}</option>
        </select>
        <button
          v-if="running"
          class="secondary compact"
          v-bind="writeGate(false)"
          @click="cancel">
          {{ t("panel.seo.cancel") }}
        </button>
        <button
          v-else
          class="compact"
          v-bind="writeGate(!canScan, canScan ? undefined : t('panel.seo.cannotScanHint'))"
          @click="scan">
          {{ overview?.latest ? t("panel.seo.rescan") : t("panel.seo.scan") }}
        </button>
      </div>
    </div>

    <p v-if="notice" :class="['notice', 'page-notice', noticeError ? 'error' : '']" role="alert">{{ notice }}</p>

    <p v-if="!siteName" class="empty-state">{{ t("panel.seo.selectSiteHint") }}</p>
    <p v-else-if="!overview" class="empty-state" role="status" aria-live="polite">{{ t("panel.common.loading") }}</p>

    <template v-else>
      <!-- Alan adı bağlaması olmayan site TARANAMAZ: Host başlığı olmadan IIS hangi siteyi
           döneceğini bilemez ve ölçüm başka bir sitenin sayfasını ölçebilirdi. -->
      <p v-if="!overview.host" class="notice warn-banner" role="status">{{ t("panel.seo.cannotScanHint") }}</p>

      <!-- SÜREN tarama: kullanıcı "başladı mı, nerede" sorusunu ekrandan cevaplayabilmeli. -->
      <p v-if="running" class="notice" role="status" aria-live="polite">
        {{ state.state === "queued" ? t("panel.seo.queued", { count: state.queueLength }) : t("panel.seo.running", { done: state.pagesCrawled, total: state.pageLimit }) }}
      </p>

      <p v-if="!overview.latest && !running" class="empty-state">{{ t("panel.seo.neverScanned") }}</p>

      <template v-if="report">
        <div class="summary-strip">
          <div class="summary-item">
            <span>{{ t("panel.seo.severity.error") }}</span>
            <strong class="sev error">{{ count("error") }}</strong>
          </div>
          <div class="summary-item">
            <span>{{ t("panel.seo.severity.warn") }}</span>
            <strong class="sev warn">{{ count("warn") }}</strong>
          </div>
          <div class="summary-item">
            <span>{{ t("panel.seo.severity.info") }}</span>
            <strong class="sev info">{{ count("info") }}</strong>
          </div>
          <div class="summary-item">
            <span>{{ t("panel.seo.pagesCrawled") }}</span>
            <strong>{{ report.pagesCrawled }} / {{ report.pageLimit }}</strong>
          </div>
          <div class="summary-item">
            <span>{{ t("panel.seo.duration") }}</span>
            <strong>{{ (report.durationMs / 1000).toFixed(1) }} sn</strong>
          </div>
          <div class="summary-item">
            <span>{{ t("panel.seo.scannedHost") }}</span>
            <strong :title="report.host">{{ report.host || "-" }}</strong>
          </div>
        </div>

        <p class="meta scan-meta">
          {{ t("panel.seo.completedAt") }}: {{ formatUtc(report.completedAtUtc) }}
          <span v-if="report.comparedToId">
            · {{ t("panel.seo.newFindings", { count: report.newFindingCount }) }}
            · {{ t("panel.seo.resolvedFindings", { count: report.resolvedFindingCount }) }}
          </span>
        </p>

        <!-- Kırpılmış tarama "temiz" gibi görünmemeli. -->
        <p v-if="report.limitReached" class="notice warn-banner" role="status">
          {{ t("panel.seo.limitReachedHint", { count: report.pagesCrawled }) }}
        </p>
        <p v-if="report.status === 'failed'" class="notice error" role="alert">
          {{ t("panel.seo.scanFailed") }} <code>{{ report.error }}</code>
        </p>
        <p v-else-if="report.status === 'cancelled'" class="notice warn-banner" role="status">
          {{ t("panel.seo.scanCancelled") }}
        </p>

        <div class="seo-columns">
          <div class="form-surface">
            <h5>robots.txt</h5>
            <p class="meta">
              <span :class="['status-pill', report.robots.found ? (report.robots.disallowsAll ? 'error' : 'ok') : 'warn']">
                {{ report.robots.found ? (report.robots.disallowsAll ? t("panel.seo.robotsBlocking") : t("panel.seo.present")) : t("panel.seo.missing") }}
              </span>
            </p>
            <p v-if="report.robots.sitemapUrls.length" class="meta">
              {{ t("panel.seo.declaredSitemaps") }}: <code>{{ report.robots.sitemapUrls.join(", ") }}</code>
            </p>
          </div>
          <div class="form-surface">
            <h5>sitemap.xml</h5>
            <p class="meta">
              <span :class="['status-pill', report.sitemap.found ? 'ok' : 'warn']">
                {{ report.sitemap.found ? t("panel.seo.present") : t("panel.seo.missing") }}
              </span>
              <code v-if="report.sitemap.path"> {{ report.sitemap.path }}</code>
            </p>
            <p v-if="report.sitemap.found" class="meta">
              {{ t("panel.seo.sitemapUrls", { count: report.sitemap.urlCount, checked: report.sitemap.checkedCount }) }}
            </p>
            <p v-if="report.sitemap.externalUrls.length" class="meta">
              {{ t("panel.seo.sitemapExternalHint", { count: report.sitemap.externalUrls.length }) }}
            </p>
          </div>
        </div>

        <div class="section-head compact-head">
          <h5>{{ t("panel.seo.findings") }}</h5>
          <select v-model="severityFilter" class="compact-select">
            <option value="">{{ t("panel.seo.allSeverities") }}</option>
            <option value="error">{{ t("panel.seo.severity.error") }}</option>
            <option value="warn">{{ t("panel.seo.severity.warn") }}</option>
            <option value="info">{{ t("panel.seo.severity.info") }}</option>
          </select>
        </div>

        <p v-if="visibleFindings.length === 0" class="empty-state">{{ t("panel.seo.noFindings") }}</p>
        <ul v-else class="seo-findings">
          <li v-for="(finding, index) in visibleFindings" :key="`${finding.key}-${finding.url}-${index}`" :class="['seo-finding', finding.severity]">
            <span class="sev-dot" :class="finding.severity" aria-hidden="true"></span>
            <div class="finding-body">
              <strong>{{ t(`panel.seo.finding.${finding.key}`) }}</strong>
              <p class="meta">
                <code v-if="finding.url">{{ finding.url }}</code>
                <span v-if="finding.detail"> {{ finding.detail }}</span>
              </p>
            </div>
          </li>
        </ul>

        <details v-if="report.pages.length" class="seo-pages">
          <summary>{{ t("panel.seo.crawledPages", { count: report.pages.length }) }}</summary>
          <div class="table-scroll">
            <table class="data-table">
              <thead>
                <tr>
                  <th>{{ t("panel.seo.table.path") }}</th>
                  <th>{{ t("panel.seo.table.status") }}</th>
                  <th>{{ t("panel.seo.table.time") }}</th>
                  <th>{{ t("panel.seo.table.title") }}</th>
                  <th>H1</th>
                  <th>{{ t("panel.seo.table.images") }}</th>
                </tr>
              </thead>
              <tbody>
                <tr v-for="page in report.pages" :key="page.path">
                  <td><code>{{ page.path }}</code></td>
                  <td>{{ page.error ? "-" : page.statusCode }}</td>
                  <td>{{ page.responseMs }} ms</td>
                  <td :title="page.title">{{ page.title || "-" }}</td>
                  <td>{{ page.h1Count }}</td>
                  <td>{{ page.imagesWithoutAlt }} / {{ page.imageCount }}</td>
                </tr>
              </tbody>
            </table>
          </div>
        </details>
      </template>

      <template v-if="overview.history.length > 1">
        <h5>{{ t("panel.seo.history") }}</h5>
        <ul class="seo-history">
          <li v-for="item in overview.history" :key="item.id">
            <button
              :class="['history-row', { active: item.id === shownReportId }]"
              @click="showReport(item.id)">
              <span>{{ formatUtc(item.completedAtUtc) }}</span>
              <span class="meta">{{ t("panel.seo.pagesCrawled") }}: {{ item.pagesCrawled }}</span>
              <span class="sev error">{{ item.errorCount }}</span>
              <span class="sev warn">{{ item.warningCount }}</span>
              <span class="sev info">{{ item.infoCount }}</span>
              <span v-if="item.newFindingCount || item.resolvedFindingCount" class="meta">
                +{{ item.newFindingCount }} / -{{ item.resolvedFindingCount }}
              </span>
            </button>
          </li>
        </ul>
      </template>
    </template>
  </section>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from "vue";
import { useI18n } from "vue-i18n";

import {
  cancelSeoScan,
  getSeoOverview,
  getSeoReport,
  startSeoScan,
  type SeoOverview,
  type SeoScanReport,
  type SeoSeverity
} from "../api/seo";
import { useSitesStore } from "../stores/sites";
import { extractApiMessage } from "../stores/auth";
import { useSubscriptionGate } from "../composables/useSubscriptionGate";

const { t } = useI18n();
const sitesStore = useSitesStore();
const { writeGate } = useSubscriptionGate();

const siteName = ref("");
const maxPages = ref(50);
const overview = ref<SeoOverview | null>(null);
/** Geçmişten açılan rapor; null ise en son rapor gösterilir. */
const shownReport = ref<SeoScanReport | null>(null);
const severityFilter = ref<"" | SeoSeverity>("");
const busy = ref(false);
const notice = ref("");
const noticeError = ref(false);

/**
 * Tarama arka planda koşar; ekran durumu YOKLAYARAK izler.
 * Yoklama yalnız iş sürerken çalışır — boşta sunucuya bedava istek atmak yok.
 */
let poller: ReturnType<typeof setInterval> | undefined;

const state = computed(() => overview.value?.state ?? { state: "idle", pagesCrawled: 0, pageLimit: 0, queueLength: 0 });
const running = computed(() => state.value.state !== "idle");
const canScan = computed(() => Boolean(overview.value?.host) && !running.value && !busy.value);
const report = computed(() => shownReport.value ?? overview.value?.latest ?? null);
const shownReportId = computed(() => report.value?.id ?? "");

const visibleFindings = computed(() => {
  const findings = report.value?.findings ?? [];
  return severityFilter.value ? findings.filter((finding) => finding.severity === severityFilter.value) : findings;
});

function count(severity: SeoSeverity): number {
  return (report.value?.findings ?? []).filter((finding) => finding.severity === severity).length;
}

onMounted(async () => {
  if (sitesStore.sites.length === 0) {
    try {
      await sitesStore.fetchSites();
    } catch (error) {
      show(extractApiMessage(error), true);
    }
  }

  // Değer DEĞİŞİRSE aşağıdaki izleyici yükler; değişmiyorsa (site yoksa) burada bir kez denenir.
  // İkisini birden yapmak, her açılışta aynı isteği iki kez göndermek olurdu.
  const secilecek = sitesStore.selectedSiteName || sitesStore.sites[0]?.name || "";
  if (secilecek === siteName.value) {
    await load();
  } else {
    siteName.value = secilecek;
  }
});

onBeforeUnmount(stopPolling);

watch(siteName, async () => {
  overview.value = null;
  shownReport.value = null;
  await load();
});

async function load() {
  if (!siteName.value) {
    return;
  }

  try {
    overview.value = await getSeoOverview(siteName.value);
    syncPolling();
  } catch (error) {
    show(extractApiMessage(error), true);
  }
}

function syncPolling() {
  if (running.value) {
    startPolling();
  } else {
    stopPolling();
  }
}

function startPolling() {
  if (poller !== undefined) {
    return;
  }

  poller = setInterval(async () => {
    if (!siteName.value) {
      return;
    }

    try {
      overview.value = await getSeoOverview(siteName.value);
      if (!running.value) {
        stopPolling();
        // Tarama bitti: geçmişten açılmış rapor varsa bırakılır, yoksa yeni rapor görünür.
        shownReport.value = null;
      }
    } catch {
      // Tek yoklamanın hatası ekranı bozmaz; bir sonraki turda yeniden denenir.
    }
  }, 3000);
}

function stopPolling() {
  if (poller !== undefined) {
    clearInterval(poller);
    poller = undefined;
  }
}

async function scan() {
  if (!canScan.value) {
    return;
  }

  busy.value = true;
  try {
    shownReport.value = null;
    await startSeoScan(siteName.value, maxPages.value);
    await load();
    show(t("panel.seo.scanQueued"), false);
  } catch (error) {
    show(extractApiMessage(error), true);
  } finally {
    busy.value = false;
  }
}

async function cancel() {
  busy.value = true;
  try {
    await cancelSeoScan(siteName.value);
    await load();
  } catch (error) {
    show(extractApiMessage(error), true);
  } finally {
    busy.value = false;
  }
}

async function showReport(reportId: string) {
  if (reportId === shownReportId.value) {
    return;
  }

  try {
    shownReport.value = await getSeoReport(siteName.value, reportId);
  } catch (error) {
    show(extractApiMessage(error), true);
  }
}

function show(message: string, isError: boolean) {
  notice.value = message;
  noticeError.value = isError;
}

function formatUtc(value: string): string {
  if (!value) {
    return "-";
  }

  const parsed = new Date(value.endsWith("Z") ? value : `${value}Z`);
  return Number.isNaN(parsed.getTime()) ? value : parsed.toLocaleString();
}
</script>

<style scoped>
.seo-view {
  display: grid;
  gap: 16px;
}

.seo-controls {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
}

.summary-strip {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(130px, 1fr));
  gap: 10px;
}

.summary-item {
  display: grid;
  gap: 4px;
  padding: 12px 14px;
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-lg);
  background: var(--surface-1);
}

.summary-item span {
  color: var(--text-muted);
  font-size: 0.72rem;
}

.summary-item strong {
  font-size: 1.05rem;
}

.seo-columns {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(260px, 1fr));
  gap: 12px;
}

.compact-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
}

.seo-findings,
.seo-history {
  list-style: none;
  margin: 0;
  padding: 0;
  display: grid;
  gap: 6px;
}

.seo-finding {
  display: flex;
  align-items: flex-start;
  gap: 10px;
  padding: 10px 12px;
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-lg);
  background: var(--surface-1);
}

.finding-body {
  min-width: 0;
}

.finding-body .meta {
  margin: 2px 0 0;
  word-break: break-word;
}

.sev-dot {
  flex: 0 0 auto;
  width: 9px;
  height: 9px;
  margin-top: 6px;
  border-radius: 999px;
  background: var(--text-muted);
}

.sev-dot.error {
  background: var(--danger, #e5484d);
}

.sev-dot.warn {
  background: var(--warning, #f5a524);
}

.sev-dot.info {
  background: var(--accent-1, #3b82f6);
}

.sev.error {
  color: var(--danger, #e5484d);
}

.sev.warn {
  color: var(--warning, #f5a524);
}

.sev.info {
  color: var(--accent-1, #3b82f6);
}

.history-row {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 12px;
  width: 100%;
  padding: 8px 12px;
  text-align: left;
  background: var(--surface-1);
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-lg);
  color: inherit;
}

.history-row.active {
  border-color: var(--accent-1, #3b82f6);
}

.table-scroll {
  overflow-x: auto;
}

.scan-meta {
  margin: 0;
}

h5 {
  margin: 0 0 4px;
}
</style>
