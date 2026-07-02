<template>
  <section class="panel module-shell dns-template">
    <div class="module-head">
      <div>
        <h3>{{ t("panel.dnsTemplate.title") }}</h3>
        <p class="meta">{{ t("panel.dnsTemplate.subtitle") }}</p>
      </div>
      <button class="secondary compact" :disabled="loading" @click="load">{{ t("panel.dnsTemplate.refresh") }}</button>
    </div>

    <p v-if="notice" :class="['notice', { error: noticeError }]">{{ notice }}</p>
    <p class="field-hint">{{ t("panel.dnsTemplate.placeholderHint") }}</p>

    <p v-if="loading && records.length === 0" class="empty-state">{{ t("panel.common.loading") }}</p>

    <div v-else class="tmpl-table">
      <div class="tmpl-row head">
        <span>{{ t("panel.dnsTemplate.type") }}</span>
        <span>{{ t("panel.dnsTemplate.host") }}</span>
        <span>{{ t("panel.dnsTemplate.value") }}</span>
        <span>{{ t("panel.dnsTemplate.ttl") }}</span>
        <span></span>
      </div>
      <div v-for="(record, index) in records" :key="index" class="tmpl-row">
        <select v-model="record.type">
          <option v-for="type in types" :key="type" :value="type">{{ type }}</option>
        </select>
        <input v-model.trim="record.host" placeholder="@" spellcheck="false" />
        <input v-model.trim="record.value" placeholder="{ip}" spellcheck="false" />
        <input v-model.number="record.ttl" type="number" min="60" max="604800" />
        <button class="danger compact" type="button" @click="records.splice(index, 1)">{{ t("panel.common.delete") }}</button>
      </div>
      <p v-if="records.length === 0" class="empty-state">{{ t("panel.dnsTemplate.empty") }}</p>
    </div>

    <div class="row-actions">
      <button class="secondary compact" type="button" @click="addRow">{{ t("panel.dnsTemplate.addRow") }}</button>
      <button class="compact" type="button" :disabled="saving" @click="save">
        {{ saving ? t("panel.dnsTemplate.saving") : t("panel.common.save") }}
      </button>
    </div>
  </section>
</template>

<script setup lang="ts">
import { onMounted, ref } from "vue";
import { useI18n } from "vue-i18n";
import { getDnsTemplate, updateDnsTemplate, type DnsTemplateRecord } from "../api/dns";
import { extractApiMessage } from "../stores/auth";

const { t } = useI18n();
const records = ref<DnsTemplateRecord[]>([]);
const loading = ref(false);
const saving = ref(false);
const notice = ref("");
const noticeError = ref(false);
const types = ["A", "AAAA", "CNAME", "MX", "TXT", "NS", "SRV", "CAA"];

onMounted(load);

async function load() {
  loading.value = true;
  notice.value = "";
  try {
    const template = await getDnsTemplate();
    records.value = template.records;
  } catch (error) {
    showNotice(resolveMessage(error), true);
  } finally {
    loading.value = false;
  }
}

function addRow() {
  records.value.push({ type: "A", host: "@", value: "{ip}", ttl: 3600 });
}

async function save() {
  saving.value = true;
  notice.value = "";
  try {
    const template = await updateDnsTemplate(records.value);
    records.value = template.records;
    showNotice(t("panel.dnsTemplate.saved"), false);
  } catch (error) {
    showNotice(resolveMessage(error), true);
  } finally {
    saving.value = false;
  }
}

function showNotice(message: string, isError: boolean) {
  notice.value = message;
  noticeError.value = isError;
}

function resolveMessage(error: unknown) {
  const key = extractApiMessage(error);
  const translated = t(key);
  return translated === key ? key : translated;
}
</script>

<style scoped>
.dns-template {
  display: grid;
  gap: 12px;
}

.tmpl-table {
  display: grid;
  gap: 6px;
}

.tmpl-row {
  display: grid;
  grid-template-columns: 0.8fr 1fr 1.6fr 0.7fr auto;
  gap: 8px;
  align-items: center;
}

.tmpl-row.head {
  font-weight: 700;
  color: var(--text-muted);
  font-size: 0.82rem;
}

.tmpl-row input,
.tmpl-row select {
  width: 100%;
}

.field-hint {
  color: var(--text-muted);
  font-size: 0.82rem;
}

@media (max-width: 820px) {
  .tmpl-row {
    grid-template-columns: 1fr 1fr;
  }
  .tmpl-row.head {
    display: none;
  }
}
</style>
