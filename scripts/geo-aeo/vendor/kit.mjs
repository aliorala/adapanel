import { mkdir, writeFile, rm } from 'node:fs/promises';
import path from 'node:path';

function assert(value, message) { if (!value) throw new Error(message); }
function nonempty(value) { return typeof value === 'string' && value.trim().length > 0; }
export function httpsUrl(value) {
  assert(nonempty(value), 'URL gerekli');
  const url = new URL(value);
  assert(url.protocol === 'https:' && !url.username && !url.password && !url.hash, 'Mutlak HTTPS URL gerekli; kimlik bilgisi ve fragment yasak');
  assert(!/[\\\u0000-\u0020]/u.test(value) && !/%(?:2e|2f|5c)/i.test(value) && !/(?:^|\/)\.{1,2}(?:\/|$)/.test(value), 'URL traversal veya güvensiz karakter');
  return url.href;
}
function isoDate(value) {
  const today = new Date().toLocaleDateString('sv-SE', {timeZone:'Europe/Istanbul'});
  return typeof value === 'string' && /^\d{4}-\d{2}-\d{2}$/.test(value) && !Number.isNaN(Date.parse(value)) && new Date(value).toISOString().slice(0, 10) === value && value <= today;
}
function validatePrivate(value) {
  assert(typeof value === 'string' && /^\/[A-Za-z0-9_./-]+$/.test(value) && !value.includes('//') && !value.split('/').some(p => p === '.' || p === '..'), 'privatePaths düz mutlak yol olmalı');
}
export function isPrivate(url, privatePaths) {
  const pathname = decodeURIComponent(new URL(url).pathname);
  return privatePaths.some(p => pathname === p.replace(/\/$/, '') || pathname.startsWith(p.endsWith('/') ? p : p + '/'));
}
export function validateConfig(c) {
  assert(c && typeof c === 'object', 'Config nesne olmalı');
  assert(c.factsVerified === true, 'Uygulama ve publisher bilgileri doğrulanmalı (factsVerified)');
  for (const key of ['title', 'description', 'shortAnswer', 'locale', 'updatedAt']) assert(nonempty(c[key]), `${key} gerekli`);
  assert(/^[a-z]{2,3}(?:-[A-Za-z0-9]{2,8})*$/.test(c.locale), 'Geçersiz locale');
  assert(isoDate(c.updatedAt), 'updatedAt gerçek ve gelecekte olmayan YYYY-MM-DD olmalı');
  const canonical = httpsUrl(c.canonical);
  for (const key of ['application', 'publisher']) {
    assert(c[key] && nonempty(c[key].name), `${key}.name gerekli`);
    httpsUrl(c[key].url);
  }
  assert(c.publisher.type === undefined || ['Person','Organization'].includes(c.publisher.type), 'publisher.type Person veya Organization olmalı');
  assert(Array.isArray(c.privatePaths), 'privatePaths açıkça dizi olarak belirtilmeli');
  c.privatePaths.forEach(validatePrivate);
  assert(!isPrivate(canonical, c.privatePaths), 'Canonical özel bir URL olamaz');
  assert(Array.isArray(c.faq) && Array.isArray(c.relatedLinks) && Array.isArray(c.publicUrls), 'faq, relatedLinks ve publicUrls dizileri gerekli');
  for (const faq of c.faq) assert(nonempty(faq.question) && nonempty(faq.answer), 'SSS soru/cevap gerekli');
  for (const link of c.relatedLinks) { assert(nonempty(link.label), 'Bağlantı etiketi gerekli'); httpsUrl(link.url); }
  const urls = new Set();
  for (const entry of c.publicUrls) {
    const url = httpsUrl(entry.url);
    assert(new URL(url).origin === new URL(canonical).origin, 'Sitemap aynı origin kullanmalı');
    assert(entry.updatedAt === undefined || isoDate(entry.updatedAt), 'Public URL updatedAt geçersiz');
    assert(!urls.has(url), 'Yinelenen public URL'); urls.add(url);
  }
  assert(urls.has(canonical), 'Canonical publicUrls içinde olmalı');
  assert(c.training === 'allow' || c.training === 'block', 'training açıkça allow veya block olmalı');
  assert(typeof c.experimentalLlmsTxt === 'boolean', 'experimentalLlmsTxt açık boolean olmalı');
  return c;
}
export function escapeHtml(value) { return String(value).replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c])); }
export function safeJson(value) { return JSON.stringify(value).replace(/[<>&\u2028\u2029]/g, c => '\\u' + c.charCodeAt(0).toString(16).padStart(4, '0')); }
export function render(c) {
  validateConfig(c);
  const h = escapeHtml;
  const canonical = httpsUrl(c.canonical);
  const publisherType=c.publisher.type ?? 'Organization';
  const publisherId=c.publisher.url + (publisherType === 'Person' ? '#person' : '#organization');
  const graph = [
    { '@type':publisherType, '@id':publisherId, name:c.publisher.name, url:c.publisher.url },
    { '@type':'SoftwareApplication', '@id':c.application.url + '#application', name:c.application.name, url:c.application.url, description:c.description, publisher:{'@id':publisherId} },
    { '@type':'WebPage', '@id':canonical + '#webpage', url:canonical, name:c.title, description:c.description, inLanguage:c.locale, dateModified:c.updatedAt, about:{'@id':c.application.url + '#application'} }
  ];
  const head = `<title>${h(c.title)}</title>\n<meta name="description" content="${h(c.description)}">\n<link rel="canonical" href="${h(canonical)}">\n<script type="application/ld+json">${safeJson({'@context':'https://schema.org','@graph':graph})}</script>\n`;
  const content = `<section lang="${h(c.locale)}" aria-label="Kısa cevap"><h1>${h(c.title)}</h1><p>${h(c.shortAnswer)}</p><p>${h(c.description)}</p><p>Uygulama: <a href="${h(c.application.url)}">${h(c.application.name)}</a></p><p>Yayıncı: <a href="${h(c.publisher.url)}">${h(c.publisher.name)}</a></p><p>Güncelleme: <time datetime="${h(c.updatedAt)}">${h(c.updatedAt)}</time></p></section>\n` +
    (c.faq.length ? `<section aria-label="Sık sorulan sorular"><h2>Sık sorulan sorular</h2>${c.faq.map(f => `<h3>${h(f.question)}</h3><p>${h(f.answer)}</p>`).join('')}</section>\n` : '') +
    (c.relatedLinks.length ? `<nav aria-label="İlgili bağlantılar">${c.relatedLinks.map(l => `<a href="${h(l.url)}">${h(l.label)}</a>`).join('\n')}</nav>\n` : '');
  const publicUrls = c.publicUrls.filter(e => !isPrivate(e.url, c.privatePaths));
  const sitemap = `<?xml version="1.0" encoding="UTF-8"?>\n<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">\n${publicUrls.map(e => `<url><loc>${h(httpsUrl(e.url))}</loc>${e.updatedAt === undefined ? '' : `<lastmod>${e.updatedAt}</lastmod>`}</url>`).join('\n')}\n</urlset>\n`;
  const disallows = c.privatePaths.flatMap(p => {
    const base=p.replace(/\/$/, '');
    return [`Disallow: ${base}$`, `Disallow: ${base}?`, `Disallow: ${base}/`];
  }).join('\n');
  const robots = '# CANDIDATE: mevcut robots.txt ile gözden geçirilerek birleştirin.\n' +
    ['*','OAI-SearchBot','PerplexityBot'].map(bot => `User-agent: ${bot}\n${disallows}\n`).join('\n') +
    `\nUser-agent: GPTBot\n${disallows}\n${c.training === 'block' ? 'Disallow: /' : '# Eğitim erişimi açık; özel yollar engelli.'}\n`;
  const files = {'head.html':head, 'content.html':content, 'sitemap.xml':sitemap, 'robots.candidate.txt':robots};
  if (c.experimentalLlmsTxt) files['llms.experimental.txt'] = `# ${c.application.name.replace(/[\r\n]/g,' ')}\n\n> Deneysel, keşif veya alıntı garantisi vermez.\n\n${c.shortAnswer.replace(/[\r\n]/g,' ')}\n\n${publicUrls.map(e => `- ${httpsUrl(e.url)}`).join('\n')}\n`;
  return files;
}
export async function build(c, outputDirectory) {
  const files = render(c);
  assert(nonempty(outputDirectory) && !outputDirectory.split(/[\\/]/).includes('..'), 'Çıktı yolunda traversal yasak');
  const target = path.resolve(outputDirectory);
  // mkdir recursive:false: existing directory, file, or symlink is never reused.
  await mkdir(target);
  try { for (const [name, body] of Object.entries(files)) await writeFile(path.join(target, name), body, {flag:'wx'}); }
  catch (error) { await rm(target, {recursive:true, force:true}); throw error; }
  return Object.keys(files);
}
