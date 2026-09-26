import type { SiteBinding } from '../api/sites';
import { toReachableUrl } from './url';

/** Prefer the site's public HTTPS binding; preview domains are a last resort. */
export function primarySiteUrl(bindings: SiteBinding[]): string {
  const candidates = bindings.filter(binding => ['http', 'https'].includes(binding.protocol.toLowerCase()));
  const publicBindings = candidates.filter(binding => binding.hostName && !binding.hostName.toLowerCase().endsWith('.nip.io'));
  const pool = publicBindings.length ? publicBindings : candidates;
  const binding = pool.find(item => item.protocol.toLowerCase() === 'https') ?? pool[0];
  if (!binding) return '';
  const protocol = binding.protocol.toLowerCase();
  let host = binding.hostName || (['*', '0.0.0.0', '::', ''].includes(binding.ipAddress) ? window.location.hostname : binding.ipAddress);
  if (host.includes(':') && !host.startsWith('[')) host = `[${host}]`;
  const standard = protocol === 'https' ? binding.port === 443 : binding.port === 80;
  return toReachableUrl(`${protocol}://${host}${standard ? '' : `:${binding.port}`}`);
}
