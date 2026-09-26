/** Windows service IDs are stable; display names can come from a different OS language. */
export function serviceLabel(service: { name: string; label: string }, t: (key: string) => string): string {
  const keys: Record<string, string> = {
    w3svc: 'panel.health.service.iisWeb',
    was: 'panel.health.service.iisActivation',
    ftpsvc: 'panel.health.service.ftp',
    dns: 'panel.health.service.dns'
  };
  const key = keys[service.name.toLowerCase()];
  if (key) return t(key);
  const components: Record<string, string> = { 'mariadb-11': 'MariaDB', 'stalwart-mail': 'Stalwart Mail Server', redis: 'Redis' };
  return components[service.label] ?? service.label;
}
