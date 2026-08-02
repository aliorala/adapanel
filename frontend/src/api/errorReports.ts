import { apiClient } from "./client";

type ApiEnvelope<T> = {
  success: boolean;
  data: T;
  message: string;
  code: number;
  details?: string;
};

// Sunucuda tutulan MASKELENMİŞ hata kaydı. Buradaki her alan toplayıcıya gönderilebilir
// olmalıdır — arayüzdeki "ne gönderiliyor" listesi bu tiple aynı hikâyeyi anlatır.
export type ErrorReport = {
  id: number;
  fingerprint: string;
  createdAtUtc: string;
  lastSeenAtUtc: string;
  panelVersion: string;
  osVersion: string;
  exceptionType: string;
  message: string;
  stackTrace: string;
  routePattern: string;
  correlationId: string;
  occurrenceCount: number;
  sentAtUtc: string | null;
};

export type ErrorReportingSettings = {
  enabled: boolean;
  collectorUrl: string;
  consentGrantedAtUtc: string | null;
  batchSize: number;
  retentionDays: number;
  lastSentAtUtc: string | null;
  lastError: string;
};

export type ErrorReportSendResult = {
  sentCount: number;
};

export async function getErrorReports(take = 50) {
  const response = await apiClient.get<ApiEnvelope<ErrorReport[]>>("/error-reports", { params: { take } });
  return response.data.data;
}

export async function getErrorReportingSettings() {
  const response = await apiClient.get<ApiEnvelope<ErrorReportingSettings>>("/error-reports/settings");
  return response.data.data;
}

export async function saveErrorReportingSettings(payload: ErrorReportingSettings) {
  const response = await apiClient.put<ApiEnvelope<ErrorReportingSettings>>("/error-reports/settings", payload);
  return response.data.data;
}

// Toplayıcıya erişim yavaş olabilir (sunucu tarafı 20 sn ile sınırlar); istemci onu aşan bir pay bırakır.
export async function sendPendingErrorReports() {
  const response = await apiClient.post<ApiEnvelope<ErrorReportSendResult>>("/error-reports/send", {}, { timeout: 60_000 });
  return response.data.data;
}
