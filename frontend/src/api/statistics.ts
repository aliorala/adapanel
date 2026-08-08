import { apiClient } from "./client";

type ApiEnvelope<T> = {
  success: boolean;
  data: T;
  message: string;
  code: number;
};

export type StatBucket = {
  key: string;
  count: number;
};

export type WebStatistics = {
  siteName: string;
  periodDays: number;
  generatedAtUtc: string;
  logPath: string;
  hasData: boolean;
  totalRequests: number;
  uniqueVisitors: number;
  /** true ise sayı üst sınıra dayandı ve gerçek sayının ALTINDA; ekranda "+" ile gösterilir. */
  uniqueVisitorsApproximate: boolean;
  totalBytes: number;
  status2xx: number;
  status3xx: number;
  status4xx: number;
  status5xx: number;
  topPages: StatBucket[];
  topReferrers: StatBucket[];
  dailyHits: StatBucket[];
};

export async function getSiteStatistics(siteName: string, days = 7) {
  const response = await apiClient.get<ApiEnvelope<WebStatistics>>(
    `/sites/${encodeURIComponent(siteName)}/statistics`,
    { params: { days } }
  );
  return response.data.data;
}
