import { apiClient } from "./client";

type ApiEnvelope<T> = {
  success: boolean;
  data: T;
  message: string;
  code: number;
  details?: string;
};

/** Tek denetim kaydı satırı (sunucudaki AuditLogDto ile birebir). */
export type AuditLogItem = {
  id: number;
  userId: string | null;
  userName: string;
  action: string;
  resource: string;
  details: string;
  ipAddress: string;
  createdAtUtc: string;
};

/** Sayfalanmış sonuç: toplam olmadan arayüz "sonraki" düğmesini yönetemez. */
export type AuditLogPage = {
  items: AuditLogItem[];
  totalCount: number;
  page: number;
  pageSize: number;
  /** Eylem açılır listesi VERİDEN gelir; sabit liste gömülmez. */
  availableActions: string[];
};

export type AuditRetentionSettings = {
  retentionDays: number;
  lastPurgeAtUtc: string | null;
  lastPurgedCount: number;
  totalCount: number;
  oldestAtUtc: string | null;
};

/**
 * Arama ölçütleri. Tarih alanları ISO-8601 (UTC) metindir.
 *
 * Boş alan "bu ölçüte bakma" demektir; sunucu da aynı kuralı uygular.
 */
export type AuditLogFilter = {
  fromUtc?: string;
  toUtc?: string;
  userName?: string;
  action?: string;
  resource?: string;
  search?: string;
};

/**
 * Boş/whitespace alanları AT: sunucuya `userName=` göndermek ile hiç göndermemek aynı sonucu
 * verir ama URL'i okunmaz yapar ve tarayıcı geçmişinde gereksiz varyant üretir.
 */
function buildParams(filter: AuditLogFilter): Record<string, string> {
  const params: Record<string, string> = {};
  for (const [key, value] of Object.entries(filter)) {
    const trimmed = (value ?? "").trim();
    if (trimmed.length > 0) {
      params[key] = trimmed;
    }
  }

  return params;
}

export async function searchAuditLogs(filter: AuditLogFilter, page: number, pageSize: number) {
  const response = await apiClient.get<ApiEnvelope<AuditLogPage>>("/audit-logs/search", {
    params: { ...buildParams(filter), page, pageSize }
  });
  return response.data.data;
}

/**
 * Filtreye uyan TÜM kayıtları CSV olarak indirir (sayfalama uygulanmaz).
 *
 * Sunucu satırları akıtarak yazar; istemci tarafında da yanıt blob olarak alınıp doğrudan
 * dosyaya bağlanır — ara bir dizi kurulmaz.
 */
export async function downloadAuditLogsCsv(filter: AuditLogFilter) {
  const response = await apiClient.get("/audit-logs/export", {
    params: buildParams(filter),
    responseType: "blob"
  });

  const url = window.URL.createObjectURL(response.data as Blob);
  const link = document.createElement("a");
  link.href = url;
  link.download = `audit-logs-${new Date().toISOString().slice(0, 10)}.csv`;
  document.body.appendChild(link);
  link.click();
  link.remove();
  window.URL.revokeObjectURL(url);
}

export async function getAuditRetention() {
  const response = await apiClient.get<ApiEnvelope<AuditRetentionSettings>>("/audit-logs/retention");
  return response.data.data;
}

export async function saveAuditRetention(retentionDays: number) {
  const response = await apiClient.put<ApiEnvelope<AuditRetentionSettings>>("/audit-logs/retention", { retentionDays });
  return response.data.data;
}

/** Saklama süresinden eski kayıtları hemen siler; güncel ölçümü döner. */
export async function purgeAuditLogs() {
  const response = await apiClient.post<ApiEnvelope<AuditRetentionSettings>>("/audit-logs/purge", {});
  return response.data.data;
}
