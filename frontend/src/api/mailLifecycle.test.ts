import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { apiClient, invalidateSessionRequests } from './client';
import { getMailForward, rotateMailDkim, setMailAccountActive, setMailForward, updateMailAlias } from './mail';

const original = apiClient.defaults.adapter;
let body: unknown;
let lastRequest: { method?: string; url?: string; data?: unknown } = {};
beforeEach(() => {
  invalidateSessionRequests(); vi.stubGlobal('window', { location: { pathname: '/panel' } });
  vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify({ success: true, data: { requestToken: 'fixture' } }), { status: 200 })));
  apiClient.defaults.adapter = async config => { lastRequest = { method: config.method, url: config.url, data: config.data }; return { config, status: 200, statusText: 'OK', headers: {}, data: body }; };
});
afterEach(() => { apiClient.defaults.adapter = original; invalidateSessionRequests(); vi.unstubAllGlobals(); });

it('posts the suspend flag to the account endpoint', async () => {
  body = { success: true, data: { accounts: [] } };
  await setMailAccountActive('a@example.test', false);
  expect(lastRequest.method).toBe('post');
  expect(lastRequest.url).toBe('/mail/accounts/active');
  expect(JSON.parse(String(lastRequest.data))).toEqual({ address: 'a@example.test', active: false });
});
it('PUTs alias changes with only the given fields', async () => {
  body = { success: true, data: { aliases: [] } };
  await updateMailAlias('info@example.test', { active: false });
  expect(lastRequest.method).toBe('put');
  expect(lastRequest.url).toBe('/mail/aliases');
  expect(JSON.parse(String(lastRequest.data))).toEqual({ address: 'info@example.test', active: false });
});
it('reads and saves forwarding', async () => {
  body = { success: true, data: { address: 'a@example.test', enabled: true, target: 'b@other.test', keepCopy: false } };
  await expect(getMailForward('a@example.test')).resolves.toMatchObject({ keepCopy: false });
  await setMailForward({ address: 'a@example.test', enabled: true, target: 'b@other.test', keepCopy: false });
  expect(lastRequest.method).toBe('put');
  expect(lastRequest.url).toBe('/mail/accounts/forward');
});
it('rotates the DKIM key', async () => {
  body = { success: true, data: { domain: 'example.test', oldSelector: 'a', newSelector: 'r1', retireAfterUtc: '2026-12-01T00:00:00Z', status: 'rotated' } };
  await expect(rotateMailDkim('example.test')).resolves.toMatchObject({ newSelector: 'r1' });
  expect(lastRequest.url).toBe('/mail/dkim/rotate');
});
