import { apiClient } from './client';

export type InstallationReporting = {
  enabled: boolean; revision: string;
  consentAtUtc: string | null; lastAttemptAtUtc: string | null;
  lastAcceptedAtUtc: string | null; nextAttemptAtUtc: string | null; lastError: string;
};

function read(payload: { success?: boolean; data?: InstallationReporting }): InstallationReporting {
  const value = payload?.data;
  if (payload?.success !== true || !value || typeof value.enabled !== 'boolean'
    || typeof value.revision !== 'string' || !value.revision || typeof value.lastError !== 'string'
    || ![value.consentAtUtc, value.lastAttemptAtUtc, value.lastAcceptedAtUtc, value.nextAttemptAtUtc]
      .every(date => date === null || (typeof date === 'string' && Number.isFinite(Date.parse(date)))))
    throw new Error('Invalid installation reporting response');
  return value;
}

export async function getInstallationReporting() {
  return read((await apiClient.get('/installation-reporting')).data);
}

export async function saveInstallationReporting(value: Pick<InstallationReporting, 'enabled' | 'revision'>) {
  return read((await apiClient.put('/installation-reporting', value)).data);
}
