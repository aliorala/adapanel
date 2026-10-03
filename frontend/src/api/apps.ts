import { apiClient } from "./client";
import { UserFacingError } from "../utils/apiError";

type ApiEnvelope<T> = {
  success: boolean;
  data: T;
  message: string;
  code: number;
};

export type AppRuntimeSiteConfiguration = {
  siteName: string;
  runtimeType: string;
  componentId: string;
  processPath: string;
  startupFile: string;
  workingDirectory: string;
  arguments: string;
  webConfigPath: string;
  logsDirectory: string;
  isEnabled: boolean;
  hasHttpPlatformHandler: boolean;
  managedRuntimeDisabled: boolean;
  environmentVariables: string[];
};

export type HostNodeAppPayload = {
  startupFile: string;
  workingDirectory: string;
  arguments: string;
  componentId: string;
  environmentVariables: string[];
};

export type HostPythonAppPayload = HostNodeAppPayload;

export type AppRuntimeInventoryItem = { componentId: string; isInstalled: boolean };

export async function getSiteAppRuntimeInventory(siteName: string): Promise<AppRuntimeInventoryItem[]> {
  const response = await apiClient.get<ApiEnvelope<AppRuntimeInventoryItem[]>>(`/sites/${encodeURIComponent(siteName)}/apps/runtimes`);
  const items = response.data?.data;
  // A successful scoped measurement covers both runtimes. Missing/malformed
  // rows must not turn an unknown server state into an installation decision.
  if (response.data?.success !== true || !Array.isArray(items) || items.length !== 2 ||
    !["nodejs-lts", "python-3"].every(id => items.filter(item => item?.componentId === id && typeof item.isInstalled === "boolean").length === 1)) {
    throw new UserFacingError("appruntime.inventory_unavailable");
  }
  return items;
}

export async function getSiteAppRuntimeConfiguration(siteName: string) {
  const response = await apiClient.get<ApiEnvelope<AppRuntimeSiteConfiguration>>(`/sites/${encodeURIComponent(siteName)}/apps`);
  return response.data.data;
}

export async function hostNodeApp(siteName: string, payload: HostNodeAppPayload) {
  const response = await apiClient.post<ApiEnvelope<AppRuntimeSiteConfiguration>>(`/sites/${encodeURIComponent(siteName)}/node`, payload);
  return response.data.data;
}

export async function hostPythonApp(siteName: string, payload: HostPythonAppPayload) {
  const response = await apiClient.post<ApiEnvelope<AppRuntimeSiteConfiguration>>(`/sites/${encodeURIComponent(siteName)}/python`, payload);
  return response.data.data;
}
