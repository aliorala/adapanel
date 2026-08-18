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
