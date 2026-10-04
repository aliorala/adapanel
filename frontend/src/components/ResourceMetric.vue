<template>
  <article class="resource-metric" :class="[kind, tone, { unmeasured: percent === null }]">
    <div class="resource-metric-heading"><span class="resource-metric-dot" aria-hidden="true"></span>{{ label }}</div>
    <div class="resource-metric-reading">
      <div class="resource-metric-ring" :style="{ '--progress': `${percent ?? 0}%` }" :role="percent === null ? 'img' : 'meter'" :aria-label="percent === null ? label + ': ' + t('panel.monitor.unmeasured') : label" :aria-valuemin="percent === null ? undefined : 0" :aria-valuemax="percent === null ? undefined : 100" :aria-valuenow="percent ?? undefined">
        <strong class="resource-metric-value">{{ percent === null ? '—' : `${percent.toFixed(1)}%` }}</strong>
      </div>
      <div class="resource-metric-details">
        <span>{{ detail }}</span>
        <span v-if="hint">{{ hint }}</span>
        <span v-if="tone" class="status-pill" :class="tone">{{ t(tone === 'danger' ? 'panel.common.severityCritical' : 'panel.common.severityWarning') }}</span>
        <span v-if="percent === null" class="measurement-label">{{ t('panel.monitor.unmeasured') }}</span>
      </div>
    </div>
    <slot />
  </article>
</template>

<script setup lang="ts">
import { computed } from 'vue';
import { useI18n } from 'vue-i18n';
const props = withDefaults(defineProps<{ label: string; value: number | null; detail: string; hint?: string; kind?: 'cpu' | 'memory' | 'disk' }>(), { kind: 'cpu' });
const { t } = useI18n();
const percent = computed(() => typeof props.value === 'number' && Number.isFinite(props.value) && props.value >= 0 && props.value <= 100 ? props.value : null);
const tone = computed(() => percent.value === null || percent.value < 80 ? '' : percent.value >= 90 ? 'danger' : 'warn');
</script>

<style scoped>
.resource-metric {
  --metric-accent: var(--resource-cpu);
  --metric-ink: var(--metric-accent);
  --progress: 0%;
  min-width: 0; display: grid; align-content: start; gap: 10px; padding: 14px; background: linear-gradient(120deg, color-mix(in srgb, var(--metric-accent) 8%, var(--surface-1)), var(--surface-1)); border: 1px solid color-mix(in srgb, var(--metric-accent) 22%, var(--panel-border)); border-radius: var(--radius-md); }
.resource-metric.memory { --metric-accent: var(--resource-memory); }
.resource-metric.disk { --metric-accent: var(--resource-disk); }
.resource-metric.warn { --metric-ink: var(--warn-1); }
.resource-metric.danger { --metric-ink: var(--danger-1); }
.resource-metric.unmeasured { --metric-accent: var(--text-muted); --metric-ink: var(--text-muted); border-style: dashed; }
.measurement-label { font-weight: 600; color: var(--text-soft); }
.resource-metric-heading { min-height: 20px; display: flex; align-items: center; gap: 7px; color: var(--text-soft); font-weight: 600; }
.resource-metric-dot { width: 6px; height: 6px; flex: 0 0 6px; border-radius: 50%; background: var(--metric-accent); }
.resource-metric-reading { display: flex; align-items: center; gap: 16px; min-width: 0; }
.resource-metric-ring { width: 78px; height: 78px; flex: 0 0 78px; border-radius: 50%; display: grid; place-items: center; background: conic-gradient(var(--metric-ink) var(--progress), var(--ring-track) 0); }
.resource-metric-value { width: 64px; height: 64px; display: grid; place-items: center; border-radius: 50%; background: var(--surface-1); color: var(--text-strong); font-size: 1.32rem; font-variant-numeric: tabular-nums; line-height: 1; letter-spacing: -0.04em; }
.resource-metric-details { min-width: 0; display: flex; flex-direction: column; align-items: flex-start; gap: 6px; color: var(--text-muted); font-size: var(--fs-xs); overflow-wrap: anywhere; }
@media (max-width: 700px) { .resource-metric { padding: 12px; } .resource-metric-reading { gap: 10px; } .resource-metric-ring { width: 68px; height: 68px; flex-basis: 68px; } .resource-metric-value { width: 56px; height: 56px; font-size: 1.14rem; } }
@media (max-width: 440px) { .resource-metric:not(.disk) .resource-metric-reading { flex-direction: column; align-items: flex-start; } .resource-metric:not(.disk) .resource-metric-details { gap: 4px; } }
</style>
