<template>
  <article class="resource-metric" :class="tone">
    <div class="resource-metric-heading">
      <span>{{ label }}</span>
    </div>
    <div class="resource-metric-reading">
      <strong class="resource-metric-value">{{ percent === null ? t('panel.common.notAvailable') : `${percent.toFixed(1)}%` }}</strong>
      <span v-if="tone" class="status-pill" :class="tone">{{ t(tone === 'danger' ? 'panel.common.severityCritical' : 'panel.common.severityWarning') }}</span>
    </div>
    <div class="resource-metric-track" :role="percent === null ? 'img' : 'meter'" :aria-label="percent === null ? label + ': ' + t('panel.common.notAvailable') : label" :aria-valuemin="percent === null ? undefined : 0" :aria-valuemax="percent === null ? undefined : 100" :aria-valuenow="percent ?? undefined">
      <span :style="{ width: `${percent ?? 0}%` }"></span>
    </div>
    <div class="resource-metric-details"><span>{{ detail }}</span><span v-if="hint">{{ hint }}</span></div>
    <slot />
  </article>
</template>

<script setup lang="ts">
import { computed } from 'vue';
import { useI18n } from 'vue-i18n';
const props = defineProps<{ label: string; value: number | null; detail: string; hint?: string }>();
const { t } = useI18n();
const percent = computed(() => typeof props.value === 'number' && Number.isFinite(props.value) ? Math.max(0, Math.min(100, props.value)) : null);
const tone = computed(() => percent.value === null || percent.value < 80 ? '' : percent.value >= 90 ? 'danger' : 'warn');
</script>

<style scoped>
.resource-metric { min-width: 0; display: grid; align-content: start; gap: 8px; padding: 14px; background: var(--surface-1); border: 1px solid var(--panel-border); border-radius: var(--radius-md); }
.resource-metric-heading { min-height: 24px; display: flex; justify-content: space-between; align-items: center; flex-wrap: wrap; gap: 6px; color: var(--text-soft); font-weight: 600; }
.resource-metric-reading { display: flex; align-items: center; justify-content: space-between; gap: 4px; flex-wrap: wrap; }
.resource-metric-value { font-size: clamp(1.65rem, 2.4vw, 2.15rem); font-variant-numeric: tabular-nums; line-height: 1.1; letter-spacing: -0.03em; }
.resource-metric-track { height: 5px; border-radius: 8px; overflow: hidden; background: var(--ring-track); }
.resource-metric-track span { display: block; height: 100%; border-radius: inherit; background: var(--accent-1); transition: width .25s ease; }
.warn .resource-metric-track span { background: var(--warn-1); }
.danger .resource-metric-track span { background: var(--danger-1); }
.resource-metric-details { display: flex; flex-wrap: wrap; justify-content: space-between; gap: 4px 12px; color: var(--text-muted); font-size: var(--fs-xs); overflow-wrap: anywhere; }
@media (max-width: 700px) { .resource-metric { padding: 12px; } }
@media (prefers-reduced-motion: reduce) { .resource-metric-track span { transition: none; } }
</style>
