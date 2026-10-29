import { httpsUrl } from './kit.mjs';
function check(condition, message) { if (!condition) throw new Error(message); }
function text(value) { return typeof value === 'string' && value.trim().length > 0; }
export function summarize(plan, observations) {
  check(plan && Array.isArray(plan.questions) && plan.questions.length > 0, 'Sabit soru seti gerekli');
  check(Array.isArray(plan.engines) && plan.engines.length > 0 && plan.engines.every(text), 'Plan engines gerekli');
  check(new Set(plan.engines).size === plan.engines.length, 'Yinelenen engine');
  check(Number.isInteger(plan.repetitions) && plan.repetitions > 0, 'repetitions pozitif tamsayı olmalı');
  check(text(plan.locale) && /^[A-Z]{2}$/.test(plan.country), 'Plan locale ve ISO ülke gerekli');
  check(Array.isArray(plan.targetUrlPrefixes) && plan.targetUrlPrefixes.length > 0, 'Hedef kaynak targetUrlPrefixes gerekli');
  const prefixes=plan.targetUrlPrefixes.map(p => new URL(httpsUrl(p)));
  check(prefixes.every(p => !p.search), 'Hedef prefix query içeremez');
  const questions = new Set();
  for (const q of plan.questions) {
    check(text(q.id) && text(q.question) && !questions.has(q.id), 'Geçersiz veya yinelenen soru'); questions.add(q.id);
  }
  check(Array.isArray(observations), 'Gözlemler dizi olmalı');
  const seen = new Set(); let measured = 0, errors = 0, mentioned = 0, assessed = 0, correct = 0, cited = 0;
  for (const o of observations) {
    check(o && questions.has(o.questionId) && plan.engines.includes(o.engine), 'Gözlem plan dışında');
    check(o.locale === plan.locale && o.country === plan.country && text(o.model), 'Gözlem ülke/locale/model geçersiz');
    check(Number.isInteger(o.repetition) && o.repetition >= 1 && o.repetition <= plan.repetitions, 'Gözlem repetition plan dışında');
    check(text(o.measuredAt) && /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?Z$/.test(o.measuredAt) && !Number.isNaN(Date.parse(o.measuredAt)) && new Date(o.measuredAt).toISOString().slice(0,19) === o.measuredAt.slice(0,19) && Date.parse(o.measuredAt) <= Date.now(), 'measuredAt gerçek geçmiş UTC zaman olmalı');
    check(o.status === 'measured' || o.status === 'error', 'Gözlem status geçersiz');
    check(typeof o.mentioned === 'boolean' && (o.correctDescription === null || typeof o.correctDescription === 'boolean'), 'Gözlem boolean/null alanları geçersiz');
    check(Array.isArray(o.citedUrls) && o.citedUrls.every(u => { try { const x = new URL(u); return ['https:', 'http:'].includes(x.protocol) && !x.username && !x.password; } catch { return false; } }), 'citedUrls HTTP(S) dizi olmalı');
    check(text(o.evidenceRef), 'evidenceRef gerekli');
    const key = JSON.stringify([o.questionId, o.engine, o.locale, o.country, o.repetition]);
    check(!seen.has(key), 'Yinelenen gözlem: ' + key); seen.add(key);
    if (o.status === 'error') {
      check(!o.mentioned && o.correctDescription === null && o.citedUrls.length === 0, 'error kaydı sonuç iddia edemez'); errors++; continue;
    }
    measured++;
    if (o.mentioned) mentioned++;
    if (o.correctDescription !== null) { assessed++; if (o.correctDescription) correct++; }
    if (o.citedUrls.some(value => {
      const url=new URL(value);
      return prefixes.some(p => url.origin === p.origin && (url.pathname === p.pathname.replace(/\/$/, '') || url.pathname.startsWith(p.pathname.endsWith('/') ? p.pathname : p.pathname + '/')));
    })) cited++;
  }
  const planned = questions.size * plan.engines.length * plan.repetitions;
  const rate = (n, d) => d ? n / d : null;
  return { totalQuestions:questions.size, plannedObservations:planned, recordedObservations:seen.size,
    measured, error:errors, unobserved:planned - seen.size, coverage:rate(measured, planned),
    mentionedRate:rate(mentioned, measured), mentionedDenominator:measured,
    accuracy:rate(correct, assessed), accuracyAssessedDenominator:assessed,
    citationRate:rate(cited, measured), citationDenominator:measured };
}
export function parseNdjson(body) {
  return body.split(/\r?\n/).filter(line => line.trim()).map((line, i) => {
    try { return JSON.parse(line); } catch { throw new Error(`NDJSON satır ${i + 1} geçersiz`); }
  });
}
