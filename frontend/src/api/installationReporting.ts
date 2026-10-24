import { apiClient } from './client';

export type InstallationReporting = {
  enabled: boolean; location: string; datacenter: string; revision: string;
  consentAtUtc: string | null; lastAttemptAtUtc: string | null;
  lastAcceptedAtUtc: string | null; lastError: string;
};

function read(payload: { success?: boolean; data?: InstallationReporting }): InstallationReporting {
  const value = payload?.data;
  if (payload?.success !== true || !value || typeof value.enabled !== 'boolean'
    || typeof value.revision !== 'string' || !value.revision
    || typeof value.location !== 'string' || typeof value.datacenter !== 'string')
    throw new Error('Invalid installation reporting response');
  return value;
}

export async function getInstallationReporting() {
  return read((await apiClient.get('/installation-reporting')).data);
}

export async function saveInstallationReporting(value: Pick<InstallationReporting, 'enabled' | 'location' | 'datacenter' | 'revision'>) {
  return read((await apiClient.put('/installation-reporting', value)).data);
}
