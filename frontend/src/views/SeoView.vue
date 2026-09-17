<template>
  <section class="panel seo-view">
    <div class="section-head">
      <div>
        <h2>{{ t("panel.seo.title") }}</h2>
        <p class="meta">{{ t("panel.seo.subtitle") }}</p>
      </div>
      <!-- `seo-controls` KALDIRILDI: tanimi (`flex` + `wrap` + `align-items:center` + 8px)
           base.css'teki `.row-actions` ile BIREBIR ayniydi. Ayni seyin iki adi olmasi, yarin
           birinin degisip otekinin geride kalmasi demektir. -->
      <div class="row-actions">
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
        <!-- `seo-strip`: ortak `.summary-strip`in TEK olculmus farki. Bu ekranda serit HER
             ZAMAN alti kutu tasiyor (hata/uyari/bilgi/sayfa/sure/host ve gunlukte de alti);
             base.css'in dort sutunu 4 + 2 gibi tirtikli bir ikinci satir birakiyordu. Uc sutun
             alti kutuyu 3 + 3 diye tam bolerek dizer. Kutu GORUNUMU artik ortak tanimdan gelir. -->
        <div class="summary-strip seo-strip">
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
            <h4>robots.txt</h4>
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
            <h4>sitemap.xml</h4>
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

        <!-- BASLIK MERDIVENI DUZELTILDI (bu ekranin en buyuk gorsel sorunuydu).
             OLCULDU: sayfa `h2` ile basliyor, sonra DOGRUDAN `h5` ve `h6`ya atliyordu. base.css
             yalniz `h2/h3/h4` icin boy kurali yazar; `h5` hicbir kural almadigi icin TARAYICI
             VARSAYILANINA dusuyordu: 0.83em = 11.2px, yani "Bulgular" / "Gunluk analizi" gibi
             bolum basliklari GOVDE METNINDEN (13.5px) KUCUK ciziliyordu. `h6` icin yerel bir
             kural vardi (0.86rem) ama o da govdeden kucuk. Yani ekranin butun ara basliklari
             icerdikleri cumlelerden silikti; sayfa tek bir metin yiginidir gibi okunuyordu.
             `h5` -> `h3` (15.9px), `h6` -> `h4` (14.3px): merdiven artik h2 > h3 > h4 > govde.
             `compact-head` -> ortak `section-head` (tanimi birebir ayniydi, tek fark
             `align-items`; base'in `flex-start` gerekcesi orada yazili). -->
        <div class="section-head">
          <h3>{{ t("panel.seo.findings") }}</h3>
          <select v-model="severityFilter" class="compact-select">
            <option value="">{{ t("panel.seo.allSeverities") }}</option>
            <option value="error">{{ t("panel.seo.severity.error") }}</option>
            <option value="warn">{{ t("panel.seo.severity.warn") }}</option>
            <option value="info">{{ t("panel.seo.severity.info") }}</option>
          </select>
        </div>

        <p v-if="visibleFindings.length === 0" class="empty-state">{{ t("panel.seo.noFindings") }}</p>
        <!-- ONEM DERECESI ARTIK YALNIZ RENKLE ANLATILMIYOR.
             Oncesi olculdu: bu listede bir bulgunun "hata mi, uyari mi, bilgi mi" oldugunu
             soyleyen TEK isaret 9px'lik renkli noktaydi ve nokta `aria-hidden`di — yani renk
             koru kullanici ve ekran okuyucu icin bu bilgi HIC YOKTU. Ustelik ayni listedeki
             OBUR uc liste (hata veren adresler, israf, dogrulama) derecesini zaten METINLE
             soyluyordu; tutarsizlik da buradaydi.
             Nokta yerine rozet: rengi korur (tarama hizi icin) ve derecenin adini YAZAR.
             Yeni ceviri anahtari YOK — serit kutularinda kullanilan
             `panel.seo.severity.*` anahtarlari devralindi. -->
        <ul v-else class="seo-findings">
          <li v-for="(finding, index) in visibleFindings" :key="`${finding.key}-${finding.url}-${index}`" :class="['seo-finding', finding.severity]">
            <span :class="['status-pill', severityPill(finding.severity)]">{{ t(`panel.seo.severity.${finding.severity}`) }}</span>
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
          <!-- `data-table` KALDIRILDI, cerceve KAPSAYICIYA tasindi: `.data-table` bir DIV
               kalibidir (kenar + `overflow-x: auto` + koseleri kirpma). Bir `<table>` uzerinde
               `overflow` calismaz ve `border-radius` cokmus kenarlarla kirpmaz — yani bu ekranin
               uc tablosu cerceve almiyor, hucreleri de HIC bicimlenmiyordu (base.css'te
               `th`/`td` kurali yok): sayilar ve yollar dolgusuz, ayracsiz, birbirine yapisik
               ciziliyordu. Cerceve artik zaten kaydirma kapsayicisi olan `.table-scroll`ta. -->
          <div class="table-scroll">
            <table class="pages-table">
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

      <!-- ================= GÜNLÜK ANALİZİ =================
           Tarama sitenin SAYFALARINI ölçer; bu bölüm ARAMA MOTORUNUN DAVRANIŞINI ölçer.
           İkisi ayrı durur çünkü kaynakları da ayrıdır: biri HTTP istekleri, öteki IIS
           erişim günlükleri. Aynı karta koymak, "tarama yapılmadı" ile "günlük okunamadı"
           durumlarını birbirine karıştırırdı. -->
      <div class="section-head compact-head log-head">
        <div>
          <h5>{{ t("panel.seo.log.title") }}</h5>
          <p class="meta">{{ t("panel.seo.log.subtitle") }}</p>
        </div>
        <div class="seo-controls">
          <select v-model.number="logDays" class="compact-select" :disabled="logBusy">
            <option :value="7">{{ t("panel.seo.log.days", { count: 7 }) }}</option>
            <option :value="14">{{ t("panel.seo.log.days", { count: 14 }) }}</option>
            <option :value="30">{{ t("panel.seo.log.days", { count: 30 }) }}</option>
          </select>
          <label class="verify-toggle" :title="t('panel.seo.log.verifyHint')">
            <input v-model="verifyBots" type="checkbox" :disabled="logBusy" />
            {{ t("panel.seo.log.verify") }}
          </label>
          <button class="compact" v-bind="writeGate(logBusy)" @click="analyzeLogs">
            {{ logBusy ? t("panel.seo.log.running") : t("panel.seo.log.analyze") }}
          </button>
        </div>
      </div>

      <p v-if="logBusy" class="notice" role="status" aria-live="polite">{{ t("panel.seo.log.runningHint") }}</p>
      <p v-else-if="!logReport" class="empty-state">{{ t("panel.seo.log.neverRun") }}</p>

      <template v-if="logReport">
        <!-- ÖLÇÜM YOKSA SIFIRLARI GÖSTERME: "bot gelmemiş" ile "okuyacak dosya bulamadım"
             aynı ekran olamaz — birincisi SEO kararı doğurur, ikincisi yapılandırma sorunudur. -->
        <p v-if="logReport.error" class="notice error" role="alert">
          {{ t(`panel.seo.log.error.${logReport.error}`) }} <code>{{ logReport.logDirectory }}</code>
        </p>
        <template v-else>
          <p v-if="logReport.limitReached" class="notice warn-banner" role="status">
            {{ t("panel.seo.log.limitReached", { lines: logReport.linesScanned, skipped: logReport.filesSkipped }) }}
          </p>

          <div class="summary-strip">
            <div class="summary-item">
              <span>{{ t("panel.seo.log.botRequests") }}</span>
              <strong>{{ logReport.botRequests }}</strong>
            </div>
            <div class="summary-item">
              <span>{{ t("panel.seo.log.botShare") }}</span>
              <strong>{{ botShare }}</strong>
            </div>
            <div class="summary-item">
              <span>{{ t("panel.seo.log.wasted") }}</span>
              <strong :class="{ 'sev warn': logReport.wastedRequests > 0 }">{{ logReport.wastedRequests }}</strong>
            </div>
            <div class="summary-item">
              <span>{{ t("panel.seo.log.filesScanned") }}</span>
              <strong>{{ logReport.filesScanned }} <em v-if="logReport.filesCompressed">({{ t("panel.seo.log.compressed", { count: logReport.filesCompressed }) }})</em></strong>
            </div>
            <div class="summary-item">
              <span>{{ t("panel.seo.log.window") }}</span>
              <strong>{{ t("panel.seo.log.days", { count: logReport.periodDays }) }}</strong>
            </div>
            <div class="summary-item">
              <span>{{ t("panel.seo.log.generatedAt") }}</span>
              <strong>{{ formatUtc(logReport.generatedAtUtc) }}</strong>
            </div>
          </div>

          <p v-if="!logReport.hasData" class="empty-state">{{ t("panel.seo.log.noRequests") }}</p>

          <template v-else>
            <h6>{{ t("panel.seo.log.engines") }}</h6>
            <div class="table-scroll">
              <table class="data-table">
                <thead>
                  <tr>
                    <th>{{ t("panel.seo.log.table.engine") }}</th>
                    <th>{{ t("panel.seo.log.table.requests") }}</th>
                    <th>{{ t("panel.seo.log.table.pages") }}</th>
                    <th>{{ t("panel.seo.log.table.activeDays") }}</th>
                    <th>2xx</th>
                    <th>3xx</th>
                    <th>4xx</th>
                    <th>5xx</th>
                    <th>{{ t("panel.seo.log.table.lastSeen") }}</th>
                  </tr>
                </thead>
                <tbody>
                  <tr v-for="engine in logReport.engines" :key="engine.key">
                    <td>
                      {{ t(`panel.seo.log.engine.${engine.key}`) }}
                      <span class="status-pill">{{ t(`panel.seo.log.category.${engine.category}`) }}</span>
                    </td>
                    <td>{{ engine.requests }}</td>
                    <td>{{ engine.uniquePaths }}</td>
                    <td>{{ engine.activeDays }}</td>
                    <td>{{ engine.status2xx }}</td>
                    <td>{{ engine.status3xx }}</td>
                    <td :class="{ 'sev warn': engine.status4xx > 0 }">{{ engine.status4xx }}</td>
                    <td :class="{ 'sev error': engine.status5xx > 0 }">{{ engine.status5xx }}</td>
                    <td>{{ formatUtc(engine.lastSeenUtc) }}</td>
                  </tr>
                </tbody>
              </table>
            </div>

            <template v-if="logReport.errorPaths.length">
              <h6>{{ t("panel.seo.log.errorPaths") }}</h6>
              <p class="meta">{{ t("panel.seo.log.errorPathsHint") }}</p>
              <ul class="seo-findings">
                <li v-for="item in logReport.errorPaths" :key="`err-${item.path}`" class="seo-finding">
                  <span class="sev-dot" :class="item.lastStatus >= 500 ? 'error' : 'warn'" aria-hidden="true"></span>
                  <div class="finding-body">
                    <strong>{{ item.lastStatus }}</strong> <code>{{ item.path }}</code>
                    <p class="meta">
                      {{ t("panel.seo.log.hits", { count: item.hits }) }}
                      <span v-if="item.topEngine"> · {{ t(`panel.seo.log.engine.${item.topEngine}`) }}</span>
                    </p>
                  </div>
                </li>
              </ul>
            </template>

            <template v-if="logReport.waste.length">
              <h6>{{ t("panel.seo.log.wasteTitle") }}</h6>
              <p class="meta">{{ t("panel.seo.log.wasteHint") }}</p>
              <ul class="seo-findings">
                <li v-for="item in logReport.waste" :key="`waste-${item.reason}-${item.path}`" class="seo-finding">
                  <span class="sev-dot warn" aria-hidden="true"></span>
                  <div class="finding-body">
                    <strong>{{ t(`panel.seo.log.waste.${item.reason}`) }}</strong>
                    <p class="meta">
                      <code>{{ item.path }}</code>
                      · {{ t("panel.seo.log.hits", { count: item.hits }) }}
                      <span v-if="item.detail"> · <code>{{ item.detail }}</code></span>
                    </p>
                  </div>
                </li>
              </ul>
            </template>

            <template v-if="logReport.topBotPaths.length">
              <details class="seo-pages">
                <summary>{{ t("panel.seo.log.topPaths", { count: logReport.topBotPaths.length }) }}</summary>
                <div class="table-scroll">
                  <table class="data-table">
                    <thead>
                      <tr>
                        <th>{{ t("panel.seo.table.path") }}</th>
                        <th>{{ t("panel.seo.log.table.requests") }}</th>
                        <th>{{ t("panel.seo.table.status") }}</th>
                        <th>{{ t("panel.seo.log.table.lastSeen") }}</th>
                      </tr>
                    </thead>
                    <tbody>
                      <tr v-for="item in logReport.topBotPaths" :key="`top-${item.path}`">
                        <td><code>{{ item.path }}</code></td>
                        <td>{{ item.hits }}</td>
                        <td>{{ item.lastStatus }}</td>
                        <td>{{ formatUtc(item.lastSeenUtc) }}</td>
                      </tr>
                    </tbody>
                  </table>
                </div>
              </details>
            </template>

            <!-- KAYNAK OLMADAN LİSTE GÖSTERİLMEZ: boş bir "gezilmemiş sayfa" listesi,
                 karşılaştırılacak bir sayfa listesi YOKKEN "her sayfa geziliyor" gibi okunurdu. -->
            <template v-if="logReport.uncrawledSourceReportId">
              <h6>{{ t("panel.seo.log.uncrawled") }}</h6>
              <p class="meta">
                {{ t("panel.seo.log.uncrawledHint", { known: logReport.knownPageCount, missing: logReport.uncrawledPaths.length }) }}
              </p>
              <p v-if="logReport.uncrawledPaths.length === 0" class="empty-state">{{ t("panel.seo.log.allCrawled") }}</p>
              <ul v-else class="seo-findings">
                <li v-for="path in logReport.uncrawledPaths" :key="`unc-${path}`" class="seo-finding">
                  <span class="sev-dot info" aria-hidden="true"></span>
                  <div class="finding-body"><code>{{ path }}</code></div>
                </li>
              </ul>
            </template>
            <p v-else class="meta">{{ t("panel.seo.log.uncrawledNeedsScan") }}</p>

            <h6>{{ t("panel.seo.log.verification") }}</h6>
            <!-- DOĞRULANMAYAN, "SAHTE" DEĞİLDİR. Ters DNS yapılmadıysa ya da cevap vermediyse
                 panel hüküm VERMEZ; aksi hâlde kullanıcı gerçek Googlebot'u engellerdi. -->
            <p v-if="!logReport.verification.performed" class="notice" role="status">
              {{ t("panel.seo.log.verifyNotRun", { count: logReport.verification.candidateIps }) }}
            </p>
            <template v-else>
              <p class="meta">
                {{ t("panel.seo.log.verifySummary", {
                  checked: logReport.verification.checkedIps,
                  verified: logReport.verification.verifiedIps,
                  spoofed: logReport.verification.spoofedIps,
                  unknown: logReport.verification.undeterminedIps
                }) }}
              </p>
              <ul v-if="logReport.verification.identities.length" class="seo-findings">
                <li
                  v-for="item in logReport.verification.identities"
                  :key="`id-${item.ip}-${item.engineKey}`"
                  class="seo-finding">
                  <span class="sev-dot" :class="item.verdict === 'spoofed' ? 'error' : item.verdict === 'verified' ? 'info' : ''" aria-hidden="true"></span>
                  <div class="finding-body">
                    <strong>{{ t(`panel.seo.log.verdict.${item.verdict}`) }}</strong>
                    <p class="meta">
                      <code>{{ item.ip }}</code>
                      · {{ t(`panel.seo.log.engine.${item.engineKey}`) }}
                      · {{ t("panel.seo.log.hits", { count: item.requests }) }}
                      <span v-if="item.reverseDns"> · <code>{{ item.reverseDns }}</code></span>
                    </p>
                  </div>
                </li>
              </ul>
            </template>
          </template>
        </template>
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
  analyzeSeoLogs,
  cancelSeoScan,
  getSeoLogReport,
  getSeoOverview,
  getSeoReport,
  startSeoScan,
  type SeoLogReport,
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
 * GÜNLÜK ANALİZİ durumu. Taramadan AYRI tutulur: tarama arka planda koşup yoklanır, günlük
 * analizi ise sunucuda sınırlı (dosya/satır/süre) ve tek istekte biten bir iştir. Aynı `busy`
 * bayrağını paylaşsalardı biri sürerken öteki sebepsiz kapanırdı.
 */
const logReport = ref<SeoLogReport | null>(null);
const logDays = ref(7);
const verifyBots = ref(false);
const logBusy = ref(false);

/** Bot oranı: payda 0 iken bölme YAPILMAZ — "NaN%" ya da "Infinity%" ekrana düşerdi. */
const botShare = computed(() => {
  const total = logReport.value?.totalRequests ?? 0;
  if (!total) {
    return "-";
  }

  return `${Math.round(((logReport.value?.botRequests ?? 0) / total) * 100)}%`;
});

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

let contextVersion = 0;
let active = true;
let overviewVersion = 0;
let logVersion = 0;
let reportVersion = 0;
onBeforeUnmount(() => { active = false; contextVersion++; stopPolling(); });

watch(siteName, async () => {
  contextVersion++;
  stopPolling();
  notice.value = "";
  overview.value = null;
  shownReport.value = null;
  // Site değişince ESKİ sitenin günlük raporu ekranda KALMAMALI: başka bir sitenin bot
  // sayılarını yeni seçilen sitenin sayıları sanmak, panelin en sinsi yanlış bilgisi olurdu.
  logReport.value = null;
  await load();
});

async function load() {
  if (!active) return;
  const context = contextVersion;
  const site = siteName.value;
  const request = ++overviewVersion;
  const logRequest = ++logVersion;
  if (!siteName.value) {
    return;
  }

  try {
    const result = await getSeoOverview(site);
    if (context !== contextVersion || request !== overviewVersion) return;
    overview.value = result;
    syncPolling();
  } catch (error) {
    if (context !== contextVersion || request !== overviewVersion) return;
    show(extractApiMessage(error), true);
  }

  // Saklanan günlük raporu AYRI uçtan gelir ve hatası taramayı bozmamalı; bu yüzden ayrı
  // try/catch. Yoksa günlük klasörüne erişilemeyen bir sitede tarama sonuçları da kaybolurdu.
  try {
    const result = await getSeoLogReport(site);
    if (context === contextVersion && logRequest === logVersion) logReport.value = result;
  } catch (error) {
    if (context !== contextVersion || logRequest !== logVersion) return;
    show(extractApiMessage(error), true);
  }
}

async function analyzeLogs() {
  if (!siteName.value || logBusy.value) {
    return;
  }

  logBusy.value = true;
  const context = contextVersion;
  const request = ++logVersion;
  try {
    const result = await analyzeSeoLogs(siteName.value, logDays.value, verifyBots.value);
    if (context !== contextVersion || request !== logVersion) return;
    logReport.value = result;
    show(t("panel.seo.log.completed"), false);
  } catch (error) {
    if (context !== contextVersion || request !== logVersion) return;
    show(extractApiMessage(error), true);
  } finally {
    logBusy.value = false;
  }
}

function syncPolling() {
  if (running.value) {
    startPolling();
  } else {
    stopPolling();
    if (!noticeError.value && notice.value === t("panel.seo.scanQueued")) notice.value = "";
  }
}

function startPolling() {
  if (poller !== undefined) {
    return;
  }

  let polling = false;
  poller = setInterval(async () => {
    if (!siteName.value || polling) {
      return;
    }

    polling = true;
    const context = contextVersion;
    const request = ++overviewVersion;
    try {
      const result = await getSeoOverview(siteName.value);
      if (context !== contextVersion || request !== overviewVersion) return;
      overview.value = result;
      if (!running.value) {
        stopPolling();
        // Tarama bitti: geçmişten açılmış rapor varsa bırakılır, yoksa yeni rapor görünür.
        shownReport.value = null;
        syncPolling();
      }
    } catch {
      // Tek yoklamanın hatası ekranı bozmaz; bir sonraki turda yeniden denenir.
    } finally { polling = false; }
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
  const context = contextVersion;
  try {
    shownReport.value = null;
    await startSeoScan(siteName.value, maxPages.value);
    if (context !== contextVersion) return;
    await load();
    if (context !== contextVersion) return;
    if (running.value) show(t("panel.seo.scanQueued"), false);
  } catch (error) {
    if (context !== contextVersion) return;
    show(extractApiMessage(error), true);
  } finally {
    busy.value = false;
  }
}

async function cancel() {
  if (busy.value) return;
  busy.value = true;
  const context = contextVersion;
  try {
    await cancelSeoScan(siteName.value);
    if (context !== contextVersion) return;
    await load();
  } catch (error) {
    if (context !== contextVersion) return;
    show(extractApiMessage(error), true);
  } finally {
    busy.value = false;
  }
}

async function showReport(reportId: string) {
  if (reportId === shownReportId.value) {
    return;
  }

  const context = contextVersion;
  const request = ++reportVersion;
  try {
    const result = await getSeoReport(siteName.value, reportId);
    if (context === contextVersion && request === reportVersion) shownReport.value = result;
  } catch (error) {
    if (context !== contextVersion || request !== reportVersion) return;
    show(extractApiMessage(error), true);
  }
}

function show(message: string, isError: boolean) {
  notice.value = message;
  noticeError.value = isError;
}

/**
 * Bulgu ağırlığını panelin ORTAK rozet diline çevirir.
 *
 * NEDEN eşleme gerekiyor: SEO motoru `error` / `warn` / `info` diyor, panelin rozet sınıfları ise
 * `danger` / `warn` / (nötr). İkisini eşitlemeden `status-pill error` yazmak, base.css'te böyle
 * bir sınıf OLMADIĞI için rozeti sessizce renksiz bırakırdı — bu turda tam olarak bu hata sınıfı
 * (tanımsız sınıf sessizce yanlış çizim) panelin birkaç yerinde bulundu.
 *
 * `info` bilerek BOŞ döner: bilgi düzeyindeki bulgu nötr rozetle gösterilir; ona da renk vermek,
 * gerçek uyarıların yanında üçüncü bir renk daha üretip önem sırasını okunmaz yapardı.
 */
function severityPill(severity: string): string {
  if (severity === "error") {
    return "danger";
  }

  return severity === "warn" ? "warn" : "";
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
  /* 0.72rem = 9.72px idi — olcek tabani `--fs-2xs` (10.53px); altina inen kural yok. */
  font-size: var(--fs-2xs);
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
  background: var(--danger-1);
}

.sev-dot.warn {
  background: var(--warn-1);
}

.sev-dot.info {
  background: var(--accent-1);
}

.sev.error {
  color: var(--danger-text);
}

.sev.warn {
  color: var(--warn-text);
}

.sev.info {
  color: var(--accent-1);
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
  border-color: var(--accent-1);
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

h6 {
  margin: 12px 0 4px;
  font-size: 0.86rem;
}

.log-head {
  align-items: flex-start;
  border-top: 1px solid var(--panel-border);
  padding-top: 16px;
}

.log-head .meta {
  margin: 2px 0 0;
}

.verify-toggle {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  font-size: 0.8rem;
  color: var(--text-muted);
}

.summary-item em {
  font-style: normal;
  color: var(--text-muted);
  /* 0.75rem = 10.12px idi — olcek tabani `--fs-2xs` (10.53px); altina inen kural yok. */
  font-size: var(--fs-2xs);
}
</style>
