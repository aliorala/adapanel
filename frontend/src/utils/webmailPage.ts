/** A new popup has no panel stylesheet; all translated text is escaped as plain text. */
export function webmailPage(language: string, title: string, hint: string, failed = false): string {
  const escape = (value: string) => value.replace(/[&<>"']/g, char => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[char]!));
  const locale = language.toLowerCase().startsWith('en') ? 'en' : 'tr';
  return `<!doctype html><html lang="${locale}"><head><meta charset="utf-8"><meta name="referrer" content="strict-origin"><meta name="viewport" content="width=device-width,initial-scale=1"><title>${escape(title)}</title></head><body style="margin:0;min-height:100vh;display:grid;place-items:center;font-family:Segoe UI,system-ui,sans-serif;background:#111820;color:#eef2f7"><main style="padding:24px;max-width:480px;text-align:center"><h1 style="font-size:1.2rem;color:${failed ? '#ff9a9a' : 'inherit'}">${escape(title)}</h1><p style="color:#aeb9c8;line-height:1.6">${escape(hint)}</p></main></body></html>`;
}
