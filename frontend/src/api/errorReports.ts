import { apiClient } from "./client";
import { UserFacingError } from "../utils/apiError";

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

const nullableText = (value: unknown) => value === null || typeof value === "string";
function unconfirmed(): never { throw new UserFacingError("panel.errorReports.resultUnconfirmed"); }
function measuredSettings(envelope: ApiEnvelope<ErrorReportingSettings>): ErrorReportingSettings {
  const value = envelope?.data;
  if (envelope?.success !== true || !value || typeof value.enabled !== "boolean" || typeof value.collectorUrl !== "string" || typeof value.lastError !== "string" ||
    !nullableText(value.consentGrantedAtUtc) || !nullableText(value.lastSentAtUtc) ||
    !Number.isSafeInteger(value.batchSize) || value.batchSize < 1 || value.batchSize > 50 ||
    !Number.isSafeInteger(value.retentionDays) || value.retentionDays < 1 || value.retentionDays > 365) unconfirmed();
  return value;
}

export async function getErrorReports(take = 50) {
  const response = await apiClient.get<ApiEnvelope<ErrorReport[]>>("/error-reports", { params: { take } });
  const value = response.data?.data;
  if (response.data?.success !== true || !Array.isArray(value) || value.some(item => !item ||
    !Number.isSafeInteger(item.id) || item.id < 1 || !Number.isSafeInteger(item.occurrenceCount) || item.occurrenceCount < 1 ||
    !nullableText(item.sentAtUtc) || [item.fingerprint, item.createdAtUtc, item.lastSeenAtUtc, item.panelVersion, item.osVersion, item.exceptionType,
      item.message, item.stackTrace, item.routePattern, item.correlationId].some(field => typeof field !== "string"))) unconfirmed();
  return value;
}

export async function getErrorReportingSettings() {
  const response = await apiClient.get<ApiEnvelope<ErrorReportingSettings>>("/error-reports/settings");
  return measuredSettings(response.data);
}

export async function saveErrorReportingSettings(payload: ErrorReportingSettings) {
  const response = await apiClient.put<ApiEnvelope<ErrorReportingSettings>>("/error-reports/settings", payload);
  return measuredSettings(response.data);
}

// Toplayıcıya erişim yavaş olabilir (sunucu tarafı 20 sn ile sınırlar); istemci onu aşan bir pay bırakır.
export async function sendPendingErrorReports() {
  const response = await apiClient.post<ApiEnvelope<ErrorReportSendResult>>("/error-reports/send", {}, { timeout: 60_000 });
  const result = response.data?.data;
  if (response.data?.success !== true || !result || !Number.isSafeInteger(result.sentCount) || result.sentCount < 0) unconfirmed();
  return result;
}
