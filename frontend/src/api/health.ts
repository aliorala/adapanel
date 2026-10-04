import { apiClient } from "./client";
import { UserFacingError } from "../utils/apiError";

type ApiEnvelope<T> = { success: boolean; data: T; message: string; code: number };

export type ServiceHealth = {
  name: string;
  label: string;
  status: string; // Includes Windows pending and paused states, validated below.
};

export async function getServerHealth() {
  const response = await apiClient.get<ApiEnvelope<ServiceHealth[]>>("/server-health");
  const rows = response.data?.data;
  if (response.data?.success !== true || !Array.isArray(rows) || !rows.every(validService) ||
    new Set(rows.map(row => row.name.toLowerCase())).size !== rows.length) throw unconfirmed();
  return rows;
}

export async function restartService(name: string) {
  const response = await apiClient.post<ApiEnvelope<ServiceHealth>>("/server-health/restart", { name });
  const row = response.data?.data;
  if (response.data?.success !== true || !validService(row) || row.name.toLowerCase() !== name.toLowerCase()) throw unconfirmed();
  return row;
}

const unconfirmed = () => new UserFacingError("panel.health.resultUnconfirmed");
function validService(row: ServiceHealth | null | undefined): row is ServiceHealth {
  return !!row && typeof row.name === "string" && !!row.name.trim() && typeof row.label === "string" &&
    ["Running", "Stopped", "NotInstalled", "Unknown", "StartPending", "StopPending", "ContinuePending", "PausePending", "Paused"].includes(row.status);
}

/** Bulgu: onarmanın bulduğu, düzelttiği ya da onay beklediği tek bir sorun. */
export type ServerFinding = {
  key: string;
  severity: "warn" | "fail";
  repaired: boolean;
  needsConfirmation: boolean;
  detail: string;
};

export type DomainRefreshResult = {
  siteName: string;
  zoneName: string;
  addedDnsRecords: string[];
  appliedMailRecords: number;
  /** Bu alan adında bulunan sorunlar; eklenen kayıt sayısıyla anlatılamayanlar burada. */
  findings: ServerFinding[];
  error: string;
  alreadyCurrent: boolean;
};


export type DomainRefreshSummary = {
  results: DomainRefreshResult[];
  totalSites: number;
  updatedSites: number;
  failedSites: number;
  /**
   * Sunucu geneli bulgular. Bunlar hiçbir alan adına ait olmadığı için updatedSites'a
   * yansımaz; ayrıca sayılmazlarsa onarma düğmesi hiç görünmez ve sorun kalıcı olur.
   */
  serverFindings: ServerFinding[];
};

function measuredDomains(envelope: ApiEnvelope<DomainRefreshSummary>): DomainRefreshSummary {
  const value = envelope?.data;
  const count = (n: number) => Number.isSafeInteger(n) && n >= 0;
  const findings = (rows: ServerFinding[]) => Array.isArray(rows) && rows.every(row => row && typeof row.key === "string" &&
    ["warn", "fail"].includes(row.severity) && typeof row.repaired === "boolean" && typeof row.needsConfirmation === "boolean" && typeof row.detail === "string");
  if (envelope?.success !== true || !value || !count(value.totalSites) || !count(value.updatedSites) || !count(value.failedSites) ||
    value.updatedSites + value.failedSites > value.totalSites || !findings(value.serverFindings) || !Array.isArray(value.results) || value.results.length !== value.totalSites ||
    !value.results.every(row => row && typeof row.siteName === "string" && !!row.siteName.trim() && typeof row.zoneName === "string" &&
      Array.isArray(row.addedDnsRecords) && row.addedDnsRecords.every(item => typeof item === "string") && count(row.appliedMailRecords) &&
      typeof row.error === "string" && typeof row.alreadyCurrent === "boolean" && findings(row.findings))) throw unconfirmed();
  return value;
}

/** Değişiklik YAPMADAN kaç alan adının eski kaldığını sorar (uyarı göstermek için). */
export async function inspectDomains() {
  const response = await apiClient.get<ApiEnvelope<DomainRefreshSummary>>("/server-health/refresh-domains");
  return measuredDomains(response.data);
}

/** Eksik varsayılanları tamamlar; var olan kayıtlara dokunmaz. */
export async function refreshDomains() {
  const response = await apiClient.post<ApiEnvelope<DomainRefreshSummary>>("/server-health/refresh-domains");
  return measuredDomains(response.data);
}
