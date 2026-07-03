import { apiClient } from "./client";

type ApiEnvelope<T> = {
  success: boolean;
  data: T;
  message: string;
  code: number;
};

export type AdvisorItem = {
  id: string;
  severity: "critical" | "warning" | "info";
  titleKey: string;
  detailKey: string;
  detail: string;
  actionSection: string;
};

export type AdvisorReport = {
  criticalCount: number;
  warningCount: number;
  infoCount: number;
  checkedAtUtc: string;
  items: AdvisorItem[];
};

export async function getAdvisorReport() {
  const response = await apiClient.get<ApiEnvelope<AdvisorReport>>("/advisor");
  return response.data.data;
}
