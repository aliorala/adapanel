import { apiClient } from "./client";

type ApiEnvelope<T> = { success: boolean; data: T; message: string; code: number };

/** Sabit kategori anahtarı; UI bunu i18n anahtarına çevirir (sunucu çevrilmiş metin döndürmez). */
export type ProcessCategory =
  | "site"
  | "php"
  | "database"
  | "cache"
  | "mail"
  | "panel"
  | "web"
  | "ftp"
  | "dns"
  | "docker"
  | "service"
  | "windows"
  | "other";

export type ProcessUsage = {
  processId: number;
  name: string;
  category: ProcessCategory;
  label: string;
  detail: string;
  cpuPercent: number;
  workingSetBytes: number;
  privateMemoryBytes: number;
  memoryPercent: number;
  threadCount: number;
  startTimeUtc: string | null;
  uptimeSeconds: number;
};

export type ProcessSnapshot = {
  capturedAtUtc: string;
  sampleWindowSeconds: number;
  /** false ise bu ilk ölçüm: CPU yüzdeleri henüz güvenilir değil (fark hesabı iki ölçüm ister). */
  cpuBaselineReady: boolean;
  totalCpuPercent: number;
  logicalProcessorCount: number;
  totalMemoryBytes: number;
  usedMemoryBytes: number;
  availableMemoryBytes: number;
  memoryUsagePercent: number;
  totalProcessCount: number;
  sortBy: string;
  processes: ProcessUsage[];
};

export async function getProcessSnapshot(sortBy: "cpu" | "memory" = "cpu", limit = 30) {
  const response = await apiClient.get<ApiEnvelope<ProcessSnapshot>>(
    `/monitoring/processes?limit=${limit}&sortBy=${sortBy}`
  );
  return response.data.data;
}
