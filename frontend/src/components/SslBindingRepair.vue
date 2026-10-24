<template>
  <div class="ssl-repair">
    <button type="button" class="secondary compact" :disabled="busy" @click="inspect">{{ t('ssl.repair.inspect') }}</button>
    <p v-if="busy" role="status">{{ t('panel.common.loading') }}</p>
    <p v-if="message" :class="['notice', verified ? 'success' : 'error']" role="status">{{ message }}</p>
    <section v-if="plan" class="repair-plan" :aria-label="t('ssl.repair.review')">
      <h4>{{ t('ssl.repair.review') }} · {{ plan.siteName }}</h4>
      <p>{{ t('ssl.repair.explanation') }}</p>
      <p v-if="!plan.options.length" class="notice">{{ t('ssl.repair.no_targets') }}</p>
      <article v-for="option in plan.options" :key="option.bindingInformation" class="repair-option">
        <strong>{{ option.hostName }}</strong>
        <small>{{ option.bindingInformation }}</small>
        <dl>
          <dt>{{ t('ssl.repair.current') }}</dt>
          <dd v-if="option.current"><code>{{ option.current.thumbprint }}</code><br>{{ formatDateTime(option.current.notAfterUtc) }} · {{ t(option.current.valid ? 'ssl.repair.valid' : 'ssl.repair.unverified') }}</dd>
          <dd v-else>{{ t('ssl.repair.unverified') }}</dd>
          <dt>{{ t('ssl.repair.candidate') }}</dt>
          <dd><code>{{ option.candidate.thumbprint }}</code><br>{{ option.candidate.subject }}<br>{{ formatDateTime(option.candidate.notAfterUtc) }} · {{ t(option.candidate.valid ? 'ssl.repair.valid' : 'ssl.repair.unverified') }}</dd>
        </dl>
        <p v-if="option.blockedReason" class="notice">{{ t(option.blockedReason) }}</p>
        <button v-else type="button" class="compact" v-bind="writeGate(busy || !option.planToken)" @click="apply(option)">{{ t('ssl.repair.apply') }}</button>
      </article>
      <button type="button" class="ghost compact" :disabled="busy" @click="plan = null">{{ t('panel.attention.close') }}</button>
    </section>
  </div>
</template>

<script setup lang="ts">
import { ref } from 'vue';
import { useI18n } from 'vue-i18n';
import { getSslRepairPlan, applySslRepair, type SslRepairTarget, type SslRepairPlan, type SslRepairOption } from '../api/sslRepair';
import { extractApiMessage } from '../stores/auth';
import { useSubscriptionGate } from '../composables/useSubscriptionGate';
import { formatDateTime } from '../utils/datetime';
const props = defineProps<{ target: SslRepairTarget }>();
const emit = defineEmits<{ (event: 'refresh'): void; (event: 'result', value: { message: string; verified: boolean }): void }>();
const { t } = useI18n();
const { writeGate } = useSubscriptionGate();
const plan = ref<SslRepairPlan | null>(null);
const busy = ref(false);
const message = ref('');
const verified = ref(false);
async function inspect() {
  if (busy.value) return;
  busy.value = true; message.value = ''; verified.value = false; plan.value = null;
  try { plan.value = await getSslRepairPlan(props.target); }
  catch (error) { message.value = t(extractApiMessage(error)); }
  finally { busy.value = false; }
}
async function apply(option: SslRepairOption) {
  if (busy.value || !option.planToken) return;
  busy.value = true; message.value = ''; verified.value = false;
  try {
    const result = await applySslRepair(option.planToken);
    verified.value = result.bindingApplied === true && result.tlsVerified === true;
    message.value = t(verified.value ? 'ssl.repair.verified' : result.detail || 'ssl.repair.readback_failed');
  } catch (error) { message.value = t(extractApiMessage(error)); }
  finally { plan.value = null; busy.value = false; emit('result', { message: message.value, verified: verified.value }); emit('refresh'); }
}
</script>

<style scoped>
.ssl-repair { margin-top: .6rem; min-width: 0; }
.repair-plan { margin-top: .7rem; padding: 1rem; border: 1px solid var(--panel-border); border-radius: 8px; }
.repair-plan h4 { margin: 0; }
.repair-option { margin: 1rem 0; padding-top: .8rem; border-top: 1px solid var(--panel-border); }
.repair-option small { display: block; overflow-wrap: anywhere; }
.repair-option dl { display: grid; grid-template-columns: minmax(110px, 1fr) minmax(0, 3fr); gap: .6rem; }
.repair-option dd { margin: 0; overflow-wrap: anywhere; }
.repair-option code { font-size: .8rem; overflow-wrap: anywhere; }
.ssl-repair p { overflow-wrap: anywhere; }
@media (max-width: 600px) { .repair-option dl { grid-template-columns: 1fr; } .repair-plan { padding: .7rem; } }
</style>
