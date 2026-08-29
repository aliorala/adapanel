<template>
  <section class="panel module-shell">
    <div class="module-head">
      <div>
        <h2>{{ t("panel.audit.title") }}</h2>
        <p>{{ isAdmin ? t("panel.audit.subtitleAdmin") : t("panel.audit.subtitleTenant") }}</p>
      </div>
      <button class="secondary compact" :disabled="loading" @click="reload">
        {{ t("panel.audit.refresh") }}
      </button>
    </div>

    <p
      v-if="notice"
      :class="['notice', 'page-notice', { error: noticeError }]"
      :role="noticeError ? 'alert' : 'status'">
      {{ notice }}
    </p>

    <!-- FİLTRELER -->
    <section class="form-surface settings-section">
      <div class="section-head">
        <div>
          <h3>{{ t("panel.audit.filterTitle") }}</h3>
          <p class="meta">{{ t("panel.audit.filterHint") }}</p>
        </div>
      </div>

      <div class="form-grid">
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.audit.filter.from") }}</span>
          <input v-model="filterForm.from" type="date" />
        </label>
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.audit.filter.to") }}</span>
          <input v-model="filterForm.to" type="date" />
        </label>
        <label v-if="isAdmin" class="stacked-field">
          <span class="field-label">{{ t("panel.audit.filter.user") }}</span>
          <input v-model.trim="filterForm.userName" type="text" spellcheck="false" autocomplete="off" />
        </label>
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.audit.filter.action") }}</span>
          <select v-model="filterForm.action">
            <option value="">{{ t("panel.audit.filter.allActions") }}</option>
            <option v-for="group in actionGroups" :key="group" :value="group">{{ group }}</option>
            <option v-for="item in availableActions" :key="item" :value="item">{{ item }}</option>
          </select>
        </label>
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.audit.filter.resource") }}</span>
          <input v-model.trim="filterForm.resource" type="text" spellcheck="false" autocomplete="off" />
          <small class="field-hint">{{ t("panel.audit.filter.resourceHint") }}</small>
        </label>
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.audit.filter.search") }}</span>
          <input v-model.trim="filterForm.search" type="search" spellcheck="false" autocomplete="off" />
          <small class="field-hint">{{ t("panel.audit.filter.searchHint") }}</small>
        </label>
      </div>

      <div class="row-actions">
        <button class="compact" :disabled="loading" @click="applyFilters">
          {{ t("panel.audit.filter.apply") }}
        </button>
        <button class="secondary compact" :disabled="loading" @click="resetFilters">
          {{ t("panel.audit.filter.reset") }}
        </button>
        <button class="secondary compact" :disabled="exporting || total === 0" @click="exportCsv">
          {{ exporting ? t("panel.audit.exporting") : t("panel.audit.exportCsv") }}
        </button>
      </div>
      <p class="field-hint">{{ t("panel.audit.exportHint") }}</p>
    </section>

    <!-- KAYIT LİSTESİ -->
    <section class="settings-section">
      <div class="section-head">
        <div>
          <h3>{{ t("panel.audit.listTitle") }}</h3>
          <p class="meta">{{ t("panel.audit.matchCount", { count: total }) }}</p>
        </div>
      </div>

      <!-- "Okunamadı" ile "kayıt yok" ayrı durumlardır: ikisini karıştırmak, yöneticinin
           hiç kayıt tutulmadığını sanmasına yol açardı. -->
      <SectionLoadError v-if="listLoad.message.value" :message="listLoad.message.value" :busy="loading" @retry="reload" />

      <p v-else-if="loading" class="empty-state" role="status" aria-live="polite">
        {{ t("panel.common.loading") }}
      </p>

      <p v-else-if="items.length === 0" class="empty-state">
        {{ hasActiveFilter ? t("panel.audit.emptyFiltered") : t("panel.audit.empty") }}
      </p>

      <div v-else class="data-table">
        <div class="data-row table-head audit-row">
          <span>{{ t("panel.audit.column.time") }}</span>
          <span>{{ t("panel.audit.column.user") }}</span>
          <span>{{ t("panel.audit.column.action") }}</span>
          <span>{{ t("panel.audit.column.resource") }}</span>
          <span>{{ t("panel.audit.column.details") }}</span>
          <span>{{ t("panel.audit.column.ip") }}</span>
        </div>

        <div v-for="item in items" :key="item.id" class="data-row audit-row">
          <span class="cell-muted">{{ formatUtc(item.createdAtUtc) }}</span>
          <span>{{ item.userName }}</span>
          <span class="cell-main"><strong>{{ item.action }}</strong></span>
          <span class="cell-muted breakable">{{ item.resource || "-" }}</span>
          <span class="cell-muted breakable">{{ item.details || "-" }}</span>
          <span class="cell-muted">{{ item.ipAddress || t("panel.common.notAvailable") }}</span>
        </div>
      </div>

      <div v-if="totalPages > 1" class="pager row-actions">
        <button class="secondary compact" :disabled="page <= 1 || loading" @click="goToPage(page - 1)">
          {{ t("panel.audit.previousPage") }}
        </button>
        <span class="pager-status" role="status">{{ t("panel.audit.pageOf", { page, pages: totalPages }) }}</span>
        <button class="secondary compact" :disabled="page >= totalPages || loading" @click="goToPage(page + 1)">
          {{ t("panel.audit.nextPage") }}
        </button>
      </div>
    </section>

    <!-- SAKLAMA POLİTİKASI (yalnız yönetici) -->
    <section v-if="isAdmin" class="form-surface settings-section">
      <div class="section-head">
        <div>
          <h3>{{ t("panel.audit.retentionTitle") }}</h3>
          <p class="meta">{{ t("panel.audit.retentionHint") }}</p>
        </div>
      </div>

      <SectionLoadError
        v-if="retentionLoad.message.value"
        :message="retentionLoad.message.value"
        :busy="savingRetention"
        @retry="loadRetention" />

      <template v-else-if="retention">
        <div class="summary-strip">
          <div class="summary-item">
            <span>{{ t("panel.audit.retention.totalCount") }}</span>
            <strong>{{ retention.totalCount }}</strong>
          </div>
          <div class="summary-item">
            <span>{{ t("panel.audit.retention.oldest") }}</span>
            <strong>{{ formatUtc(retention.oldestAtUtc) }}</strong>
          </div>
          <div class="summary-item">
            <span>{{ t("panel.audit.retention.lastPurge") }}</span>
            <strong>{{ formatUtc(retention.lastPurgeAtUtc) }}</strong>
          </div>
          <div class="summary-item">
            <span>{{ t("panel.audit.retention.lastPurgedCount") }}</span>
            <strong>{{ retention.lastPurgedCount }}</strong>
          </div>
        </div>

        <div class="form-grid">
          <label class="stacked-field">
            <span class="field-label">{{ t("panel.audit.retention.days") }}</span>
            <input v-model.number="retentionDays" type="number" min="0" max="3650" />
            <small class="field-hint">{{ t("panel.audit.retention.daysHint") }}</small>
          </label>
        </div>

        <div class="row-actions">
          <button class="compact" :disabled="savingRetention" @click="saveRetention">
            {{ savingRetention ? t("panel.audit.retention.saving") : t("panel.audit.retention.save") }}
          </button>
          <button class="secondary compact" :disabled="savingRetention || retentionDays <= 0" @click="purgeNow">
            {{ t("panel.audit.retention.purgeNow") }}
          </button>
        </div>
      </template>
    </section>
  </section>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref } from "vue";
import { useI18n } from "vue-i18n";
import {
  downloadAuditLogsCsv,
  getAuditRetention,
  purgeAuditLogs,
  saveAuditRetention,
  searchAuditLogs,
  type AuditLogFilter,
  type AuditLogItem,
  type AuditRetentionSettings
} from "../api/audit";
import { useConfirm } from "../components/ConfirmDialog.vue";
import SectionLoadError from "../components/SectionLoadError.vue";
import { useNotice, useSectionError } from "../composables/useNotice";
import { useAuthStore } from "../stores/auth";
import { formatDateTime } from "../utils/datetime";

const { t } = useI18n();
const { confirm } = useConfirm();
const { notice, noticeError, showNotice, showError } = useNotice();
const listLoad = useSectionError();
const retentionLoad = useSectionError();
const authStore = useAuthStore();

// Saklama politikası SUNUCU GENELİ bir ayardır; kiracıya gösterilmez (uç zaten yalnız yönetici).
// Kullanıcı adı filtresi de aynı sebeple gizlenir: kiracı zaten yalnız kendi aboneliğini görür.
const isAdmin = computed(() => authStore.session?.user?.role === "Administrator");

const PAGE_SIZE = 25;

const items = ref<AuditLogItem[]>([]);
const availableActions = ref<string[]>([]);
const total = ref(0);
const page = ref(1);
const loading = ref(false);
const exporting = ref(false);
const savingRetention = ref(false);
const retention = ref<AuditRetentionSettings | null>(null);
const retentionDays = ref(0);

/** Formdaki ham değerler (tarihler yerel `date` girdisi). */
const filterForm = reactive({
  from: "",
  to: "",
  userName: "",
  action: "",
  resource: "",
  search: ""
});

/** Uygulanan filtre — forma her yazışta sorgu atmamak için ayrı tutulur. */
const appliedFilter = ref<AuditLogFilter>({});

const totalPages = computed(() => Math.max(1, Math.ceil(total.value / PAGE_SIZE)));
const hasActiveFilter = computed(() => Object.keys(appliedFilter.value).length > 0);

/**
 * Modül önekleri: kullanıcı önce "mail" gibi bir grup seçip sonra daralt(ma)abilsin.
 * Sunucu noktasız değeri ÖN EK olarak yorumlar; burada uydurulan bir sözleşme değil.
 */
const actionGroups = computed(() => {
  const groups = new Set<string>();
  for (const action of availableActions.value) {
    const index = action.indexOf(".");
    if (index > 0) {
      groups.add(action.slice(0, index));
    }
  }

  return [...groups].sort();
});

onMounted(async () => {
  await reload();
  if (isAdmin.value) {
    await loadRetention();
  }
});

function buildFilter(): AuditLogFilter {
  const filter: AuditLogFilter = {};

  // `date` girdisi yerel bir gün verir; sunucu UTC bekler. Bitiş günü GÜN SONUNA çekilir,
  // yoksa "1 Ocak'a kadar" seçen kullanıcı 1 Ocak kayıtlarını göremez ve filtre yalan söylerdi.
  if (filterForm.from) {
    filter.fromUtc = new Date(`${filterForm.from}T00:00:00`).toISOString();
  }

  if (filterForm.to) {
    filter.toUtc = new Date(`${filterForm.to}T23:59:59.999`).toISOString();
  }

  if (filterForm.userName) {
    filter.userName = filterForm.userName;
  }

  if (filterForm.action) {
    filter.action = filterForm.action;
  }

  if (filterForm.resource) {
    filter.resource = filterForm.resource;
  }

  if (filterForm.search) {
    filter.search = filterForm.search;
  }

  return filter;
}

async function applyFilters() {
  appliedFilter.value = buildFilter();
  page.value = 1;
  await reload();
}

async function resetFilters() {
  filterForm.from = "";
  filterForm.to = "";
  filterForm.userName = "";
  filterForm.action = "";
  filterForm.resource = "";
  filterForm.search = "";
  appliedFilter.value = {};
  page.value = 1;
  await reload();
}

async function goToPage(target: number) {
  page.value = Math.min(Math.max(1, target), totalPages.value);
  await reload();
}

async function reload() {
  loading.value = true;
  try {
    const result = await searchAuditLogs(appliedFilter.value, page.value, PAGE_SIZE);
    items.value = result.items;
    total.value = result.totalCount;
    availableActions.value = result.availableActions;
    listLoad.clear();
  } catch (error) {
    // Liste sessizce boşalmaz: "okunamadı" kendi şeridinde, tekrar deneme yoluyla gösterilir.
    items.value = [];
    total.value = 0;
    listLoad.capture(error);
  } finally {
    loading.value = false;
  }
}

async function exportCsv() {
  exporting.value = true;
  try {
    await downloadAuditLogsCsv(appliedFilter.value);
    showNotice(t("panel.audit.exportStarted"), false);
  } catch (error) {
    showError(error);
  } finally {
    exporting.value = false;
  }
}

async function loadRetention() {
  try {
    const settings = await getAuditRetention();
    retention.value = settings;
    retentionDays.value = settings.retentionDays;
    retentionLoad.clear();
  } catch (error) {
    retention.value = null;
    retentionLoad.capture(error);
  }
}

async function saveRetention() {
  savingRetention.value = true;
  try {
    const settings = await saveAuditRetention(retentionDays.value);
    retention.value = settings;
    retentionDays.value = settings.retentionDays;
    showNotice(t("panel.audit.retention.saved"), false);
  } catch (error) {
    showError(error);
  } finally {
    savingRetention.value = false;
  }
}

async function purgeNow() {
  // GERİ ALINAMAZ ve tam da uyuşmazlıkta lazım olacak kaydı siler → onay zorunlu, "tehlikeli" tonda.
  await confirm({
    title: t("panel.audit.retention.purgeConfirmTitle"),
    message: t("panel.audit.retention.purgeConfirmMessage", { days: retentionDays.value }),
    impact: [t("panel.audit.retention.purgeImpact", { days: retentionDays.value })],
    action: async () => {
      savingRetention.value = true;
      try {
        const settings = await purgeAuditLogs();
        retention.value = settings;
        showNotice(t("panel.audit.retention.purged", { count: settings.lastPurgedCount }), false);
        await reload();
      } catch (error) {
        showError(error);
      } finally {
        savingRetention.value = false;
      }
    }
  });
}

/** Tarih bicimi panelin geri kalaniyla AYNI (bkz. utils/datetime.ts). */
function formatUtc(value: string | null) {
  return formatDateTime(value, t("panel.common.notAvailable"));
}
</script>

<style scoped>
.row-actions button {
  width: auto;
}

.audit-row {
  grid-template-columns: 170px 140px 200px minmax(0, 1fr) minmax(0, 1.4fr) 120px;
}

/* Yol/adres/komut gibi uzun değerler satırı yatay kaydırmaya zorlamamalı. */
.breakable {
  word-break: break-word;
}

.pager {
  display: flex;
  align-items: center;
  gap: 10px;
  flex-wrap: wrap;
}

.pager-status {
  color: var(--text-muted);
  font-size: 0.85rem;
}
</style>
