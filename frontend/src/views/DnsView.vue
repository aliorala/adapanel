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
          <div class="record-main">
            <label class="stacked-field type-field">
              <span class="field-label">{{ t("panel.dns.type") }}</span>
              <select v-model="recordForm.type">
                <option v-for="type in recordTypes" :key="type" :value="type">{{ type }}</option>
              </select>
            </label>
            <label class="stacked-field host-field">
              <span class="field-label">{{ t("panel.dns.host") }}</span>
              <input v-model="recordForm.host" placeholder="@ / www / mail" />
            </label>
            <!-- Tek değerli tipler (A, AAAA, CNAME, MX, TXT, NS, PTR) eskisi gibi tek kutu kullanır. -->
            <!-- for/id şart: etiketin içindeki "?" düğmesi aksi halde etiketin örtük kontrolü olur. -->
            <label v-if="!isMultiPartType" class="stacked-field value-field" for="dns-record-value">
              <span class="field-label">
                {{ t("panel.dns.value") }}
                <FieldHint v-if="valueHint" :label="t('panel.dns.value')" :text="valueHint" />
              </span>
              <input id="dns-record-value" v-model="recordForm.value" :placeholder="valuePlaceholder" />
            </label>
            <label v-if="recordForm.type === 'MX' || recordForm.type === 'SRV'" class="stacked-field narrow-field">
              <span class="field-label">{{ t("panel.dns.priority") }}</span>
              <input v-model.number="recordForm.priority" type="number" min="0" max="65535" />
            </label>
            <label class="stacked-field narrow-field">
              <span class="field-label">{{ t("panel.dns.ttl") }}</span>
              <input v-model.number="recordForm.ttl" type="number" min="60" max="86400" />
            </label>
            <div class="row-actions form-actions">
              <button class="compact" :disabled="busy">{{ editingId ? t("panel.dns.updateRecord") : t("panel.dns.addRecordAction") }}</button>
              <button type="button" class="secondary compact" @click="resetRecordForm">{{ t("panel.common.cancel") }}</button>
            </div>
          </div>

          <!-- Çok alanlı tipler: biçim ezberletmek yerine her parça ayrı kutu; kaydederken boşlukla birleştirilir. -->
          <div v-if="isMultiPartType" class="stacked-field record-multi">
            <span class="field-label">
              {{ t("panel.dns.value") }}
              <FieldHint v-if="valueHint" :label="t('panel.dns.value')" :text="valueHint" />
            </span>

            <div v-if="recordForm.type === 'SRV'" class="multi-grid srv">
              <label class="stacked-field">
                <span class="field-hint">{{ t("panel.dns.srvWeight") }}</span>
                <input v-model.trim="recordForm.srvWeight" type="number" min="0" max="65535" placeholder="5" />
              </label>
              <label class="stacked-field">
                <span class="field-hint">{{ t("panel.dns.srvPort") }}</span>
                <input v-model.trim="recordForm.srvPort" type="number" min="1" max="65535" placeholder="5060" />
              </label>
              <label class="stacked-field">
                <span class="field-hint">{{ t("panel.dns.srvTarget") }}</span>
                <input v-model.trim="recordForm.srvTarget" spellcheck="false" :placeholder="`sip.${zoneNameOrExample}`" />
              </label>
            </div>

            <div v-else-if="recordForm.type === 'CAA'" class="multi-grid caa">
              <label class="stacked-field">
                <span class="field-hint">{{ t("panel.dns.caaFlags") }}</span>
                <input v-model.trim="recordForm.caaFlags" type="number" min="0" max="255" placeholder="0" />
              </label>
              <label class="stacked-field">
                <span class="field-hint">{{ t("panel.dns.caaTag") }}</span>
                <select v-model="recordForm.caaTag">
                  <option v-for="tag in caaTagOptions" :key="tag" :value="tag">{{ tag }}</option>
                </select>
              </label>
              <label class="stacked-field">
                <span class="field-hint">{{ t("panel.dns.value") }}</span>
                <input v-model.trim="recordForm.caaValue" spellcheck="false" placeholder="letsencrypt.org" />
              </label>
            </div>

            <div v-else class="multi-grid tlsa">
              <label class="stacked-field">
                <span class="field-hint">{{ t("panel.dns.tlsaUsage") }}</span>
                <select v-model="recordForm.tlsaUsage">
                  <option v-for="option in ['0', '1', '2', '3']" :key="option" :value="option">{{ option }}</option>
                </select>
              </label>
              <label class="stacked-field">
                <span class="field-hint">{{ t("panel.dns.tlsaSelector") }}</span>
                <select v-model="recordForm.tlsaSelector">
                  <option v-for="option in ['0', '1']" :key="option" :value="option">{{ option }}</option>
                </select>
              </label>
              <label class="stacked-field">
                <span class="field-hint">{{ t("panel.dns.tlsaMatching") }}</span>
                <select v-model="recordForm.tlsaMatching">
                  <option v-for="option in ['0', '1', '2']" :key="option" :value="option">{{ option }}</option>
                </select>
              </label>
              <label class="stacked-field tlsa-cert">
                <span class="field-hint">{{ t("panel.dns.tlsaCertificate") }}</span>
                <input v-model.trim="recordForm.tlsaCert" spellcheck="false" placeholder="abcdef0123456789…" />
              </label>
            </div>
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
          <!-- for/id şart: etiketin içindeki "?" düğmesi aksi halde etiketin örtük kontrolü olur. -->
          <label class="stacked-field" for="dns-zone-name">
            <span class="field-label">
              {{ t("panel.dns.zone") }}
              <FieldHint :label="t('panel.dns.zone')" :text="t('panel.dns.zoneHelp')" />
            </span>
            <input id="dns-zone-name" v-model="zoneForm.zoneName" placeholder="example.com" />
          </label>
          <label class="stacked-field" for="dns-zone-nameservers">
            <span class="field-label">
              {{ t("panel.dns.nameServers") }}
              <FieldHint :label="t('panel.dns.nameServers')" :text="t('panel.dns.nameServersHelp')" />
            </span>
            <textarea id="dns-zone-nameservers" v-model="nameServersText" rows="3" placeholder="ns1.example.com&#10;ns2.example.com"></textarea>
          </label>
          <label class="stacked-field" for="dns-zone-transfer">
            <span class="field-label">
              {{ t("panel.dns.zoneTransfer") }}
              <FieldHint :label="t('panel.dns.zoneTransfer')" :text="t('panel.dns.zoneTransferHelp')" />
            </span>
            <textarea id="dns-zone-transfer" v-model="zoneTransferText" rows="3" placeholder="192.0.2.53&#10;2001:db8::53"></textarea>
          </label>
        </div>
      </form>

      <div v-if="zone" class="form-surface dnssec-card">
        <div class="section-head">
          <div>
            <h4>{{ t("panel.dns.dnssecTitle") }}</h4>
            <p class="meta">{{ t("panel.dns.dnssecHint") }}</p>
          </div>
          <span :class="['status-pill', zone.isDnssecSigned ? 'ok' : '']">
            {{ zone.isDnssecSigned ? t("panel.dns.dnssecOn") : t("panel.dns.dnssecOff") }}
          </span>
        </div>
        <p v-if="!zone.isWindowsDnsAvailable" class="field-hint">{{ t("panel.dns.dnssecRequiresWindows") }}</p>
        <template v-else>
          <div class="row-actions">
            <button v-if="!zone.isDnssecSigned" class="compact" :disabled="dnssecBusy" @click="toggleDnssec(true)">
              {{ dnssecBusy ? t("panel.dns.dnssecWorking") : t("panel.dns.dnssecEnable") }}
            </button>
            <button v-else class="secondary compact danger" :disabled="dnssecBusy" @click="toggleDnssec(false)">
              {{ dnssecBusy ? t("panel.dns.dnssecWorking") : t("panel.dns.dnssecDisable") }}
            </button>
          </div>
          <div v-if="zone.isDnssecSigned" class="dnssec-ds">
            <p class="field-label">
              {{ t("panel.dns.dnssecDsTitle") }}
              <FieldHint :label="t('panel.dns.dnssecDsTitle')" :text="t('panel.dns.dnssecDsHint')" />
            </p>
            <pre v-if="zone.dnssecDsRecords.length" class="ds-box">{{ zone.dnssecDsRecords.join("\n") }}</pre>
            <p v-else class="field-hint">{{ t("panel.dns.dnssecDsUnavailable") }}</p>
          </div>
        </template>
      </div>
    </template>
  </section>
</template>

<script setup lang="ts">
import { computed, reactive, ref, watch } from "vue";
import { useI18n } from "vue-i18n";
import { deleteDnsRecord, getDnsZone, installWindowsDns as installWindowsDnsApi, resetDnsDefaults, setDnssec, updateDnsZone, upsertDnsRecord, type DnsRecord, type DnsZone } from "../api/dns";
import { useConfirm } from "../components/ConfirmDialog.vue";
import FieldHint from "../components/FieldHint.vue";
import { extractApiDetails, extractApiMessage } from "../stores/auth";
import { useSitesStore } from "../stores/sites";

const { t } = useI18n();
const { confirm } = useConfirm();
const sitesStore = useSitesStore();
const siteName = computed(() => sitesStore.selectedSiteName);
const zone = ref<DnsZone | null>(null);
const busy = ref(false);
const installingWindowsDns = ref(false);
const dnssecBusy = ref(false);
const notice = ref("");
const noticeError = ref(false);
const nameServersText = ref("");
const zoneTransferText = ref("");
const editingId = ref("");
const activeTab = ref<"records" | "settings">("records");
const showRecordForm = ref(false);
const recordTypes = ["A", "AAAA", "CNAME", "MX", "TXT", "NS", "SRV", "CAA", "TLSA", "PTR"];
// Tek "değer" kutusu yerine ayrı alanlarla girilen tipler (API yine tek string bekler).
const multiPartTypes = ["SRV", "CAA", "TLSA"];
const standardCaaTags = ["issue", "issuewild", "iodef"];
const zoneForm = reactive({ zoneName: "" });
const recordForm = reactive({
  type: "A",
  host: "@",
  value: "",
  ttl: 3600,
  priority: 10 as number | null,
  // SRV: <ağırlık> <port> <hedef>
  srvWeight: "0",
  srvPort: "",
  srvTarget: "",
  // CAA: <bayrak> <etiket> <değer>
  caaFlags: "0",
  caaTag: "issue",
  caaValue: "",
  // TLSA: <kullanım> <seçici> <eşleşme> <sertifika-hex>
  tlsaUsage: "3",
  tlsaSelector: "1",
  tlsaMatching: "1",
  tlsaCert: ""
});

const zoneStatus = computed(() => zone.value ? t(zone.value.statusMessage) : t("panel.dns.loading"));
const zoneNameOrExample = computed(() => zone.value?.zoneName ?? "example.com");
const isMultiPartType = computed(() => multiPartTypes.includes(recordForm.type));
// Standart dışı bir etiketle kaydedilmiş CAA kaydı düzenlenirken listeden düşmesin.
const caaTagOptions = computed(() => {
  const tag = recordForm.caaTag.trim();
  return tag && !standardCaaTags.includes(tag) ? [...standardCaaTags, tag] : standardCaaTags;
});

const valuePlaceholder = computed(() => ({
  A: "203.0.113.10",
  AAAA: "2001:db8::10",
  CNAME: zoneNameOrExample.value,
  MX: `mail.${zoneNameOrExample.value}`,
  TXT: "v=spf1 a mx ~all",
  NS: `ns1.${zoneNameOrExample.value}`,
  PTR: `mail.${zoneNameOrExample.value}`
}[recordForm.type] ?? ""));

// Uzun açıklamalar formu şişirmesin diye etiketin yanındaki "?" ipucunda gösterilir.
const valueHint = computed(() => {
  const key = {
    SRV: "panel.dns.hintSrv",
    CAA: "panel.dns.hintCaa",
    TLSA: "panel.dns.hintTlsa",
    PTR: "panel.dns.hintPtr"
  }[recordForm.type];
  return key ? t(key) : "";
});

watch(siteName, load, { immediate: true });
// flush: "sync" — editRecord() önce tipi atayıp hemen ardından alanları dolduruyor;
// izleyici geç çalışsaydı doldurulan alanları varsayılanlarla ezerdi.
watch(() => recordForm.type, (type) => {
  if (type === "MX") {
    recordForm.priority = recordForm.priority ?? 10;
  } else if (type === "SRV") {
    recordForm.priority = recordForm.priority ?? 0;
  } else {
    recordForm.priority = null;
  }
  resetMultiPartFields();
}, { flush: "sync" });

async function load() {
  if (!siteName.value) return;
  busy.value = true;
  try {
    zone.value = await getDnsZone(siteName.value);
    zoneForm.zoneName = zone.value.zoneName;
    nameServersText.value = zone.value.nameServers.join("\n");
    zoneTransferText.value = (zone.value.zoneTransferIps ?? []).join("\n");
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
      nameServers: splitLines(nameServersText.value),
      zoneTransferIps: splitLines(zoneTransferText.value)
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
      value: composeRecordValue(),
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
  // Tip önce atanır: sync izleyici çok alanlı kutuları sıfırlar, ardından kaydın değeri dağıtılır.
  recordForm.type = record.type;
  recordForm.host = record.host;
  recordForm.value = record.value;
  recordForm.ttl = record.ttl;
  recordForm.priority = record.priority;
  applyValueToMultiPartFields(record.type, record.value);
}

/** Ayrı kutuları API'nin beklediği tek "value" string'ine çevirir (boşlukla birleştirme). */
function composeRecordValue() {
  if (recordForm.type === "SRV") {
    return joinValueParts([recordForm.srvWeight, recordForm.srvPort, recordForm.srvTarget]);
  }
  if (recordForm.type === "CAA") {
    return joinValueParts([recordForm.caaFlags, recordForm.caaTag, recordForm.caaValue]);
  }
  if (recordForm.type === "TLSA") {
    return joinValueParts([recordForm.tlsaUsage, recordForm.tlsaSelector, recordForm.tlsaMatching, recordForm.tlsaCert]);
  }
  return recordForm.value.trim();
}

// Eksik bırakılan bir parça boş kalır; sunucu parça sayısını sayarak dns.invalid_*_value döndürür.
function joinValueParts(parts: string[]) {
  return parts.map(part => (part ?? "").trim()).join(" ").trim();
}

/** Kayıt düzenlenirken tek string'i kutulara dağıtır. */
function applyValueToMultiPartFields(type: string, value: string) {
  if (type === "SRV") {
    const [weight, port, target] = splitValueParts(value, 3);
    recordForm.srvWeight = weight ?? "";
    recordForm.srvPort = port ?? "";
    recordForm.srvTarget = target ?? "";
  } else if (type === "CAA") {
    const [flags, tag, caaValue] = splitValueParts(value, 3);
    recordForm.caaFlags = flags ?? "0";
    recordForm.caaTag = tag ?? "issue";
    // Değer tırnaklı yazılmış olabilir: 0 issue "letsencrypt.org"
    recordForm.caaValue = (caaValue ?? "").replace(/^"|"$/g, "");
  } else if (type === "TLSA") {
    const [usage, selector, matching, certificate] = splitValueParts(value, 4);
    recordForm.tlsaUsage = usage ?? "3";
    recordForm.tlsaSelector = selector ?? "1";
    recordForm.tlsaMatching = matching ?? "1";
    recordForm.tlsaCert = certificate ?? "";
  }
}

/**
 * Boşluklardan EN FAZLA `limit` parçaya böler; artan kısım tümüyle son parçada kalır.
 * CAA değeri boşluk içerebildiği için (0 issue "ca.example; account=1") bu şart.
 */
function splitValueParts(value: string, limit: number) {
  const parts: string[] = [];
  let rest = (value ?? "").trim();
  while (parts.length < limit - 1) {
    const index = rest.search(/\s/);
    if (index < 0) break;
    parts.push(rest.slice(0, index));
    rest = rest.slice(index + 1).trim();
  }
  if (rest.length > 0) parts.push(rest);
  return parts;
}

function resetMultiPartFields() {
  recordForm.srvWeight = "0";
  recordForm.srvPort = "";
  recordForm.srvTarget = "";
  recordForm.caaFlags = "0";
  recordForm.caaTag = "issue";
  recordForm.caaValue = "";
  recordForm.tlsaUsage = "3";
  recordForm.tlsaSelector = "1";
  recordForm.tlsaMatching = "1";
  recordForm.tlsaCert = "";
}

async function removeRecord(recordId: string) {
  if (!siteName.value) return;

  const record = zone.value?.records.find((item) => item.id === recordId) ?? null;
  const impact = record
    ? [
        `${t("panel.dns.type")}: ${record.type}`,
        `${t("panel.dns.host")}: ${displayHost(record.host)}`,
        `${t("panel.dns.value")}: ${displayValue(record)}`,
        `${t("panel.dns.ttl")}: ${record.ttl}`
      ]
    : [];

  // Normal onay: tek DNS kaydı silinir, yeniden eklenebilir → ad yazdırmaya gerek yok.
  await confirm({
    title: t("panel.confirm.deleteDnsRecordTitle", { name: record ? displayHost(record.host) : "" }),
    message: t("panel.dns.confirmDelete"),
    impact,
    action: async () => {
      busy.value = true;
      try {
        zone.value = await deleteDnsRecord(siteName.value, recordId);
        showNotice(t("panel.dns.recordDeleted"), false);
      } catch (error) {
        showNotice(resolveApiMessage(error), true);
      } finally {
        busy.value = false;
      }
    },
  });
}

function resetRecordForm() {
  editingId.value = "";
  recordForm.type = "A";
  recordForm.host = "@";
  recordForm.value = "";
  recordForm.ttl = 3600;
  recordForm.priority = null;
  // Tip zaten "A" ise izleyici tetiklenmez; kutuları burada da temizliyoruz.
  resetMultiPartFields();
  showRecordForm.value = false;
}

async function restoreDefaults() {
  if (!siteName.value) return;

  const managed = (zone.value?.records ?? []).filter((item) => item.isManaged).length;
  const impact = [
    `${t("panel.dns.zone")}: ${zone.value?.zoneName || siteName.value}`,
    `${t("panel.dns.managedRecord")}: ${managed}`
  ];

  // Normal onay: yalnız panelin yönettiği kayıtlar varsayılana döner (kapsam dar, yeniden düzenlenebilir).
  await confirm({
    title: t("panel.confirm.restoreDnsDefaultsTitle"),
    message: t("panel.dns.confirmRestoreDefaults"),
    impact,
    confirmLabel: t("panel.dns.restoreDefaults"),
    action: async () => {
      busy.value = true;
      try {
        zone.value = await resetDnsDefaults(siteName.value);
        zoneForm.zoneName = zone.value.zoneName;
        nameServersText.value = zone.value.nameServers.join("\n");
        zoneTransferText.value = (zone.value.zoneTransferIps ?? []).join("\n");
        showNotice(t("panel.dns.defaultsRestored"), false);
      } catch (error) {
        showNotice(resolveApiMessage(error), true);
      } finally {
        busy.value = false;
      }
    },
  });
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

async function toggleDnssec(enable: boolean) {
  if (!siteName.value) return;
  dnssecBusy.value = true;
  try {
    zone.value = await setDnssec(siteName.value, enable);
    showNotice(t(enable ? "panel.dns.dnssecEnabledMsg" : "panel.dns.dnssecDisabledMsg"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    dnssecBusy.value = false;
  }
}

function displayHost(host: string) {
  return host === "@" && zone.value ? zone.value.zoneName : host;
}

// Listede zone dosyasındaki okunuşu koruyoruz: MX/SRV için öncelik değerin başında gelir.
function displayValue(record: DnsRecord) {
  return (record.type === "MX" || record.type === "SRV") && record.priority !== null
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

/* Alanlar tipe göre gelip gittiği için sabit kolonlu grid yerine esnek satır. */
.record-main {
  display: flex;
  flex-wrap: wrap;
  gap: 10px;
  align-items: end;
}

.type-field {
  flex: 0 0 90px;
}

.host-field {
  flex: 0 0 140px;
}

.narrow-field {
  flex: 0 0 95px;
}

.value-field {
  flex: 1 1 220px;
  min-width: 0;
}

.form-actions {
  margin-inline-start: auto;
}

.record-multi .multi-grid {
  display: grid;
  gap: 10px;
}

.multi-grid.srv {
  grid-template-columns: 95px 95px minmax(160px, 1fr);
}

.multi-grid.caa {
  grid-template-columns: 95px 130px minmax(160px, 1fr);
}

.multi-grid.tlsa {
  grid-template-columns: 95px 95px 95px minmax(160px, 1fr);
}

.dns-row {
  grid-template-columns: 82px minmax(120px, 0.7fr) minmax(220px, 1.2fr) 80px minmax(170px, auto);
}

.record-value {
  overflow-wrap: anywhere;
  color: var(--text-soft);
}

@media (max-width: 1080px) {
  .record-main > .stacked-field {
    flex: 1 1 100%;
  }

  .form-actions {
    margin-inline-start: 0;
  }

  .multi-grid.srv,
  .multi-grid.caa {
    grid-template-columns: 1fr 1fr;
  }

  .multi-grid.tlsa {
    grid-template-columns: repeat(3, 1fr);
  }

  .multi-grid.srv > :last-child,
  .multi-grid.caa > :last-child,
  .multi-grid.tlsa > .tlsa-cert {
    grid-column: 1 / -1;
  }
}
</style>
