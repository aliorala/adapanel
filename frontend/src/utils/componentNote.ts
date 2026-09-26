const translatedComponents = new Set([
  'vc-redist-x64', 'mysql-8', 'pgadmin4', 'url-rewrite', 'iis-arr', 'nginx',
  'http-platform-handler', 'stalwart-cli', 'vandelay', 'stalwart-mail',
  'mailenable-standard', 'clamav', 'docker-engine'
]);

export function componentNote(item: { id: string; note: string }, t: (key: string) => string): string {
  return item.note && translatedComponents.has(item.id)
    ? t(`panel.software.componentNote.${item.id}`)
    : item.note;
}
