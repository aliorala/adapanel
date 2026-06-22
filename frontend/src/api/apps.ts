import { apiClient } from "./client";

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
