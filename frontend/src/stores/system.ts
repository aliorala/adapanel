import { defineStore } from "pinia";
import { apiClient } from "../api/client";

type ApiEnvelope<T> = {
  success: boolean;
  data: T;
  message: string;
  code: number;
};

export type DiskUsage = {
  name: string;
  label: string;
  format: string;
  totalBytes: number;
  freeBytes: number;
  usedBytes: number;
  usagePercent: number;
};

export type NetworkInterfaceMetric = {
  name: string;
  type: string;
  status: string;
  totalBytesReceived: number;
  totalBytesSent: number;
  bytesReceivedPerSecond: number;
  bytesSentPerSecond: number;
};

export type SystemOverview = {
  machineName: string;
  operatingSystem: string;
  osArchitecture: string;
  frameworkDescription: string;
  panelVersion: string;
  currentServerTimeUtc: string;
  bootTimeUtc: string;
  uptime: string;
  cpuUsagePercent: number;
  logicalProcessorCount: number;
  totalMemoryBytes: number;
  availableMemoryBytes: number;
  usedMemoryBytes: number;
  memoryUsagePercent: number;
  disks: DiskUsage[];
  networkInterfaces: NetworkInterfaceMetric[];
};

export type SystemHistorySample = {
  timestampUtc: string;
  cpuPercent: number;
  memoryPercent: number;
  networkDownBytesPerSec: number;
  networkUpBytesPerSec: number;
};

export const useSystemStore = defineStore("system", {
  state: () => ({
    overview: null as SystemOverview | null,
    history: [] as SystemHistorySample[],
    loading: false
  }),
  actions: {
    async fetchOverview() {
      this.loading = true;

      try {
        const response = await apiClient.get<ApiEnvelope<SystemOverview>>("/system/overview");
        this.overview = response.data.data;
        return this.overview;
      } finally {
        this.loading = false;
      }
    },
    // Kalıcı izleme geçmişi (sunucu dakikada bir örnekler) → trend grafikleri sayfa yenilense de dolu gelir.
    async fetchHistory() {
      const response = await apiClient.get<ApiEnvelope<SystemHistorySample[]>>("/system/history");
      this.history = response.data.data;
      return this.history;
    }
  }
});
