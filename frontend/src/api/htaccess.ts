import { apiClient } from "./client";

type ApiEnvelope<T> = {
  success: boolean;
  data: T;
  message: string;
  code: number;
};

/**
 * Bulgu ağırlığı — SAYI olarak gelir.
 *
 * ÖLÇÜLDÜ, VARSAYILMADI: `Program.cs` yalnız `AddControllersWithViews()` çağırıyor, yani hiçbir
 * `JsonStringEnumConverter` kayıtlı DEĞİL ve System.Text.Json enum'ları sayısal serileştiriyor.
 * (Tek istisna `LicenseState`; o kendi üzerinde `[JsonConverter]` taşıyor ve gerekçesi orada
 * yazılı.) Adları beklemiş olsaydık her bulgu "bilinmeyen" dalına düşer, panel onları hiç
 * gösteremezdi — yani tam olarak düzeltmeye çalıştığımız sessiz arıza.
 *
 * Sıra `HtaccessDiagnosticLevel` enum'unun bildirim sırasıdır; araya bir seviye eklenirse
 * numaralar kayar, bu yüzden eşleme TEK yerde tutuluyor.
 */
export const DIAGNOSTIC_LEVELS = ["info", "unsupported", "warning", "error", "fatal"] as const;

export type HtaccessDiagnosticLevelName = (typeof DIAGNOSTIC_LEVELS)[number];

/** Bilinmeyen bir sayı gelirse "warning" denir: sessizce gizlemek yerine GÖRÜNÜR kalır. */
export function diagnosticLevelName(level: number): HtaccessDiagnosticLevelName {
  return DIAGNOSTIC_LEVELS[level] ?? "warning";
}

export type HtaccessDiagnostic = {
  /** Sayısal ağırlık; ada çevirmek için `diagnosticLevelName`. */
  level: number;
  /** Makine-okur kod (`DESTEKLENMEYEN_DIREKTIF`, `RISKLI_DUZENLI_IFADE`, ...). */
  code: string;
  file: string;
  line: number;
  directive: string;
  /** Kullanıcının okuyacağı açıklama — SUNUCUDAN gelir, arayüz metni uydurmaz. */
  message: string;
};

export type HtaccessParseResult = {
  filePath: string;
  virtualPath: string;
  diagnostics: HtaccessDiagnostic[];
  sizeBytes: number;
  usable: boolean;
  hasUnsupported: boolean;
};

export type HtaccessEvaluationResult = {
  /** `HtaccessOutcome` enum'unun sayısal karşılığı (0 = hiçbir kural eşleşmedi). */
  outcome: number;
  rewrittenPath: string;
  rewrittenQuery: string;
  statusCode: number;
  redirectLocation: string;
  directoryIndex: string[];
  diagnostics: HtaccessDiagnostic[];
  appliedFiles: string[];
};

export type HtaccessInspectionResult = {
  siteRootPath: string;
  virtualPath: string;
  files: HtaccessParseResult[];
  diagnostics: HtaccessDiagnostic[];
  unsupported: HtaccessDiagnostic[];
  hasFatal: boolean;
  rewriteEngineEnabled: boolean;
  rewriteRuleCount: number;
  effective: HtaccessEvaluationResult;
};

export type HtaccessFileSummary = {
  relativeDirectory: string;
  virtualPath: string;
  sizeBytes: number;
  lastWriteUtc: string;
};

export type HtaccessMode = "off" | "observe" | "enforce";

/** `unknown` = ÖLÇÜLEMEDİ. "supported" ile karıştırılmamalı; arayüz ikisini ayrı gösterir. */
export type HtaccessSupport = "supported" | "no_managed_code" | "runtime_version" | "unknown";

export type HtaccessSiteReport = {
  siteName: string;
  documentRoot: string;
  mode: HtaccessMode;
  serverMode: HtaccessMode;
  hasFiles: boolean;
  hasRootFile: boolean;
  files: HtaccessFileSummary[];
  inspection: HtaccessInspectionResult;
  applicationPoolName: string;
  managedRuntimeVersion: string;
  support: HtaccessSupport;
  /** Motor IIS içinde YÜKLÜ mü. Bugün her koşulda false — sebebi `engineBlockers`'ta. */
  engineLoadedInIis: boolean;
  /** i18n anahtarları (`htaccess.blocker.*`). Metin sunucuda üretilmez, burada çevrilir. */
  engineBlockers: string[];
  /** i18n anahtarları (`htaccess.deviation.*`) — motorun Apache'den bilinçli sapmaları. */
  deviations: string[];
};

export type HtaccessSimulationResult = {
  requestPath: string;
  action: "none" | "rewrite" | "redirect" | "deny";
  target: string;
  statusCode: number;
  detail: HtaccessEvaluationResult;
};

function base(siteName: string): string {
  return `/sites/${encodeURIComponent(siteName)}/htaccess`;
}

export async function getHtaccessReport(siteName: string) {
  const response = await apiClient.get<ApiEnvelope<HtaccessSiteReport>>(base(siteName));
  return response.data.data;
}

export async function inspectHtaccess(siteName: string, directory: string) {
  const response = await apiClient.get<ApiEnvelope<HtaccessInspectionResult>>(
    `${base(siteName)}/inspect`,
    { params: directory ? { directory } : {} }
  );
  return response.data.data;
}

export async function validateHtaccess(siteName: string, content: string, virtualPath: string) {
  const response = await apiClient.post<ApiEnvelope<HtaccessParseResult>>(
    `${base(siteName)}/validate`,
    { content, virtualPath }
  );
  return response.data.data;
}

export async function simulateHtaccess(
  siteName: string,
  payload: { requestPath: string; method: string; https: boolean }
) {
  const response = await apiClient.post<ApiEnvelope<HtaccessSimulationResult>>(
    `${base(siteName)}/simulate`,
    payload
  );
  return response.data.data;
}

export async function setHtaccessMode(siteName: string, mode: HtaccessMode, note: string) {
  const response = await apiClient.post<ApiEnvelope<HtaccessSiteReport>>(
    `${base(siteName)}/mode`,
    { mode, note }
  );
  return response.data.data;
}
