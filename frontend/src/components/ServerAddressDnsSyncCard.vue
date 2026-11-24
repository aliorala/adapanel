<template>
  <section v-if="visible" class="dns-address-sync" :aria-busy="busy">
    <h3>{{ t("panel.settings.dnsSync.title") }}</h3>
    <p class="field-hint">{{ t("panel.settings.dnsSync.scope") }}</p>
    <div v-if="result" role="status" aria-live="polite">
      <p :class="{ 'warn-text': dnsSyncHasFailure(result) }">{{ t(result.pending ? "panel.settings.dnsSync.pending" : dnsSyncHasFailure(result) ? "panel.settings.dnsSync.partial" : result.skipped ? "panel.settings.dnsSync.unchanged" : "panel.settings.dnsSync.success") }}</p>
      <p>{{ result.oldAddress }} → {{ result.newAddress }}</p>
      <p v-if="result.error" class="warn-text">{{ result.error }}</p>
      <ul v-if="result.zones.length">
        <li v-for="zone in result.zones" :key="`${zone.siteName}/${zone.zoneName}`">
          <strong>{{ zone.zoneName }}</strong>: {{ t(zone.skipped ? "panel.settings.dnsSync.zoneSkipped" : zone.succeeded ? "panel.settings.dnsSync.zoneSuccess" : "panel.settings.dnsSync.zoneFailed", { count: zone.changedRecords }) }}
          <span v-if="zone.error" class="warn-text"> — {{ zone.error }}</span>
        </li>
      </ul>
      <p v-if="result.ftp">
        <strong>FTP</strong>: {{ t(result.ftp.skipped ? "panel.settings.dnsSync.ftpSkipped" : !result.ftp.succeeded ? "panel.settings.dnsSync.ftpFailed" : result.ftp.changed ? "panel.settings.dnsSync.ftpChanged" : "panel.settings.dnsSync.ftpCurrent") }}
        <span v-if="result.ftp.currentAddress"> ({{ result.ftp.currentAddress }})</span>
        <span v-if="result.ftp.error" class="warn-text"> — {{ result.ftp.error }}</span>
      </p>
    </div>
    <template v-if="dnsSyncHasFailure(result)">
    <label class="stacked-field">
      <span class="field-label">{{ t("panel.settings.dnsSync.oldAddress") }}</span>
      <input v-model="oldAddress" :disabled="busy" :placeholder="result?.oldAddress || ''" />
    </label>
    <p class="field-hint">{{ t("panel.settings.dnsSync.retryHint", { address: currentAddress }) }}</p>
    <button class="compact" :disabled="busy || !oldAddress.trim()" @click="$emit('retry', oldAddress.trim())">{{ t("panel.settings.dnsSync.retry") }}</button>
    </template>
  </section>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from "vue";
import { useI18n } from "vue-i18n";
import type { ServerAddressDnsSync } from "../api/panelSettings";
import { dnsSyncHasFailure } from "../composables/usePanelPreferencesMutation";
const props = defineProps<{ result?: ServerAddressDnsSync | null; currentAddress: string; busy: boolean; feedbackVersion?: number }>();
defineEmits<{ retry: [oldAddress: string] }>();
const { t } = useI18n();
const oldAddress = ref(props.result?.oldAddress || "");
watch(() => props.result?.oldAddress, value => { if (value) oldAddress.value = value; });
const showFreshResult = ref(false);
const visible = computed(() => dnsSyncHasFailure(props.result) || showFreshResult.value);
let feedbackTimer: ReturnType<typeof setTimeout> | undefined;
// Persisted results are history. Only an operation completed in this mounted
// screen can show a temporary success; reopening settings must not replay it.
watch(() => props.feedbackVersion, () => {
  clearTimeout(feedbackTimer);
  showFreshResult.value = !!props.result && !dnsSyncHasFailure(props.result);
  if (showFreshResult.value) feedbackTimer = setTimeout(() => { showFreshResult.value = false; }, 15_000);
});
onBeforeUnmount(() => clearTimeout(feedbackTimer));
</script>

<style scoped>
.dns-address-sync { margin-top: 1rem; padding: 1rem; border: 1px solid var(--panel-border); border-radius: 8px; overflow-wrap: anywhere; }
.dns-address-sync ul { padding-left: 1.25rem; }
.dns-address-sync .stacked-field { max-width: 28rem; margin-bottom: .5rem; }
</style>
