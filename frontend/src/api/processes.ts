import { apiClient } from "./client";
import { UserFacingError } from "../utils/apiError";

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
  const value = response.data?.data;
  const number = (n: number) => typeof n === "number" && Number.isFinite(n) && n >= 0;
  if (response.data?.success !== true || !value || typeof value.capturedAtUtc !== "string" || !value.capturedAtUtc.trim() ||
    typeof value.cpuBaselineReady !== "boolean" || value.sortBy !== sortBy ||
    [value.sampleWindowSeconds, value.totalCpuPercent, value.logicalProcessorCount, value.totalMemoryBytes, value.usedMemoryBytes,
      value.availableMemoryBytes, value.memoryUsagePercent, value.totalProcessCount].some(n => !number(n)) ||
    !Array.isArray(value.processes) || !value.processes.every(row => row &&
      [row.name, row.category, row.label, row.detail].every(text => typeof text === "string") &&
      [row.processId, row.cpuPercent, row.workingSetBytes, row.privateMemoryBytes, row.memoryPercent, row.threadCount, row.uptimeSeconds].every(number) &&
      (row.startTimeUtc === null || typeof row.startTimeUtc === "string"))) throw new UserFacingError("panel.processes.failed");
  return value;
}
