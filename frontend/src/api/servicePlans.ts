import { apiClient } from "./client";

type ApiEnvelope<T> = {
  success: boolean;
  data: T;
  message: string;
  code: number;
};

export type ServicePlan = {
  id: string;
  name: string;
  maxSites: number;
  maxDiskMb: number;
  maxDatabases: number;
  maxMailboxes: number;
  maxScheduledTasks: number;
};

export async function getServicePlans() {
  const response = await apiClient.get<ApiEnvelope<ServicePlan[]>>("/service-plans");
  return response.data.data;
}

export async function createServicePlan(payload: { name: string; maxSites: number; maxDiskMb: number; maxDatabases?: number; maxMailboxes?: number; maxScheduledTasks?: number }) {
  const response = await apiClient.post<ApiEnvelope<ServicePlan>>("/service-plans", payload);
  return response.data.data;
}

export async function deleteServicePlan(id: string) {
  await apiClient.delete(`/service-plans/${encodeURIComponent(id)}`);
}
