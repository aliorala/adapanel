<template>
  <section class="panel module-shell cluster-workspace">
    <div class="module-head">
      <div>
        <h2>{{ t("panel.cluster.title") }}</h2>
        <p class="meta">{{ t("panel.cluster.subtitle") }}</p>
      </div>
      <button class="secondary compact" :disabled="loading" @click="refresh">
        {{ loading ? t("panel.cluster.refreshing") : t("panel.cluster.refresh") }}
      </button>
    </div>

    <p v-if="notice" :class="['notice', 'page-notice', { error: noticeError }]" :role="noticeError ? 'alert' : 'status'">
      {{ notice }}
    </p>

    <!-- Toplam sayilar. Ulasilamayan dugum varsa toplamin EKSIK oldugu ACIKCA soylenir:
         bilinmeyeni sifir saymak, panelin yalan soylemesi olurdu. -->
    <section class="form-surface summary-surface">
      <div class="summary-grid">
        <div class="summary-cell">
          <span class="field-label">{{ t("panel.cluster.summaryNodes") }}</span>
          <strong>{{ overview?.nodes.length ?? 0 }}</strong>
        </div>
        <div class="summary-cell">
          <span class="field-label">{{ t("panel.cluster.summaryReachable") }}</span>
          <strong>{{ overview?.reachableCount ?? 0 }}</strong>
        </div>
        <div class="summary-cell">
          <span class="field-label">{{ t("panel.cluster.summaryUnreachable") }}</span>
          <strong :class="{ 'danger-text': (overview?.unreachableCount ?? 0) > 0 }">
            {{ overview?.unreachableCount ?? 0 }}
          </strong>
        </div>
        <div class="summary-cell">
          <span class="field-label">{{ t("panel.cluster.summarySites") }}</span>
          <strong>{{ overview?.totalSites ?? 0 }}</strong>
          <small v-if="(overview?.unreachableCount ?? 0) > 0" class="warn-text">
            {{ t("panel.cluster.summarySitesPartial") }}
          </small>
        </div>
      </div>
    </section>

    <!-- Yeni dugum / duzenleme formu -->
    <section class="form-surface">
      <div class="section-head">
        <div>
          <h3>{{ editingId ? t("panel.cluster.formEditTitle") : t("panel.cluster.formTitle") }}</h3>
          <p class="meta">{{ t("panel.cluster.formHint") }}</p>
        </div>
        <button class="secondary compact" @click="toggleForm">
          {{ showForm ? t("panel.common.cancel") : t("panel.cluster.addNode") }}
        </button>
      </div>

      <template v-if="showForm">
        <div class="form-grid">
          <label class="stacked-field">
            <span class="field-label">{{ t("panel.cluster.fieldLabel") }}</span>
            <input v-model="form.label" maxlength="64" :placeholder="t('panel.cluster.fieldLabelPlaceholder')" />
          </label>
          <label class="stacked-field">
            <span class="field-label">{{ t("panel.cluster.fieldAddress") }}</span>
            <input v-model="form.address" maxlength="255" :placeholder="t('panel.cluster.fieldAddressPlaceholder')" />
            <span class="field-hint">{{ t("panel.cluster.fieldAddressHint") }}</span>
          </label>
        </div>

        <label class="stacked-field">
          <span class="field-label">{{ t("panel.cluster.fieldToken") }}</span>
          <PasswordField v-model="form.apiToken" autocomplete="off" :placeholder="editingId ? t('panel.cluster.fieldTokenKeep') : 'adap_…'" />
          <span class="field-hint">{{ t("panel.cluster.fieldTokenHint") }}</span>
        </label>

        <label class="check-field">
          <input v-model="form.enabled" type="checkbox" />
          <span>{{ t("panel.cluster.fieldEnabled") }}</span>
        </label>
        <p class="field-hint">{{ t("panel.cluster.fieldEnabledHint") }}</p>

        <!-- TLS doğrulaması VARSAYILAN AÇIK. Kapatmak, merkezin yönetici belirtecini araya
             girene teslim etmeyi göze almaktır; bu yüzden kutu İŞARETLİ başlar ve işaret
             kaldırılınca kırmızı uyarı çıkar. -->
        <label class="check-field">
          <input v-model="form.verifyTlsCertificate" type="checkbox" />
          <span>{{ t("panel.cluster.fieldTls") }}</span>
        </label>
        <p v-if="form.verifyTlsCertificate" class="field-hint">{{ t("panel.cluster.fieldTlsHint") }}</p>
        <p v-else class="notice error">{{ t("panel.cluster.fieldTlsWarning") }}</p>

        <div class="row-actions">
          <button class="compact" :disabled="saving" @click="save">
            {{ saving ? t("panel.cluster.saving") : t("panel.cluster.save") }}
          </button>
        </div>
      </template>
    </section>

    <!-- Dugum listesi -->
    <section class="module-shell">
      <div class="section-head">
        <div>
          <h3>{{ t("panel.cluster.nodesTitle") }}</h3>
          <p class="meta">{{ t("panel.cluster.nodesHint") }}</p>
        </div>
      </div>

      <div class="node-cards">
        <article v-for="row in overview?.nodes ?? []" :key="row.node.id" class="node-card">
          <header class="node-head">
            <div class="cell-main">
              <strong>{{ row.node.label }}</strong>
              <small>{{ row.node.address }}</small>
            </div>
            <span :class="['status-pill', statusTone(row)]">{{ statusLabel(row) }}</span>
          </header>

          <!-- Ulasilabilen dugum: gercek olcumler -->
          <div v-if="row.reachable && row.info" class="node-metrics">
            <div class="metric">
              <span class="field-label">{{ t("panel.cluster.metricVersion") }}</span>
              <strong>{{ row.info.panelVersion || "—" }}</strong>
            </div>
            <div class="metric">
              <span class="field-label">{{ t("panel.cluster.metricSites") }}</span>
              <strong>{{ row.info.siteCount }}</strong>
            </div>
            <div class="metric">
              <span class="field-label">{{ t("panel.cluster.metricCpu") }}</span>
              <strong>{{ yuzde(row.info.overview.cpuUsagePercent) }}</strong>
            </div>
            <div class="metric">
              <span class="field-label">{{ t("panel.cluster.metricMemory") }}</span>
              <strong>{{ yuzde(row.info.overview.memoryUsagePercent) }}</strong>
            </div>
            <div class="metric">
              <span class="field-label">{{ t("panel.cluster.metricDisk") }}</span>
              <strong :class="{ 'warn-text': enDoluDisk(row) >= 85 }">{{ yuzde(enDoluDisk(row)) }}</strong>
            </div>
            <div class="metric">
              <span class="field-label">{{ t("panel.cluster.metricAlerts") }}</span>
              <strong :class="{ 'danger-text': uyariSayisi(row) > 0 }">{{ uyariSayisi(row) }}</strong>
            </div>
          </div>

          <!-- Ulasilamayan dugum: satir DUSMEZ, sebebini SOYLER -->
          <p v-else class="notice error node-down">
            {{ statusMessage(row) }}
            <small v-if="row.detail"> ({{ row.detail }})</small>
          </p>

          <footer class="node-actions">
            <span v-if="!row.node.verifyTlsCertificate" class="status-pill danger">
              {{ t("panel.cluster.tlsOff") }}
            </span>
            <span v-if="!row.node.hasToken" class="status-pill warn">{{ t("panel.cluster.noToken") }}</span>
            <span class="cell-muted">{{ sonGorulme(row) }}</span>
            <button class="secondary compact row-button" :disabled="busyId === row.node.id" @click="probe(row)">
              {{ t("panel.cluster.test") }}
            </button>
            <button
              class="secondary compact row-button"
              :disabled="busyId === row.node.id || !row.reachable"
              :title="row.reachable ? t('panel.cluster.openHint') : t('panel.cluster.openDisabled')"
              @click="open(row)">
              {{ t("panel.cluster.open") }}
            </button>
            <button class="secondary compact row-button" @click="edit(row)">{{ t("panel.common.edit") }}</button>
            <button class="danger compact row-button" :disabled="busyId === row.node.id" @click="remove(row)">
              {{ t("panel.cluster.remove") }}
            </button>
          </footer>
        </article>

        <p v-if="!loaded" class="empty-state" role="status" aria-live="polite">{{ t("panel.common.loading") }}</p>
        <p v-else-if="(overview?.nodes.length ?? 0) === 0" class="empty-state">{{ t("panel.cluster.empty") }}</p>
      </div>
    </section>

    <!-- Yeni site: hangi sunucuya gidecegi SECILIR (Plesk'te abonelik tek dugume kurulur) -->
    <section v-if="(overview?.reachableCount ?? 0) > 0" class="form-surface">
      <div class="section-head">
        <div>
          <h3>{{ t("panel.cluster.siteTitle") }}</h3>
          <p class="meta">{{ t("panel.cluster.siteHint") }}</p>
        </div>
      </div>

      <div class="form-grid">
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.cluster.siteName") }}</span>
          <input v-model="siteForm.name" maxlength="128" placeholder="ornek.test" />
        </label>
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.cluster.siteNode") }}</span>
          <select v-model="siteForm.nodeId">
            <option value="">{{ otomatikEtiketi }}</option>
            <option v-for="row in ulasilabilirler" :key="row.node.id" :value="row.node.id">
              {{ row.node.label }} ({{ row.info?.siteCount ?? 0 }})
            </option>
          </select>
          <span class="field-hint">{{ t("panel.cluster.siteNodeHint") }}</span>
        </label>
      </div>

      <div class="row-actions">
        <button class="compact" :disabled="creatingSite" @click="createSite">
          {{ creatingSite ? t("panel.cluster.siteCreating") : t("panel.cluster.siteCreate") }}
        </button>
      </div>
    </section>
  </section>
</template>

<script setup lang="ts">
/*
 * ÇOKLU SUNUCU YÖNETİMİ (Plesk Multi Server karşılığı).
 *
 * Model kopyalandı, icat edilmedi: bir "yönetim düğümü" (bu panel) ve ona kayıtlı "hizmet
 * düğümleri" vardır. Merkez düğümleri izler, tek tıkla düğüme geçilir ve yeni site tek bir
 * düğüme kurulur (Plesk'te de bir abonelik düğümler arasına BÖLÜNMEZ).
 *
 * Bu ekran YALNIZ YÖNETİCİ içindir; uç da sınıf düzeyinde Administrator ister (asıl dayatma orada).
 */
import { computed, onMounted, reactive, ref } from "vue";
import { useI18n } from "vue-i18n";
import PasswordField from "../components/PasswordField.vue";
import { useConfirm } from "../components/ConfirmDialog.vue";
import {
  createClusterSite,
  getClusterNodeLink,
  getClusterOverview,
  probeClusterNode,
  registerClusterNode,
  removeClusterNode,
  updateClusterNode,
  type ClusterNodeStatus,
  type ClusterOverview
} from "../api/cluster";
import { extractApiDetails, extractApiMessage } from "../stores/auth";

const { t } = useI18n();
const { confirm } = useConfirm();

const overview = ref<ClusterOverview | null>(null);
// Veri gelmeden "kayıt yok" gösterilmez: yönetici düğümü yok sanıp yeniden eklemesin.
const loaded = ref(false);
const loading = ref(false);
const saving = ref(false);
const creatingSite = ref(false);
const busyId = ref("");
const showForm = ref(false);
const editingId = ref("");
const notice = ref("");
const noticeError = ref(false);

const form = reactive({
  label: "",
  address: "",
  apiToken: "",
  enabled: true,
  // VARSAYILAN AÇIK — sunucu tarafındaki varsayılanla aynı.
  verifyTlsCertificate: true
});

const siteForm = reactive({ name: "", nodeId: "" });

const ulasilabilirler = computed(() => (overview.value?.nodes ?? []).filter((row) => row.reachable));

const otomatikEtiketi = computed(() => t("panel.cluster.siteNodeAuto"));

onMounted(refresh);

async function refresh() {
  loading.value = true;
  try {
    overview.value = await getClusterOverview();
    loaded.value = true;
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    loading.value = false;
  }
}

function toggleForm() {
  showForm.value = !showForm.value;
  if (!showForm.value) {
    resetForm();
  }
}

function resetForm() {
  editingId.value = "";
  form.label = "";
  form.address = "";
  form.apiToken = "";
  form.enabled = true;
  form.verifyTlsCertificate = true;
}

function edit(row: ClusterNodeStatus) {
  editingId.value = row.node.id;
  form.label = row.node.label;
  form.address = row.node.address;
  // Belirteç OKUNAMAZ: sunucu yalnız korunmuş hâlini saklar ve geri döndürmez.
  // Boş bırakılırsa mevcut anahtar korunur.
  form.apiToken = "";
  form.enabled = row.node.enabled;
  form.verifyTlsCertificate = row.node.verifyTlsCertificate;
  showForm.value = true;
}

async function save() {
  if (form.label.trim().length < 2 || form.address.trim().length === 0) {
    showNotice(t("panel.cluster.formIncomplete"), true);
    return;
  }

  if (!editingId.value && form.apiToken.trim().length === 0) {
    showNotice(t("cluster.token_required"), true);
    return;
  }

  saving.value = true;
  try {
    const payload = {
      label: form.label.trim(),
      address: form.address.trim(),
      apiToken: form.apiToken.trim(),
      enabled: form.enabled,
      verifyTlsCertificate: form.verifyTlsCertificate
    };

    if (editingId.value) {
      await updateClusterNode(editingId.value, payload);
    } else {
      await registerClusterNode(payload);
    }

    showForm.value = false;
    resetForm();
    await refresh();
    showNotice(t("panel.cluster.saved"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    saving.value = false;
  }
}

async function probe(row: ClusterNodeStatus) {
  busyId.value = row.node.id;
  try {
    const durum = await probeClusterNode(row.node.id);
    await refresh();
    showNotice(
      durum.reachable
        ? t("panel.cluster.testOk", { label: row.node.label, ms: durum.durationMs })
        : `${t(durum.statusKey)}${durum.detail ? ` (${durum.detail})` : ""}`,
      !durum.reachable
    );
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    busyId.value = "";
  }
}

/**
 * "Düğüme git": düğümün GERÇEK panel adresini (gizli giriş yolu dahil) çözer ve yeni sekmede açar.
 *
 * Bu bir OTURUM DEVRİ DEĞİLDİR ve olmaması bilinçlidir — yönetici düğümde kendi kimliğiyle
 * giriş yapar. Gerekçe sunucu tarafında (ClusterNodeLinkDto) yazılı: bir API anahtarının oturum
 * çerezine dönüşebilmesi, panelin kurduğu yetenek modelini anlamsızlaştırırdı.
 */
async function open(row: ClusterNodeStatus) {
  busyId.value = row.node.id;
  try {
    const link = await getClusterNodeLink(row.node.id);
    window.open(link.url, "_blank", "noopener,noreferrer");
    showNotice(t("panel.cluster.openedHint", { label: row.node.label }), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    busyId.value = "";
  }
}

async function remove(row: ClusterNodeStatus) {
  const confirmed = await confirm({
    title: t("panel.cluster.removeTitle"),
    message: t("panel.cluster.removeMessage", { label: row.node.label }),
    impact: [t("panel.cluster.removeImpactLocal"), t("panel.cluster.removeImpactRemote")],
    confirmLabel: t("panel.cluster.remove"),
    busyLabel: t("panel.cluster.removing")
  });

  if (!confirmed) {
    return;
  }

  busyId.value = row.node.id;
  try {
    await removeClusterNode(row.node.id);
    await refresh();
    showNotice(t("panel.cluster.removed"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    busyId.value = "";
  }
}

async function createSite() {
  if (siteForm.name.trim().length < 2) {
    showNotice(t("panel.cluster.siteNameRequired"), true);
    return;
  }

  creatingSite.value = true;
  try {
    const sonuc = await createClusterSite({
      nodeId: siteForm.nodeId || null,
      name: siteForm.name.trim(),
      hostName: siteForm.name.trim()
    });

    siteForm.name = "";
    await refresh();
    showNotice(t("panel.cluster.siteCreated", { site: sonuc.siteName, label: sonuc.nodeLabel }), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    creatingSite.value = false;
  }
}

function statusTone(row: ClusterNodeStatus) {
  if (row.reachable) {
    return uyariSayisi(row) > 0 ? "warn" : "ok";
  }

  return row.node.enabled ? "danger" : "warn";
}

function statusLabel(row: ClusterNodeStatus) {
  if (row.reachable) {
    return t("panel.cluster.statusOnline");
  }

  return row.node.enabled ? t("panel.cluster.statusOffline") : t("panel.cluster.statusDisabled");
}

/** Sunucunun döndürdüğü anahtarı çevirir; çeviri yoksa anahtarın kendisi görünmesin diye yedek metin. */
function statusMessage(row: ClusterNodeStatus) {
  if (!row.statusKey) {
    return t("cluster.node_unreachable");
  }

  const translated = t(row.statusKey);
  return translated === row.statusKey ? t("cluster.node_unreachable") : translated;
}

function enDoluDisk(row: ClusterNodeStatus) {
  const disks = row.info?.overview.disks ?? [];
  // Ortalama DEĞİL en kötü disk: sunucuyu durduran şey disklerin ortalaması değil, dolan ilk diski.
  return disks.length === 0 ? 0 : Math.max(...disks.map((disk) => disk.usagePercent));
}

function uyariSayisi(row: ClusterNodeStatus) {
  // "NotInstalled" bir uyarı değildir (o bileşen o sunucuda kurulu değil); çalışmayan servis uyarıdır.
  return (row.info?.services ?? []).filter((service) => service.status === "Stopped").length;
}

function sonGorulme(row: ClusterNodeStatus) {
  if (!row.node.lastSeenAtUtc) {
    return t("panel.cluster.neverSeen");
  }

  return t("panel.cluster.lastSeen", { time: new Date(row.node.lastSeenAtUtc).toLocaleString() });
}

function yuzde(value: number) {
  return `${Math.round(value)}%`;
}

function resolveApiMessage(error: unknown) {
  const key = extractApiMessage(error);
  const translated = t(key);
  const message = translated === key ? key : translated;
  const details = extractApiDetails(error);
  return details ? `${message}\n\n${details}` : message;
}

function showNotice(message: string, isError: boolean) {
  notice.value = message;
  noticeError.value = isError;
}
</script>

<style scoped>
.cluster-workspace {
  align-content: start;
}

.section-head {
  display: flex;
  justify-content: space-between;
  gap: 10px;
  align-items: flex-start;
  flex-wrap: wrap;
}

.summary-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(140px, 1fr));
  gap: 14px;
}

.summary-cell {
  display: grid;
  gap: 4px;
}

.summary-cell strong {
  font-size: 1.6rem;
  line-height: 1.1;
}

.node-cards {
  display: grid;
  gap: 12px;
}

.node-card {
  display: grid;
  gap: 12px;
  padding: 14px;
  border: 1px solid var(--border-1);
  border-radius: 10px;
  background: var(--surface-1);
}

.node-head {
  display: flex;
  justify-content: space-between;
  align-items: flex-start;
  gap: 10px;
  flex-wrap: wrap;
}

.node-metrics {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(110px, 1fr));
  gap: 10px;
}

.metric {
  display: grid;
  gap: 3px;
}

.node-down {
  margin: 0;
}

.node-actions {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
}

.node-actions .cell-muted {
  margin-right: auto;
}

.row-button {
  width: auto;
}

.row-actions button {
  width: auto;
}

@media (max-width: 760px) {
  .form-grid {
    grid-template-columns: 1fr;
  }

  .node-actions .cell-muted {
    margin-right: 0;
    width: 100%;
  }
}
</style>
