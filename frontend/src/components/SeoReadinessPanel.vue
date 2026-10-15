<template>
  <section class="readiness" :aria-label="t('panel.seo.readiness.title')">
    <h3>{{ t('panel.seo.readiness.title') }}</h3>
    <p class="meta">{{ t('panel.seo.readiness.scope') }}</p>
    <p v-if="!report.analysisVersion" class="notice" role="status">{{ t('panel.seo.readiness.rescan') }}</p>
    <template v-else>
      <p v-if="!measured.length" class="notice" role="status">{{ t('panel.seo.readiness.noMeasurement') }}</p>
      <div v-else class="readiness-grid">
        <article class="form-surface">
          <h4>SEO</h4>
          <p>{{ t('panel.seo.readiness.canonical', { count: withCanonical, total: measured.length }) }}</p>
          <p class="meta">{{ t('panel.seo.readiness.seoHint') }}</p>
        </article>
        <article class="form-surface">
          <h4>AEO · {{ t('panel.seo.readiness.answers') }}</h4>
          <p>{{ t('panel.seo.readiness.text', { count: withText, total: measured.length }) }}</p>
          <p class="meta">{{ t('panel.seo.readiness.aeoHint') }}</p>
        </article>
        <article class="form-surface">
          <h4>GEO · {{ t('panel.seo.readiness.generative') }}</h4>
          <p>{{ t('panel.seo.readiness.schema', { count: withJsonLd, total: measured.length }) }}</p>
          <p class="meta">{{ t('panel.seo.readiness.geoHint') }}</p>
        </article>
      </div>
      <h4>{{ t('panel.seo.readiness.botPolicy') }}</h4>
      <p class="meta">{{ t('panel.seo.readiness.botScope') }}</p>
      <p v-if="!report.robots.botAccess?.length" class="notice">{{ t('panel.seo.readiness.noBotMeasurement') }}</p>
      <div v-else class="table-scroll">
        <table>
          <thead><tr><th>{{ t('panel.seo.readiness.bot') }}</th><th>{{ t('panel.seo.readiness.purpose') }}</th><th>robots.txt</th><th>{{ t('panel.seo.readiness.group') }}</th></tr></thead>
          <tbody>
            <tr v-for="bot in report.robots.botAccess" :key="bot.agent">
              <td>{{ bot.agent }}</td><td>{{ t(`panel.seo.readiness.purpose.${bot.purpose}`) }}</td>
              <td><span :class="['status-pill', bot.status === 'allowed' ? 'ok' : 'warn']">{{ t(`panel.seo.readiness.access.${bot.status}`) }}</span></td>
              <td><code>{{ bot.matchedGroup || '—' }}</code></td>
            </tr>
          </tbody>
        </table>
      </div>
      <details v-if="measured.length">
        <summary>{{ t('panel.seo.readiness.evidence') }}</summary>
        <div v-for="page in measured" :key="page.path" class="form-surface page-evidence">
          <strong><code>{{ page.path }}</code></strong>
          <dl>
            <dt>Canonical</dt><dd><code>{{ page.contentSignals!.canonical || '—' }}</code></dd>
            <dt>{{ t('panel.seo.readiness.language') }}</dt><dd>{{ page.contentSignals!.language || '—' }}</dd>
            <dt>{{ t('panel.seo.readiness.structure') }}</dt><dd>{{ t('panel.seo.readiness.structureValue', { text: page.contentSignals!.textCharacters, headings: page.contentSignals!.sectionHeadingCount }) }}</dd>
            <dt>JSON-LD</dt><dd>{{ t('panel.seo.readiness.jsonValue', { count: page.contentSignals!.jsonLdBlocks, invalid: page.contentSignals!.invalidJsonLdBlocks }) }} · {{ page.contentSignals!.schemaTypes.join(', ') || '—' }}</dd>
            <dt>{{ t('panel.seo.readiness.otherMarkup') }}</dt><dd>{{ page.contentSignals!.otherStructuredData ? t('panel.seo.present') : '—' }}</dd>
            <dt>{{ t('panel.seo.readiness.directives') }}</dt><dd><code>{{ page.contentSignals!.robotDirectives.join('; ') || '—' }}</code></dd>
          </dl>
        </div>
      </details>
    </template>
    <details class="readiness-guide">
      <summary>{{ t('panel.seo.readiness.guide') }}</summary>
      <ol>
        <li>{{ t('panel.seo.readiness.stepSeo') }}</li>
        <li>{{ t('panel.seo.readiness.stepAeo') }}</li>
        <li>{{ t('panel.seo.readiness.stepGeo') }}</li>
        <li>{{ t('panel.seo.readiness.stepMeasure') }}</li>
      </ol>
      <p class="meta">{{ t('panel.seo.readiness.noSpecialFile') }}</p>
      <div class="row-actions">
        <a href="https://developers.google.com/search/docs/appearance/ai-features" target="_blank" rel="noopener noreferrer">Google · {{ t('panel.seo.readiness.officialGuide') }}</a>
        <a href="https://developers.openai.com/api/docs/bots" target="_blank" rel="noopener noreferrer">OpenAI · {{ t('panel.seo.readiness.botPolicy') }}</a>
        <a href="https://search.google.com/test/rich-results" target="_blank" rel="noopener noreferrer">Rich Results Test</a>
        <a href="https://pagespeed.web.dev/" target="_blank" rel="noopener noreferrer">PageSpeed Insights</a>
      </div>
    </details>
  </section>
</template>

<script setup lang="ts">
import { computed } from 'vue';
import { useI18n } from 'vue-i18n';
import type { SeoScanReport } from '../api/seo';
const props = defineProps<{ report: SeoScanReport }>();
const { t } = useI18n();
const measured = computed(() => props.report.pages.filter(page => page.statusCode >= 200 && page.statusCode < 300 && !page.error && page.contentSignals && !page.contentSignals.bodyTruncated));
const withCanonical = computed(() => measured.value.filter(page => page.contentSignals!.canonicalCount === 1 && /^https?:\/\//i.test(page.contentSignals!.canonical)).length);
const withText = computed(() => measured.value.filter(page => page.contentSignals!.textCharacters > 0).length);
const withJsonLd = computed(() => measured.value.filter(page => page.contentSignals!.jsonLdBlocks > 0 && !page.contentSignals!.invalidJsonLdBlocks && !page.contentSignals!.structuredDataLimited).length);
</script>

<style scoped>
.readiness { margin-block: 20px; }
.readiness-grid { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 12px; margin-block: 12px 20px; }
.readiness table { width: 100%; border-collapse: collapse; }
.readiness th, .readiness td { text-align: left; padding: 10px; border-bottom: 1px solid var(--panel-border); }
.table-scroll { overflow-x: auto; }
.readiness details { margin-top: 16px; }
.readiness summary { cursor: pointer; font-weight: 600; }
.page-evidence { margin-top: 10px; overflow-wrap: anywhere; }
.page-evidence dl { display: grid; grid-template-columns: minmax(100px, 1fr) minmax(0, 4fr); gap: 8px; }
.page-evidence dd { margin: 0; }
.readiness-guide li { margin-block: 10px; }
@media (max-width: 850px) { .readiness-grid { grid-template-columns: 1fr; } }
</style>
