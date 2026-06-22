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

export const useSystemStore = defineStore("system", {
  state: () => ({
    overview: null as SystemOverview | null,
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
    }
  }
});
