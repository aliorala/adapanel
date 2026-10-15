import { describe, expect, it } from 'vitest';
import { createSSRApp } from 'vue';
import { renderToString } from 'vue/server-renderer';
import { createI18n } from 'vue-i18n';
import SeoReadinessPanel from './SeoReadinessPanel.vue';
import en from '../i18n/en.json';
import tr from '../i18n/tr.json';
import type { SeoContentSignals, SeoPage, SeoScanReport } from '../api/seo';

const signals = (patch: Partial<SeoContentSignals> = {}): SeoContentSignals => ({
  canonical: 'https://example.test/', canonicalCount: 1, language: 'en', titleCount: 1,
  sectionHeadingCount: 2, textCharacters: 500, jsonLdBlocks: 1, invalidJsonLdBlocks: 0,
  structuredDataLimited: false, otherStructuredData: false, schemaTypes: ['Article'],
  noSnippet: false, bodyTruncated: false, robotDirectives: [], ...patch,
});
const page = (contentSignals: SeoContentSignals | null, statusCode = 200): SeoPage => ({
  path: '/', statusCode, responseMs: 50, contentType: 'text/html', title: 'Example',
  metaDescription: 'Example', h1Count: 1, imageCount: 0, imagesWithoutAlt: 0,
  redirectDepth: 0, redirectTo: '', noIndex: false, linkedFrom: '', error: '', contentSignals,
});
const report = (patch: Partial<SeoScanReport> = {}): SeoScanReport => ({
  id: 'test', siteName: 'example.test', host: 'example.test', analysisVersion: 1,
  startedAtUtc: '', completedAtUtc: '', durationMs: 100, status: 'completed', pagesCrawled: 1,
  pageLimit: 25, limitReached: false, findings: [], pages: [page(signals())],
  robots: { found: true, statusCode: 200, disallowsAll: false, sitemapUrls: [], error: '', botAccess: [] },
  sitemap: { path: '/sitemap.xml', found: true, statusCode: 200, urlCount: 1, checkedCount: 1, unreachableUrls: [], externalUrls: [], error: '' },
  error: '', newFindingCount: 0, resolvedFindingCount: 0, comparedToId: '', ...patch,
});
async function render(value: SeoScanReport, locale = 'en') {
  const app = createSSRApp(SeoReadinessPanel, { report: value });
  app.use(createI18n({ legacy: false, locale, flatJson: true, messages: { en, tr } }));
  return renderToString(app);
}

describe('SEO/AEO/GEO evidence', () => {
  it('asks to rescan old reports without inventing positive measurements', async () => {
    const html = await render(report({ analysisVersion: undefined, pages: [page(null)] }));
    expect(html).toContain('historical report');
    expect(html).not.toContain('Pages with one absolute canonical URL:');
  });
  it('excludes failed, legacy and truncated pages from measured denominators', async () => {
    const html = await render(report({ pages: [page(signals()), page(signals(), 404), page(null), page(signals({ bodyTruncated: true }))] }));
    expect(html).toContain('Pages with one absolute canonical URL: 1/1');
    expect(html).toContain('Pages with text in the HTML body: 1/1');
  });
  it('does not count malformed, limited or missing JSON-LD as syntax checked', async () => {
    const html = await render(report({ pages: [page(signals({ invalidJsonLdBlocks: 1 })), page(signals({ structuredDataLimited: true })), page(signals({ jsonLdBlocks: 0 }))] }));
    expect(html).toContain('Pages with JSON-LD and no detected JSON syntax errors: 0/3');
  });
  it('shows absence of measurements instead of a zero-score or all-clear', async () => {
    const html = await render(report({ pages: [] }));
    expect(html).toContain('readiness could not be assessed');
    expect(html).toContain('Bot policy was not measured');
  });
  it('keeps training purpose distinct and renders unknown policy explicitly', async () => {
    const value = report();
    value.robots.botAccess = [{ agent: 'GPTBot', purpose: 'training', status: 'blocked', matchedGroup: 'GPTBot' }, { agent: 'OAI-SearchBot', purpose: 'search', status: 'unknown', matchedGroup: '' }];
    const html = await render(value);
    expect(html).toContain('Model training');
    expect(html).toContain('All blocked');
    expect(html).toContain('Unknown');
    expect(html).toContain('training permission is separate');
  });
  it('escapes untrusted canonical, schema type and directive values', async () => {
    const html = await render(report({ pages: [page(signals({ canonical: '<script>alert(1)</script>', schemaTypes: ['<img src=x onerror=alert(1)>'], robotDirectives: ['<script>bad</script>'] }))] }));
    expect(html).not.toContain('<script>');
    expect(html).not.toContain('<img src=x');
    expect(html).toContain('&lt;script&gt;');
  });
  it('renders Turkish and explains measurement limits', async () => {
    const html = await render(report(), 'tr');
    expect(html).toContain('Arama ve yapay zekâ için hazırlık');
    expect(html).toContain('JavaScript çalıştırmaz');
    expect(html).not.toContain('panel.seo.readiness.');
  });
});
