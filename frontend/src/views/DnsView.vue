<template>
  <section class="panel module-shell dns-workspace">
    <div class="module-head">
      <div>
        <h3>{{ t("panel.dns.title") }}</h3>
        <p class="meta">{{ t("panel.dns.subtitle") }}</p>
      </div>
      <div class="row-actions">
        <button class="secondary compact" :disabled="!canReload" @click="load()">{{ busy ? t("panel.common.loading") : t("panel.dns.refresh") }}</button>
      </div>
    </div>

    <div v-if="notice" :class="['notice', 'page-notice', 'dns-notice', { error: noticeError }]" :role="noticeError ? 'alert' : 'status'">
      <span>{{ notice }}</span>
      <button type="button" class="secondary compact notice-close" :aria-label="t('panel.common.dismissNotice')" @click="notice = ''">×</button>
    </div>
    <p v-if="!siteName" class="meta">{{ t("panel.dns.selectSiteHint") }}</p>

    <template v-else-if="canReadZone">
      <SectionLoadError v-if="loadError" :message="loadError" :busy="!canReload" @retry="load()" />
      <p v-if="zone && !zoneConfirmed" class="dns-last-known" role="status">{{ t("panel.dns.lastKnownZone") }}</p>
      <div v-if="zone?.isSharedZone" class="dns-info dns-shared-note" role="status">
        <strong>{{ t("panel.dns.sharedZoneTitle") }}</strong>
        <p>{{ t("panel.dns.sharedZoneHint") }}</p>
      </div>
      <div v-if="hasConfigurationConflict" class="dns-last-known dns-policy-conflict" role="alert">
        <strong>{{ t("panel.dns.configurationConflictTitle") }}</strong>
        <p>{{ t(isAdmin ? "panel.dns.configurationConflictAdminHint" : "panel.dns.configurationConflictResellerHint") }}</p>
        <ul>
          <li v-if="zone?.modeConfigurationConflict">{{ t("panel.dns.modeConfigurationConflict") }}</li>
          <li v-if="zone?.transferConfigurationConflict">{{ t("panel.dns.transferConfigurationConflict") }}</li>
        </ul>
        <button v-if="activeTab !== 'settings'" type="button" class="secondary compact" @click="activeTab = 'settings'">{{ t("panel.dns.zoneSettings") }}</button>
      </div>
      <div class="module-toolbar">
        <div class="segmented-tabs" role="tablist">
          <button role="tab" :aria-selected="activeTab === 'records'" :class="{ active: activeTab === 'records' }" @click="activeTab = 'records'">{{ t("panel.dns.records") }}</button>
          <button role="tab" :aria-selected="activeTab === 'settings'" :class="{ active: activeTab === 'settings' }" @click="activeTab = 'settings'">{{ t("panel.dns.zoneSettings") }}</button>
        </div>
        <div class="toolbar-group">
          <span :class="['status-pill', zoneConfirmed && zone?.isWindowsZonePresent && !hasConfigurationConflict && zone.statusMessage !== 'dns.zone_type_mismatch' ? 'ok' : 'warn']">{{ zoneStatus }}</span>
          <button
            v-if="isAdmin && zone && !zone.isWindowsDnsAvailable"
            class="secondary compact"
            v-bind="writeGate(!canInstallWindowsDns, t('panel.dns.installWindowsHint'))"
            @click="installWindowsDns">
            {{ installingWindowsDns ? t("panel.dns.installingWindows") : t("panel.dns.installWindows") }}
          </button>
          <button
            v-if="activeTab === 'records' && !isSecondary"
            class="compact"
            v-bind="writeGate(!canWriteRecords)"
            @click="showRecordForm = !showRecordForm">
            {{ showRecordForm ? t("panel.common.cancel") : t("panel.dns.addRecordAction") }}
          </button>
          <!-- "Varsayılana sıfırla" panelin YÖNETTİĞİ kayıtları geri alır; ekleme düğmesiyle
               aynı kümede durmaz. Ayraç bir CSS numarası değil GERÇEK bir küme: satır
               kaydığında çizgi de düğmeyle birlikte gider (bkz. .reset-group). -->
          <div v-if="!isSecondary" class="reset-group">
            <button class="secondary compact" v-bind="writeGate(!canWriteRecords)" @click="restoreDefaults">{{ t("panel.dns.restoreDefaults") }}</button>
          </div>
        </div>
      </div>

      <template v-if="activeTab === 'records'">
        <!-- İkincil bölgede kayıtlar master'dan gelir; panelden düzenlenemez. Düğmeleri gizleyip
             sebebini YAZMAK şart: yoksa kullanıcı "kayıt ekle düğmesi nerede" diye arar. -->
        <p v-if="isSecondary" class="dns-info" role="status">{{ t("panel.dns.secondaryRecordsReadOnly") }}</p>

        <!-- `record-form` sınıfı KALDIRILDI: hiçbir yerde tanımlı değildi, yani yazılmış ama
             hiçbir şey yapmıyordu. Formun görünümünü zaten `.form-surface` (base.css) veriyor. -->
        <form v-if="!isSecondary && (showRecordForm || editingId)" class="form-surface" @submit.prevent="saveRecord">
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
              <button class="compact" v-bind="writeGate(!canWriteRecords)">{{ editingId ? t("panel.dns.updateRecord") : t("panel.dns.addRecordAction") }}</button>
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

        <div v-if="!isSecondary && selection.count.value > 0" class="bulk-bar" role="status">
          <span class="status-pill">{{ t("panel.bulk.selectedCount", { count: selection.count.value }) }}</span>
          <button class="danger compact" v-bind="writeGate(!canWriteRecords)" @click="removeSelectedRecords">
            {{ t("panel.bulk.deleteSelected", { count: selection.count.value }) }}
          </button>
          <button class="secondary compact" :disabled="actionBusy" @click="selection.clear">
            {{ t("panel.bulk.clearSelection") }}
          </button>
        </div>

        <div v-if="zone || busy" class="data-table">
          <div class="data-row table-head dns-row">
            <span class="select-cell">
              <input
                v-if="!isSecondary"
                type="checkbox"
                :disabled="!canWriteRecords"
                :checked="selection.allSelected(visibleRecordIds)"
                :aria-label="t('panel.bulk.selectAll')"
                @change="selection.toggleAll(visibleRecordIds)" />
            </span>
            <span>{{ t("panel.dns.type") }}</span>
            <span>{{ t("panel.dns.host") }}</span>
            <span>{{ t("panel.dns.value") }}</span>
            <span>{{ t("panel.dns.ttl") }}</span>
            <span>{{ t("panel.dns.actions") }}</span>
          </div>
          <div v-for="record in recordPager.items.value" :key="record.id" class="data-row dns-row">
            <span class="select-cell">
              <input
                v-if="!isSecondary"
                type="checkbox"
                :disabled="!canWriteRecords"
                :checked="selection.isSelected(record.id)"
                :aria-label="t('panel.bulk.selectRow', { name: displayHost(record.host) })"
                @change="selection.toggle(record.id)" />
            </span>
            <!-- Kayıt tipi bir DURUM değil, bir SINIFLANDIRMA: yeşil "ok" rozeti değil nötr
                 etiket (gerekçe .type-tag kuralında). -->
            <strong class="type-tag">{{ record.type }}</strong>
            <div class="cell-main">
              <strong>{{ displayHost(record.host) }}</strong>
              <small v-if="record.isManaged">{{ t("panel.dns.managedRecord") }}</small>
            </div>
            <span class="record-value">{{ displayValue(record) }}</span>
            <span>{{ record.ttl }}</span>
            <span class="row-actions">
              <template v-if="!isSecondary">
                <button class="secondary compact" v-bind="writeGate(!canWriteRecords)" @click="editRecord(record)">{{ t("panel.dns.edit") }}</button>
                <button class="danger compact" v-bind="writeGate(!canWriteRecords)" @click="removeRecord(record.id)">{{ t("panel.dns.delete") }}</button>
              </template>
              <small v-else>{{ t("panel.dns.secondaryReadOnlyTag") }}</small>
            </span>
          </div>
          <!-- Bölge gelmeden "kayıt yok" yazıyordu; dolu bir DNS bölgesi boş sanılabiliyordu. -->
          <p v-if="!zone && busy" class="empty-state" role="status" aria-live="polite">{{ t("panel.common.loading") }}</p>
          <p v-else-if="zoneConfirmed && !zone?.records.length" class="empty-state">{{ t("panel.dns.noRecords") }}</p>
        </div>

        <TablePager v-if="zone" :state="recordPager" :disabled="busy || actionBusy" />
      </template>

      <!-- Bölge ayarları gelmeden alanlar boş görünüyordu; dolu bir zone tanımsız sanılıp
           boş değerlerle kaydedilebiliyordu. -->
      <p v-else-if="!zone && busy" class="empty-state" role="status" aria-live="polite">{{ t("panel.common.loading") }}</p>

      <form v-else-if="zone" class="form-surface" @submit.prevent="saveZone">
        <div class="section-head">
          <div>
            <h4>{{ t("panel.dns.zoneSettings") }}</h4>
            <p class="meta">{{ t("panel.dns.zoneSettingsHint") }}</p>
          </div>
          <button class="compact" v-bind="writeGate(!canWriteZone)">{{ t("panel.dns.saveZone") }}</button>
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

      <!-- BÖLGE TÜRÜ: birincil (master) ↔ ikincil (slave). Plesk'in "Switch to Secondary" /
           "Switch to Primary" düğmelerinin karşılığı; ikincilde master IP listesi girilir. -->
      <div v-if="zone && activeTab === 'settings'" class="form-surface zone-mode-card">
        <div class="section-head">
          <div>
            <h4>{{ t("panel.dns.zoneTypeTitle") }}</h4>
            <p class="meta">{{ t("panel.dns.zoneTypeHint") }}</p>
          </div>
          <span :class="['status-pill', zoneConfirmed && !isSecondary && !hasConfigurationConflict ? 'ok' : 'warn']">
            {{ zone.modeConfigurationConflict ? t("panel.dns.configurationConflictTitle") : isSecondary ? t("panel.dns.zoneTypeSecondary") : t("panel.dns.zoneTypePrimary") }}
          </span>
        </div>

        <!-- Panelin kaydı ile Windows'un gerçeği ayrıldıysa SUSMAYIZ: bu durumda ya kayıt yazma
             sessizce başarısız olur ya da bölge master'dan hiç beslenmez. -->
        <p v-if="zone.statusMessage === 'dns.zone_type_mismatch'" class="notice page-notice error" role="alert">
          {{ t("dns.zone_type_mismatch") }} ({{ t("panel.dns.windowsZoneType") }}: {{ zone.windowsZoneType || "—" }})
        </p>

        <template v-if="isSecondary">
          <p v-if="!zone.modeConfigurationConflict" class="field-hint">{{ t("panel.dns.secondaryActiveHint", { masters: zone.masterServers.join(", ") || "—" }) }}</p>
          <div class="row-actions">
            <button v-if="isAdmin" class="secondary compact" v-bind="writeGate(!canManageDnsServer || hasConfigurationConflict)" @click="transferNow">
              {{ modeBusy ? t("panel.dns.zoneTypeWorking") : t("panel.dns.transferNow") }}
            </button>
          </div>
        </template>

        <template v-else>
          <!-- Sunucu geneli listeden gelen IP'ler de burada gösterilir: bölgede tek IP görünürken
               transferi fiilen üç sunucunun alabiliyor olması, okunamayan bir güvenlik ayarıdır. -->
          <p v-if="!zone.transferConfigurationConflict && zone.effectiveNotifyServers.length" class="field-hint">
            {{ t("panel.dns.effectiveNotifyHint", { list: zone.effectiveNotifyServers.join(", ") }) }}
          </p>
        </template>

        <div v-if="isAdmin && (isSecondary || zone.modeConfigurationConflict)" class="row-actions">
          <button class="secondary compact" v-bind="writeGate(!canManageDnsServer)" @click="switchToPrimary">
            {{ modeBusy ? t("panel.dns.zoneTypeWorking") : t("panel.dns.switchToPrimary") }}
          </button>
        </div>
        <!-- Keep both explicit mode choices available while resolving a shared conflict. -->
        <form v-if="isAdmin" class="mode-form" @submit.prevent="switchToSecondary">
          <label class="stacked-field" for="dns-master-servers">
            <span class="field-label">
              {{ t("panel.dns.masterServers") }}
              <FieldHint :label="t('panel.dns.masterServers')" :text="t('panel.dns.masterServersHelp')" />
            </span>
            <textarea id="dns-master-servers" v-model="masterServersText" rows="2" placeholder="192.0.2.53&#10;2001:db8::53"></textarea>
          </label>
          <button class="secondary compact danger" v-bind="writeGate(!canManageDnsServer)">
            {{ modeBusy ? t("panel.dns.zoneTypeWorking") : t(isSecondary ? "panel.dns.saveSecondarySettings" : "panel.dns.switchToSecondary") }}
          </button>
        </form>
      </div>

      <div v-if="zone && !isSecondary" class="form-surface dnssec-card">
        <div class="section-head">
          <div>
            <h4>{{ t("panel.dns.dnssecTitle") }}</h4>
            <p class="meta">{{ t("panel.dns.dnssecHint") }}</p>
          </div>
          <span :class="['status-pill', zoneConfirmed && zone.isDnssecSigned ? 'ok' : '']">
            {{ zone.isDnssecSigned ? t("panel.dns.dnssecOn") : t("panel.dns.dnssecOff") }}
          </span>
        </div>
        <p v-if="!zone.isWindowsDnsAvailable" class="field-hint">{{ t("panel.dns.dnssecRequiresWindows") }}</p>
        <template v-else>
          <div v-if="isAdmin" class="row-actions">
            <button v-if="!zone.isDnssecSigned" class="compact" v-bind="writeGate(!canManageDnsServer || hasConfigurationConflict)" @click="toggleDnssec(true)">
              {{ dnssecBusy ? t("panel.dns.dnssecWorking") : t("panel.dns.dnssecEnable") }}
            </button>
            <button v-else class="secondary compact danger" v-bind="writeGate(!canManageDnsServer || hasConfigurationConflict)" @click="toggleDnssec(false)">
              {{ dnssecBusy ? t("panel.dns.dnssecWorking") : t("panel.dns.dnssecDisable") }}
            </button>
          </div>
          <div v-if="zone.isDnssecSigned" class="dnssec-ds">
            <p class="field-label">
              {{ t("panel.dns.dnssecDsTitle") }}
              <FieldHint :label="t('panel.dns.dnssecDsTitle')" :text="t('panel.dns.dnssecDsHint')" />
            </p>
            <pre v-if="zone.dnssecDsRecords.length" class="code-viewer ds-box">{{ zone.dnssecDsRecords.join("\n") }}</pre>
            <p v-else class="field-hint">{{ t("panel.dns.dnssecDsUnavailable") }}</p>
          </div>
        </template>
      </div>
    </template>
    <p v-else class="meta" role="status">{{ t("panel.dns.accessHint") }}</p>
  </section>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, reactive, ref, watch, type Ref } from "vue";
import { getSessionEpoch } from "../api/client";
import { useI18n } from "vue-i18n";
import { deleteDnsRecord, getDnsZone, installWindowsDns as installWindowsDnsApi, resetDnsDefaults, setDnsZoneMode, setDnssec, transferDnsZoneNow, updateDnsZone, upsertDnsRecord, type DnsRecord, type DnsZone } from "../api/dns";
import { useConfirm, type ConfirmOptions } from "../components/ConfirmDialog.vue";
import FieldHint from "../components/FieldHint.vue";
import TablePager from "../components/TablePager.vue";
import SectionLoadError from "../components/SectionLoadError.vue";
import { useBulkAction, useBulkSelection } from "../composables/useBulkSelection";
import { usePagination } from "../composables/usePagination";
import { extractApiDetails, extractApiMessage, useAuthStore } from "../stores/auth";
import { useSitesStore } from "../stores/sites";
import { useSubscriptionGate } from "../composables/useSubscriptionGate";
import { UserFacingError } from "../utils/apiError";

const { t } = useI18n();
// Askidaki abonelikte YAZMA dugmeleri bastan devre disi + sebebi title/aria ile soylenir
// (bkz. composables/useSubscriptionGate.ts). Okuma dugmeleri kapiya girmez.
const { writeGate, locked } = useSubscriptionGate();
const { confirm } = useConfirm();
const authStore = useAuthStore();
const sitesStore = useSitesStore();
const siteName = computed(() => sitesStore.selectedSiteName);
const zone = ref<DnsZone | null>(null);
const zoneConfirmed = ref(false);
const loadError = ref("");
const busy = ref(false);
const mutationToken = ref<symbol | null>(null);
const installingWindowsDns = ref(false);
const dnssecBusy = ref(false);
const notice = ref("");
const noticeError = ref(false);
const nameServersText = ref("");
const zoneTransferText = ref("");
// İkincil (slave) kip: master DNS sunucularının IP'leri + kip değiştirme işlemi sürüyor mu.
const masterServersText = ref("");
const modeBusy = ref(false);
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

// Büyük bölgeler (yüzlerce kayıt) tek seferde DOM'a basılmaz (bkz. composables/usePagination.ts).
const zoneRecords = computed(() => zone.value?.records ?? []);
const recordPager = usePagination(zoneRecords);
const selection = useBulkSelection();
const bulk = useBulkAction();
const visibleRecordIds = computed(() => recordPager.items.value.map((record) => record.id));
const actionBusy = computed(() => mutationToken.value !== null || bulk.busy.value);
const isAdmin = computed(() => authStore.session?.user?.role === "Administrator");
const viewEpoch = getSessionEpoch();
const canReadZone = computed(() => authStore.session?.isAuthenticated === true &&
  (isAdmin.value || authStore.session?.user?.role === "Reseller"));
const canReload = computed(() => canReadZone.value && !!siteName.value && !busy.value && !actionBusy.value);
const canWriteZone = computed(() => canReadZone.value && zoneConfirmed.value && !!siteName.value && !locked.value && !busy.value && !actionBusy.value);
const hasConfigurationConflict = computed(() => zone.value?.modeConfigurationConflict === true || zone.value?.transferConfigurationConflict === true || zone.value?.statusMessage === "dns.shared_configuration_conflict");
const canWriteRecords = computed(() => canWriteZone.value && !isSecondary.value && !hasConfigurationConflict.value);
const canManageDnsServer = computed(() => canWriteZone.value && isAdmin.value);
const canInstallWindowsDns = computed(() => canManageDnsServer.value && zone.value?.isWindowsDnsAvailable === false);

const zoneStatus = computed(() => zoneConfirmed.value && zone.value ? t(zone.value.statusMessage) :
  t(busy.value ? "panel.dns.loading" : "panel.dns.stateUnconfirmed"));
// Bölge yüklenmeden ikincil SAYILMAZ: aksi hâlde ilk render'da kayıt düğmeleri bir an gizlenirdi.
const isSecondary = computed(() => zone.value?.isSecondary === true);
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

let zoneRequest = 0;
let disposed = false;
let savedZoneDraft = "", savedMasterDraft = "";
onBeforeUnmount(() => { disposed = true; zoneRequest++; });
function actorKey() {
  const session = authStore.session;
  return JSON.stringify([session?.isAuthenticated, session?.user?.id, session?.user?.role,
    session?.isSubUser, session?.allowedSections]);
}
function zoneContext() {
  const site = siteName.value, request = zoneRequest, epoch = getSessionEpoch();
  const session = authStore.session, actor = actorKey();
  return { site, current: () => !disposed && viewEpoch === getSessionEpoch() && siteName.value === site && zoneRequest === request &&
    getSessionEpoch() === epoch && authStore.session === session && actorKey() === actor && canReadZone.value };
}
watch([siteName, () => authStore.session, actorKey], () => {
  zone.value = null;
  zoneConfirmed.value = false; loadError.value = "";
  notice.value = "";
  nameServersText.value = ""; zoneTransferText.value = ""; masterServersText.value = "";
  zoneForm.zoneName = "";
  savedZoneDraft = zoneDraft(); savedMasterDraft = "";
  selection.clear(); resetRecordForm();
  mutationToken.value = null;
  modeBusy.value = false; dnssecBusy.value = false; installingWindowsDns.value = false;
  void load(true);
}, { immediate: true, flush: "sync" });
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

async function load(siteChanged = false) {
  if (disposed || viewEpoch !== getSessionEpoch() || (!siteChanged && !canReload.value)) return;
  zoneRequest++;
  const context = zoneContext();
  if (!context.site || !canReadZone.value) { busy.value = false; return; }
  const draft = captureDrafts();
  busy.value = true;
  zoneConfirmed.value = false; loadError.value = "";
  try {
    const result = await getDnsZone(context.site);
    if (!context.current()) return;
    applyZone(result, draft);
    // Başka bir bölgenin/eski listenin seçimi taşınmasın.
    selection.keepOnly(zone.value?.records.map((record) => record.id) ?? []);
  } catch (error) {
    if (context.current()) loadError.value = resolveApiMessage(error);
  } finally {
    if (context.current()) busy.value = false;
  }
}

/**
 * Sunucudan gelen bölgeyi ekrana yansıtır.
 *
 * Tek yerde toplandı: aynı kopyalama yükleme, varsayılana sıfırlama ve kip değiştirme yollarında
 * ayrı ayrı yazılıydı; yeni bir alan (master sunucular) eklendiğinde bir yolda güncellenip
 * diğerinde unutulması kaçınılmazdı — ekran o zaman eski değeri gösterip kaydederdi.
 */
function zoneDraft() { return JSON.stringify([zoneForm.zoneName, nameServersText.value, zoneTransferText.value]); }
function recordDraft() { return JSON.stringify([editingId.value, showRecordForm.value, recordForm]); }
function captureDrafts(saveZone = false, saveMasters = false) {
  return { zone: zoneDraft(), masters: masterServersText.value,
    applyZone: saveZone || zoneDraft() === savedZoneDraft,
    applyMasters: saveMasters || masterServersText.value === savedMasterDraft };
}
function applyZone(next: DnsZone, draft = captureDrafts()) {
  zone.value = next;
  zoneConfirmed.value = true; loadError.value = "";
  if (draft.applyZone && zoneDraft() === draft.zone) {
    zoneForm.zoneName = next.zoneName;
    nameServersText.value = next.nameServers.join("\n");
    zoneTransferText.value = next.zoneTransferIps.join("\n");
  }
  if (draft.applyMasters && masterServersText.value === draft.masters) masterServersText.value = next.masterServers.join("\n");
  savedZoneDraft = JSON.stringify([next.zoneName, next.nameServers.join("\n"), next.zoneTransferIps.join("\n")]);
  savedMasterDraft = next.masterServers.join("\n");
}

async function saveZone() {
  const draft = captureDrafts(true);
  const payload = {
      zoneName: zoneForm.zoneName,
      nameServers: splitLines(nameServersText.value),
      zoneTransferIps: splitLines(zoneTransferText.value)
  };
  await mutateZone(site => updateDnsZone(site, payload), () => zoneDraft() === draft.zone ? "panel.dns.zoneSaved" : "panel.dns.savedWithDraft",
    busy, undefined, { drafts: draft });
}

async function saveRecord() {
  if (!canWriteRecords.value) return;
  const draft = recordDraft();
  const payload = {
      id: editingId.value || undefined,
      type: recordForm.type,
      host: recordForm.host,
      value: composeRecordValue(),
      ttl: recordForm.ttl,
      priority: recordForm.priority
  };
  await mutateZone(site => upsertDnsRecord(site, payload), () => recordDraft() === draft ? "panel.dns.recordSaved" : "panel.dns.savedWithDraft", busy, () => {
    if (recordDraft() === draft) resetRecordForm();
  }, { records: true });
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
  if (!canWriteRecords.value) return;

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
  await mutateZone(site => deleteDnsRecord(site, recordId), "panel.dns.recordDeleted", busy, undefined, { records: true, confirmation: {
    title: t("panel.confirm.deleteDnsRecordTitle", { name: record ? displayHost(record.host) : "" }),
    message: t("panel.dns.confirmDelete"),
    impact,
  } });
}

/**
 * TOPLU DNS KAYDI SİLME — ONAY ZORUNLU, KISMİ BAŞARISIZLIK SESSİZ KALMAZ.
 *
 * DNS kaydı silmek alan adını dünyaya karşı bozabilir (A/MX gidince site ve posta düşer), bu
 * yüzden onay kaç kaydın gideceğini VE hangilerini (tip + host + değer) yazar. Panelin YÖNETTİĞİ
 * kayıtlar ayrıca sayılır: onlar silinse bile "Varsayılanlara dön" ile geri gelir, kullanıcının
 * bunu bilmesi kararı değiştirir.
 *
 * `DELETE` tek kayıt alır; kayıtlar tek tek denenir ve özet kaçının silindiğini/silinemediğini
 * sebebiyle söyler.
 */
async function removeSelectedRecords() {
  if (!canWriteRecords.value) return;
  const context = zoneContext();
  const targetSite = context.site;
  const isCurrent = context.current;

  const targets = zoneRecords.value.filter((record) => selection.isSelected(record.id));
  if (targets.length === 0) {
    return;
  }

  const labels = targets.map((record) =>
    `${record.type} ${displayHost(record.host)} → ${displayValue(record)}`);
  const impact = bulk.impactList(labels);
  const managed = targets.filter((record) => record.isManaged).length;
  if (managed > 0) {
    impact.unshift(t("panel.dns.bulkManagedWarn", { count: managed }));
  }

  const token = Symbol("dns-bulk");
  mutationToken.value = token;
  const draft = captureDrafts();
  try {
    const onay = await confirm({
      title: t("panel.bulk.confirmDeleteTitle", { count: targets.length }),
      message: t("panel.dns.confirmBulkDelete", { count: targets.length }),
      impact,
      requireText: String(targets.length)
    });
    if (!onay || !isCurrent() || locked.value) return;

    notice.value = "";
    const outcome = await bulk.runBulk(
      targets,
      (record) => `${record.type} ${displayHost(record.host)}`,
      async (record) => {
        try {
          const result = await deleteDnsRecord(targetSite, record.id);
          if (isCurrent()) applyZone(result, draft);
        } catch (error) {
          if (isCurrent()) zoneConfirmed.value = false;
          throw error;
        }
      }, () => isCurrent() && !locked.value
    );
    if (!isCurrent()) return;
    selection.keepOnly(zoneRecords.value.map((record) => record.id));
    showNotice(bulk.deleteSummary(outcome), outcome.failures.length > 0);
    if (!zoneConfirmed.value) loadError.value = t("panel.dns.resultUnconfirmed");
  } finally {
    if (mutationToken.value === token) mutationToken.value = null;
  }
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
  if (!canWriteRecords.value) return;

  const managed = (zone.value?.records ?? []).filter((item) => item.isManaged).length;
  const impact = [
    `${t("panel.dns.zone")}: ${zone.value?.zoneName || siteName.value}`,
    `${t("panel.dns.managedRecord")}: ${managed}`
  ];

  // Normal onay: yalnız panelin yönettiği kayıtlar varsayılana döner (kapsam dar, yeniden düzenlenebilir).
  await mutateZone(resetDnsDefaults, "panel.dns.defaultsRestored", busy, undefined, { records: true, confirmation: {
    title: t("panel.confirm.restoreDnsDefaultsTitle"),
    message: t("panel.dns.confirmRestoreDefaults"),
    impact,
    confirmLabel: t("panel.dns.restoreDefaults"),
  } });
}

/**
 * BÖLGEYİ İKİNCİL (slave) YAP — ONAY ZORUNLU.
 *
 * Yıkıcı bir karardır: bölgenin yetkili kaynağı bu sunucudan alınıp master'a devredilir. Panelin
 * kayıtları silinmez ama YAYINDAN DÜŞER; master'da eksik bir kayıt varsa (örneğin MX) o alan adının
 * postası anında durur. Bu yüzden bölge adı yazdırılarak onaylatılır.
 */
async function switchToSecondary() {
  if (!canManageDnsServer.value) return;

  const masters = splitLines(masterServersText.value);
  if (masters.length === 0) {
    showNotice(t("dns.master_required"), true);
    return;
  }

  await mutateZone(site => setDnsZoneMode(site, true, masters), "panel.dns.secondaryEnabledMsg", modeBusy, undefined,
    { administrator: true, expected: result => result.isSecondary && result.masterServers.length > 0,
      drafts: captureDrafts(false, true), confirmation: {
    title: t(isSecondary.value ? "panel.dns.saveSecondarySettings" : "panel.dns.confirmSecondaryTitle"),
    message: t(isSecondary.value ? "panel.dns.confirmSecondarySettingsMessage" : "panel.dns.confirmSecondaryMessage", { zone: zone.value?.zoneName || siteName.value }),
    impact: [
      `${t("panel.dns.zone")}: ${zone.value?.zoneName || siteName.value}`,
      `${t("panel.dns.masterServers")}: ${masters.join(", ")}`,
      `${t("panel.dns.records")}: ${zone.value?.records.length ?? 0}`
    ],
    requireText: zone.value?.zoneName || siteName.value,
    confirmLabel: t(isSecondary.value ? "panel.dns.saveSecondarySettings" : "panel.dns.switchToSecondary")
  } });
}

/** Birincile dönüş: panelin kayıtları yeniden yayınlanır (Plesk de eski kayıtları geri getirir). */
async function switchToPrimary() {
  if (!canManageDnsServer.value) return;

  await mutateZone(site => setDnsZoneMode(site, false, []), "panel.dns.primaryRestoredMsg", modeBusy, undefined, {
    administrator: true, expected: result => !result.isSecondary, confirmation: {
    title: t("panel.dns.confirmPrimaryTitle"),
    message: t("panel.dns.confirmPrimaryMessage", { zone: zone.value?.zoneName || siteName.value }),
    confirmLabel: t("panel.dns.switchToPrimary")
  } });
}

/** Master'dan tam transferi (AXFR) hemen başlatır; onay istemez çünkü yıkıcı değildir. */
async function transferNow() {
  if (!canManageDnsServer.value || !isSecondary.value || hasConfigurationConflict.value) return;
  await mutateZone(transferDnsZoneNow, "panel.dns.transferStartedMsg", modeBusy, undefined, { administrator: true });
}

async function mutateZone(action: (site: string) => Promise<DnsZone>, successKey: string | (() => string), flag: Ref<boolean> = busy, onSuccess?: () => void,
    options: { confirmation?: ConfirmOptions; administrator?: boolean; records?: boolean; drafts?: ReturnType<typeof captureDrafts>; expected?: (result: DnsZone) => boolean } = {}) {
  const context = zoneContext();
  if (!context.current() || !canWriteZone.value || (options.administrator && !isAdmin.value) || (options.records && (isSecondary.value || hasConfigurationConflict.value))) return;
  const token = Symbol("dns-mutation"), draft = options.drafts ?? captureDrafts();
  mutationToken.value = token;
  flag.value = true;
  try {
    if (options.confirmation && !await confirm(options.confirmation)) return;
    if (!context.current() || locked.value || (options.administrator && !isAdmin.value)) return;
    notice.value = "";
    const result = await action(context.site);
    if (!context.current()) return;
    if (options.expected && !options.expected(result)) throw new UserFacingError("panel.dns.resultUnconfirmed");
    const messageKey = typeof successKey === "function" ? successKey() : successKey;
    applyZone(result, draft);
    onSuccess?.();
    if (hasConfigurationConflict.value) showNotice(t("dns.shared_configuration_conflict"), true);
    else showNotice(t(messageKey), false);
  } catch (error) {
    if (context.current()) {
      zoneConfirmed.value = false;
      loadError.value = t("panel.dns.resultUnconfirmed");
      showNotice(resolveApiMessage(error), true);
    }
  } finally {
    if (mutationToken.value === token) { flag.value = false; mutationToken.value = null; }
  }
}

async function installWindowsDns() {
  if (!canInstallWindowsDns.value) return;
  await mutateZone(installWindowsDnsApi, "panel.dns.windowsInstalled", installingWindowsDns, undefined,
    { administrator: true, expected: result => result.isWindowsDnsAvailable });
}

async function toggleDnssec(enable: boolean) {
  if (!canManageDnsServer.value || isSecondary.value || hasConfigurationConflict.value || !zone.value?.isWindowsDnsAvailable) return;

  // KAPATMAK yıkıcıdır: bölgenin imzası kalkar, üst kayıttaki DS kaydı geçersizleşir ve
  // doğrulayan çözümleyiciler alan adını GEÇİCİ OLARAK ÇÖZEMEZ. Onaysız yapılmaz.
  const confirmation = !enable ? {
      title: t("panel.dns.confirmDnssecDisableTitle"),
      message: t("panel.dns.confirmDnssecDisableMessage", { zone: zone.value?.zoneName || siteName.value }),
      confirmLabel: t("panel.dns.dnssecDisable")
    } : undefined;

  await mutateZone(site => setDnssec(site, enable), enable ? "panel.dns.dnssecEnabledMsg" : "panel.dns.dnssecDisabledMsg", dnssecBusy, undefined,
    { administrator: true, confirmation, expected: result => result.isDnssecSigned === enable });
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

.dns-notice { display: flex; align-items: flex-start; gap: var(--sp-12); }
.dns-notice > span { min-width: 0; overflow-wrap: anywhere; }
.notice-close { flex: 0 0 28px; width: 28px; height: 28px; padding: 0; color: inherit; }

.dns-last-known,
.dns-info {
  margin: 0;
  padding: var(--sp-12) var(--sp-16);
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-sm);
  background: var(--surface-2);
  color: var(--text-soft);
  line-height: 1.6;
  overflow-wrap: anywhere;
}

.dns-last-known {
  border-inline-start: 3px solid var(--warn-1);
}
.dns-shared-note strong, .dns-policy-conflict strong { color: var(--text-strong); }
.dns-shared-note p, .dns-policy-conflict p { margin: var(--sp-6) 0 0; }
.dns-policy-conflict ul { margin: var(--sp-8) 0; padding-inline-start: var(--sp-20); }

/* `.section-head` KOPYASI SİLİNDİ: ortak tanım base.css'te. Aradaki tek fark boşluktu
   (`--sp-10` -> `--sp-12`); hizalama (`start` = `flex-start`) ve `flex-wrap` zaten aynıydı.
   Görünen etki: bölüm başlığı ile yanındaki düğme arası 2px açılır. */

/* DNSSEC DS KAYITLARI — İKİ TANIMSIZ SINIF KAPATILDI.
   `dnssec-ds` ve `ds-box` şablonda yazılıydı ama HİÇBİR YERDE tanımlı değildi. CSS bunu hata
   saymaz: `<pre>` tarayıcı varsayılanıyla çiziliyordu ve İKİ TEMADA İKİ FARKLI ŞEY oluyordu —
   açık temada base.css'in `:is(.code-viewer, pre)` kuralı ona koyu kod zemini veriyor, koyu
   temada hiçbir şey vermiyordu. Üstelik bir DS kaydı tek parça uzun bir dizedir
   ("2371 13 2 9F86D08...") ve `<pre>` varsayılan olarak SARMAZ; kayıt kartın dışına taşıyordu.
   ÇÖZÜM YENİ BİR YÜZEY İCAT ETMEK DEĞİL: panelin zaten tanımlı kod yüzeyi olan `.code-viewer`
   şablonda eklendi (base.css, iki temada da ölçülü). `.ds-box` yalnız onun tek uymayan
   ölçüsünü düzeltir — `min-height: 260px`, iki satırlık bir DS kaydının altında 250px boşluk
   bırakırdı. */
.dnssec-ds {
  display: grid;
  gap: var(--sp-6);
}

.ds-box {
  min-height: 0;
}

/* NOT — BİLEREK DOKUNULMADI: bu ekranın `<h4>` kart başlıkları 13.5px, yani gövde metniyle
   aynı boyutta. Ama bu bir sapma DEĞİL, base.css'in ölçeği: `--fs-lg` de 1rem = 13.5px ve
   `.panel h3` de aynı boyutta çiziliyor. Panelin tipografisi boyutla değil AĞIRLIKLA
   ayrışıyor. Burada tek başına büyütmek, bu ekranın h4'ünü panelin geri kalanındaki h3'ten
   büyük gösterirdi — yani rütbeyi ters çevirirdi. Ölçek kararı base.css'e aittir. */

/*
  YIKICI/GERİ ALICI EYLEM GÖRSEL OLARAK AYRI.
  Araç şeridinde üç düğme yan yana duruyordu: "Windows DNS kur", "Kayıt ekle" (birincil) ve
  "Varsayılana sıfırla". Üçüncüsü diğer ikisinden farklı bir sınıfta: panelin yönettiği bütün
  kayıtları geri alır. Aynı kümede, aynı boşlukla dizilince "ekle" ile "sıfırla" arasındaki
  fark yalnızca okumaya kalıyordu. İnce bir ayraç, iki kümeyi bakışla ayırır — onay diyaloğu
  yerine geçmez, ona GİDEN yolu belirginleştirir.
*/
.reset-group {
  display: flex;
  align-items: center;
  padding-inline-start: var(--sp-12);
  border-inline-start: 1px solid var(--panel-border);
}

/* Alanlar tipe göre gelip gittiği için sabit kolonlu grid yerine esnek satır. */
.record-main {
  display: flex;
  flex-wrap: wrap;
  gap: var(--sp-10);
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
  gap: var(--sp-10);
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
  grid-template-columns: 30px 82px minmax(120px, 0.7fr) minmax(220px, 1.2fr) 80px minmax(170px, auto);
}

/*
  KAYIT TİPİ ARTIK "OK" ROZETİ DEĞİL.
  ÖLÇÜLDÜ: her satırın tip hücresi `class="status-pill ok"` ile çiziliyordu, yani YEŞİL bir
  durum rozeti. Tipik bir bölgede 15-40 kayıt var; sayfa açılınca alt alta 40 yeşil "iyi"
  rozeti diziliyordu. İki zararı vardı: (1) "A" ya da "TXT" bir sağlık durumu değil, bir
  sınıflandırmadır — rozet olmayan bir şeyi söylüyordu; (2) aynı sayfanın ÜSTÜNDE, araç
  şeridinde, bölgenin GERÇEK sağlık rozeti duruyor ve o da yeşil; 40 sahte yeşilin içinde
  gerçek olan görünmez oluyordu. Renk ancak seyrek kullanıldığında anlam taşır.
  Nötr, tek aralıklı bir ETİKET seçildi: tipler sabit genişlikte olduğu için sütunda hizalı
  durur ve göz "MX nerede" sorusunu tarayarak cevaplayabilir. Bilgi aynen yerinde.
*/
.type-tag {
  display: inline-flex;
  width: fit-content;
  align-items: center;
  padding: var(--sp-2) var(--sp-8);
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-sm);
  background: var(--surface-2);
  color: var(--text-soft);
  font-family: var(--font-mono);
  font-size: var(--fs-2xs);
  font-weight: 700;
  letter-spacing: 0.02em;
}

/*
  DEĞER SÜTUNU TEK ARALIKLI.
  Bu sütunun içeriği IP (203.0.113.10), IPv6 (2001:db8::10), SPF/DKIM dizeleri ve TLSA
  parmak izleri: hepsi karakter karakter okunan, karşılaştırılan veriler. Oransal yazı
  tipinde "1" ile "l", "0" ile "O" ayırt edilmiyordu ve iki IP'nin farkı gözle bulunamıyordu.
*/
.record-value {
  font-family: var(--font-mono);
  font-size: var(--fs-xs);
  color: var(--text-soft);
  overflow-wrap: anywhere;
}

/*
  TTL BİR SAYI: sağa hizalı ve tek genişlikli rakamlarla.
  Sola hizalı olduğunda "60", "3600" ve "86400" farklı yerlerde bitiyor; kısa TTL'i (yani
  yayılması hızlı olan kaydı) listeden bakışla ayırmak mümkün değildi. Başlık da hizaya
  dahil, aksi hâlde sütunun neyi saydığı bulanıklaşır.
*/
.dns-row > :nth-child(5) {
  text-align: right;
  font-variant-numeric: tabular-nums;
}

.zone-mode-card {
  display: grid;
  gap: var(--sp-10);
}

/* Master listesi ile düğme aynı satırda; dar ekranda alt alta iner. */
.mode-form {
  display: flex;
  gap: var(--sp-10);
  align-items: end;
  flex-wrap: wrap;
}

.mode-form .stacked-field {
  flex: 1 1 260px;
  min-width: 0;
}

.select-cell {
  display: flex;
  align-items: center;
}

.select-cell input {
  width: 16px;
  height: 16px;
  margin: 0;
}

/* Seçim şeridi: içinde bir SİLME düğmesi var ve yalnız seçim yapılınca belirir. Zemini
   `--surface-2` iken sayfadaki diğer gri kutulardan ayırt edilemiyordu; vurgu tonlu kenar,
   "şu an bir seçimin var" durumunu şeridin kendisine yazar. */
.bulk-bar {
  display: flex;
  align-items: center;
  gap: var(--sp-10);
  flex-wrap: wrap;
  padding: var(--sp-8) var(--sp-10);
  border: 1px solid color-mix(in srgb, var(--accent-1) 35%, var(--panel-border));
  border-radius: var(--radius-sm);
  background: var(--surface-2);
}

.bulk-bar button {
  width: auto;
}

@media (max-width: 1080px) {
  /*
    ÖNCESİ: `flex: 1 1 100%` ile HER alan tam genişliğe geçiyordu — 90px'lik "Tip" seçimi ve
    95px'lik "TTL" kutusu da dahil. Dört-beş satırlık bir form, üç rakamlık bir alan için
    ekranın tamamını harcıyordu.
    ŞİMDİ: kısa alanlar 160px tabanla yan yana sarılır (900px'de tip + host + öncelik + TTL
    tek satıra sığar), yalnız gerçekten uzun olan DEĞER alanı tam genişlik alır.
  */
  .record-main > .stacked-field {
    flex: 1 1 160px;
  }

  .record-main > .value-field {
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
