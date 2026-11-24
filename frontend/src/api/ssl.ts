import { apiClient } from "./client";
import { UserFacingError } from "../utils/apiError";

type ApiEnvelope<T> = {
  success: boolean;
  data: T;
  message: string;
  code: number;
};

export type SslCertificate = {
  siteName: string;
  hostName: string;
  thumbprint: string;
  friendlyName: string;
  subject: string;
  notAfterUtc: string;
  /** Sunucunun hesapladigi durum: valid | renewal_due (30 gun) | expiring (7 gun) | expired. Eski sunucuda yok. */
  expiryStatus?: string;
  /** Panel bu kaydin otomatik yenilemesini duraklatti (panel tetikli turlar atlar). */
  renewalPaused?: boolean;
  /** Panelin win-acme yenileme kimligi; yoksa (elle yuklenen) yenileme kontrolu yoktur. */
  managedRenewalId?: string | null;
  // "letsencrypt" | "self-signed" | "manual" (başka bir sertifika otoritesi ya da elle yüklenmiş).
  source: string;
  // null = ÖLÇÜLEMEDİ. "Bağlama yok" ile "bakamadık" aynı şey değildir.
  isHttpsBindingPresent: boolean | null;
  // Bağlama BİZİM sertifikamızı taşıyor mu. false = ölçüldü, taşımıyor (bağlama sertifikasız ya da
  // başka sertifika) → site 443'te ölü olabilir. null = ölçülemedi.
  // "https bağlaması var" tek başına yetmez: bağlama önce kurulur, sertifika sonra iliştirilir.
  isCertificateBound: boolean | null;
  /** null/missing means certificate-store availability was not measured. */
  isPresentInStore?: boolean | null;
  bindingDetail: string;
  port: number;
  // Let's Encrypt alınamadığı için kendinden imzalıya düşüldüyse sebebin çeviri anahtarı.
  // Boş değilse ekran "SSL kuruldu" gibi davranmamalı.
  fallbackReason: string;
};

export type SslWorkspace = {
  siteName: string;
  hostNames: string[];
  certificates: SslCertificate[];
  httpsRedirectEnabled: boolean;
  /** Missing/false means redirect state could not be measured, including legacy responses. */
  httpsRedirectMeasured?: boolean;
};

export async function setHttpsRedirect(siteName: string, enabled: boolean) {
  const response = await apiClient.post<ApiEnvelope<SslWorkspace>>("/ssl/https-redirect", { siteName, enabled });
  return response.data.data;
}

export async function getSslWorkspace(siteName: string) {
  const response = await apiClient.get<ApiEnvelope<SslWorkspace>>(`/ssl/${encodeURIComponent(siteName)}`);
  const value = response.data?.data;
  if (response.data?.success !== true || !value || typeof value.siteName !== "string" || value.siteName.toLowerCase() !== siteName.toLowerCase() ||
    !Array.isArray(value.hostNames) || value.hostNames.some(host => typeof host !== "string") ||
    !Array.isArray(value.certificates) || value.certificates.some(item => !item || typeof item !== "object"))
    throw new UserFacingError("panel.ssl.workspaceUnverified");
  return response.data.data;
}

export async function issueLetsEncrypt(payload: { siteName: string; hostName: string; contactEmail: string; useStaging: boolean; wildcard?: boolean; }) {
  // Wildcard DNS-01 turları uzun sürebilir (kayıt yazma + yetkili doğrulama) → geniş timeout.
  const response = await apiClient.post<ApiEnvelope<SslCertificate>>("/ssl/letsencrypt", payload, { timeout: 12 * 60_000 });
  if (response.data?.success !== true) throw new UserFacingError("panel.ssl.installationUnverified");
  return response.data.data;
}

export async function importCertificate(payload: {
  siteName: string;
  hostName: string;
  fileName: string;
  base64Content: string;
  password: string;
  port: number;
}) {
  const response = await apiClient.post<ApiEnvelope<SslCertificate>>("/ssl/import", payload);
  if (response.data?.success !== true) throw new UserFacingError("panel.ssl.installationUnverified");
  return response.data.data;
}

export async function renewAllCertificates() {
  const response = await apiClient.post<ApiEnvelope<SslRenewalResult>>("/ssl/renew", {});
  return response.data.data;
}

export type SslRemovalResult = {
  siteName: string;
  hostName: string;
  port: number;
  bindingRemoved: boolean;
  httpsRedirectDisabled: boolean;
  certificateThumbprint: string;
  certificateAction: string;
  certificateUsedBy: string[];
  detail: string;
  /** none | cancelled | kept_shared | cancel_failed: win-acme yenileme kaydina ne yapildi. */
  renewalAction?: string;
  renewalDetail?: string;
  workspace: SslWorkspace;
};

export async function removeCertificate(payload: { siteName: string; hostName: string; port: number; removeCertificateFromStore: false }) {
  const response = await apiClient.post<ApiEnvelope<SslRemovalResult>>("/ssl/remove", payload);
  if (response.data?.success !== true) throw new UserFacingError("panel.ssl.removalUnverified");
  return response.data.data;
}

export type SslRenewalResult = {
  measured: boolean;
  renewedCount: number;
  verifiedCount: number;
  missingFromStoreCount: number;
  unverifiedCount: number;
  bindingDriftCount: number;
  bindingsMeasured: boolean;
  failureReason: string;
  output: string;
};

export type RenewalAction = "pause" | "resume" | "cancel";

export async function setRenewalState(payload: { siteName: string; hostName: string; port: number; action: RenewalAction }) {
  const response = await apiClient.post<ApiEnvelope<SslCertificate>>("/ssl/renewal", payload);
  if (response.data?.success !== true || !response.data.data) throw new UserFacingError("panel.ssl.renewalControlUnverified");
  return response.data.data;
}

export type SslCsr = {
  id: string;
  hostName: string;
  altNames: string[];
  subject: string;
  keySize: number;
  createdAtUtc: string;
  status: "pending" | "imported";
  importedThumbprint: string;
  /** Yalniz olusturma yanitinda dolu; listede bos. Ozel anahtar HICBIR yanitta yoktur. */
  csrPem: string;
};

export async function listSslCsrs() {
  const response = await apiClient.get<ApiEnvelope<SslCsr[]>>("/ssl-csr");
  return response.data.data;
}

export async function createSslCsr(payload: { hostName: string; altNames: string[]; organization: string; country: string; state: string; locality: string; keySize: number }) {
  const response = await apiClient.post<ApiEnvelope<SslCsr>>("/ssl-csr", payload);
  if (response.data?.success !== true || !response.data.data?.csrPem) throw new UserFacingError("panel.ssl.csr.unverified");
  return response.data.data;
}

export async function downloadSslCsr(id: string) {
  const response = await apiClient.get<Blob>(`/ssl-csr/${encodeURIComponent(id)}/download`, { responseType: "blob" });
  return response.data;
}

export async function importSignedCertificate(id: string, payload: { siteName: string; base64Content: string; port: number }) {
  const response = await apiClient.post<ApiEnvelope<SslCertificate>>(`/ssl-csr/${encodeURIComponent(id)}/import`, payload);
  if (response.data?.success !== true) throw new UserFacingError("panel.ssl.installationUnverified");
  return response.data.data;
}

export async function deleteSslCsr(id: string) {
  await apiClient.delete(`/ssl-csr/${encodeURIComponent(id)}`);
}
