import { apiClient } from "./client";

type ApiEnvelope<T> = {
  success: boolean;
  data: T;
  message: string;
  code: number;
};

export type AttentionSeverity = "critical" | "warning" | "info";

/**
 * AttentionItemDto (src/AdaPanel.Core/Dtos/Advisor/AttentionSummaryDto.cs) ile birebir.
 * `titleKey` ÇEVRİLMİŞ METİN DEĞİL, i18n anahtarıdır → arayüzde t(item.titleKey).
 */
export type AttentionItem = {
  id: string;
  severity: AttentionSeverity;
  titleKey: string;
  /** Açıklamanın çeviri anahtarı: "ne yapılabilir"i insan diliyle söyler. Boş olabilir. */
  descriptionKey: string;
  detail: string;
  affectedCount?: number;
  section: string;
  actionEndpoint: string;
  actionMethod: string;
  actionPayload: Record<string, string>;
  sslRepair?: import('./sslRepair').SslRepairTarget | null;
};

/** AttentionSummaryDto ile birebir. */
export type AttentionSummary = {
  criticalCount: number;
  warningCount: number;
  infoCount: number;
  totalCount: number;
  checkedAtUtc: string;
  sectionCounts: Record<string, number>;
  failedSources: string[];
  items: AttentionItem[];
};

const SEVERITIES: AttentionSeverity[] = ["critical", "warning", "info"];

function toSeverity(value: unknown): AttentionSeverity {
  return SEVERITIES.includes(value as AttentionSeverity) ? (value as AttentionSeverity) : "info";
}

function normalizeItem(raw: Partial<AttentionItem> | null | undefined): AttentionItem {
  return {
    id: raw?.id ?? "",
    severity: toSeverity(raw?.severity),
    titleKey: raw?.titleKey ?? "",
    descriptionKey: raw?.descriptionKey ?? "",
    detail: raw?.detail ?? "",
    affectedCount: Number.isInteger(raw?.affectedCount) && raw!.affectedCount! > 0 ? raw!.affectedCount : 1,
    section: raw?.section ?? "",
    actionEndpoint: raw?.actionEndpoint ?? "",
    actionMethod: raw?.actionMethod ?? "",
    actionPayload: raw?.actionPayload ?? {},
    sslRepair: raw?.sslRepair ?? null
  };
}

export function emptyAttentionSummary(): AttentionSummary {
  return {
    criticalCount: 0,
    warningCount: 0,
    infoCount: 0,
    totalCount: 0,
    checkedAtUtc: "",
    sectionCounts: {},
    failedSources: [],
    items: []
  };
}

/**
 * Sunucu tüm alanları doldursa da (eski sürüm / kısmi yanıt) arayüz asla `undefined.length`
 * ile çökmesin diye yanıt normalize edilir.
 */
function normalizeSummary(raw: Partial<AttentionSummary> | null | undefined): AttentionSummary {
  const items = (raw?.items ?? []).map(normalizeItem);
  return {
    criticalCount: raw?.criticalCount ?? items.filter((item) => item.severity === "critical").length,
    warningCount: raw?.warningCount ?? items.filter((item) => item.severity === "warning").length,
    infoCount: raw?.infoCount ?? items.filter((item) => item.severity === "info").length,
    totalCount: raw?.totalCount ?? items.length,
    checkedAtUtc: raw?.checkedAtUtc ?? "",
    sectionCounts: raw?.sectionCounts ?? {},
    failedSources: raw?.failedSources ?? [],
    items
  };
}

export async function getAttentionSummary(signal?: AbortSignal): Promise<AttentionSummary> {
  const response = await apiClient.get<ApiEnvelope<AttentionSummary>>("/attention", { signal });
  return normalizeSummary(response.data?.data);
}

/**
 * Backend TAM yolu döner ("/api/v1/dns/server/repair"), apiClient'ın baseURL'i zaten "/api/v1"
 * olduğundan ön ek tekrarlanmasın diye kırpılır.
 */
export function toClientPath(actionEndpoint: string): string {
  const trimmed = (actionEndpoint ?? "").trim();
  if (!trimmed) {
    return "";
  }

  const withoutPrefix = trimmed.replace(/^\/?api\/v1\/?/i, "/");
  return withoutPrefix.startsWith("/") ? withoutPrefix : `/${withoutPrefix}`;
}

export function hasQuickFix(item: AttentionItem): boolean {
  return toClientPath(item.actionEndpoint).length > 1;
}

/**
 * Maddenin tek-tık düzeltme ucunu çağırır. Gövde backend'in hazırladığı ActionPayload'dır
 * (ör. {"name":"W3SVC"}); frontend gövde UYDURMAZ.
 */
export async function runAttentionAction(item: AttentionItem): Promise<string> {
  const path = toClientPath(item.actionEndpoint);
  if (!path) {
    throw new Error("attention.action.missingEndpoint");
  }

  const method = (item.actionMethod || "POST").toUpperCase();
  const payload = item.actionPayload ?? {};
  const response = await apiClient.request<ApiEnvelope<unknown>>({
    url: path,
    method: method === "GET" ? "GET" : method === "PUT" ? "PUT" : method === "DELETE" ? "DELETE" : "POST",
    data: method === "GET" ? undefined : payload
  });

  return response.data?.message ?? "";
}
