import { defineStore } from "pinia";
import { toRaw } from "vue";
import { apiClient, getSessionEpoch } from "../api/client";
import { UserFacingError } from "../utils/apiError";
import { extractApiMessage } from "./auth";

type ApiEnvelope<T> = {
  success: boolean;
  data: T;
  message: string;
  code: number;
};

export type DiskUsage = {
  measured: boolean;
  name: string;
  label: string;
  format: string;
  totalBytes: number;
  freeBytes: number;
  usedBytes: number;
  usagePercent: number;
};

export type NetworkInterfaceMetric = {
  ratesMeasured: boolean;
  name: string;
  type: string;
  status: string;
  totalBytesReceived: number;
  totalBytesSent: number;
  bytesReceivedPerSecond: number;
  bytesSentPerSecond: number;
};

export type SystemOverview = {
  cpuMeasured: boolean;
  memoryMeasured: boolean;
  disksMeasured: boolean;
  networkMeasured: boolean;
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
  cpuMeasured: boolean;
  memoryMeasured: boolean;
  networkMeasured: boolean;
  timestampUtc: string;
  cpuPercent: number;
  memoryPercent: number;
  networkDownBytesPerSec: number;
  networkUpBytesPerSec: number;
};

const nonnegative = (value: number) => typeof value === "number" && Number.isFinite(value) && value >= 0;
const percent = (value: number) => nonnegative(value) && value <= 100;
const optionalFlag = (value: unknown) => value === undefined || typeof value === "boolean";
const invalid = () => new UserFacingError("panel.monitor.measurementFailed");
function measuredOverview(envelope: ApiEnvelope<SystemOverview>): SystemOverview {
  const value = envelope?.data;
  if (envelope?.success !== true || !value ||
    [value.machineName, value.operatingSystem, value.osArchitecture, value.frameworkDescription, value.panelVersion, value.currentServerTimeUtc, value.bootTimeUtc, value.uptime].some(item => typeof item !== "string") ||
    !percent(value.cpuUsagePercent) || !percent(value.memoryUsagePercent) ||
    [value.logicalProcessorCount, value.totalMemoryBytes, value.availableMemoryBytes, value.usedMemoryBytes].some(item => !nonnegative(item)) ||
    [value.cpuMeasured, value.memoryMeasured, value.disksMeasured, value.networkMeasured].some(item => !optionalFlag(item)) ||
    !Array.isArray(value.disks) || !value.disks.every(disk => disk && typeof disk.name === "string" && typeof disk.label === "string" && typeof disk.format === "string" &&
      optionalFlag(disk.measured) && percent(disk.usagePercent) && [disk.totalBytes, disk.freeBytes, disk.usedBytes].every(nonnegative) &&
      (!disk.measured || (disk.totalBytes > 0 && disk.freeBytes <= disk.totalBytes && disk.usedBytes <= disk.totalBytes)))) throw invalid();
  if (!Array.isArray(value.networkInterfaces) || !value.networkInterfaces.every(item => item && [item.name, item.type, item.status].every(text => typeof text === "string") &&
    optionalFlag(item.ratesMeasured) && [item.totalBytesReceived, item.totalBytesSent, item.bytesReceivedPerSecond, item.bytesSentPerSecond].every(nonnegative)) ||
    (value.memoryMeasured && (value.totalMemoryBytes <= 0 || value.availableMemoryBytes > value.totalMemoryBytes || value.usedMemoryBytes > value.totalMemoryBytes))) throw invalid();
  // Older servers have no affirmative measurement flags. Never infer measurement from zero.
  return { ...value, cpuMeasured: value.cpuMeasured === true, memoryMeasured: value.memoryMeasured === true,
    disksMeasured: value.disksMeasured === true && value.disks.every(disk => disk.measured === true),
    networkMeasured: value.networkMeasured === true && value.networkInterfaces.every(item => item.ratesMeasured === true),
    disks: value.disks.map(disk => ({ ...disk, measured: disk.measured === true })),
    networkInterfaces: value.networkInterfaces.map(item => ({ ...item, ratesMeasured: item.ratesMeasured === true })) };
}
function measuredHistory(envelope: ApiEnvelope<SystemHistorySample[]>): SystemHistorySample[] {
  if (envelope?.success !== true || !Array.isArray(envelope.data) || !envelope.data.every(row => row && typeof row.timestampUtc === "string" &&
    percent(row.cpuPercent) && percent(row.memoryPercent) && nonnegative(row.networkDownBytesPerSec) && nonnegative(row.networkUpBytesPerSec) &&
    [row.cpuMeasured, row.memoryMeasured, row.networkMeasured].every(optionalFlag))) throw invalid();
  return envelope.data.map(row => ({ ...row, cpuMeasured: row.cpuMeasured === true, memoryMeasured: row.memoryMeasured === true, networkMeasured: row.networkMeasured === true }));
}
type RequestState = { requestScope: object };
type Requests = { epoch: number; scope: object; overview: number; history: number };
const requestsByStore = new WeakMap<object, Requests>();
function requestsFor(store: RequestState) {
  const key = toRaw(store), scope = toRaw(store.requestScope);
  let requests = requestsByStore.get(key);
  if (!requests || requests.epoch !== getSessionEpoch() || requests.scope !== scope) {
    requests = { epoch: getSessionEpoch(), scope, overview: 0, history: 0 }; requestsByStore.set(key, requests);
  }
  return requests;
}

export const useSystemStore = defineStore("system", {
  state: () => ({
    overview: null as SystemOverview | null,
    history: [] as SystemHistorySample[],
    loading: false,
    overviewConfirmed: false,
    overviewError: "",
    // Replaced by Pinia $reset; request metadata itself stays outside reactive state.
    requestScope: {}
  }),
  actions: {
    async fetchOverview() {
      const requests = requestsFor(this), id = ++requests.overview;
      const current = () => requestsFor(this) === requests && requests.overview === id;
      this.loading = true;

      try {
        const response = await apiClient.get<ApiEnvelope<SystemOverview>>("/system/overview");
        const value = measuredOverview(response.data);
        if (current()) { this.overview = value; this.overviewConfirmed = true; this.overviewError = ""; }
        return value;
      } catch (error) {
        if (current()) { this.overviewConfirmed = false; this.overviewError = extractApiMessage(error); }
        throw error;
      } finally {
        if (current()) this.loading = false;
      }
    },
    // Kalıcı izleme geçmişi (sunucu dakikada bir örnekler) → trend grafikleri sayfa yenilense de dolu gelir.
    async fetchHistory(canPublish: () => boolean = () => true) {
      const requests = requestsFor(this), id = ++requests.history;
      const response = await apiClient.get<ApiEnvelope<SystemHistorySample[]>>("/system/history");
      const value = measuredHistory(response.data);
      if (requestsFor(this) === requests && requests.history === id && canPublish()) this.history = value;
      return value;
    }
  }
});
