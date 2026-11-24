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
it('opens the named site instead of an earlier mail alias', () => {
  expect(primarySiteUrl([binding('https', 'mail.videotools.com.tr', 443), binding('https', 'videotools.com.tr', 443)], 'videotools.com.tr')).toBe('https://videotools.com.tr/');
});
it('keeps the primary HTTP host ahead of HTTPS on another alias', () => {
  expect(primarySiteUrl([binding('https', 'mail.videotools.com.tr', 443), binding('http', 'videotools.com.tr', 8080)], 'videotools.com.tr')).toBe('http://videotools.com.tr:8080/');
});
it('matches site names without case or trailing-dot differences and prefers HTTPS on that host', () => {
  expect(primarySiteUrl([binding('http', 'videotools.com.tr', 80), binding('https', 'mail.videotools.com.tr', 443), binding('https', 'videotools.com.tr', 443)], 'VIDEOTOOLS.COM.TR.')).toBe('https://videotools.com.tr/');
});
it('uses a bound www variant if the named host itself is not bound', () => {
  expect(primarySiteUrl([binding('https', 'mail.videotools.com.tr', 443), binding('https', 'www.videotools.com.tr', 443)], 'videotools.com.tr')).toBe('https://www.videotools.com.tr/');
});
it('does not invent a domain from an IIS display name', () => {
  expect(primarySiteUrl([binding('https', 'service.example', 443)], 'Customer Website')).toBe('https://service.example/');
});
it('does not replace a mail-only site with an unbound apex', () => {
  expect(primarySiteUrl([binding('https', 'mail.example', 443)], 'example')).toBe('https://mail.example/');
});
