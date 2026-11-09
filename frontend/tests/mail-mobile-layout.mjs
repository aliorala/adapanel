// Real browser layout regression with representative markup and production CSS.
// This fixture does not verify live API data, Vue lifecycle, or production screenshots.
// Run with PLAYWRIGHT_MODULE pointing to an installed playwright ESM entry point.
// MAIL_VIEW_SOURCE may point to an isolated old source copy for mutation verification.
import assert from 'node:assert/strict';
import { mkdir, readFile } from 'node:fs/promises';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { resolve } from 'node:path';

const root = fileURLToPath(new URL('../', import.meta.url));
const source = await readFile(process.env.MAIL_VIEW_SOURCE || resolve(root, 'src/views/MailView.vue'), 'utf8');
const css = await readFile(resolve(root, 'src/styles/base.css'), 'utf8');
const scopedCss = source.match(/<style scoped>([\s\S]*?)<\/style>/)?.[1];
assert.ok(scopedCss, 'MailView scoped CSS is present');
const { chromium } = await import(process.env.PLAYWRIGHT_MODULE
  ? pathToFileURL(resolve(process.env.PLAYWRIGHT_MODULE)).href : 'playwright');
const browser = await chromium.launch(process.env.BROWSER_EXECUTABLE
  ? { executablePath: process.env.BROWSER_EXECUTABLE, headless: true }
  : { channel: 'msedge', headless: true });
const longAddress = 'operations-and-accounting@a-very-long-customer-domain.example.test';
const actions = '<div class="row-actions">' + ['Web postayı aç', 'Kota', 'Parolayı değiştir', 'Otomatik yanıtlayıcı', 'Sil']
  .map(text => `<button class="secondary compact row-button">${text}</button>`).join('') + '</div>';
function table(kind, cells) {
  return `<div class="data-table"><div class="data-row table-head ${kind}"><span>Başlık</span></div>
    <div class="data-row ${kind}">${cells}</div></div>`;
}
const mailbox = status => table('mail-row', `<span class="select-cell"><input type="checkbox" aria-label="Select mailbox"></span>
  <strong>${longAddress}</strong><span data-label="Kota">0 / 1024 MiB</span><span class="status-pill warn">${status}</span>${actions}`);
const full = mailbox('Ölçülemedi') + mailbox('Etkin') + mailbox('Devre dışı')
  + table('maildomain-row', `<strong>${longAddress}</strong><span data-label="Posta kutuları">Ölçülemedi</span><span class="status-pill warn">Posta kapalı</span><button class="compact">Yönet</button>`)
  + table('alias-row', `<strong>${longAddress}</strong><span data-label="Hedef">${longAddress}</span><button class="compact">Sil</button>`)
  + table('list-row', `<div class="list-identity"><strong>${longAddress}</strong><small class="list-policy policy-open">Herkes gönderebilir</small></div>
    <div class="list-subscribers" data-label="Aboneler"><span class="subscriber-chip">${longAddress}<button class="chip-remove">×</button></span></div>${actions}`)
  + table('client-row', '<strong>IMAP</strong><span>mail.example.test</span><span>993</span><span>TLS</span>')
  + table('dns-row', `<span>TXT</span><strong>@</strong><code>${'long-dns-token'.repeat(40)}</code><button>Kopyala</button>`)
  + `<div class="form-surface"><div class="address-field"><input placeholder="info"><span>@${longAddress}</span></div></div>`;

try {
  for (const width of [390, 760, 1280]) {
    const page = await browser.newPage({ viewport: { width, height: 900 } });
    for (const [state, content] of Object.entries({ full, loading: '<div class="empty-state mail-loading" role="status">Posta yükleniyor</div>',
      empty: '<div class="data-table"><p class="empty-state">Posta kutusu yok</p></div>',
      error: '<p class="notice error" role="alert">Posta okunamadı</p><details class="notice-details" open><summary>Ayrıntılar</summary><pre>Server unavailable</pre></details>' })) {
      await page.setContent(`<!doctype html><html><head><meta name="viewport" content="width=device-width, initial-scale=1"><style>${css}\n${scopedCss}</style></head>
        <body><main style="padding:16px"><section class="panel module-shell mail-panel">${content}</section></main></body></html>`);
      assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth), true, `${width}px ${state}: document must not overflow`);
      if (state === 'full' && width <= 760) {
        for (const kind of ['mail-row', 'maildomain-row', 'alias-row', 'list-row']) {
          const row = page.locator(`.${kind}:not(.table-head)`).first();
          assert.equal(await row.evaluate(el => el.scrollWidth <= el.clientWidth), true, `${width}px: ${kind} must fit its card`);
          assert.equal(await page.locator(`.${kind}.table-head`).first().evaluate(el => el.getBoundingClientRect().height > 0), false, `${width}px: card headers are replaced with field labels`);
        }
        const statuses = await page.locator('.mail-row .status-pill').evaluateAll(els => els.map(el => {
          const range = document.createRange(); range.selectNodeContents(el);
          return range.getClientRects().length;
        }));
        assert.deepEqual(statuses, [1, 1, 1], `${width}px: unknown, enabled and disabled statuses must remain readable on one line`);
        for (const button of await page.locator('.mail-row button').all()) {
          assert.equal(await button.evaluate(el => {
            const rect = el.getBoundingClientRect(); return rect.left >= 0 && rect.right <= document.documentElement.clientWidth;
          }), true, `${width}px: mailbox actions must be reachable without horizontal page scrolling`);
        }
        assert.equal(await page.locator('.list-policy.policy-open').evaluate(el => el.getBoundingClientRect().height > 0), true, 'open posting policy stays visible');
        assert.equal(await page.locator('.client-row').last().evaluate(el => el.closest('.data-table').scrollWidth > el.closest('.data-table').clientWidth), true,
          'advanced table keeps local horizontal scrolling');
      }
      if (state === 'full' && width === 1280) {
        assert.equal(await page.locator('.mail-row.table-head').first().evaluate(el => el.getBoundingClientRect().height > 0), true, 'desktop table headers remain visible');
        assert.equal(await page.locator('.mail-row').last().evaluate(el => getComputedStyle(el).gridTemplateColumns.split(' ').length), 5,
          'desktop retains its five columns');
      }
      if (state === 'error') assert.equal(await page.getByRole('alert').evaluate(el => el.getBoundingClientRect().height > 0), true, 'errors remain visible');
      if (state === 'full' && process.env.MAIL_LAYOUT_ARTIFACT_DIR) {
        await mkdir(process.env.MAIL_LAYOUT_ARTIFACT_DIR, { recursive: true });
        await page.screenshot({ path: resolve(process.env.MAIL_LAYOUT_ARTIFACT_DIR, `fixture-${width}.png`), fullPage: true });
      }
      console.log(`PASS ${width}px ${state}`);
    }
    await page.close();
  }
} finally { await browser.close(); }
