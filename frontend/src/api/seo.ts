import { apiClient } from "./client";

type ApiEnvelope<T> = { success: boolean; data: T; message: string; code: number };

/**
 * SEO ARAÇ KUTUSU — sitenin kendi sayfalarını gezip teknik SEO sorunlarını bulur.
 *
 * Sözleşme: src/AdaPanel.Core/Dtos/Seo/*.cs + src/AdaPanel.Api/Controllers/SeoController.cs
 * (/api/v1/seo/sites/{siteName} — Administrator/Reseller/Customer, kapsam EnsureSiteAsync).
 *
 * ADRES ALANI YOKTUR: taranacak konak, sitenin IIS bağlamalarından sunucuda türetilir. Buraya bir
 * "url" alanı eklemek paneli bir SSRF aracına çevirirdi (bkz. SeoLinkPolicy / SeoSsrfGuardTests).
 */

/** "error" sitenin görünürlüğünü engelleyen, "warn" iyileştirilmesi gereken, "info" bilgi. */
export type SeoSeverity = "error" | "warn" | "info";

export type SeoFinding = {
  /** Çeviri anahtarının son parçası: t(`panel.seo.finding.${key}`). */
  key: string;
  severity: SeoSeverity;
  /** Sorunun görüldüğü sayfa yolu; site geneli bulgularda boş. */
  url: string;
  /** Dile bağımsız ölçüm (HTTP kodu, süre, sayı). */
  detail: string;
};

export type SeoPage = {
  contentSignals?: SeoContentSignals | null;
  path: string;
  statusCode: number;
  responseMs: number;
  contentType: string;
  title: string;
  metaDescription: string;
  h1Count: number;
  imageCount: number;
  imagesWithoutAlt: number;
  redirectDepth: number;
  redirectTo: string;
  noIndex: boolean;
  linkedFrom: string;
  /** Doluysa sayfa HİÇ ölçülemedi (zaman aşımı, bağlantı hatası). */
  error: string;
};

export type SeoRobots = {
  botAccess?: SeoBotAccess[];
  found: boolean;
  statusCode: number;
  disallowsAll: boolean;
  sitemapUrls: string[];
  error: string;
};

export type SeoSitemap = {
  path: string;
  found: boolean;
  statusCode: number;
  urlCount: number;
  checkedCount: number;
  unreachableUrls: string[];
  /** Siteye ait olmayan adresler — DENENMEZ, yalnız listelenir. */
  externalUrls: string[];
  error: string;
};

export type SeoScanReport = {
  analysisVersion?: number;
  id: string;
  siteName: string;
  host: string;
  startedAtUtc: string;
  completedAtUtc: string;
  durationMs: number;
  status: "completed" | "failed" | "cancelled";
  pagesCrawled: number;
  pageLimit: number;
  /** true ise rapor sitenin TAMAMI değildir. */
  limitReached: boolean;
  findings: SeoFinding[];
  pages: SeoPage[];
  robots: SeoRobots;
  sitemap: SeoSitemap;
  error: string;
  newFindingCount: number;
  resolvedFindingCount: number;
  comparedToId: string;
};

export type SeoContentSignals = {
  canonical: string;
  canonicalCount: number;
  language: string;
  titleCount: number;
  sectionHeadingCount: number;
  textCharacters: number;
  jsonLdBlocks: number;
  invalidJsonLdBlocks: number;
  structuredDataLimited: boolean;
  otherStructuredData: boolean;
  schemaTypes: string[];
  noSnippet: boolean;
  bodyTruncated: boolean;
  robotDirectives: string[];
};

export type SeoBotAccess = {
  agent: string;
  purpose: "search" | "training";
  status: "allowed" | "restricted" | "blocked" | "unknown";
  matchedGroup: string;
};

export type SeoScanState = {
  siteName: string;
  state: "idle" | "queued" | "running";
  pagesCrawled: number;
  pageLimit: number;
  queueLength: number;
  startedAtUtc: string | null;
};

export type SeoScanSummary = {
  id: string;
  completedAtUtc: string;
  status: string;
  pagesCrawled: number;
  errorCount: number;
  warningCount: number;
  infoCount: number;
  newFindingCount: number;
  resolvedFindingCount: number;
};

export type SeoOverview = {
  siteName: string;
  /** Boşsa site TARANAMAZ (alan adı bağlaması yok) — düğme buna bakarak kapanır. */
  host: string;
  state: SeoScanState;
  latest: SeoScanReport | null;
  history: SeoScanSummary[];
};

export async function getSeoOverview(siteName: string) {
  const response = await apiClient.get<ApiEnvelope<SeoOverview>>(`/seo/sites/${encodeURIComponent(siteName)}`);
  return response.data.data;
}

/** Taramayı KUYRUĞA ALIR ve hemen döner; ilerleme yoklanarak izlenir. */
export async function startSeoScan(siteName: string, maxPages: number, startPath = "") {
  const response = await apiClient.post<ApiEnvelope<SeoScanState>>(
    `/seo/sites/${encodeURIComponent(siteName)}/scan`,
    { maxPages, startPath }
  );
  return response.data.data;
}

export async function cancelSeoScan(siteName: string) {
  const response = await apiClient.post<ApiEnvelope<SeoScanState>>(`/seo/sites/${encodeURIComponent(siteName)}/cancel`);
  return response.data.data;
}

export async function getSeoReport(siteName: string, reportId: string) {
  const response = await apiClient.get<ApiEnvelope<SeoScanReport>>(
    `/seo/sites/${encodeURIComponent(siteName)}/reports/${encodeURIComponent(reportId)}`
  );
  return response.data.data;
}

/* ------------------------------------------------------------------------- *
 * GÜNLÜK ANALİZİ — arama motoru botlarının siteyi GERÇEKTE nasıl gezdiği.
 *
 * Bu, ziyaretçi istatistiği DEĞİLDİR (o SiteStatisticsView'de). Buradaki tek soru:
 * bot ne sıklıkla geliyor, hangi sayfaları geziyor, ne cevap alıyor, emeğin ne kadarı
 * boşa gidiyor.
 *
 * BURAYA DA DOSYA YOLU ALANI EKLENMEZ: okunacak klasör site adından ve o sitenin IIS
 * kimliğinden sunucuda türetilir. Bir yol alanı, kiracıya BAŞKA kiracının erişim
 * günlüğünü (ziyaretçi IP'leri dahil) okutmak olurdu.
 * ------------------------------------------------------------------------- */

/** `search` klasik arama motoru, `ai` yapay zekâ tarayıcısı, `tool` üçüncü taraf SEO aracı. */
export type SeoBotCategory = "search" | "ai" | "tool";

export type SeoLogEngine = {
  /** Dile bağımsız motor anahtarı; t(`panel.seo.log.engine.${key}`). */
  key: string;
  category: SeoBotCategory;
  requests: number;
  uniquePaths: number;
  status2xx: number;
  status3xx: number;
  status4xx: number;
  status5xx: number;
  lastSeenUtc: string;
  activeDays: number;
};

export type SeoLogDay = { date: string; requests: number };

export type SeoLogPath = {
  path: string;
  hits: number;
  lastStatus: number;
  topEngine: string;
  lastSeenUtc: string;
};

/** `reason` t(`panel.seo.log.waste.${reason}`) ile çevrilir; `detail` HAM veridir. */
export type SeoLogWaste = {
  reason: "redirect" | "not_found" | "server_error" | "blocked" | "query_duplicate";
  path: string;
  hits: number;
  detail: string;
};

/**
 * `verdict` ASLA tahmin değildir:
 * - `verified`/`spoofed` yalnız ters DNS + ileri teyit ZİNCİRİ tamamlandığında verilir.
 * - `unresolved`/`unsupported`/`not_checked` "bilinmiyor" demektir — "sahte" DEĞİL.
 */
export type SeoLogBotIdentity = {
  ip: string;
  engineKey: string;
  requests: number;
  verdict: "verified" | "spoofed" | "unresolved" | "unsupported" | "not_checked";
  reverseDns: string;
};

export type SeoLogVerification = {
  /** false ise HİÇ ağ isteği yapılmadı; hiçbir IP hakkında hüküm yoktur. */
  performed: boolean;
  checkedIps: number;
  verifiedIps: number;
  spoofedIps: number;
  undeterminedIps: number;
  candidateIps: number;
  identities: SeoLogBotIdentity[];
};

export type SeoLogReport = {
  siteName: string;
  generatedAtUtc: string;
  durationMs: number;
  periodDays: number;
  logDirectory: string;
  /** Doluysa ölçüm YOKTUR (ör. `log_directory_missing`); sıfırları "bot gelmemiş" diye okuma. */
  error: string;
  hasData: boolean;
  filesScanned: number;
  filesCompressed: number;
  filesSkipped: number;
  linesScanned: number;
  /** true ise rapor pencerenin TAMAMI değildir. */
  limitReached: boolean;
  totalRequests: number;
  botRequests: number;
  engines: SeoLogEngine[];
  dailyBotRequests: SeoLogDay[];
  topBotPaths: SeoLogPath[];
  errorPaths: SeoLogPath[];
  waste: SeoLogWaste[];
  wastedRequests: number;
  uncrawledPaths: string[];
  /** Boşsa hiç tarama yapılmamıştır — boş `uncrawledPaths` "her sayfa geziliyor" ANLAMINA GELMEZ. */
  uncrawledSourceReportId: string;
  knownPageCount: number;
  verification: SeoLogVerification;
};

/** Saklanan son analiz; hiç çalıştırılmadıysa null. */
export async function getSeoLogReport(siteName: string) {
  const response = await apiClient.get<ApiEnvelope<SeoLogReport | null>>(
    `/seo/sites/${encodeURIComponent(siteName)}/logs`
  );
  return response.data.data;
}

/** Analizi çalıştırır. Sunucuda dosya/satır/süre olarak sınırlıdır ve sonucu diske yazar. */
export async function analyzeSeoLogs(siteName: string, days: number, verifyBots: boolean) {
  const response = await apiClient.post<ApiEnvelope<SeoLogReport>>(
    `/seo/sites/${encodeURIComponent(siteName)}/logs`,
    { days, verifyBots }
  );
  return response.data.data;
}
