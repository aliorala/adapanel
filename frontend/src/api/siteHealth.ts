import { apiClient } from "./client";

type ApiEnvelope<T> = { success: boolean; data: T; message: string; code: number };

/**
 * Sitenin DIŞARIDAN görünen sağlığı — sunucunun kendi iç durumu DEĞİL.
 * Sözleşme: src/AdaPanel.Core/Dtos/Health/SiteHealthDto.cs + src/AdaPanel.Api/Controllers/SiteHealthController.cs
 * (GET /api/v1/sites/{siteName}/health — Administrator/Reseller).
 *
 * NOT: "./health.ts" BAŞKA bir şeydir (sunucu servis durumu); karıştırma.
 */

/** "unknown" = ÖLÇEMEDİK demektir, "sorun yok" DEMEK DEĞİLDİR. */
export type SiteHealthStatus = "ok" | "warn" | "fail" | "unknown";

export type SiteHealthCheckKey =
  | "dnsResolves"
  | "dnsMatchesServer"
  | "http"
  | "https"
  | "mail"
  | "mail.mx"
  | "mail.spf"
  | "mail.dkim"
  | "mail.dmarc";

/** Tek bir dış-gözlem kontrolünün sonucu (SiteHealthCheckDto). */
export type SiteHealthCheck = {
  key: SiteHealthCheckKey;
  status: SiteHealthStatus;
  /** Rozette gösterilecek kısa değer (çözülen IP, "HTTP 200", "42 gün", "yok"). */
  value: string;
  /** Dile bağımsız teknik ayrıntı (hata metni, sorgulanan çözümleyici, ham kayıt). */
  detail: string;
};

/** Bir sitenin dışarıdan görünen sağlığı (SiteHealthDto). */
export type SiteHealth = {
  siteName: string;
  /** Ölçülen alan adı (IIS binding'inden çözülür). Boşsa sitede alan adı yok demektir. */
  domain: string;
  mailDomain?: string;
  /** Panelin bildiği sunucu adresi — dnsMatchesServer bununla karşılaştırır. */
  serverAddress: string;
  overallStatus: SiteHealthStatus;
  /** ISO 8601 (UTC) — JSON'da string olarak gelir. */
  checkedAtUtc: string;
  /** Ölçümün toplam süresi (ms). */
  durationMs: number;
  dnsResolves: SiteHealthCheck;
  dnsMatchesServer: SiteHealthCheck;
  http: SiteHealthCheck;
  https: SiteHealthCheck;
  /** Sertifikanın bitişine kalan gün (ölçülemediyse null). Negatif = süresi dolmuş. */
  certificateDaysLeft: number | null;
  mail: SiteHealthCheck;
  /** mail.mx, mail.spf, mail.dkim, mail.dmarc */
  mailRecords: SiteHealthCheck[];
};

/**
 * Ölçüm DIŞ AĞA çıkar ve birkaç saniye sürebilir → yalnız kullanıcı istediğinde çağır,
 * otomatik polling YAPMA.
 */
export async function getSiteHealth(siteName: string) {
  const response = await apiClient.get<ApiEnvelope<SiteHealth>>(
    `/sites/${encodeURIComponent(siteName)}/health`
  );
  return response.data.data;
}

/** Onarma bulgusu: panelin bulduğu, düzelttiği ya da onay beklediği tek bir sorun. */
export type RepairFinding = {
  /** Çeviri anahtarı (ör. "repair.site_stopped"). */
  key: string;
  severity: "warn" | "fail";
  repaired: boolean;
  /** Panel düzeltebilir ama kullanıcı onayı olmadan yapmaz. */
  needsConfirmation: boolean;
  detail: string;
};

export type SiteRepairResult = {
  siteName: string;
  zoneName: string;
  addedDnsRecords: string[];
  appliedMailRecords: number;
  mailCertificateRefreshed: boolean;
  findings: RepairFinding[];
  error: string;
  alreadyCurrent: boolean;
};

/**
 * Bu sitede onarılacak bir şey var mı? HİÇBİR ŞEY DEĞİŞTİRMEZ.
 *
 * Sayfa açılışında çağrılır: kullanıcı bir sorunu, aramaya gitmeden, bulunduğu yerde görmeli.
 */
export async function inspectSiteRepair(siteName: string) {
  const response = await apiClient.get<ApiEnvelope<SiteRepairResult>>(
    `/sites/${encodeURIComponent(siteName)}/health/repair`
  );
  return response.data.data;
}

/** Eksikleri tamamlar ve NE YAPILDIĞINI döner. */
export async function repairSite(siteName: string) {
  const response = await apiClient.post<ApiEnvelope<SiteRepairResult>>(
    `/sites/${encodeURIComponent(siteName)}/health/repair`
  );
  return response.data.data;
}
