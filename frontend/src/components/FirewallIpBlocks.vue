<template>
  <section class="ip-blocks" aria-labelledby="ip-blocks-title">
    <div class="section-head">
      <h3 id="ip-blocks-title">{{ t("panel.firewall.ipBlocks.title") }}</h3>
    </div>
    <p class="field-hint">{{ t("panel.firewall.ipBlocks.hint") }}</p>
    <p class="field-hint">{{ t("panel.firewall.ipBlocks.countryUnavailable") }}</p>
    <p v-if="message" :class="['notice', { error: messageError }]" :role="messageError ? 'alert' : 'status'">{{ message }}</p>

    <div class="form-grid two">
      <label>
        <span class="field-label">{{ t("panel.firewall.ipBlocks.target") }}</span>
        <input v-model.trim="form.target" :disabled="busy" placeholder="203.0.113.7 / 203.0.113.0/24" autocomplete="off" />
      </label>
      <label>
        <span class="field-label">{{ t("panel.firewall.ipBlocks.duration") }}</span>
        <select v-model.number="form.durationMinutes" :disabled="busy">
          <option :value="0">{{ t("panel.firewall.ipBlocks.permanent") }}</option>
          <option :value="60">{{ t("panel.firewall.ipBlocks.hour1") }}</option>
          <option :value="1440">{{ t("panel.firewall.ipBlocks.day1") }}</option>
          <option :value="10080">{{ t("panel.firewall.ipBlocks.day7") }}</option>
          <option :value="43200">{{ t("panel.firewall.ipBlocks.day30") }}</option>
        </select>
      </label>
      <label>
        <span class="field-label">{{ t("panel.firewall.ipBlocks.reason") }}</span>
        <input v-model.trim="form.reason" :disabled="busy" maxlength="200" autocomplete="off" />
      </label>
    </div>
    <div class="row-actions">
      <button :disabled="busy || disabled || !form.target" @click="add">{{ t("panel.firewall.ipBlocks.add") }}</button>
    </div>

    <div v-if="entries.length" class="data-table">
      <div class="data-row table-head">
        <span>{{ t("panel.firewall.ipBlocks.target") }}</span>
        <span>{{ t("panel.firewall.ipBlocks.state") }}</span>
        <span>{{ t("panel.firewall.ipBlocks.expires") }}</span>
        <span>{{ t("panel.ssl.bindingActions") }}</span>
      </div>
      <div v-for="entry in entries" :key="entry.id" class="data-row">
        <span class="cell-main"><strong>{{ entry.target }}</strong><small>{{ entry.reason || entry.createdBy }}</small></span>
        <span>
          <span v-if="entry.ruleState === 'applied'" class="status-pill ok">{{ t("panel.firewall.ipBlocks.applied") }}</span>
          <span v-else class="status-pill danger" :title="entry.ruleError">{{ t("panel.firewall.ipBlocks.notEnforced") }}</span>
        </span>
        <span class="cell-muted">{{ entry.expiresAtUtc ? formatDateTime(entry.expiresAtUtc, "-") : t("panel.firewall.ipBlocks.permanent") }}</span>
        <span class="row-actions">
          <button type="button" class="secondary compact" :disabled="busy || disabled" @click="remove(entry)">{{ t("panel.firewall.ipBlocks.remove") }}</button>
        </span>
      </div>
    </div>
    <p v-else-if="loaded" class="empty-state">{{ t("panel.firewall.ipBlocks.none") }}</p>
  </section>
</template>

<script setup lang="ts">
import { onBeforeUnmount, onMounted, reactive, ref } from "vue";
import { useI18n } from "vue-i18n";
import { addIpBlock, getIpBlocks, removeIpBlock, type IpBlockEntry } from "../api/firewall";
import { extractApiMessage } from "../stores/auth";
import { formatDateTime } from "../utils/datetime";
import { useConfirm } from "./ConfirmDialog.vue";

defineProps<{ disabled?: boolean }>();
const { t } = useI18n();
const { confirm } = useConfirm();

const form = reactive({ target: "", reason: "", durationMinutes: 0 });
const entries = ref<IpBlockEntry[]>([]);
const loaded = ref(false);
const busy = ref(false);
const message = ref("");
const messageError = ref(false);
let disposed = false;
onBeforeUnmount(() => { disposed = true; });

function show(text: string, error: boolean) { message.value = text; messageError.value = error; }
function failure(error: unknown) {
  const key = extractApiMessage(error);
  const translated = t(key);
  show(translated === key ? key : translated, true);
}

async function load() {
  try {
    const result = await getIpBlocks();
    if (disposed) return true;
    entries.value = result.entries;
    loaded.value = true;
    return true;
  } catch (error) {
    if (!disposed) failure(error);
    return false;
  }
}
onMounted(() => { void load(); });

async function add() {
  if (busy.value) return;
  busy.value = true;
  try {
    const entry = await addIpBlock({ target: form.target, reason: form.reason, durationMinutes: form.durationMinutes || null });
    if (disposed) return;
    form.target = ""; form.reason = "";
    const listed = await load();
    // Kural yazilamadiysa "engellendi" DENMEZ: adres agda engellenmemistir.
    if (entry.ruleState !== "applied") show(t("panel.firewall.ipBlocks.ruleFailed", { target: entry.target }), true);
    else if (!listed) show(`${t("panel.firewall.ipBlocks.added", { target: entry.target })}\n${message.value}`, true);
    else show(t("panel.firewall.ipBlocks.added", { target: entry.target }), false);
  } catch (error) {
    if (!disposed) failure(error);
  } finally {
    if (!disposed) busy.value = false;
  }
}

async function remove(entry: IpBlockEntry) {
  if (busy.value) return;
  if (!await confirm({ title: t("panel.firewall.ipBlocks.remove"), message: t("panel.firewall.ipBlocks.removeConfirm", { target: entry.target }), confirmLabel: t("panel.firewall.ipBlocks.remove") })) return;
  busy.value = true;
  try {
    await removeIpBlock(entry.id);
    if (disposed) return;
    const listed = await load();
    show(listed ? t("panel.firewall.ipBlocks.removed", { target: entry.target }) : `${t("panel.firewall.ipBlocks.removed", { target: entry.target })}\n${message.value}`, !listed);
  } catch (error) {
    if (!disposed) failure(error);
  } finally {
    if (!disposed) busy.value = false;
  }
}
</script>
