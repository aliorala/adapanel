import { apiClient } from "./client";

type ApiEnvelope<T> = { success: boolean; data: T; message: string; code: number };

/** Biten tek bir sorgu kaydı. */
export type QueryLogEntry = {
  firstSeenUtc: string;
  endedAtUtc: string;
  durationSeconds: number;
  source: string;
  siteName: string;
  target: string;
  user: string;
  clientHost: string;
  statement: string;
  redacted: boolean;
  remoteClient: boolean;
  ownerName: string;
};

/** Aynı şekildeki sorguların özeti — "hangi sorgu çok çalışmış". */
export type QueryLogSummaryRow = {
  shape: string;
  count: number;
  totalSeconds: number;
  maxSeconds: number;
  siteName: string;
  sample: string;
};

export type QueryLogPage = {
  entries: QueryLogEntry[];
  totalCount: number;
  topShapes: QueryLogSummaryRow[];
  /** Kaydın bulunduğu günler (yyyy-MM-dd, en yeniden eskiye). */
  availableDays: string[];
  retentionDays: number;
  /** Örnekleme aralığı — bundan kısa sorgular kaçmış olabilir; ekranda söylenir. */
  sampleIntervalSeconds: number;
};

export type QueryLogFilter = {
  from?: string;
  to?: string;
  source?: string;
  siteName?: string;
  target?: string;
  user?: string;
  minDurationSeconds?: number;
  search?: string;
  remoteOnly?: boolean;
  skip?: number;
  take?: number;
};

/**
 * Sorgu geçmişi (Administrator-only: yanıt başka müşterilerin sorgu metinlerini içerir).
 *
 * Boş/varsayılan alanlar gönderilmez — sunucuda "kısıtlama yok" anlamına gelirler ve boş dizeyi
 * göndermek gereksiz sorgu dizesi şişirir.
 */
export async function getQueryHistory(filter: QueryLogFilter, signal?: AbortSignal): Promise<QueryLogPage> {
  const params = new URLSearchParams();
  for (const [key, value] of Object.entries(filter)) {
    if (value === undefined || value === null || value === "" || value === false || value === 0) {
      continue;
    }
    params.set(key, String(value));
  }

  const response = await apiClient.get<ApiEnvelope<QueryLogPage>>(
    `/monitoring/consumers/history?${params.toString()}`,
    { signal }
  );
  return response.data.data;
}
