type Translate = (key: string, values?: Record<string, string | number>) => string;

// Stored job results predate panel-language selection. Translate only complete,
// known summary formats; retain unrecognized diagnostics verbatim.
export function automationDetails(details: string, t: Translate): string {
  return details.split(/\r?\n/).map(line => {
    let match = /^(\d+) site, (\d+) veritabanı kopyası doğrulandı \(site arşivlerinde: (\d+), ayrı dökümlerde: (\d+)\), (\d+) eski yedek silindi\.$/.exec(line);
    if (match) return t('panel.automation.result.backupMeasured', {
      sites: match[1]!, databases: match[2]!, siteDatabases: match[3]!, separateDatabases: match[4]!, deleted: match[5]!
    });
    match = /^(\d+) site, (\d+) veritabanı yedeklendi, (\d+) eski yedek silindi\.$/.exec(line);
    if (match) return t('panel.automation.result.backup', { sites: match[1]!, databases: match[2]!, deleted: match[3]! });
    match = /^(mail|panel): yedeklendi$/.exec(line);
    if (match) return t(match[1] === 'mail' ? 'panel.automation.result.mail' : 'panel.automation.result.panel');
    match = /^(\d+) sertifika yenilendi\.(?: Mail sertifikası \(([^\r\n]+)\) güncellendi\.)?$/.exec(line);
    if (match) return t('panel.automation.result.ssl', { count: match[1]! }) + (match[2] ? ' ' + t('panel.automation.result.mailSsl', { host: match[2] }) : '');
    match = /^Mail sertifikası \(([^\r\n]+)\) güncellendi\.$/.exec(line);
    if (match) return t('panel.automation.result.mailSsl', { host: match[1]! });
    match = /^Certificate verification: verified=(\d+); missing=(\d+); unverified=(\d+); binding-drift=(\d+); bindings-measured=(true|false)\.$/.exec(line);
    if (match) return t('panel.automation.result.sslVerification', {
      verified: match[1]!, missing: match[2]!, unverified: match[3]!, drift: match[5] === 'false' ? '—' : match[4]!
    }) + (match[5] === 'false' ? ' ' + t('panel.automation.result.sslBindingsUnknown') : '');
    match = /^Mail certificate synchronization failed: (.+)$/.exec(line);
    if (match) {
      const reason = match[1]!;
      const known = /^(mail\.(?:certificate_not_activated|certificate_push_failed|certificate_files_missing|not_installed|not_configured))(?:: (.*))?$/.exec(reason);
      return t('panel.automation.result.mailSslFailed', {
        reason: known ? t(known[1]!) + (known[2] ? ': ' + known[2] : '') : reason
      });
    }
    match = /^Version checks failed: (.+)$/.exec(line);
    if (match) return t('panel.automation.result.failedChecks', { components: match[1]! });
    const [headline, unchecked, ...rest] = line.split(' | ');
    if (rest.length) return line;
    match = /^(\d+) update\(s\) installed, (\d+) pending: (.*)$/.exec(headline!);
    let translated = match ? t('panel.automation.result.updates', { installed: match[1]!, pending: match[2]!, components: match[3]! }) : headline === 'All checkable components are up to date.' ? t('panel.automation.result.upToDate') : headline === 'Component version checks could not be completed.' ? t('panel.automation.result.incompleteChecks') : null;
    if (translated === null) return line;
    if (unchecked) {
      const pending = /^Version cannot be checked automatically \((\d+)\): (.*)$/.exec(unchecked);
      if (!pending) return line;
      translated += ' | ' + t('panel.automation.result.unchecked', { count: pending[1]!, components: pending[2]! });
    }
    return translated;
  }).join('\n');
}
