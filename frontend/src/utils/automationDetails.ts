type Translate = (key: string, values?: Record<string, string | number>) => string;

// Stored job results predate panel-language selection. Translate only complete,
// known summary formats; retain unrecognized diagnostics verbatim.
export function automationDetails(details: string, t: Translate): string {
  return details.split(/\r?\n/).map(line => {
    let match = /^(\d+) site, (\d+) veritabanı yedeklendi, (\d+) eski yedek silindi\.$/.exec(line);
    if (match) return t('panel.automation.result.backup', { sites: match[1]!, databases: match[2]!, deleted: match[3]! });
    match = /^(mail|panel): yedeklendi$/.exec(line);
    if (match) return t(match[1] === 'mail' ? 'panel.automation.result.mail' : 'panel.automation.result.panel');
    match = /^(\d+) sertifika yenilendi\.(?: Mail sertifikası \(([^\r\n]+)\) güncellendi\.)?$/.exec(line);
    if (match) return t('panel.automation.result.ssl', { count: match[1]! }) + (match[2] ? ' ' + t('panel.automation.result.mailSsl', { host: match[2] }) : '');
    const [headline, unchecked, ...rest] = line.split(' | ');
    if (rest.length) return line;
    match = /^(\d+) update\(s\) installed, (\d+) pending: (.*)$/.exec(headline!);
    let translated = match ? t('panel.automation.result.updates', { installed: match[1]!, pending: match[2]!, components: match[3]! }) : headline === 'All checkable components are up to date.' ? t('panel.automation.result.upToDate') : null;
    if (translated === null) return line;
    if (unchecked) {
      const pending = /^Version cannot be checked automatically \((\d+)\): (.*)$/.exec(unchecked);
      if (!pending) return line;
      translated += ' | ' + t('panel.automation.result.unchecked', { count: pending[1]!, components: pending[2]! });
    }
    return translated;
  }).join('\n');
}
