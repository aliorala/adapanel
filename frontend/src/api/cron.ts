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
  lastTaskResult: number | null;
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
  // script tipi: doluysa "site betiği" modu (yol o sitenin kökünde kalmak zorunda), boşsa sunucu betiği.
  siteName?: string;
  // script tipi: "system" (varsayılan) veya "limited" (yönetici olmayan LOCAL SERVICE hesabı).
  runAs?: string;
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

// Görevi beklemeden hemen tetikler — yeni kurulan cron'u anında test etmek için.
export async function runScheduledTask(name: string) {
  await apiClient.post<ApiEnvelope<object>>(`/cron/${encodeURIComponent(name)}/run`);
}

export type ScheduledTaskStatus = {
  name: string;
  state: string;
  isRunning: boolean;
  lastTaskResult: number | null;
  lastRunTimeUtc: string | null;
};

// Tek görevin anlık durumu — "Şimdi çalıştır"dan sonra canlı bilgi vermek için kısa aralıklarla
// okunur. Tüm listeyi tazelemek yerine tek görevi sorar: liste turu her görevi gezen bir
// PowerShell çalıştırması demek.
export async function getScheduledTaskStatus(name: string) {
  const response = await apiClient.get<ApiEnvelope<ScheduledTaskStatus>>(
    `/cron/${encodeURIComponent(name)}/status`
  );
  return response.data.data;
}

// Takılı kalmış çalışmayı sonlandırır. Görev silinmez; bir sonraki zamanında normal çalışır.
export async function stopScheduledTask(name: string) {
  await apiClient.post<ApiEnvelope<object>>(`/cron/${encodeURIComponent(name)}/stop`);
}

// Görevi silmeden aç/kapat.
export async function setScheduledTaskEnabled(name: string, enabled: boolean) {
  await apiClient.post<ApiEnvelope<object>>(`/cron/${encodeURIComponent(name)}/${enabled ? "enable" : "disable"}`);
}
