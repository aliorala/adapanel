<template>
  <section :class="['update-status', `tone-${presentation.tone}`]" role="status" aria-live="polite">
    <div class="status-heading">
      <span class="status-symbol" aria-hidden="true">
        <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">
          <template v-if="presentation.icon === 'check'"><circle cx="12" cy="12" r="9" /><path d="m8 12 2.5 2.5L16 9" /></template>
          <template v-else-if="presentation.icon === 'download'"><path d="M12 3v12m-4-4 4 4 4-4M5 16v4h14v-4" /></template>
          <template v-else-if="presentation.icon === 'clock'"><circle cx="12" cy="12" r="9" /><path d="M12 7v5l3 2" /></template>
          <template v-else><path d="m12 3 10 18H2L12 3Z" /><path d="M12 9v5m0 3h.01" /></template>
        </svg>
      </span>
      <div class="status-copy">
        <h3>{{ t(`panel.update.state.${presentation.state}.title`) }}</h3>
        <p>{{ t(`panel.update.state.${presentation.state}.hint`) }}</p>
      </div>
    </div>
    <dl v-if="status" class="version-list">
      <div>
        <dt>{{ t("panel.update.current") }}</dt>
        <dd>{{ status.currentVersion || "—" }}</dd>
      </div>
      <div>
        <dt>{{ t(status.isCheckFailed ? "panel.update.lastKnownVersion" : "panel.update.latest") }}</dt>
        <dd>{{ status.latestVersion || "—" }}</dd>
      </div>
      <div v-if="status.isPackageReady && status.preparedVersion" class="prepared-version">
        <dt>{{ t("panel.update.preparedVersion") }}</dt>
        <dd>{{ status.preparedVersion }}</dd>
      </div>
    </dl>
    <p v-if="status?.isCheckFailed && presentation.state === 'ready'" class="check-warning">{{ t("panel.update.checkFailedPrepared") }}</p>
  </section>
</template>

<script setup lang="ts">
import { computed } from "vue";
import { useI18n } from "vue-i18n";
import type { UpdateStatus } from "../api/update";
import { describeUpdateState } from "../utils/updatePresentation";

const props = defineProps<{ status: UpdateStatus | null; confirmed: boolean; checking: boolean; phase?: string }>();
const { t } = useI18n();
const presentation = computed(() => describeUpdateState(props.status, props.confirmed, props.checking, props.phase));
</script>

<style scoped>
.update-status {
  --state-color: var(--accent-1);
  display: grid;
  gap: var(--sp-16);
  padding: clamp(16px, 2.4vw, 24px);
  border: 1px solid color-mix(in srgb, var(--state-color) 30%, var(--panel-border));
  border-radius: var(--radius-md);
  background: color-mix(in srgb, var(--state-color) 5%, var(--surface-1));
  min-width: 0;
}
.tone-ok { --state-color: var(--ok-1); }
.tone-warn { --state-color: var(--warn-1); }
.tone-error { --state-color: var(--danger-1); }
.status-heading { display: flex; gap: var(--sp-12); align-items: flex-start; }
.status-symbol {
  flex: 0 0 44px;
  display: grid;
  place-items: center;
  width: 44px;
  height: 44px;
  border: 1px solid color-mix(in srgb, var(--state-color) 28%, transparent);
  border-radius: 50%;
  background: color-mix(in srgb, var(--state-color) 9%, var(--surface-1));
  color: var(--text-strong);
}
.status-symbol svg { width: 25px; height: 25px; }
.status-copy { min-width: 0; }
.status-copy h3 { margin: 0 0 6px; font-size: clamp(1.08rem, 2vw, 1.28rem); color: var(--text-strong); }
.status-copy p { margin: 0; color: var(--text-muted); line-height: 1.6; max-width: 75ch; overflow-wrap: anywhere; }
.version-list { display: flex; flex-wrap: wrap; gap: var(--sp-16) 32px; margin: 0; padding-top: var(--sp-16); border-top: 1px solid var(--panel-border); }
.version-list > div { display: grid; gap: var(--sp-4); min-width: 100px; max-width: 100%; }
.version-list dt { color: var(--text-muted); font-size: var(--fs-xs); }
.version-list dd { margin: 0; color: var(--text-strong); font-size: 1.1rem; font-weight: 650; font-variant-numeric: tabular-nums; overflow-wrap: anywhere; }
.prepared-version { padding-left: var(--sp-16); border-left: 2px solid var(--state-color); }
.check-warning { margin: 0; padding-top: var(--sp-12); border-top: 1px solid var(--panel-border); color: var(--text-muted); line-height: 1.6; }
@media (max-width: 420px) {
  .status-heading { flex-direction: column; }
  .version-list { gap: var(--sp-12); }
  .version-list > div { min-width: 0; flex: 1 1 90px; }
  .prepared-version { padding-left: 0; border-left: 0; }
}
</style>
