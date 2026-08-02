<template>
  <section class="panel module-shell">
    <div class="module-head">
      <div>
        <h2>{{ t("panel.ssl.title") }}</h2>
        <p>{{ t("panel.ssl.subtitle") }}</p>
      </div>
      <button class="secondary compact" :disabled="!selectedSiteName || busyAction === 'refresh'" @click="refresh">
        {{ t("panel.ssl.refresh") }}
      </button>
    </div>

    <p v-if="notice" :class="['notice', { error: noticeError }]">{{ notice }}</p>

    <div class="module-toolbar">
      <div class="segmented-tabs">
        <button :class="{ active: activeTab === 'current' }" @click="activeTab = 'current'">{{ t("panel.ssl.currentTab") }}</button>
        <button :class="{ active: activeTab === 'letsencrypt' }" @click="activeTab = 'letsencrypt'">Let's Encrypt</button>
        <button :class="{ active: activeTab === 'import' }" @click="activeTab = 'import'">{{ t("panel.ssl.importTitle") }}</button>
      </div>
      <!-- Domain hub'dan gelince site zaten seçili (breadcrumb); tekrar seçici gösterme. -->
      <select v-if="!selectedSiteName" v-model="selectedSiteName" class="site-select">
        <option value="">{{ t("panel.php.selectSite") }}</option>
        <option v-for="site in sitesStore.sites" :key="site.name" :value="site.name">{{ site.name }}</option>
      </select>
    </div>

    <div class="summary-strip">
      <div class="summary-item">
        <span>{{ t("panel.ssl.targetSite") }}</span>
        <strong>{{ selectedSiteName || "-" }}</strong>
      </div>
      <!-- Sayaçlar veri gelmeden 0 gösteriyordu; sertifikası olan site sertifikasız sanılıyordu. -->
      <div class="summary-item">
        <span>{{ t("panel.ssl.hostNames") }}</span>
        <strong>{{ workspace ? workspace.hostNames.length : "…" }}</strong>
      </div>
      <div class="summary-item">
        <span>{{ t("panel.ssl.certificateCount") }}</span>
        <strong>{{ workspace ? workspace.certificates.length : "…" }}</strong>
      </div>
      <div class="summary-item">
        <span>{{ t("panel.ssl.httpsBindings") }}</span>
        <strong>{{ workspace ? httpsBindingCount : "…" }}</strong>
      </div>
    </div>

    <label v-if="workspace" class="force-https">
      <input
        type="checkbox"
        :checked="workspace.httpsRedirectEnabled"
        :disabled="busyAction === 'redirect'"
        @change="toggleHttpsRedirect(($event.target as HTMLInputElement).checked)" />
      <span>
        <strong>{{ t("panel.ssl.forceHttps") }}</strong>
        <small>{{ t("panel.ssl.forceHttpsHint") }}</small>
      </span>
    </label>

    <div v-if="activeTab === 'current'" class="data-table">
      <div class="data-row table-head cert-row">
        <span>{{ t("panel.ssl.hostName") }}</span>
        <span>{{ t("panel.ssl.source") }}</span>
        <span>{{ t("panel.ssl.subject") }}</span>
        <span>{{ t("panel.ssl.expires") }}</span>
        <span>HTTPS</span>
      </div>
      <div v-for="item in workspace?.certificates ?? []" :key="`${item.thumbprint}:${item.hostName}`" class="data-row cert-row">
        <span class="cell-main"><strong>{{ item.hostName }}</strong><small>{{ item.thumbprint }}</small></span>
        <span>{{ item.source }}</span>
        <span class="cell-muted">{{ item.subject || "-" }}</span>
        <span :class="['status-pill', expiryClass(item.notAfterUtc)]" :title="formatUtc(item.notAfterUtc)">{{ formatUtc(item.notAfterUtc) }} · {{ expiryLabel(item.notAfterUtc) }}</span>
        <span :class="['status-pill', item.isHttpsBindingPresent ? 'ok' : 'warn']">
          {{ item.isHttpsBindingPresent ? `${item.port}` : t("panel.ssl.notBound") }}
        </span>
      </div>
      <!-- Liste yüklenmeden "sertifika yok" yazıyordu; henüz bilinmiyor ile gerçekten yok
           ekranda ayırt edilemiyordu. -->
      <p v-if="!workspace" class="empty-state" role="status" aria-live="polite">{{ t("panel.common.loading") }}</p>
      <p v-else-if="!workspace.certificates.length" class="empty-state">{{ t("panel.ssl.noCertificates") }}</p>
    </div>

    <section v-else-if="activeTab === 'letsencrypt'" class="form-surface">
      <div class="form-grid two">
        <label>
          <span class="field-label">{{ t("panel.ssl.hostName") }}</span>
          <select v-if="workspace?.hostNames.length" v-model="issueForm.hostName">
            <option v-for="host in workspace.hostNames" :key="host" :value="host">{{ host }}</option>
          </select>
          <input v-else v-model.trim="issueForm.hostName" />
        </label>
        <label>
          <span class="field-label">{{ t("panel.ssl.contactEmail") }}</span>
          <input v-model.trim="issueForm.contactEmail" type="email" placeholder="admin@example.com" />
        </label>
      </div>
      <label v-if="isAdmin" class="toggle-field wildcard-toggle">
        <input v-model="issueForm.wildcard" type="checkbox" />
        <span>{{ t("panel.ssl.wildcardOption") }}</span>
      </label>
      <p v-if="issueForm.wildcard" class="field-hint">{{ t("panel.ssl.wildcardHint") }}</p>
      <div class="row-actions">
        <button :disabled="!canIssue || busyAction === 'issue'" @click="issue">
          {{ busyAction === "issue" ? t("panel.ssl.busy") : t("panel.ssl.issue") }}
        </button>
        <button class="secondary compact" :disabled="busyAction === 'renew'" @click="renew">
          {{ busyAction === "renew" ? t("panel.ssl.busy") : t("panel.ssl.renew") }}
        </button>
      </div>
      <p class="field-hint">{{ t("panel.ssl.letsencryptHint") }}</p>
    </section>

    <section v-else class="form-surface">
      <div class="form-grid">
        <label>
          <span class="field-label">{{ t("panel.ssl.hostName") }}</span>
          <input v-model.trim="importForm.hostName" />
        </label>
        <label>
          <span class="field-label">{{ t("panel.form.password") }}</span>
          <input v-model="importForm.password" type="password" />
        </label>
        <label>
          <span class="field-label">{{ t("panel.ssl.file") }}</span>
          <input type="file" accept=".pfx,.p12" @change="handleFileChange" />
        </label>
      </div>
      <div class="row-actions">
        <button :disabled="!selectedSiteName || busyAction === 'import' || !importForm.base64Content" @click="submitImport">
          {{ busyAction === "import" ? t("panel.ssl.busy") : t("panel.ssl.importAction") }}
        </button>
        <span class="field-hint">{{ importForm.fileName || t("panel.ssl.noFile") }}</span>
      </div>
    </section>
  </section>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from "vue";
import { useI18n } from "vue-i18n";
import { getSslWorkspace, importCertificate, issueLetsEncrypt, renewAllCertificates, setHttpsRedirect, type SslWorkspace } from "../api/ssl";
import { extractApiDetails, extractApiMessage, useAuthStore } from "../stores/auth";
import { useSitesStore } from "../stores/sites";

const { t } = useI18n();
const sitesStore = useSitesStore();
const workspace = ref<SslWorkspace | null>(null);
const activeTab = ref<"current" | "letsencrypt" | "import">("current");
const notice = ref("");
const noticeError = ref(false);
const busyAction = ref("");
const selectedSiteName = computed({
  get: () => sitesStore.selectedSiteName,
  set: (value: string) => sitesStore.selectSite(value)
});
const authStore = useAuthStore();
const isAdmin = computed(() => authStore.session?.user?.role === "Administrator");
const issueForm = reactive({ contactEmail: "", hostName: "", useStaging: false, wildcard: false });
const importForm = reactive({ hostName: "", password: "", fileName: "", base64Content: "" });
const httpsBindingCount = computed(() => workspace.value?.certificates.filter((item) => item.isHttpsBindingPresent).length ?? 0);
const canIssue = computed(() => Boolean(selectedSiteName.value && issueForm.hostName && issueForm.contactEmail));

onMounted(async () => {
  try {
    if (sitesStore.sites.length === 0) await sitesStore.fetchSites();
    await refresh();
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
});
watch(selectedSiteName, refresh);

async function refresh() {
  if (!selectedSiteName.value) {
    workspace.value = null;
    return;
  }
  busyAction.value ||= "refresh";
  try {
    workspace.value = await getSslWorkspace(selectedSiteName.value);
    const firstHost = workspace.value.hostNames[0] || "";
    if (!workspace.value.hostNames.includes(issueForm.hostName)) issueForm.hostName = firstHost;
    if (!importForm.hostName) importForm.hostName = firstHost;
    notice.value = "";
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    if (busyAction.value === "refresh") busyAction.value = "";
  }
}

async function toggleHttpsRedirect(enabled: boolean) {
  if (!selectedSiteName.value) return;
  busyAction.value = "redirect";
  try {
    workspace.value = await setHttpsRedirect(selectedSiteName.value, enabled);
    showNotice(enabled ? t("panel.ssl.forceHttpsOn") : t("panel.ssl.forceHttpsOff"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    busyAction.value = "";
  }
}

async function issue() {
  busyAction.value = "issue";
  try {
    await issueLetsEncrypt({ siteName: selectedSiteName.value, ...issueForm });
    await refresh();
    activeTab.value = "current";
    showNotice(t("panel.ssl.issued"), false);
  } catch (error) {
    // Wildcard/DNS-01 hatasında gerçek neden (win-acme çıktısı) Details'te gelir — kör mesaj bırakma.
    const details = extractApiDetails(error);
    showNotice(details ? `${resolveApiMessage(error)} — ${details}` : resolveApiMessage(error), true);
  } finally {
    busyAction.value = "";
  }
}

async function renew() {
  busyAction.value = "renew";
  try {
    const result = await renewAllCertificates();
    await refresh();
    showNotice(result.output || t("panel.ssl.renewed"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    busyAction.value = "";
  }
}

async function submitImport() {
  busyAction.value = "import";
  try {
    await importCertificate({ siteName: selectedSiteName.value, ...importForm, port: 443 });
    Object.assign(importForm, { password: "", fileName: "", base64Content: "" });
    await refresh();
    activeTab.value = "current";
    showNotice(t("panel.ssl.imported"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    busyAction.value = "";
  }
}

async function handleFileChange(event: Event) {
  const file = (event.target as HTMLInputElement).files?.[0];
  if (!file) {
    importForm.fileName = "";
    importForm.base64Content = "";
    return;
  }
  importForm.fileName = file.name;
  const bytes = new Uint8Array(await file.arrayBuffer());
  let binary = "";
  for (let offset = 0; offset < bytes.length; offset += 0x8000) {
    binary += String.fromCharCode(...bytes.subarray(offset, offset + 0x8000));
  }
  importForm.base64Content = btoa(binary);
}

function expiryClass(value: string) {
  const days = (new Date(value).getTime() - Date.now()) / 86400000;
  return days < 0 ? "danger" : days < 30 ? "warn" : "ok";
}

// Açık geçerlilik etiketi: "X gün kaldı" / "Bugün doluyor" / "Süresi doldu" — sertifikanın geçerli/yenilenmiş olduğunu netleştirir.
function expiryLabel(value: string) {
  if (!value) return "-";
  const days = Math.floor((new Date(value).getTime() - Date.now()) / 86400000);
  if (days < 0) return t("panel.ssl.expired");
  if (days === 0) return t("panel.ssl.expiresToday");
  return t("panel.ssl.daysRemaining", { days });
}

function formatUtc(value: string) {
  return value ? new Date(value).toLocaleString() : "-";
}

function showNotice(message: string, isError: boolean) {
  notice.value = message;
  noticeError.value = isError;
}

function resolveApiMessage(error: unknown) {
  const key = extractApiMessage(error);
  const translated = t(key);
  return translated === key ? key : translated;
}
</script>

<style scoped>
.force-https {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 10px 12px;
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-md);
  background: var(--surface-1);
}

.force-https span {
  display: grid;
  gap: 2px;
}

.force-https small {
  color: var(--text-muted);
  font-size: 0.74rem;
}

.cert-row {
  grid-template-columns: 1.2fr 0.75fr 1.45fr 1fr 0.65fr;
}

.site-select {
  width: min(260px, 52vw);
}

.wildcard-toggle {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-top: 10px;
  color: var(--text-soft);
}

.wildcard-toggle input {
  width: auto;
}
</style>
