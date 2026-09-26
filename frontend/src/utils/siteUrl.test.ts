import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { primarySiteUrl } from './siteUrl';
import type { SiteBinding } from '../api/sites';

const binding = (protocol: string, hostName: string, port: number, ipAddress = '*'): SiteBinding => ({ protocol, hostName, port, ipAddress, bindingInformation: `${ipAddress}:${port}:${hostName}` });
beforeEach(() => vi.stubGlobal('window', { location: { origin: 'https://panel.example:8890', hostname: 'panel.example' } }));
afterEach(() => vi.unstubAllGlobals());
it('uses public HTTPS even when HTTP and preview bindings come first', () => {
  expect(primarySiteUrl([binding('https', '1.2.3.4.nip.io', 443), binding('http', 'site.example', 80), binding('https', 'site.example', 443)])).toBe('https://site.example/');
});
it('preserves the real domain ahead of a preview HTTPS binding', () => {
  expect(primarySiteUrl([binding('https', '1.2.3.4.nip.io', 443), binding('http', 'site.example', 8080)])).toBe('http://site.example:8080/');
});
it('ignores FTP and handles an empty binding list', () => {
  expect(primarySiteUrl([binding('ftp', '', 21)])).toBe('');
  expect(primarySiteUrl([])).toBe('');
});
it('uses the panel hostname for wildcard and loopback bindings', () => {
  expect(primarySiteUrl([binding('HTTPS', '', 9443)])).toBe('https://panel.example:9443/');
  expect(primarySiteUrl([binding('https', '', 9443, '::1')])).toBe('https://panel.example:9443/');
});
it('retains explicit IPv6 bindings with valid URL brackets', () => {
  expect(primarySiteUrl([binding('http', '', 8080, '2001:db8::1')])).toBe('http://[2001:db8::1]:8080/');
});
