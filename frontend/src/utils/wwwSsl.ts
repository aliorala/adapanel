import type { SiteBinding } from "../api/sites";

/** Only the named, bound primary domain gets an automatic www companion. */
export function companionWwwHost(siteName: string, hostName: string, bindings: SiteBinding[], wildcard: boolean): string {
  const host = hostName.trim().toLowerCase();
  if (wildcard || host !== siteName.trim().toLowerCase() || host.startsWith("www.") ||
    /^(mail|smtp|imap|pop|pop3|webmail|ftp|autodiscover|autoconfig|ns\d*)\./.test(host) ||
    host.endsWith(".nip.io") || host.endsWith(".localhost") || /^\d+(?:\.\d+){3}$/.test(host) ||
    host.length > 249 || !host.includes(".") ||
    !host.split(".").every(label => /^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$/.test(label)) ||
    !bindings.some(binding => ["http", "https"].includes(binding.protocol.toLowerCase()) && binding.hostName.toLowerCase() === host)) return "";
  return `www.${host}`;
}
