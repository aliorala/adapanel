<template>
  <section v-if="attempt.isFinished" :class="['last-attempt', { 'attempt-failed': attempt.isFailed, 'attempt-stale': !confirmed }]" aria-labelledby="last-attempt-title">
    <div class="attempt-heading">
      <span class="attempt-icon" aria-hidden="true">
        <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">
          <template v-if="attempt.isFailed"><path d="m12 3 10 18H2L12 3Z" /><path d="M12 9v5m0 3h.01" /></template>
          <template v-else><circle cx="12" cy="12" r="9" /><path d="m8 12 2.5 2.5L16 9" /></template>
        </svg>
      </span>
      <div class="attempt-copy">
        <h3 id="last-attempt-title">{{ t(confirmed ? 'panel.update.history.title' : 'panel.update.history.lastKnownTitle') }}</h3>
        <p class="attempt-result">{{ t(attempt.isFailed ? 'panel.update.history.failed' : 'panel.update.history.succeeded') }}</p>
      </div>
      <div v-if="attempt.updatedAtUtc" class="attempt-time">
        <span>{{ t('panel.update.history.recordedAt') }}</span>
        <time :datetime="attempt.updatedAtUtc">{{ formatDateTime(attempt.updatedAtUtc) }}</time>
      </div>
    </div>
    <p class="attempt-context">{{ t('panel.update.history.context') }}</p>
    <p v-if="!confirmed" class="attempt-context">{{ t('panel.update.history.stale') }}</p>
    <details v-if="attempt.isFailed" class="attempt-detail" open>
      <summary>{{ t('panel.update.history.recordedReason') }}</summary>
      <p>{{ reason }}</p>
    </details>
  </section>
</template>

<script setup lang="ts">
import { computed } from 'vue';
import { useI18n } from 'vue-i18n';
import type { UpdateAttempt } from '../api/update';
import { formatDateTime } from '../utils/datetime';

const props = defineProps<{ attempt: UpdateAttempt; confirmed: boolean }>();
const { t } = useI18n();
// This is a historical record. Do not infer the currently running version from it.
const reason = computed(() => props.attempt.installerExitCode === 5
  ? t('panel.update.history.aborted')
  : props.attempt.errorKey ? t(props.attempt.errorKey)
  : props.attempt.error || t('panel.update.history.noReason'));
</script>

<style scoped>
.last-attempt {
  --attempt-color: var(--ok-1);
  display: grid;
  gap: var(--sp-12);
  min-width: 0;
  padding: var(--sp-16);
  border: 1px solid var(--panel-border);
  border-left: 3px solid var(--attempt-color);
  border-radius: var(--radius-sm);
  background: var(--surface-1);
}
.attempt-failed { --attempt-color: var(--warn-1); }
.attempt-stale { border-left-style: dashed; }
.attempt-heading { display: flex; align-items: flex-start; gap: var(--sp-12); min-width: 0; flex-wrap: wrap; }
.attempt-icon { display: grid; place-items: center; flex: 0 0 36px; height: 36px; color: var(--text-strong); background: color-mix(in srgb, var(--attempt-color) 10%, var(--surface-1)); border-radius: 50%; }
.attempt-icon svg { width: 21px; height: 21px; }
.attempt-copy { flex: 1 1 180px; min-width: 0; }
.attempt-copy h3 { margin: 0 0 var(--sp-4); font-size: var(--fs-sm); font-weight: 500; color: var(--text-muted); }
.attempt-result { margin: 0; font-weight: 600; color: var(--text-strong); }
.attempt-time { display: grid; gap: var(--sp-4); color: var(--text-muted); font-size: var(--fs-xs); }
.attempt-time time { color: var(--text-strong); font-variant-numeric: tabular-nums; }
.attempt-context { margin: 0; color: var(--text-muted); font-size: var(--fs-sm); line-height: 1.6; }
.attempt-detail { min-width: 0; border-top: 1px solid var(--panel-border); padding-top: var(--sp-8); }
.attempt-detail summary { cursor: pointer; color: var(--text-strong); padding: var(--sp-4) 0; width: fit-content; }
.attempt-detail summary:focus-visible { outline: 2px solid var(--focus-ring); outline-offset: 3px; border-radius: var(--radius-sm); }
.attempt-detail p { margin: var(--sp-8) 0 0; color: var(--text-muted); line-height: 1.6; white-space: pre-wrap; }
.attempt-copy, .attempt-time, .attempt-context, .attempt-detail p { overflow-wrap: anywhere; }
@media (max-width: 420px) { .attempt-time { flex-basis: 100%; padding-top: var(--sp-8); border-top: 1px solid var(--panel-border); } }
</style>
