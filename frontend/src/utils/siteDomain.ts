import type { SiteRecord } from "../api/sites";

const normalize = (value: string) => value.trim().replace(/\.$/, "").toLowerCase();
const isRealDomain = (value: string) => value.includes(".") &&
  /^(?=.{1,253}$)(?:[a-z0-9](?:[a-z0-9-]*[a-z0-9])?\.)+[a-z0-9](?:[a-z0-9-]*[a-z0-9])?$/.test(value) &&
  !/^\d+(?:\.\d+){3}$/.test(value) && !value.endsWith(".nip.io") && !value.endsWith(".sslip.io");
const isServiceHost = (host: string) => ["mail.", "webmail.", "ns1.", "ns2.", "ftp.", "autodiscover.", "autoconfig."]
  .some(prefix => host.startsWith(prefix));

/** Match the backend SiteDomainSelection.WebDomain; IIS names are only hints. */
export function siteWebDomain(site?: Pick<SiteRecord, "name" | "bindings"> | null): string {
  if (!site) return "";
  const hosts = [...new Set(site.bindings.map(binding => normalize(binding.hostName ?? "")).filter(isRealDomain))];
  const name = normalize(site.name ?? "");
  if (isRealDomain(name) && (!hosts.length || hosts.includes(name) || hosts.includes(`www.${name}`))) return name;
  return hosts.sort((a, b) => Number(isServiceHost(a)) - Number(isServiceHost(b)) ||
    Number(a.startsWith("www.")) - Number(b.startsWith("www.")) || (a < b ? -1 : a > b ? 1 : 0))[0] ?? "";
}
