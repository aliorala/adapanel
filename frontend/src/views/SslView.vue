<template>
  <section class="panel module-shell">
    <div class="module-head">
      <div>
        <h2>{{ t("panel.ssl.title") }}</h2>
        <p>{{ t("panel.ssl.subtitle") }}</p>
      </div>
      <button class="secondary compact" :disabled="!selectedSiteName || !!busyAction || loading" @click="refresh()">
        {{ t("panel.ssl.refresh") }}
      </button>
    </div>

    <div v-if="notice" :class="['notice', 'page-notice', 'ssl-notice', { error: noticeError }]" :role="noticeError ? 'alert' : 'status'">
      <p>{{ notice }}</p>
      <button type="button" class="secondary notice-close" :aria-label="t('panel.common.dismissNotice')" @click="notice = ''">×</button>
      <button v-if="retryWwwHost" type="button" class="secondary compact" :disabled="!!busyAction || loading" @click="openWwwRetry">{{ t("panel.ssl.retryWww", { host: retryWwwHost }) }}</button>
    </div>

    <div class="module-toolbar">
      <div class="segmented-tabs" role="tablist">
        <button role="tab" :aria-selected="activeTab === 'current'" :class="{ active: activeTab === 'current' }" @click="activeTab = 'current'">{{ t("panel.ssl.currentTab") }}</button>
        <button role="tab" :aria-selected="activeTab === 'letsencrypt'" :class="{ active: activeTab === 'letsencrypt' }" @click="activeTab = 'letsencrypt'">Let's Encrypt</button>
        <button role="tab" :aria-selected="activeTab === 'import'" :class="{ active: activeTab === 'import' }" @click="activeTab = 'import'">{{ t("panel.ssl.importTitle") }}</button>
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
        :checked="redirectMeasured && workspace.httpsRedirectEnabled"
        :indeterminate="!redirectMeasured"
        v-bind="writeGate(!!busyAction || loading || !redirectMeasured)"
        @change="handleRedirectChange" />
      <span>
        <strong>{{ t("panel.ssl.forceHttps") }}</strong>
        <small>{{ t("panel.ssl.forceHttpsHint") }}</small>
      </span>
    </label>
    <p v-if="workspace && !redirectMeasured" class="redirect-unknown" role="status">
      <span>{{ t("panel.ssl.redirectUnknown") }}</span>
      <button type="button" class="secondary compact" :disabled="!!busyAction || loading" @click="refresh()">{{ t("panel.common.retry") }}</button>
    </p>

    <div v-if="activeTab === 'current'" class="data-table cert-table">
      <div class="data-row table-head cert-row">
        <span>{{ t("panel.ssl.hostName") }}</span>
        <span>{{ t("panel.ssl.source") }}</span>
        <span>{{ t("panel.ssl.subject") }}</span>
        <span>{{ t("panel.ssl.expires") }}</span>
        <span>HTTPS</span>
        <span>{{ t("panel.ssl.bindingActions") }}</span>
      </div>
      <template v-for="item in workspace?.certificates ?? []" :key="`${item.thumbprint}:${item.hostName}:${item.port}`">
      <div class="data-row cert-row">
        <span class="cell-main"><strong>{{ item.hostName }}</strong><small>{{ item.thumbprint }}</small>
          <small v-if="item.isPresentInStore !== true" :class="['cert-store-note', { missing: item.isPresentInStore === false }]">
            {{ t(item.isPresentInStore === false ? 'panel.ssl.certificateStoreMissing' : 'panel.ssl.certificateStoreUnknown') }}
          </small>
        </span>
        <!-- Ham değer ("self-signed") yerine kullanıcının dili. -->
        <span class="cert-field">
          <span class="cert-field-label">{{ t("panel.ssl.source") }}</span>
          <span :class="['status-pill', item.source === 'self-signed' ? 'warn' : 'ok']">{{ sourceLabel(item.source) }}</span>
        </span>
        <span class="cert-field">
          <span class="cert-field-label">{{ t("panel.ssl.subject") }}</span>
          <span class="cell-muted">{{ item.subject || "-" }}</span>
        </span>
        <span class="cert-field">
          <span class="cert-field-label">{{ t("panel.ssl.expires") }}</span>
          <span :class="['status-pill', expiryClass(item.notAfterUtc)]" :title="formatUtc(item.notAfterUtc)">{{ formatUtc(item.notAfterUtc) }} · {{ expiryLabel(item.notAfterUtc) }}</span>
        </span>
        <span class="cert-field">
        <span class="cert-field-label">HTTPS</span>
        <!-- ÖLÇÜLEMEDİ ayrı gösterilir: "bağlama yok" demek, bakamadığımızda yanlış olur. -->
        <span v-if="item.isHttpsBindingPresent === null" class="status-pill" :title="t('panel.ssl.bindingUnknownHint')">
          {{ t("panel.ssl.bindingUnknown") }}
        </span>
        <!-- SERTİFİKA AYRI SORULUR. "Bağlama var" ile "sertifika bağlamada" aynı şey değildir:
             bağlama önce kurulur, sertifika sonra iliştirilir; ikincisi düşerse bağlama
             sertifikasız kalır ve site 443'te ÖLÜ olur. Eski hücre buna yeşil "443" diyordu. -->
        <span v-else-if="item.isHttpsBindingPresent && item.isCertificateBound === false" class="status-pill warn" :title="item.bindingDetail">
          {{ t("panel.ssl.certificateNotBound") }}
        </span>
        <span v-else :class="['status-pill', item.isHttpsBindingPresent ? (item.isCertificateBound === null ? '' : 'ok') : 'warn']" :title="item.bindingDetail">
          {{ item.isHttpsBindingPresent ? (item.isCertificateBound === null ? t("panel.ssl.certificateUnverified") : `${item.port}`) : t("panel.ssl.notBound") }}
        </span>
        </span>
        <span class="row-actions">
          <button v-if="canManageBindings && item.isHttpsBindingPresent === true" type="button" class="secondary compact"
            v-bind="writeGate(!!busyAction || loading)" @click="removeBinding(item)">{{ t("panel.ssl.removeBinding") }}</button>
        </span>
      </div>

      <!-- SESSİZ DÜŞÜŞ ARTIK SESSİZ DEĞİL. Let's Encrypt alınamayıp kendinden imzalıya
           düşüldüğünde işlem "başarılı" dönüyor, kullanıcı SSL'in kurulduğunu sanıyor ve
           sorunu ancak tarayıcı "bu bağlantı güvenli değil" dediğinde fark ediyordu. -->
      <p v-if="item.fallbackReason" class="cert-fallback">
        {{ t("panel.ssl.fallbackTitle") }} — {{ t(item.fallbackReason) }}
      </p>
      </template>
      <!-- Liste yüklenmeden "sertifika yok" yazıyordu; henüz bilinmiyor ile gerçekten yok
           ekranda ayırt edilemiyordu. -->
      <p v-if="loading" class="empty-state" role="status" aria-live="polite">{{ t("panel.common.loading") }}</p>
      <p v-else-if="workspace && !workspace.certificates.length" class="empty-state">{{ t("panel.ssl.noCertificates") }}</p>
    </div>

    <section v-else-if="activeTab === 'letsencrypt'" class="form-surface">
      <div class="form-grid two">
        <label>
          <span class="field-label">{{ t("panel.ssl.hostName") }}</span>
          <select v-if="workspace?.hostNames.length" v-model="issueForm.hostName">
            <option v-if="issueForm.hostName && !workspace.hostNames.includes(issueForm.hostName)" :value="issueForm.hostName">{{ issueForm.hostName }}</option>
            <option v-for="host in workspace.hostNames" :key="host" :value="host">{{ host }}</option>
          </select>
          <input v-else v-model.trim="issueForm.hostName" />
        </label>
        <label>
          <span class="field-label">{{ t("panel.ssl.contactEmail") }}</span>
          <input v-model.trim="issueForm.contactEmail" type="email" placeholder="admin@example.com" />
        </label>
      </div>
      <label v-if="wwwCompanion" class="toggle-field wildcard-toggle">
        <input v-model="includeWww" type="checkbox" :disabled="!!busyAction" />
        <span>{{ t("panel.ssl.includeWww", { host: wwwCompanion }) }}</span>
      </label>
      <p v-if="wwwCompanion && includeWww" class="field-hint">{{ t("panel.ssl.includeWwwHint", { host: wwwCompanion }) }}</p>
      <label v-if="isAdmin" class="toggle-field wildcard-toggle">
        <input v-model="issueForm.wildcard" type="checkbox" />
        <span>{{ t("panel.ssl.wildcardOption") }}</span>
      </label>
      <p v-if="issueForm.wildcard" class="field-hint">{{ t("panel.ssl.wildcardHint") }}</p>
      <div class="row-actions">
        <button v-bind="writeGate(!canIssue || !!busyAction || loading)" @click="issue">
          {{ busyAction === "issue" ? t("panel.ssl.busy") : t("panel.ssl.issue") }}
        </button>
        <button v-if="isAdmin" class="secondary compact" v-bind="writeGate(!!busyAction || loading)" @click="renew">
          {{ busyAction === "renew" ? t("panel.ssl.busy") : t("panel.ssl.renew") }}
        </button>
      </div>
      <p class="field-hint">{{ t("panel.ssl.letsencryptHint") }}</p>
    </section>

    <section v-else class="form-surface">
      <div class="form-grid">
        <label>
          <span class="field-label">{{ t("panel.ssl.hostName") }}</span>
          <input v-model.trim="importForm.hostName" :disabled="!!busyAction" />
        </label>
        <label>
          <span class="field-label">{{ t("panel.form.password") }}</span>
          <PasswordField v-model="importForm.password" :disabled="!!busyAction" />
        </label>
        <label>
          <span class="field-label">{{ t("panel.ssl.file") }}</span>
          <input :key="fileInputVersion" type="file" :disabled="!!busyAction" accept=".pfx,.p12" @change="handleFileChange" />
        </label>
      </div>
      <div class="row-actions">
        <button v-bind="writeGate(!selectedSiteName || !workspace || loading || !!busyAction || fileLoading || !importForm.hostName.trim() || !importForm.base64Content)" @click="submitImport">
          {{ busyAction === "import" ? t("panel.ssl.busy") : t("panel.ssl.importAction") }}
        </button>
        <span class="field-hint" role="status">{{ fileLoading ? t("panel.common.loading") : (importForm.fileName || t("panel.ssl.noFile")) }}</span>
      </div>
    </section>
  </section>
</template>

<script setup lang="ts">
import { computed, inject, onBeforeUnmount, onMounted, reactive, ref, watch } from "vue";
import { routeLocationKey } from "vue-router";
import { useI18n } from "vue-i18n";
import PasswordField from "../components/PasswordField.vue";
import { useConfirm } from "../components/ConfirmDialog.vue";
import { getSslWorkspace, importCertificate, issueLetsEncrypt, renewAllCertificates, removeCertificate, setHttpsRedirect, type SslCertificate, type SslWorkspace, type SslRenewalResult } from "../api/ssl";
import { extractApiDetails, extractApiMessage, useAuthStore } from "../stores/auth";
import { useSitesStore } from "../stores/sites";
import { useSubscriptionGate } from "../composables/useSubscriptionGate";
import { formatDateTime } from "../utils/datetime";
import { getSessionEpoch } from "../api/client";
import { addSiteAlias } from "../api/aliases";
import { getSslRepairPlan } from "../api/sslRepair";
import { companionWwwHost } from "../utils/wwwSsl";

const { t } = useI18n();
const route = inject(routeLocationKey, undefined);
const { confirm } = useConfirm();
// Askidaki abonelikte YAZMA dugmeleri bastan devre disi + sebebi title/aria ile soylenir
// (bkz. composables/useSubscriptionGate.ts). Okuma dugmeleri kapiya girmez.
const { writeGate, locked } = useSubscriptionGate();
const sitesStore = useSitesStore();
const workspace = ref<SslWorkspace | null>(null);
const activeTab = ref<"current" | "letsencrypt" | "import">("current");
const notice = ref("");
const noticeError = ref(false);
const retryWwwHost = ref("");
const includeWww = ref(true);
const busyAction = ref("");
const loading = ref(false);
const fileLoading = ref(false);
const fileInputVersion = ref(0);
let siteGeneration = 0, workspaceRequest = 0, fileRequest = 0, issueRevision = 0, tabRevision = 0;
let disposed = false;
let consumedSslHost = "";
const mountedSession = getSessionEpoch();
const isActive = () => !disposed && getSessionEpoch() === mountedSession;
const selectedSiteName = computed({
  get: () => sitesStore.selectedSiteName,
  set: (value: string) => sitesStore.selectSite(value)
});
const authStore = useAuthStore();
const isAdmin = computed(() => authStore.session?.user?.role === "Administrator");
const canManageBindings = computed(() => authStore.session?.isAuthenticated === true &&
  ["Administrator", "Reseller", "Customer"].includes(authStore.session.user?.role ?? ""));
const issueForm = reactive({ contactEmail: "", hostName: "", useStaging: false, wildcard: false });
const wwwCompanion = computed(() => companionWwwHost(selectedSiteName.value, issueForm.hostName, sitesStore.selectedSite?.bindings ?? [], issueForm.wildcard));
const importForm = reactive({ hostName: "", password: "", fileName: "", base64Content: "" });
watch(issueForm, () => { issueRevision++; }, { deep: true, flush: "sync" });
watch(activeTab, () => { tabRevision++; }, { flush: "sync" });
onBeforeUnmount(() => { disposed = true; importForm.password = ""; importForm.base64Content = ""; });
function siteContext() {
  const site = selectedSiteName.value, generation = siteGeneration, actor = authStore.session?.user?.id, role = authStore.session?.user?.role;
  return { site, current: () => isActive() && selectedSiteName.value === site && generation === siteGeneration && authStore.session?.user?.id === actor && authStore.session?.user?.role === role };
}
// Yalnızca GERÇEKTEN ölçülüp "var" çıkanlar sayılır; ölçülemeyenler sayıya katılmaz.
const httpsBindingCount = computed(() =>
  workspace.value?.certificates.filter((item) => item.isHttpsBindingPresent === true).length ?? 0);
const redirectMeasured = computed(() => workspace.value?.httpsRedirectMeasured === true && typeof workspace.value.httpsRedirectEnabled === "boolean");

/** Ham kaynak değerini kullanıcının diline çevirir. */
function sourceLabel(source: string) {
  if (source === "letsencrypt") return "Let's Encrypt";
  if (source === "self-signed") return t("panel.ssl.sourceSelfSigned");
  if (source === "manual") return t("panel.ssl.sourceManual");
  return source || "-";
}
const canIssue = computed(() => Boolean(workspace.value && selectedSiteName.value && issueForm.hostName.trim() && issueForm.contactEmail.trim()));

onMounted(async () => {
  const target = siteContext();
  try {
    if (sitesStore.sites.length === 0) await sitesStore.fetchSites();
  } catch (error) {
    if (target.current()) showNotice(resolveApiMessage(error), true);
  }
});
watch(selectedSiteName, () => {
  ++siteGeneration; ++fileRequest; ++fileInputVersion.value;
  fileLoading.value = false;
  workspace.value = null;
  Object.assign(importForm, { hostName: "", password: "", fileName: "", base64Content: "" });
  issueForm.hostName = ""; issueForm.wildcard = false;
  notice.value = "";
  retryWwwHost.value = "";
  void refresh(true);
}, { immediate: true });

async function refresh(force = false, keepIssueDraft = false): Promise<boolean> {
  if (!isActive() || (!force && (busyAction.value || loading.value))) return false;
  const target = siteContext(), request = ++workspaceRequest, draft = issueRevision, tab = tabRevision;
  const query = route?.query ?? {};
  const requestedHost = query.section === "sites" && query.site === target.site && query.tool === "ssl" && typeof query.sslHost === "string" ? query.sslHost : "";
  if (!selectedSiteName.value) {
    workspace.value = null;
    loading.value = false;
    return false;
  }
  loading.value = true;
  try {
    const result = await getSslWorkspace(target.site);
    if (!target.current() || request !== workspaceRequest) return false;
    workspace.value = result;
    const firstHost = workspace.value.hostNames[0] || "";
    const draftUnchanged = !keepIssueDraft && issueRevision === draft;
    if (draftUnchanged && !workspace.value.hostNames.includes(issueForm.hostName)) issueForm.hostName = firstHost;
    // A deep link selects only a hostname measured on this site. Merely opening
    // this form never issues a certificate or broadens its domain coverage.
    const hostKey = `${target.site}:${requestedHost}`;
    const boundHost = workspace.value.hostNames.find(host => host.toLowerCase() === requestedHost.toLowerCase());
    if (requestedHost && route?.query.sslHost === requestedHost && consumedSslHost !== hostKey && boundHost && draftUnchanged) {
      issueForm.hostName = boundHost;
      issueForm.wildcard = false;
      if (tabRevision === tab) activeTab.value = "letsencrypt";
      consumedSslHost = hostKey;
    }
    if (!importForm.hostName) importForm.hostName = firstHost;
    notice.value = "";
    return true;
  } catch (error) {
    if (target.current() && request === workspaceRequest) {
      workspace.value = null;
      showNotice(resolveApiMessage(error), true);
    }
    return false;
  } finally {
    if (target.current() && request === workspaceRequest) loading.value = false;
  }
}

async function handleRedirectChange(event: Event) {
  const checkbox = event.target as HTMLInputElement, enabled = checkbox.checked;
  // Keep the native checkbox on the measured state until the server confirms
  // the change; a rejected request must not leave its optimistic DOM state on.
  checkbox.checked = redirectMeasured.value && workspace.value?.httpsRedirectEnabled === true;
  await toggleHttpsRedirect(enabled);
}
async function toggleHttpsRedirect(enabled: boolean) {
  if (!isActive() || locked.value || busyAction.value || loading.value || !workspace.value || !redirectMeasured.value || !selectedSiteName.value) return;
  const target = siteContext();
  busyAction.value = "redirect";
  try {
    const result = await setHttpsRedirect(target.site, enabled);
    if (!target.current()) return;
    if (!result || result.siteName !== target.site) {
      workspace.value = { ...workspace.value!, httpsRedirectMeasured: false };
      showNotice(t("panel.ssl.redirectUnverified"), true);
      return;
    }
    workspace.value = result;
    if (!redirectMeasured.value) {
      showNotice(t("panel.ssl.redirectUnverified"), true);
      return;
    }
    if (result.httpsRedirectEnabled !== enabled) {
      showNotice(t("ssl.https_redirect_failed"), true);
      return;
    }
    showNotice(enabled ? t("panel.ssl.forceHttpsOn") : t("panel.ssl.forceHttpsOff"), false);
  } catch (error) {
    if (target.current()) {
      if (workspace.value) workspace.value = { ...workspace.value, httpsRedirectMeasured: false };
      showNotice(resolveApiMessage(error), true);
    }
  } finally {
    busyAction.value = "";
  }
}

// TEK KAYNAK: "SSL kuruldu" cümlesi iki yerden (alma ve içe aktarma) çıkıyor ve ikisi de aynı
// üç durumu ayırmalı. Ayrı ayrı yazılsalardı sessizce ayrışırlardı.
//
// Sertifika bağlamaya işlenmediyse cümle YEŞİL OLAMAZ: bağlama duruyor olsa bile site 443'te
// ölüdür (tarayıcı bağlantıyı kapatır) ve yönetici sorunu başka yerde arar.
type InstallationTarget = { siteName: string; hostName: string; port: number; issue: boolean };
function kurulumBildirimi(sertifika: SslCertificate | null | undefined, target: InstallationTarget): [string, boolean] {
  if (!sertifika || !sameSslName(sertifika.siteName, target.siteName) || !sameSslName(sertifika.hostName, target.hostName) ||
    sertifika.port !== target.port || typeof sertifika.thumbprint !== "string" || !sertifika.thumbprint.trim())
    return [t("panel.ssl.installationUnverified"), true];
  if (target.issue && (sertifika.source === "self-signed" || !!sertifika.fallbackReason)) {
    const reason = typeof sertifika.fallbackReason === "string" ? sertifika.fallbackReason.trim() : "";
    return [[t("panel.ssl.fallbackTitle"), reason ? t(reason) : ""].filter(Boolean).join("\n"), true];
  }
  if (sertifika.isPresentInStore !== true)
    return [t(sertifika.isPresentInStore === false ? "panel.ssl.certificateStoreMissing" : "panel.ssl.certificateStoreUnknown"), true];
  if (sertifika.isCertificateBound === false) {
    const ayrinti = sertifika.bindingDetail ? ` — ${sertifika.bindingDetail}` : "";
    return [`${t("panel.ssl.certificateNotBoundNotice")}${ayrinti}`, true];
  }

  if (sertifika.isCertificateBound !== true || sertifika.isHttpsBindingPresent !== true) {
    return [t("panel.ssl.certificateUnverifiedNotice"), true];
  }

  if (!target.issue && sertifika.fallbackReason?.trim())
    return [t(sertifika.fallbackReason.trim()), true];

  return [t(target.issue ? "panel.ssl.issued" : "panel.ssl.imported"), false];
}

function installationNotice(sertifika: SslCertificate, target: InstallationTarget): [string, boolean] {
  const result = kurulumBildirimi(sertifika, target);
  if (result[1]) return result;
  const matching = workspace.value?.certificates.find(item => sameSslName(item.siteName, target.siteName) && sameSslName(item.hostName, target.hostName) &&
    item.port === target.port && typeof item.thumbprint === "string" && item.thumbprint.toLowerCase() === sertifika.thumbprint.toLowerCase());
  return matching ? kurulumBildirimi(matching, target) : [t("panel.ssl.installationUnverified"), true];
}

async function issue() {
  if (!isActive() || locked.value || busyAction.value || loading.value || !canIssue.value) return;
  const target = siteContext(), submitted = { siteName: target.site, ...issueForm }, draft = issueRevision, tab = tabRevision;
  const domain = submitted.hostName.trim().replace(/^\*\./, "").replace(/^www\./i, "");
  const installation = { siteName: target.site, hostName: submitted.wildcard ? `*.${domain}` : submitted.hostName.trim(), port: 443, issue: true };
  const wwwHost = includeWww.value ? wwwCompanion.value : "";
  retryWwwHost.value = "";
  busyAction.value = "issue";
  try {
    const sertifika = await issueLetsEncrypt(submitted);
    if (!target.current()) return;
    const preliminary = kurulumBildirimi(sertifika, installation);
    const refreshed = await refresh(true, issueRevision !== draft);
    if (!target.current()) return;
    if (!refreshed) { if (preliminary[1]) showNotice(`${preliminary[0]}\n${notice.value}`, true); return; }
    // Sabit yeşil "SSL kuruldu" yalnız sertifikanın bağlamada ÖLÇÜLDÜĞÜ durumda çıkar.
    const mainNotice = installationNotice(sertifika, installation);
    if (!mainNotice[1] && wwwHost) {
      try {
        const wwwTarget = { siteName: target.site, hostName: wwwHost, port: 443, issue: true };
        if (!target.current()) return;
        if (locked.value) throw new Error(t("panel.subscription.readOnlyAction"));
        if (!workspace.value!.hostNames.some(host => sameSslName(host, wwwHost))) {
          const alias = await addSiteAlias(target.site, { hostName: wwwHost, redirectToPrimary: true, includeWww: false });
          if (!target.current()) return;
          if (alias.redirectRuleNoticeKey) throw new Error(t(alias.redirectRuleNoticeKey));
          if (!await refresh(true, true)) throw new Error(notice.value);
          if (!target.current()) return;
          if (!workspace.value?.hostNames.some(host => sameSslName(host, wwwHost))) throw new Error(t("panel.ssl.wwwBindingUnverified"));
        }
        if (!target.current() || locked.value) return;
        const existing = workspace.value?.certificates.find(item => sameSslName(item.hostName, wwwHost) && item.port === 443 && item.isHttpsBindingPresent === true && item.isCertificateBound === true && item.isPresentInStore === true);
        // Trust inspection uses an administrator-only endpoint. Tenant users
        // must neither call it nor replace an existing www certificate because
        // they cannot perform that inspection.
        if (existing && !isAdmin.value) {
          retryWwwHost.value = "";
          showNotice(`${submitted.hostName.trim()}: ${mainNotice[0]}\n${t("panel.ssl.wwwPreservedNeedsAdmin", { host: wwwHost })}`, true);
          if (tabRevision === tab) activeTab.value = "current";
          return;
        }
        let existingValid = false;
        if (existing && existing.source !== "self-signed") {
          const plan = await getSslRepairPlan({ siteName: target.site, hostName: wwwHost, port: 443 });
          if (!target.current()) return;
          existingValid = plan.options.some(option => sameSslName(option.hostName, wwwHost) && option.current?.valid === true &&
            sameSslName(option.current.thumbprint, existing.thumbprint));
        }
        if (!existingValid) {
          if (!target.current() || locked.value) return;
          const wwwCertificate = await issueLetsEncrypt({ siteName: target.site, hostName: wwwHost, contactEmail: submitted.contactEmail, useStaging: submitted.useStaging, wildcard: false });
          if (!target.current()) return;
          const preliminaryWww = kurulumBildirimi(wwwCertificate, wwwTarget);
          if (preliminaryWww[1]) throw new Error(preliminaryWww[0]);
          if (!await refresh(true, true)) throw new Error(notice.value);
          if (!target.current()) return;
          const wwwNotice = installationNotice(wwwCertificate, wwwTarget);
          if (wwwNotice[1]) throw new Error(wwwNotice[0]);
        }
        const finalMainNotice = installationNotice(sertifika, installation);
        if (finalMainNotice[1]) { showNotice(`${submitted.hostName.trim()}: ${finalMainNotice[0]}`, true); return; }
        showNotice(t("panel.ssl.mainAndWwwIssued", { host: submitted.hostName.trim(), wwwHost }), false);
      } catch (error) {
        if (!target.current()) return;
        retryWwwHost.value = wwwHost;
        const detail = error instanceof Error ? error.message : resolveApiMessage(error);
        showNotice(`${submitted.hostName.trim()}: ${mainNotice[0]}\n${t("panel.ssl.wwwFailed", { host: wwwHost })}\n${detail}`, true);
      }
    } else showNotice(...mainNotice);
    if (tabRevision === tab) activeTab.value = "current";
  } catch (error) {
    if (!target.current()) return;
    // Wildcard/DNS-01 hatasında gerçek neden (win-acme çıktısı) Details'te gelir — kör mesaj bırakma.
    const details = extractApiDetails(error);
    showNotice(details ? `${resolveApiMessage(error)} — ${details}` : resolveApiMessage(error), true);
  } finally {
    busyAction.value = "";
  }
}

async function openWwwRetry() {
  if (!isActive() || busyAction.value || loading.value || locked.value || !workspace.value || !retryWwwHost.value) return;
  const target = siteContext(), host = retryWwwHost.value;
  busyAction.value = "prepare-www";
  try {
    if (!workspace.value.hostNames.some(value => sameSslName(value, host))) {
      const alias = await addSiteAlias(target.site, { hostName: host, redirectToPrimary: true, includeWww: false });
      if (!target.current()) return;
      if (alias.redirectRuleNoticeKey) throw new Error(t(alias.redirectRuleNoticeKey));
      if (!await refresh(true, true)) return;
      if (!target.current()) return;
    }
    if (!workspace.value?.hostNames.some(value => sameSslName(value, host))) throw new Error(t("panel.ssl.wwwBindingUnverified"));
    issueForm.hostName = host;
    issueForm.wildcard = false;
    activeTab.value = "letsencrypt";
  } catch (error) {
    if (target.current()) showNotice(error instanceof Error ? error.message : resolveApiMessage(error), true);
  } finally { busyAction.value = ""; }
}

async function renew() {
  if (!isActive() || !isAdmin.value || locked.value || busyAction.value || loading.value) return;
  const target = siteContext();
  busyAction.value = "renew";
  try {
    const result = await renewAllCertificates();
    if (!target.current()) return;
    const [message, failed] = renewalNotice(result);
    const refreshed = target.site ? await refresh(true) : true;
    if (!target.current()) return;
    showNotice(refreshed ? message : [message, notice.value].filter(Boolean).join("\n"), failed || !refreshed);
  } catch (error) {
    if (target.current()) showNotice(resolveApiMessage(error), true);
  } finally {
    busyAction.value = "";
  }
}

function sameSslName(first: unknown, second: string) {
  return typeof first === "string" && first.toLowerCase() === second.toLowerCase();
}
async function removeBinding(item: SslCertificate) {
  if (!isActive() || !canManageBindings.value || locked.value || busyAction.value || loading.value ||
    !workspace.value || !selectedSiteName.value || item.isHttpsBindingPresent !== true ||
    !sameSslName(workspace.value.siteName, selectedSiteName.value) || !sameSslName(item.siteName, selectedSiteName.value) ||
    typeof item.hostName !== "string" || !Number.isSafeInteger(item.port) || item.port < 1 || item.port > 65535) return;
  const target = siteContext();
  const actorId = authStore.session?.user?.id, actorRole = authStore.session?.user?.role;
  const current = () => target.current() && authStore.session?.user?.id === actorId && authStore.session?.user?.role === actorRole;
  const payload = { siteName: target.site, hostName: item.hostName, port: item.port, removeCertificateFromStore: false as const };
  const matchesBinding = (value: SslCertificate) => value && sameSslName(value.hostName, payload.hostName) && value.port === payload.port;
  const markUnknown = () => {
    if (workspace.value) workspace.value = { ...workspace.value, httpsRedirectMeasured: false,
      certificates: workspace.value.certificates.map(value => matchesBinding(value) ? { ...value, isHttpsBindingPresent: null } : value) };
  };
  // Reserve the operation before opening confirmation so a second click cannot
  // replace its target or start another write while the user is deciding.
  busyAction.value = "remove";
  try {
    if (!await confirm({
      title: t("panel.ssl.removeBinding"), tone: "neutral", confirmLabel: t("panel.ssl.removeBinding"),
      message: t("panel.ssl.removeBindingConfirm", { host: payload.hostName || target.site, port: payload.port }),
      impact: [target.site, `${payload.hostName || "*"}:${payload.port}`, t("panel.ssl.certificateKept")]
    })) return;
    if (!current() || !canManageBindings.value || locked.value) return;
    const result = await removeCertificate(payload);
    if (!current()) return;
    if (!result || !sameSslName(result.siteName, payload.siteName) || !sameSslName(result.hostName, payload.hostName) ||
      result.port !== payload.port || result.bindingRemoved !== true) {
      markUnknown();
      showNotice(t(result?.bindingRemoved === false ? "ssl.remove_binding_failed" : "panel.ssl.removalUnverified"), true);
      return;
    }
    const message = t("panel.ssl.bindingRemoved", { host: payload.hostName || target.site, port: payload.port });
    const measured = result.workspace;
    if (!measured || !sameSslName(measured.siteName, target.site) || !Array.isArray(measured.certificates) || !Array.isArray(measured.hostNames)) {
      markUnknown();
      showNotice(`${message}\n${t("panel.ssl.removalUnverified")}`, true);
      return;
    }
    workspace.value = measured;
    if (measured.certificates.some(value => matchesBinding(value) && value.isHttpsBindingPresent !== false)) {
      showNotice(t("panel.ssl.removalUnverified"), true);
      return;
    }
    // Binding removal and redirect verification are separate measurements. A
    // redirect read failure must not hide that the binding was actually removed.
    const redirectWarning = !redirectMeasured.value || (result.httpsRedirectDisabled === true && measured.httpsRedirectEnabled);
    showNotice(redirectWarning ? `${message}\n${t("panel.ssl.redirectUnverified")}` : message, redirectWarning);
  } catch (error) {
    if (current()) { markUnknown(); showNotice(resolveApiMessage(error), true); }
  } finally {
    if (busyAction.value === "remove") busyAction.value = "";
  }
}

function renewalNotice(result: SslRenewalResult): [string, boolean] {
  const measured: Partial<SslRenewalResult> = result && typeof result === "object" ? result : {};
  const reason = typeof measured.failureReason === "string" ? measured.failureReason.trim() : "";
  const output = typeof measured.output === "string" ? measured.output.trim() : "";
  const details = [...new Set([reason, output].filter(Boolean))];
  const counts = [measured.renewedCount, measured.verifiedCount, measured.missingFromStoreCount, measured.unverifiedCount, measured.bindingDriftCount];
  if (measured.measured === false) return [[t("panel.ssl.renewalFailed"), ...details].join("\n"), true];
  if (measured.measured !== true || typeof measured.bindingsMeasured !== "boolean" ||
    counts.some(count => typeof count !== "number" || !Number.isSafeInteger(count) || count < 0)) {
    return [[t("panel.ssl.renewalUnverified"), ...details].join("\n"), true];
  }
  const problems = measured.missingFromStoreCount! > 0 || measured.bindingDriftCount! > 0;
  const unverified = measured.unverifiedCount! > 0 || !measured.bindingsMeasured || !!reason;
  const lines = [t("panel.ssl.renewalSummary", { renewed: measured.renewedCount!, verified: measured.verifiedCount! })];
  if (problems) lines.push(t("panel.ssl.renewalProblems", { missing: measured.missingFromStoreCount!, drift: measured.bindingDriftCount! }));
  if (unverified) lines.push(t("panel.ssl.renewalUnverified"));
  return [[...lines, ...details].join("\n"), problems || unverified];
}

async function submitImport() {
  if (!isActive() || locked.value || busyAction.value || loading.value || fileLoading.value || !workspace.value || !selectedSiteName.value || !importForm.hostName.trim() || !importForm.base64Content) return;
  const target = siteContext(), submitted = { siteName: target.site, ...importForm, port: 443 }, tab = tabRevision;
  const installation = { siteName: target.site, hostName: submitted.hostName.trim(), port: submitted.port, issue: false };
  busyAction.value = "import";
  try {
    const sertifika = await importCertificate(submitted);
    if (!target.current()) return;
    const preliminary = kurulumBildirimi(sertifika, installation);
    Object.assign(importForm, { password: "", fileName: "", base64Content: "" });
    ++fileInputVersion.value;
    const refreshed = await refresh(true);
    if (!target.current()) return;
    if (!refreshed) { if (preliminary[1]) showNotice(`${preliminary[0]}\n${notice.value}`, true); return; }
    if (tabRevision === tab) activeTab.value = "current";
    showNotice(...installationNotice(sertifika, installation));
  } catch (error) {
    if (target.current()) showNotice(resolveApiMessage(error), true);
  } finally {
    busyAction.value = "";
  }
}

async function handleFileChange(event: Event) {
  if (!isActive() || busyAction.value) return;
  const request = ++fileRequest, target = siteContext();
  const file = (event.target as HTMLInputElement).files?.[0];
  importForm.base64Content = "";
  importForm.fileName = file?.name || "";
  fileLoading.value = !!file;
  if (!file) {
    return;
  }
  try {
    const bytes = new Uint8Array(await file.arrayBuffer());
    if (!target.current() || request !== fileRequest) return;
    if (!bytes.length) {
      importForm.fileName = ""; ++fileInputVersion.value;
      showNotice(t("ssl.invalid_certificate_payload"), true);
      return;
    }
    let binary = "";
    for (let offset = 0; offset < bytes.length; offset += 0x8000) {
      binary += String.fromCharCode(...bytes.subarray(offset, offset + 0x8000));
    }
    importForm.base64Content = btoa(binary);
  } catch {
    if (!target.current() || request !== fileRequest) return;
    importForm.fileName = "";
    ++fileInputVersion.value;
    showNotice(t("panel.ssl.fileReadFailed"), true);
  } finally {
    if (target.current() && request === fileRequest) fileLoading.value = false;
  }
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

/** Tarih bicimi panelin geri kalaniyla AYNI (bkz. utils/datetime.ts). */
function formatUtc(value: string) {
  return formatDateTime(value, "-");
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
.ssl-notice { display: grid; grid-template-columns: minmax(0, 1fr) auto; align-items: start; gap: var(--sp-8); }
.ssl-notice p { margin: 0; white-space: pre-wrap; overflow-wrap: anywhere; }
.notice-close { width: 28px; height: 28px; padding: 0; color: inherit; }

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

.redirect-unknown { display: flex; align-items: center; justify-content: space-between; gap: var(--sp-8); flex-wrap: wrap; margin: 0; }
.redirect-unknown span { flex: 1 1 200px; min-width: 0; overflow-wrap: anywhere; color: var(--warn-text); }
.redirect-unknown button { flex: 0 0 auto; width: auto; }

.force-https small {
  color: var(--text-muted);
  /* 0.74rem = 9.99px idi — olcek tabani `--fs-2xs` (10.53px); altina inen kural yok. */
  font-size: var(--fs-2xs);
}

.cert-row {
  grid-template-columns: 1.2fr 0.75fr 1.45fr 1fr 0.65fr minmax(160px, 0.9fr);
  min-width: 1000px;
}

.cert-field { min-width: 0; }
.cert-field-label { display: none; }
.cert-store-note { color: var(--text-muted); white-space: normal; overflow-wrap: anywhere; }
.cert-store-note.missing { color: var(--warn-text); }

@media (max-width: 640px) {
  .cert-table { display: grid; gap: var(--sp-12); border: 0; background: transparent; overflow: visible; }
  .cert-row.table-head { display: none; }
  .cert-table .cert-row:not(.table-head) {
    min-width: 0;
    grid-template-columns: minmax(0, 1fr);
    gap: var(--sp-12);
    padding: var(--sp-12);
    border: 1px solid var(--panel-border);
    border-radius: var(--radius-md);
    background: var(--surface-0);
  }
  .cert-row .cell-main { padding-bottom: var(--sp-8); border-bottom: 1px solid var(--panel-border); }
  .cert-row .cell-main strong, .cert-row .cell-main small { overflow-wrap: anywhere; word-break: break-word; }
  .cert-field { display: grid; grid-template-columns: minmax(64px, 0.4fr) minmax(0, 1fr); gap: var(--sp-8); align-items: start; }
  .cert-field-label { display: block; color: var(--text-muted); font-size: var(--fs-xs); }
  .cert-field .status-pill { max-width: 100%; white-space: normal; overflow-wrap: anywhere; }
  .cert-row .row-actions { width: 100%; }
  .cert-row .row-actions button { width: 100%; white-space: normal; }
}

/* Kendinden imzalıya düşme sebebi: sertifika satırının hemen altında, göz kaçırmasın. */
.cert-fallback {
  margin: 0;
  padding: 0.4rem 0.9rem 0.6rem;
  font-size: 0.84rem;
  color: var(--warn-text);
  border-left: 3px solid var(--warn-1);
  background: var(--warn-bg, rgba(224, 180, 0, 0.08));
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
