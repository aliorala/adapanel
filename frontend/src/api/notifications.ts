import { apiClient } from "./client";
import { UserFacingError } from "../utils/apiError";

type ApiEnvelope<T> = {
  success: boolean;
  data: T;
  message: string;
  code: number;
};

export type NotificationSettings = {
  smtpHost: string;
  smtpPort: number;
  smtpUserName: string;
  smtpPassword: string;
  hasSmtpPassword: boolean;
  fromEmail: string;
  toEmail: string;
  useSsl: boolean;
  webhookUrl: string;
};

function measuredSettings(envelope: ApiEnvelope<NotificationSettings>): NotificationSettings {
  const value = envelope?.data;
  if (envelope?.success !== true || !value ||
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

export async function saveNotificationSettings(payload: NotificationSettings) {
  const response = await apiClient.put<ApiEnvelope<NotificationSettings>>("/notify/settings", payload);
  return measuredSettings(response.data);
}

export async function sendNotificationTest(channel: string, message: string) {
  const response = await apiClient.post<ApiEnvelope<{ channel: string }>>("/notify/test", {
    channel,
    message
  });
  if (response.data?.success !== true || response.data.data?.channel !== channel)
    throw new UserFacingError("panel.notifications.resultUnconfirmed");
}
