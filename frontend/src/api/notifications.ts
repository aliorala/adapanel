import { apiClient } from "./client";

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

export async function getNotificationSettings() {
  const response = await apiClient.get<ApiEnvelope<NotificationSettings>>("/notify/settings");
  return response.data.data;
}

export async function saveNotificationSettings(payload: NotificationSettings) {
  const response = await apiClient.put<ApiEnvelope<NotificationSettings>>("/notify/settings", payload);
  return response.data.data;
}

export async function sendNotificationTest(channel: string, message: string) {
  await apiClient.post<ApiEnvelope<object>>("/notify/test", {
    channel,
    message
  });
}
