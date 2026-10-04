<template>
  <section class="form-surface restore-result" :class="{ 'has-warnings': partial }" :aria-label="t('panel.backup.lastRestoreResult')">
    <header class="section-head">
      <div class="result-heading">
        <span class="result-icon" aria-hidden="true">
          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">
            <template v-if="partial"><path d="m12 3 10 18H2L12 3Z" /><path d="M12 9v5m0 3h.01" /></template>
            <template v-else><circle cx="12" cy="12" r="9" /><path d="m8 12 2.5 2.5L16 9" /></template>
          </svg>
        </span>
        <div>
          <h3>{{ t('panel.backup.lastRestoreResult') }}</h3>
          <p class="result-name">{{ result.name }}</p>
        </div>
      </div>
      <span class="status-pill" :class="partial ? 'warn' : 'ok'">{{ t(partial ? 'panel.backup.restorePartialBadge' : 'panel.backup.status.restored') }}</span>
    </header>

    <p class="result-summary">{{ t(partial ? 'panel.backup.restoredWithErrors' : 'panel.backup.restored') }}</p>
    <p class="meta">{{ result.source }} · {{ t('panel.backup.createdAt') }}: {{ formatDateTime(result.createdAtUtc) }} · {{ formatBytes(result.size, t) }}</p>
    <p v-if="refreshError" class="refresh-warning" role="alert">{{ refreshError }}</p>

    <div v-if="result.restoredPath || result.preservedPath" class="recovery-paths">
      <section v-if="result.restoredPath" class="recovery-path">
        <h4>{{ t('panel.backup.restoreDestinationTitle') }}</h4>
        <div class="path-line">
          <code>{{ result.restoredPath }}</code>
          <button type="button" class="secondary compact" :disabled="copying" :aria-label="t('panel.backup.copyRestoreDestination')" @click="copyPath(result.restoredPath)">{{ t('panel.backup.location.copy') }}</button>
        </div>
      </section>
      <section v-if="result.preservedPath" class="recovery-path">
        <h4>{{ t('panel.backup.preservedContentTitle') }}</h4>
        <p class="field-hint">{{ t('panel.backup.preservedContentHint') }}</p>
        <div class="path-line">
          <code>{{ result.preservedPath }}</code>
          <button type="button" class="secondary compact" :disabled="copying" :aria-label="t('panel.backup.copyPreservedContent')" @click="copyPath(result.preservedPath)">{{ t('panel.backup.location.copy') }}</button>
        </div>
      </section>
    </div>
    <p v-if="copyMessage" class="field-hint" role="status">{{ copyMessage }}</p>

    <details v-if="result.warnings.length" class="restore-warnings" open>
      <summary>{{ t('panel.backup.warningsBadge', { count: result.warnings.length }) }}</summary>
      <ul>
        <li v-for="(warning, index) in result.warnings" :key="index">
          <strong>{{ warningText(warning) }}</strong>
          <p v-if="warning.detail">{{ warning.detail }}</p>
        </li>
      </ul>
    </details>
  </section>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import type { BackupRecord, BackupRestoreWarning } from '../api/backup';
import { formatBytes } from '../utils/bytes';
import { formatDateTime } from '../utils/datetime';
import { copyToClipboard } from '../utils/clipboard';

const props = defineProps<{ result: BackupRecord; refreshError?: string }>();
const { t } = useI18n();
const partial = computed(() => props.result.status === 'restored_with_errors' || props.result.warnings.length > 0);
const copyMessage = ref(''), copying = ref(false);
let revision = 0;
watch(() => props.result, () => { revision++; copyMessage.value = ''; copying.value = false; });
onBeforeUnmount(() => { revision++; });
async function copyPath(path: string) {
  if (copying.value) return;
  const accepted = revision;
  copying.value = true;
  const copied = await copyToClipboard(path);
  if (accepted !== revision) return;
  copying.value = false;
  copyMessage.value = t(copied ? 'panel.common.copied' : 'panel.common.copyFailed');
}
function warningText(warning: BackupRestoreWarning) {
  const message = t(warning.messageKey, { target: warning.target });
  return warning.target && !message.includes(warning.target) ? `${warning.target}: ${message}` : message;
}
</script>

<style scoped>
.restore-result {
  --result-tone: var(--ok-1);
  display: grid;
  gap: var(--sp-12);
  min-width: 0;
  border-color: color-mix(in srgb, var(--result-tone) 35%, var(--panel-border));
  border-left: 3px solid var(--result-tone);
}
.restore-result.has-warnings { --result-tone: var(--warn-1); }
.restore-result h3, .restore-result h4, .restore-result p { margin: 0; }
.result-heading { display: flex; align-items: flex-start; gap: var(--sp-10); min-width: 0; }
.result-heading > div { min-width: 0; }
.result-icon { flex: 0 0 32px; color: var(--text-strong); }
.result-icon svg { width: 28px; height: 28px; }
.result-name { color: var(--text-muted); overflow-wrap: anywhere; padding-top: var(--sp-4); }
.result-summary { color: var(--text-strong); }
.restore-result > .meta { overflow-wrap: anywhere; }
.refresh-warning { padding: var(--sp-10) var(--sp-12); border: 1px solid color-mix(in srgb, var(--warn-1) 35%, var(--panel-border)); border-radius: var(--radius-sm); background: var(--warn-bg); color: var(--warn-text); overflow-wrap: anywhere; }
.recovery-paths { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 350px), 1fr)); gap: var(--sp-10); }
.recovery-path { display: grid; align-content: start; gap: var(--sp-8); padding: var(--sp-12); border: 1px solid var(--panel-border); border-radius: var(--radius-sm); background: var(--surface-0); min-width: 0; }
.path-line { display: flex; align-items: center; flex-wrap: wrap; gap: var(--sp-8); min-width: 0; }
.path-line code { flex: 1 1 220px; min-width: 0; font-size: var(--fs-xs); overflow-wrap: anywhere; user-select: all; }
.path-line button { flex: 0 0 auto; width: auto; }
.restore-warnings { border-top: 1px solid var(--panel-border); padding-top: var(--sp-12); min-width: 0; }
.restore-warnings summary { cursor: pointer; font-weight: 600; }
.restore-warnings ul { list-style: none; margin: var(--sp-10) 0 0; padding: 0; display: grid; gap: var(--sp-10); }
.restore-warnings li { border-left: 2px solid var(--result-tone); padding-left: var(--sp-10); overflow-wrap: anywhere; }
.restore-warnings p { color: var(--text-muted); padding-top: var(--sp-4); white-space: pre-wrap; }
</style>
