import { apiClient } from "./client";
import { UserFacingError } from "../utils/apiError";

type ApiEnvelope<T> = {
  success: boolean;
  data: T;
  message: string;
  code: number;
};

export type NotificationSettings = {
  /** Opaque identity of the configuration measured by the server. */
  settingsRevision: string;
  smtpHost: string;
  smtpPort: number;
  smtpUserName: string;
  smtpPassword: string;
  hasSmtpPassword: boolean;
  fromEmail: string;
  toEmail: string;
  useSsl: boolean;
  webhookUrl: string;
  /** Webhook imza anahtarı, okumada MASKELİ (yalnız son 4 karakter); tam değer yalnız döndürme anında görünür. */
  webhookSecret?: string;
};

function measuredSettings(envelope: ApiEnvelope<NotificationSettings>): NotificationSettings {
  const value = envelope?.data;
  if (envelope?.success !== true || !value ||
    typeof value.settingsRevision !== "string" || !value.settingsRevision.trim() ||
    [value.smtpHost, value.smtpUserName, value.smtpPassword, value.fromEmail, value.toEmail, value.webhookUrl].some(item => typeof item !== "string") ||
    !Number.isSafeInteger(value.smtpPort) || value.smtpPort < 1 || value.smtpPort > 65535 ||
    typeof value.hasSmtpPassword !== "boolean" || typeof value.useSsl !== "boolean")
    throw new UserFacingError("panel.notifications.resultUnconfirmed");
  return value;
}

export async function getNotificationSettings() {
  const response = await apiClient.get<ApiEnvelope<NotificationSettings>>("/notify/settings");
  return measuredSettings(response.data);
}

export async function saveNotificationSettings(payload: Omit<NotificationSettings, "settingsRevision">) {
  const response = await apiClient.put<ApiEnvelope<NotificationSettings>>("/notify/settings", {
    smtpHost: payload.smtpHost, smtpPort: payload.smtpPort, smtpUserName: payload.smtpUserName, smtpPassword: payload.smtpPassword,
    fromEmail: payload.fromEmail, toEmail: payload.toEmail, useSsl: payload.useSsl, webhookUrl: payload.webhookUrl
  });
  return measuredSettings(response.data);
}

export type WebhookSecretRotation = { webhookSecret: string; masked: string };

/** Webhook imza anahtarını yeniden üretir. Yeni TAM değer yalnız bu yanıtta gelir. */
export async function rotateWebhookSecret(): Promise<WebhookSecretRotation> {
  const response = await apiClient.post<ApiEnvelope<WebhookSecretRotation>>("/notify/webhook-secret/rotate", {});
  const value = response.data?.data;
  if (response.data?.success !== true || !value || typeof value.webhookSecret !== "string" || !value.webhookSecret || typeof value.masked !== "string")
    throw new UserFacingError("panel.notifications.resultUnconfirmed");
  return value;
}

export async function sendNotificationTest(channel: string, message: string, expectedSettingsRevision: string) {
  if (typeof expectedSettingsRevision !== "string" || !expectedSettingsRevision.trim())
    throw new UserFacingError("notifications.settings_revision_required");
  const response = await apiClient.post<ApiEnvelope<{ channel: string }>>("/notify/test", {
    channel,
    message,
    expectedSettingsRevision
  });
  if (response.data?.success !== true || response.data.data?.channel !== channel)
    throw new UserFacingError("panel.notifications.resultUnconfirmed");
}
