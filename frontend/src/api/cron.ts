import { apiClient } from "./client";

type ApiEnvelope<T> = {
  success: boolean;
  data: T;
  message: string;
  code: number;
};

export type ScheduledTask = {
  name: string;
  taskType: string;
  url: string;
  scheduleType: string;
  state: string;
  command: string;
  arguments: string;
  workingDirectory: string;
  nextRunTimeUtc: string | null;
  lastRunTimeUtc: string | null;
};

export async function getScheduledTasks() {
  const response = await apiClient.get<ApiEnvelope<ScheduledTask[]>>("/cron");
  return response.data.data;
}

export async function createScheduledTask(payload: {
  name: string;
  taskType: string;
  command: string;
  arguments: string;
  workingDirectory: string;
  url: string;
  phpVersion: string;
  scheduleType: string;
  interval: number;
  timeOfDay: string;
}) {
  const response = await apiClient.post<ApiEnvelope<ScheduledTask>>("/cron", payload);
  return response.data.data;
}

export async function deleteScheduledTask(name: string) {
  await apiClient.delete<ApiEnvelope<object>>(`/cron/${encodeURIComponent(name)}`);
}

// Görevi beklemeden hemen tetikler (Plesk "Run Now") — yeni kurulan cron'u anında test etmek için.
export async function runScheduledTask(name: string) {
  await apiClient.post<ApiEnvelope<object>>(`/cron/${encodeURIComponent(name)}/run`);
}
