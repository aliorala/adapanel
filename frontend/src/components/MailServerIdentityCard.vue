<template>
  <section v-if="isAdmin" class="form-surface mail-identity-card" :aria-busy="busy || loading">
    <div class="section-head">
      <div>
        <h3>{{ t("panel.mail.identityTitle") }}</h3>
        <p class="meta">{{ t("panel.mail.identityHint") }}</p>
      </div>
      <button type="button" class="secondary compact" :disabled="busy || loading" @click="load">
        {{ t("panel.common.refresh") }}
      </button>
    </div>
    <SectionLoadError v-if="loadError.message.value" :message="loadError.message.value" :busy="busy || loading" @retry="load" />
    <p v-if="notice" :class="['notice', { error: noticeError }]" :role="noticeError ? 'alert' : 'status'">{{ notice }}</p>
    <p v-if="busy" class="notice" role="status">{{ t("panel.mail.identityWorking") }}</p>
    <template v-if="identity">
      <dl class="identity-state">
        <div><dt>{{ t("panel.mail.identityPinned") }}</dt><dd>{{ identity.pinned ? identity.configuredHostName : t("panel.mail.identityUnpinned") }}</dd></div>
        <div><dt>{{ t("panel.mail.identityCurrent") }}</dt><dd>{{ identity.currentHostName || t("panel.mail.identityUnknown") }}</dd></div>
      </dl>
      <p :class="['status-pill', verified ? 'ok' : 'warn']">{{ verified ? t("panel.mail.identityVerified") : t(!identity.pinned && !identity.steps.length ? "panel.mail.identityNotSelected" : "panel.mail.identityPending") }}</p>
      <p v-if="!identity.supported" class="empty-state">{{ t("panel.mail.identityUnsupported") }}</p>
      <form v-else @submit.prevent="save">
        <div class="form-grid">
          <label class="stacked-field">
            <span class="field-label">{{ t("panel.mail.identityDomain") }}</span>
            <select v-model="form.domain" required :disabled="busy || loading">
              <option value="" disabled>{{ t("panel.mail.identityChooseDomain") }}</option>
              <option v-for="domain in domainOptions" :key="domain" :value="domain">{{ domain }}</option>
            </select>
          </label>
          <label class="stacked-field">
            <span class="field-label">{{ t("panel.mail.identityHost") }}</span>
            <input v-model.trim="form.hostName" required maxlength="253" autocomplete="off" placeholder="mail.example.com" :disabled="busy || loading" />
            <small class="field-hint">{{ t("panel.mail.identityHostHint") }}</small>
          </label>
          <label class="stacked-field">
            <span class="field-label">{{ t("panel.mail.identityEmail") }}</span>
            <input v-model.trim="form.contactEmail" type="email" required maxlength="255" autocomplete="email" :disabled="busy || loading" />
          </label>
        </div>
        <div class="row-actions"><button type="submit" class="compact" :disabled="busy || loading">{{ busy ? t("panel.common.saving") : t("panel.common.save") }}</button></div>
      </form>
      <ol v-if="identity.steps.length" class="identity-steps">
        <li v-for="(step, index) in identity.steps" :key="`${index}-${step.key}`">
          <strong>{{ stepLabel(step.key) }}</strong> — {{ statusLabel(step.status) }}<p>{{ serverText(step.message) }}</p>
        </li>
      </ol>
    </template>
  </section>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref } from "vue";
import { useI18n } from "vue-i18n";
import { getMailServerIdentity, setMailServerIdentity, type MailServerIdentity } from "../api/mailIdentity";
import { useNotice, useSectionError, useServerText } from "../composables/useNotice";
import SectionLoadError from "./SectionLoadError.vue";

const props = defineProps<{ isAdmin: boolean; domains: { name: string }[] }>();
const { t } = useI18n();
const { notice, noticeError, showNotice, showError } = useNotice();
const loadError = useSectionError();
const { serverText } = useServerText();
const identity = ref<MailServerIdentity | null>(null);
const busy = ref(false);
const loading = ref(false);
const form = reactive({ domain: "", hostName: "", contactEmail: "" });
let initialized = false;
const verified = computed(() => identity.value?.applied === true && identity.value.pinned === true);
// Retain a saved domain even if the available site list changes. Never derive a hostname from a selected domain.
const domainOptions = computed(() => [...new Set([...props.domains.map(domain => domain.name), form.domain].filter(Boolean))]);

function stepLabel(key: string) {
  const labels: Record<string, string> = {
    dns: "panel.mail.identityStepDns", "dns-preparation": "panel.mail.identityStepDns",
    certificate: "panel.mail.identityStepCertificate", identity: "panel.mail.identityStepIdentity",
    rollback: "panel.mail.identityStepRollback", provider: "panel.mail.identityStepProvider"
  };
  return t(labels[key] ?? "panel.mail.identityStepUnknown");
}
function statusLabel(status: string) {
  const labels: Record<string, string> = {
    ok: "panel.mail.identityStatusOk", "action-required": "panel.mail.identityStatusAction",
    fail: "panel.mail.identityStatusFailed", failed: "panel.mail.identityStatusFailed",
    unsupported: "panel.mail.identityStatusUnsupported", pending: "panel.mail.identityStatusPending"
  };
  return t(labels[status] ?? "panel.mail.identityStatusUnknown");
}
onMounted(load);
async function load() {
  if (!props.isAdmin || busy.value || loading.value) return;
  loading.value = true;
  try {
    const current = await getMailServerIdentity();
    if (!props.isAdmin) return;
    identity.value = current;
    // Refresh updates measured state without silently discarding the administrator's draft.
    if (!initialized) {
      form.domain = current.domain;
      form.hostName = current.configuredHostName;
      form.contactEmail = current.contactEmail ?? "";
      initialized = true;
    }
    loadError.clear();
  } catch (error) {
    loadError.capture(error);
  } finally {
    loading.value = false;
  }
}
async function save() {
  if (!props.isAdmin || !identity.value?.supported || busy.value || loading.value) return;
  if (!form.domain || !form.hostName || !form.contactEmail) return;
  busy.value = true;
  showNotice("");
  try {
    const saved = await setMailServerIdentity({ ...form });
    identity.value = saved;
    showNotice(t(verified.value ? "panel.mail.identitySaved" : "panel.mail.identityFailed"), !verified.value);
  } catch (error) {
    const code = (error as { code?: string })?.code;
    if (code === "ECONNABORTED" || code === "ETIMEDOUT" || code === "ERR_NETWORK") {
      showNotice(t("panel.mail.identityUncertain"), true);
      // The POST may still be running. A GET observes the server without repeating the mutation.
      try { identity.value = await getMailServerIdentity(); loadError.clear(); }
      catch (readError) { loadError.capture(readError); }
    } else {
      showError(error);
    }
  } finally {
    busy.value = false;
  }
}
</script>

<style scoped>
.mail-identity-card { display: grid; gap: 12px; min-width: 0; }
.identity-state { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 240px), 1fr)); gap: 12px; margin: 0; }
.identity-state dd { margin: 4px 0 0; overflow-wrap: anywhere; }
.identity-state dt { font-weight: 600; }
.identity-steps { margin: 0; padding-inline-start: 24px; overflow-wrap: anywhere; }
.identity-steps p { margin-top: 4px; white-space: pre-wrap; }
form { display: grid; gap: 12px; }
.form-grid { align-items: start; }
</style>
