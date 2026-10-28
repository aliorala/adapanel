import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { apiClient, invalidateSessionRequests } from './client';
import { getInstallationReporting, saveInstallationReporting } from './installationReporting';

const adapter = apiClient.defaults.adapter;
const state = { enabled: true, revision: 'saved', consentAtUtc: null, lastAttemptAtUtc: null, lastAcceptedAtUtc: null, nextAttemptAtUtc: null, lastError: '' };
let payload: unknown;
beforeEach(() => {
  invalidateSessionRequests(); payload = { success: true, data: state };
  vi.stubGlobal('window', { location: { pathname: '/panel' } });
  vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify({ success: true, data: { requestToken: 'fixture' } }), { status: 200 })));
  apiClient.defaults.adapter = async config => ({ config, data: payload, status: 200, statusText: 'OK', headers: {} });
});
afterEach(() => { apiClient.defaults.adapter = adapter; invalidateSessionRequests(); vi.unstubAllGlobals(); });
it.each(['get', 'save'])('rejects malformed delivery state from %s', async operation => {
  for (const invalid of [{ ...state, nextAttemptAtUtc: 'invalid' }, { ...state, lastError: false }, { ...state, lastAcceptedAtUtc: undefined }]) {
    payload = { success: true, data: invalid };
    await expect(operation === 'get' ? getInstallationReporting() : saveInstallationReporting({ enabled: true, revision: 'initial' })).rejects.toThrow();
  }
});
it('accepts preference persistence with a measured delivery failure without pretending it was accepted', async () => {
  payload = { success: true, data: { ...state, lastError: 'network', nextAttemptAtUtc: '2026-10-28T10:05:00Z' } };
  const result = await saveInstallationReporting({ enabled: true, revision: 'initial' });
  expect(result.lastAcceptedAtUtc).toBeNull(); expect(result.lastError).toBe('network');
});
