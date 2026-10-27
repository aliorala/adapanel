import type { SiteBinding } from '../api/sites';
import { toReachableUrl } from './url';

/** Prefer the named site's bound host, then HTTPS; preview domains are a last resort. */
export function primarySiteUrl(bindings: SiteBinding[], siteName = ''): string {
  const candidates = bindings.filter(binding => ['http', 'https'].includes(binding.protocol.toLowerCase()));
  const publicBindings = candidates.filter(binding => binding.hostName && !binding.hostName.toLowerCase().endsWith('.nip.io'));
  const pool = publicBindings.length ? publicBindings : candidates;
  // IIS binding order does not identify the primary domain: mail aliases can be first.
  // A site name is only a hint when it matches a real binding, never a URL to invent.
  const normalizeHost = (host: string) => host.trim().toLowerCase().replace(/\.$/, '');
  const namedHost = normalizeHost(siteName);
  const namedBindings = namedHost ? pool.filter(item => normalizeHost(item.hostName) === namedHost) : [];
  const wwwHost = namedHost.startsWith('www.') ? namedHost.slice(4) : `www.${namedHost}`;
  const wwwBindings = namedHost ? pool.filter(item => normalizeHost(item.hostName) === wwwHost) : [];
  const preferred = namedBindings.length ? namedBindings : wwwBindings.length ? wwwBindings : pool;
  const binding = preferred.find(item => item.protocol.toLowerCase() === 'https') ?? preferred[0];
  if (!binding) return '';
  const protocol = binding.protocol.toLowerCase();
  let host = binding.hostName || (['*', '0.0.0.0', '::', ''].includes(binding.ipAddress) ? window.location.hostname : binding.ipAddress);
  if (host.includes(':') && !host.startsWith('[')) host = `[${host}]`;
  const standard = protocol === 'https' ? binding.port === 443 : binding.port === 80;
  return toReachableUrl(`${protocol}://${host}${standard ? '' : `:${binding.port}`}`);
}
