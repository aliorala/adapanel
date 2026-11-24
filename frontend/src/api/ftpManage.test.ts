import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { apiClient, invalidateSessionRequests } from './client';
import { setFtpUserEnabled, updateFtpUser } from './ftp';
import { UserFacingError } from '../utils/apiError';

const original = apiClient.defaults.adapter;
let body: unknown;
let lastRequest: { method?: string; url?: string; data?: unknown } = {};
const user = { userName: 'fixture_ftp', siteName: 'fixture.test', homePath: 'C:\sites\fixture.test\sub', isEnabled: true, existsOnSystem: true, readOnly: true, createdAtUtc: '2026-10-04T00:00:00Z' };
beforeEach(() => {
  invalidateSessionRequests(); vi.stubGlobal('window', { location: { pathname: '/panel' } });
  vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify({ success: true, data: { requestToken: 'fixture' } }), { status: 200 })));
  apiClient.defaults.adapter = async config => { lastRequest = { method: config.method, url: config.url, data: config.data }; return { config, status: 200, statusText: 'OK', headers: {}, data: body }; };
});
afterEach(() => { apiClient.defaults.adapter = original; invalidateSessionRequests(); vi.unstubAllGlobals(); });

it('PUTs home and read-only and accepts only the measured values', async () => {
  body = { success: true, data: user };
  await expect(updateFtpUser('fixture_ftp', { homePath: 'C:/sites/fixture.test/sub', readOnly: true })).resolves.toMatchObject({ readOnly: true });
  expect(lastRequest.method).toBe('put');
  expect(lastRequest.url).toBe('/ftp/users/fixture_ftp');
});
it('does not call a read-only change a success when the server measured writable', async () => {
  body = { success: true, data: { ...user, readOnly: false } };
  await expect(updateFtpUser('fixture_ftp', { readOnly: true })).rejects.toEqual(new UserFacingError('panel.ftp.resultUnconfirmed'));
});
it('does not accept another home than the requested one', async () => {
  body = { success: true, data: user };
  await expect(updateFtpUser('fixture_ftp', { homePath: 'C:/sites/fixture.test/other' })).rejects.toEqual(new UserFacingError('panel.ftp.resultUnconfirmed'));
});
it('enabled toggle posts the flag and rejects an unconfirmed state', async () => {
  body = { success: true, data: { userName: 'fixture_ftp', enabled: false } };
  await expect(setFtpUserEnabled('fixture_ftp', false)).resolves.toBeUndefined();
  expect(lastRequest.method).toBe('post');
  expect(lastRequest.url).toBe('/ftp/users/fixture_ftp/enabled');
  body = { success: true, data: { userName: 'fixture_ftp', enabled: true } };
  await expect(setFtpUserEnabled('fixture_ftp', false)).rejects.toEqual(new UserFacingError('panel.ftp.resultUnconfirmed'));
});
