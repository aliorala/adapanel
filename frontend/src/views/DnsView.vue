<template>
  <section class="panel module-shell dns-workspace">
    <div class="module-head">
      <div>
        <h3>{{ t("panel.dns.title") }}</h3>
        <p class="meta">{{ t("panel.dns.subtitle") }}</p>
      </div>
      <div class="row-actions">
        <button class="secondary compact" :disabled="!siteName || busy" @click="load">{{ t("panel.dns.refresh") }}</button>
      </div>
    </div>

    <p v-if="notice" :class="['notice', { error: noticeError }]">{{ notice }}</p>
    <p v-if="!siteName" class="meta">{{ t("panel.dns.selectSiteHint") }}</p>

    <template v-else>
      <div class="module-toolbar">
        <div class="segmented-tabs">
          <button :class="{ active: activeTab === 'records' }" @click="activeTab = 'records'">{{ t("panel.dns.records") }}</button>
          <button :class="{ active: activeTab === 'settings' }" @click="activeTab = 'settings'">{{ t("panel.dns.zoneSettings") }}</button>
        </div>
        <div class="toolbar-group">
          <span :class="['status-pill', zone?.isWindowsDnsAvailable ? 'ok' : 'warn']">{{ zoneStatus }}</span>
          <button
            v-if="zone && !zone.isWindowsDnsAvailable"
            class="secondary compact"
            :disabled="installingWindowsDns"
            :title="t('panel.dns.installWindowsHint')"
            @click="installWindowsDns">
            {{ installingWindowsDns ? t("panel.dns.installingWindows") : t("panel.dns.installWindows") }}
          </button>
          <button v-if="activeTab === 'records'" class="compact" @click="showRecordForm = !showRecordForm">
            {{ showRecordForm ? t("panel.common.cancel") : t("panel.dns.addRecordAction") }}
          </button>
          <button class="secondary compact" :disabled="busy" @click="restoreDefaults">{{ t("panel.dns.restoreDefaults") }}</button>
        </div>
      </div>

      <template v-if="activeTab === 'records'">
        <form v-if="showRecordForm || editingId" class="form-surface record-form" @submit.prevent="saveRecord">
          <label class="stacked-field">
            <span class="field-label">{{ t("panel.dns.type") }}</span>
            <select v-model="recordForm.type">
              <option v-for="type in recordTypes" :key="type" :value="type">{{ type }}</option>
            </select>
          </label>
          <label class="stacked-field">
            <span class="field-label">{{ t("panel.dns.host") }}</span>
            <input v-model="recordForm.host" placeholder="@ / www / mail" />
          </label>
          <label class="stacked-field value-field">
            <span class="field-label">{{ t("panel.dns.value") }}</span>
            <input v-model="recordForm.value" :placeholder="valuePlaceholder" />
          </label>
          <label v-if="recordForm.type === 'MX'" class="stacked-field">
            <span class="field-label">{{ t("panel.dns.priority") }}</span>
            <input v-model.number="recordForm.priority" type="number" min="0" max="65535" />
          </label>
          <label class="stacked-field">
            <span class="field-label">{{ t("panel.dns.ttl") }}</span>
            <input v-model.number="recordForm.ttl" type="number" min="60" max="86400" />
          </label>
          <div class="row-actions">
            <button class="compact" :disabled="busy">{{ editingId ? t("panel.dns.updateRecord") : t("panel.dns.addRecordAction") }}</button>
            <button type="button" class="secondary compact" @click="resetRecordForm">{{ t("panel.common.cancel") }}</button>
          </div>
        </form>

        <div class="data-table">
          <div class="data-row table-head dns-row">
            <span>{{ t("panel.dns.type") }}</span>
            <span>{{ t("panel.dns.host") }}</span>
            <span>{{ t("panel.dns.value") }}</span>
            <span>{{ t("panel.dns.ttl") }}</span>
            <span>{{ t("panel.dns.actions") }}</span>
          </div>
          <div v-for="record in zone?.records ?? []" :key="record.id" class="data-row dns-row">
            <strong class="status-pill ok">{{ record.type }}</strong>
            <div class="cell-main">
              <strong>{{ displayHost(record.host) }}</strong>
              <small v-if="record.isManaged">{{ t("panel.dns.managedRecord") }}</small>
            </div>
            <span class="record-value">{{ displayValue(record) }}</span>
            <span>{{ record.ttl }}</span>
            <span class="row-actions">
              <button class="secondary compact" @click="editRecord(record)">{{ t("panel.dns.edit") }}</button>
              <button class="danger compact" :disabled="busy" @click="removeRecord(record.id)">{{ t("panel.dns.delete") }}</button>
            </span>
          </div>
          <p v-if="!zone?.records.length" class="empty-state">{{ t("panel.dns.noRecords") }}</p>
        </div>
      </template>

      <form v-else class="form-surface" @submit.prevent="saveZone">
        <div class="section-head">
          <div>
            <h4>{{ t("panel.dns.zoneSettings") }}</h4>
            <p class="meta">{{ t("panel.dns.zoneSettingsHint") }}</p>
          </div>
          <button class="compact" :disabled="busy">{{ t("panel.dns.saveZone") }}</button>
        </div>
        <div class="form-grid two">
          <label class="stacked-field">
            <span class="field-label">{{ t("panel.dns.zone") }}</span>
            <input v-model="zoneForm.zoneName" placeholder="example.com" />
            <small class="field-hint">{{ t("panel.dns.zoneHelp") }}</small>
          </label>
          <label class="stacked-field">
            <span class="field-label">{{ t("panel.dns.nameServers") }}</span>
            <textarea v-model="nameServersText" rows="3" placeholder="ns1.example.com&#10;ns2.example.com"></textarea>
            <small class="field-hint">{{ t("panel.dns.nameServersHelp") }}</small>
          </label>
        </div>
      </form>
    </template>
  </section>
</template>

<script setup lang="ts">
import { computed, reactive, ref, watch } from "vue";
import { useI18n } from "vue-i18n";
import { deleteDnsRecord, getDnsZone, installWindowsDns as installWindowsDnsApi, resetDnsDefaults, updateDnsZone, upsertDnsRecord, type DnsRecord, type DnsZone } from "../api/dns";
import { extractApiDetails, extractApiMessage } from "../stores/auth";
import { useSitesStore } from "../stores/sites";

const { t } = useI18n();
const sitesStore = useSitesStore();
const siteName = computed(() => sitesStore.selectedSiteName);
const zone = ref<DnsZone | null>(null);
const busy = ref(false);
const installingWindowsDns = ref(false);
const notice = ref("");
const noticeError = ref(false);
const nameServersText = ref("");
const editingId = ref("");
const activeTab = ref<"records" | "settings">("records");
const showRecordForm = ref(false);
const recordTypes = ["A", "AAAA", "CNAME", "MX", "TXT", "NS"];
const zoneForm = reactive({ zoneName: "" });
const recordForm = reactive({ type: "A", host: "@", value: "", ttl: 3600, priority: 10 as number | null });

const zoneStatus = computed(() => zone.value ? t(zone.value.statusMessage) : t("panel.dns.loading"));
const valuePlaceholder = computed(() => ({
  A: "203.0.113.10",
  AAAA: "2001:db8::10",
  CNAME: zone.value?.zoneName ?? "example.com",
  MX: `mail.${zone.value?.zoneName ?? "example.com"}`,
  TXT: "v=spf1 a mx ~all",
  NS: `ns1.${zone.value?.zoneName ?? "example.com"}`
}[recordForm.type] ?? ""));

watch(siteName, load, { immediate: true });
watch(() => recordForm.type, (type) => {
  recordForm.priority = type === "MX" ? recordForm.priority ?? 10 : null;
});

async function load() {
  if (!siteName.value) return;
  busy.value = true;
  try {
    zone.value = await getDnsZone(siteName.value);
    zoneForm.zoneName = zone.value.zoneName;
    nameServersText.value = zone.value.nameServers.join("\n");
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    busy.value = false;
  }
}

async function saveZone() {
  if (!siteName.value) return;
  busy.value = true;
  try {
    zone.value = await updateDnsZone(siteName.value, {
      zoneName: zoneForm.zoneName,
      nameServers: splitLines(nameServersText.value)
    });
    showNotice(t("panel.dns.zoneSaved"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    busy.value = false;
  }
}

async function saveRecord() {
  if (!siteName.value) return;
  busy.value = true;
  try {
    zone.value = await upsertDnsRecord(siteName.value, {
      id: editingId.value || undefined,
      type: recordForm.type,
      host: recordForm.host,
      value: recordForm.value,
      ttl: recordForm.ttl,
      priority: recordForm.priority
    });
    resetRecordForm();
    showNotice(t("panel.dns.recordSaved"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    busy.value = false;
  }
}

function editRecord(record: DnsRecord) {
  showRecordForm.value = true;
  editingId.value = record.id;
  recordForm.type = record.type;
  recordForm.host = record.host;
  recordForm.value = record.value;
  recordForm.ttl = record.ttl;
  recordForm.priority = record.priority;
}

async function removeRecord(recordId: string) {
  if (!siteName.value || !window.confirm(t("panel.dns.confirmDelete"))) return;
  busy.value = true;
  try {
    zone.value = await deleteDnsRecord(siteName.value, recordId);
    showNotice(t("panel.dns.recordDeleted"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    busy.value = false;
  }
}

function resetRecordForm() {
  editingId.value = "";
  recordForm.type = "A";
  recordForm.host = "@";
  recordForm.value = "";
  recordForm.ttl = 3600;
  recordForm.priority = null;
  showRecordForm.value = false;
}

async function restoreDefaults() {
  if (!siteName.value || !window.confirm(t("panel.dns.confirmRestoreDefaults"))) return;
  busy.value = true;
  try {
    zone.value = await resetDnsDefaults(siteName.value);
    zoneForm.zoneName = zone.value.zoneName;
    nameServersText.value = zone.value.nameServers.join("\n");
    showNotice(t("panel.dns.defaultsRestored"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    busy.value = false;
  }
}

async function installWindowsDns() {
  if (!siteName.value) return;
  installingWindowsDns.value = true;
  try {
    zone.value = await installWindowsDnsApi(siteName.value);
    showNotice(t("panel.dns.windowsInstalled"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    installingWindowsDns.value = false;
  }
}

function displayHost(host: string) {
  return host === "@" && zone.value ? zone.value.zoneName : host;
}

function displayValue(record: DnsRecord) {
  return record.type === "MX" && record.priority !== null
    ? `${record.priority} ${record.value}`
    : record.value;
}

function splitLines(value: string) {
  return value.split(/[\n,]/).map(item => item.trim()).filter(Boolean);
}

function resolveApiMessage(error: unknown) {
  const key = extractApiMessage(error);
  const translated = t(key);
  const details = extractApiDetails(error);
  const message = translated === key ? key : translated;
  return details ? `${message}\n\n${details}` : message;
}

function showNotice(message: string, isError: boolean) {
  notice.value = message;
  noticeError.value = isError;
}
</script>

<style scoped>
.dns-workspace {
  min-width: 0;
}

.section-head {
  display: flex;
  justify-content: space-between;
  gap: 10px;
  align-items: start;
  flex-wrap: wrap;
}

.record-form {
  display: grid;
  grid-template-columns: 90px 140px minmax(220px, 1fr) 95px 95px auto;
  gap: 10px;
  align-items: end;
}

.value-field {
  min-width: 0;
}

.dns-row {
  grid-template-columns: 82px minmax(120px, 0.7fr) minmax(220px, 1.2fr) 80px minmax(170px, auto);
}

.record-value {
  overflow-wrap: anywhere;
  color: var(--text-soft);
}

@media (max-width: 1080px) {
  .record-form {
    grid-template-columns: 1fr;
  }
}
</style>
