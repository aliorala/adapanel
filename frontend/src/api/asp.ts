import { apiClient } from "./client";

type ApiEnvelope<T> = {
  success: boolean;
  data: T;
  message: string;
  code: number;
};

export type AspSiteConfiguration = {
  siteName: string;
  applicationPoolName: string;
  classicAspEnabled: boolean;
  managedRuntimeVersion: string;
  managedPipelineMode: string;
  enable32BitAppOnWin64: boolean;
  hasManagedRuntime: boolean;
  hasPhpHandler: boolean;
  phpComponentId: string;
  phpHandlerName: string;
  isMixedHandlersEnabled: boolean;
};

export type UpdateClassicAspPayload = {
  enabled: boolean;
};

export type UpdateDotNetRuntimePayload = {
  managedRuntimeVersion: string;
  managedPipelineMode: string;
  enable32BitAppOnWin64: boolean;
};

export type EnableMixedHandlersPayload = {
  enableClassicAsp: boolean;
  managedRuntimeVersion: string;
  managedPipelineMode: string;
  enable32BitAppOnWin64: boolean;
  phpComponentId: string;
};

export async function getSiteAspConfiguration(siteName: string) {
  const response = await apiClient.get<ApiEnvelope<AspSiteConfiguration>>(`/sites/${encodeURIComponent(siteName)}/asp`);
  return response.data.data;
}

export async function updateClassicAsp(siteName: string, payload: UpdateClassicAspPayload) {
  const response = await apiClient.post<ApiEnvelope<AspSiteConfiguration>>(`/sites/${encodeURIComponent(siteName)}/asp/classic`, payload);
  return response.data.data;
}

export async function updateDotNetRuntime(siteName: string, payload: UpdateDotNetRuntimePayload) {
  const response = await apiClient.post<ApiEnvelope<AspSiteConfiguration>>(`/sites/${encodeURIComponent(siteName)}/dotnet`, payload);
  return response.data.data;
}

export async function enableMixedHandlers(siteName: string, payload: EnableMixedHandlersPayload) {
  const response = await apiClient.post<ApiEnvelope<AspSiteConfiguration>>(`/sites/${encodeURIComponent(siteName)}/mixed`, payload);
  return response.data.data;
}
