<template>
  <section class="panel module-shell">
    <div class="module-head">
      <div>
        <h2>{{ t("panel.security.workspaceTitle") }}</h2>
        <p class="meta">{{ t("panel.security.subtitle") }}</p>
      </div>
      <button class="secondary compact" :disabled="!selectedSiteName" @click="refreshAll">
        {{ t("panel.security.refresh") }}
      </button>
    </div>

    <p v-if="notice" :class="['notice', { error: noticeError }]">{{ notice }}</p>

    <div class="module-toolbar">
      <div class="segmented-tabs">
        <button :class="{ active: activeSection === 'site' }" @click="activeSection = 'site'">{{ t("panel.security.siteTitle") }}</button>
        <button :class="{ active: activeSection === 'firewall' }" @click="activeSection = 'firewall'">{{ t("panel.firewall.title") }}</button>
        <button :class="{ active: activeSection === 'bruteforce' }" @click="activeSection = 'bruteforce'">{{ t("panel.bruteforce.title") }}</button>
        <button :class="{ active: activeSection === 'antivirus' }" @click="activeSection = 'antivirus'">{{ t("panel.antivirus.title") }}</button>
      </div>
      <select v-model="selectedSiteName" class="site-select">
        <option value="">{{ t("panel.php.selectSite") }}</option>
        <option v-for="site in sitesStore.sites" :key="site.name" :value="site.name">{{ site.name }}</option>
      </select>
    </div>

    <template v-if="activeSection === 'site'">
      <div class="section-head">
        <div>
          <h3>{{ t("panel.security.siteTitle") }}</h3>
          <p class="meta">{{ selectedSiteName || t("panel.sites.detailEmpty") }}</p>
        </div>
        <div class="row-actions">
          <button class="secondary compact" :disabled="!selectedSiteName" @click="showSiteForm = !showSiteForm">
            {{ showSiteForm ? t("panel.common.cancel") : t("panel.security.advancedSettings") }}
          </button>
          <button class="compact" :disabled="!selectedSiteName || busyAction === 'rules'" @click="runSiteAction('rules')">
            {{ busyAction === "rules" ? t("panel.security.busy") : t("panel.security.basicRules") }}
          </button>
        </div>
      </div>

      <div class="summary-strip security-summary">
        <div class="summary-item">
          <span>{{ t("panel.security.isolationStatus") }}</span>
          <strong :class="['status-pill', securityConfig?.isIsolated ? 'ok' : 'warn']">
            {{ securityConfig?.isIsolated ? t("panel.status.enabled") : t("panel.status.disabled") }}
          </strong>
        </div>
        <div class="summary-item">
          <span>{{ t("panel.security.aclStatus") }}</span>
          <strong :class="['status-pill', securityConfig?.folderAclApplied ? 'ok' : 'warn']">
            {{ securityConfig?.folderAclApplied ? t("panel.status.enabled") : t("panel.status.disabled") }}
          </strong>
        </div>
        <div class="summary-item">
          <span>{{ t("panel.security.rulesStatus") }}</span>
          <strong :class="['status-pill', securityConfig?.basicRulesEnabled ? 'ok' : 'warn']">
            {{ securityConfig?.basicRulesEnabled ? t("panel.status.enabled") : t("panel.status.disabled") }}
          </strong>
        </div>
        <div class="summary-item">
          <span>{{ t("panel.security.requestsPerMinute") }}</span>
          <strong>{{ securityConfig?.requestsPerMinute || t("panel.security.unlimited") }}</strong>
        </div>
      </div>

      <div v-if="showSiteForm" class="form-surface">
        <div class="form-grid">
          <label>
            <span class="field-label">{{ t("panel.security.principal") }}</span>
            <input v-model="aclForm.principal" />
          </label>
          <label>
            <span class="field-label">{{ t("panel.security.rights") }}</span>
            <input v-model="aclForm.rights" />
          </label>
          <label>
            <span class="field-label">{{ t("panel.security.requestsPerMinute") }}</span>
            <input v-model.number="rateForm.requestsPerMinute" type="number" min="1" />
          </label>
        </div>
        <div class="row-actions">
          <button class="compact" :disabled="!selectedSiteName || busyAction === 'isolate'" @click="runSiteAction('isolate')">
            {{ busyAction === "isolate" ? t("panel.security.busy") : t("panel.security.isolate") }}
          </button>
          <button class="secondary compact" :disabled="!selectedSiteName || busyAction === 'acl'" @click="runSiteAction('acl')">
            {{ busyAction === "acl" ? t("panel.security.busy") : t("panel.security.applyAcl") }}
          </button>
          <button class="secondary compact" :disabled="!selectedSiteName || busyAction === 'rate'" @click="runSiteAction('rate')">
            {{ busyAction === "rate" ? t("panel.security.busy") : t("panel.security.saveRate") }}
          </button>
        </div>
      </div>
    </template>

    <template v-else-if="activeSection === 'firewall'">
      <div class="section-head">
        <div>
          <h3>{{ t("panel.firewall.title") }}</h3>
          <p class="meta">{{ t("panel.firewall.subtitle") }}</p>
        </div>
        <button class="compact" @click="showFirewallForm = !showFirewallForm">
          {{ showFirewallForm ? t("panel.common.cancel") : t("panel.firewall.newRule") }}
        </button>
      </div>

      <div v-if="showFirewallForm" class="form-surface">
        <div class="form-grid">
          <label>
            <span class="field-label">{{ t("panel.firewall.ruleName") }}</span>
            <input v-model="firewallForm.ruleName" />
          </label>
          <label>
            <span class="field-label">{{ t("panel.sites.port") }}</span>
            <input v-model.number="firewallForm.port" type="number" min="1" max="65535" />
          </label>
          <label>
            <span class="field-label">{{ t("panel.firewall.protocol") }}</span>
            <select v-model="firewallForm.protocol">
              <option value="TCP">TCP</option>
              <option value="UDP">UDP</option>
            </select>
          </label>
        </div>
        <div class="row-actions">
          <button class="compact" :disabled="busyAction === 'allow'" @click="saveFirewall(true)">
            {{ busyAction === "allow" ? t("panel.security.busy") : t("panel.firewall.allow") }}
          </button>
          <button class="secondary compact" :disabled="busyAction === 'block'" @click="saveFirewall(false)">
            {{ busyAction === "block" ? t("panel.security.busy") : t("panel.firewall.block") }}
          </button>
        </div>
      </div>

      <div class="data-table">
        <div class="data-row table-head firewall-row">
          <span>{{ t("panel.firewall.ruleName") }}</span>
          <span>{{ t("panel.firewall.protocol") }}</span>
          <span>{{ t("panel.sites.port") }}</span>
          <span>{{ t("panel.sites.state") }}</span>
        </div>
        <div v-for="rule in firewallRules" :key="rule.displayName" class="data-row firewall-row">
          <strong>{{ rule.displayName }}</strong>
          <span>{{ rule.protocol }}</span>
          <span>{{ rule.localPort }}</span>
          <span :class="['status-pill', rule.action.toLowerCase() === 'allow' ? 'ok' : 'warn']">{{ rule.action }}</span>
        </div>
        <p v-if="loading && firewallRules.length === 0" class="empty-state">{{ t("panel.common.loading") }}</p>
        <p v-else-if="firewallRules.length === 0" class="empty-state">{{ t("panel.firewall.empty") }}</p>
      </div>
    </template>

    <template v-else-if="activeSection === 'antivirus'">
      <div class="section-head">
        <div>
          <h3>{{ t("panel.antivirus.title") }}</h3>
          <p class="meta">{{ t("panel.antivirus.subtitle") }}</p>
        </div>
        <span :class="['status-pill', avStatus?.isInstalled ? 'ok' : 'warn']">
          {{ avStatus?.isInstalled
            ? t("panel.antivirus.installedVersion", { version: avStatus.version })
            : t("panel.antivirus.notInstalled") }}
        </span>
      </div>

      <div v-if="!avStatus?.isInstalled" class="form-surface">
        <p class="meta">{{ t("panel.antivirus.installHint") }}</p>
        <div class="row-actions">
          <button class="compact" :disabled="busyAction === 'av'" @click="installAv">
            {{ busyAction === "av" ? t("panel.antivirus.installing") : t("panel.antivirus.install") }}
          </button>
        </div>
      </div>

      <template v-else>
        <div class="form-surface">
          <div class="section-head">
            <div>
              <h4>{{ t("panel.antivirus.defsTitle") }}</h4>
              <p class="meta">
                {{ avStatus.databaseReady && avStatus.databaseUpdatedAtUtc
                  ? t("panel.antivirus.defsUpdatedAt", { date: new Date(avStatus.databaseUpdatedAtUtc).toLocaleString() })
                  : t("panel.antivirus.defsMissing") }}
              </p>
            </div>
            <button class="secondary compact" :disabled="busyAction === 'av'" @click="updateAvDefs">
              {{ busyAction === "av" ? t("panel.antivirus.updating") : t("panel.antivirus.updateDefs") }}
            </button>
          </div>
          <p v-if="!avStatus.databaseReady" class="field-hint">{{ t("panel.antivirus.defsHint") }}</p>
        </div>

        <div class="form-surface">
          <h4>{{ t("panel.antivirus.scanTitle") }}</h4>
          <div class="form-grid">
            <label class="stacked-field">
              <span class="field-label">{{ t("panel.php.targetSite") }}</span>
              <select v-model="avSiteName">
                <option value="">{{ t("panel.php.selectSite") }}</option>
                <option v-for="site in sitesStore.sites" :key="site.name" :value="site.name">{{ site.name }}</option>
              </select>
            </label>
          </div>
          <p class="field-hint">{{ t("panel.antivirus.scanHint") }}</p>
          <div class="row-actions">
            <button class="compact" :disabled="busyAction === 'av' || !avSiteName || !avStatus.databaseReady" @click="runAvScan">
              {{ busyAction === "av" ? t("panel.antivirus.scanning") : t("panel.antivirus.scan") }}
            </button>
          </div>
        </div>

        <div v-if="avStatus.lastScan" class="form-surface">
          <div class="section-head">
            <div>
              <h4>{{ t("panel.antivirus.lastScanTitle") }}: {{ avStatus.lastScan.target }}</h4>
              <p class="meta">
                {{ new Date(avStatus.lastScan.startedAtUtc).toLocaleString() }} •
                {{ t("panel.antivirus.scanSummary", {
                  files: avStatus.lastScan.scannedFiles,
                  seconds: avStatus.lastScan.durationSeconds
                }) }}
              </p>
            </div>
            <span :class="['status-pill', avStatus.lastScan.status === 'clean' ? 'ok' : 'warn']">
              {{ t(`panel.antivirus.status.${avStatus.lastScan.status}`) }}
            </span>
          </div>
          <p v-if="avStatus.lastScan.status === 'failed'" class="notice error">{{ avStatus.lastScan.detail }}</p>
          <div v-else-if="avStatus.lastScan.infected.length > 0" class="data-table">
            <div class="data-row table-head av-finding-row">
              <span>{{ t("panel.antivirus.file") }}</span>
              <span>{{ t("panel.antivirus.signature") }}</span>
            </div>
            <div v-for="finding in avStatus.lastScan.infected" :key="finding.path" class="data-row av-finding-row">
              <span class="cell-muted av-path">{{ finding.path }}</span>
              <strong>{{ finding.signature }}</strong>
            </div>
          </div>
          <p v-else class="empty-state">{{ t("panel.antivirus.noFindings") }}</p>
        </div>
      </template>
    </template>

    <template v-else>
      <div class="section-head">
        <div>
          <h3>{{ t("panel.bruteforce.title") }}</h3>
          <p class="meta">{{ t("panel.bruteforce.subtitle") }}</p>
        </div>
        <label class="toggle-field">
          <input v-model="bruteForm.enabled" type="checkbox" />
          <span>{{ t("panel.bruteforce.enabled") }}</span>
        </label>
      </div>

      <div class="form-surface">
        <div class="form-grid">
          <label>
            <span class="field-label">{{ t("panel.bruteforce.maxAttempts") }}</span>
            <input v-model.number="bruteForm.maxFailedAttempts" type="number" min="1" max="1000" />
          </label>
          <label>
            <span class="field-label">{{ t("panel.bruteforce.windowMinutes") }}</span>
            <input v-model.number="bruteForm.windowMinutes" type="number" min="1" max="1440" />
          </label>
          <label>
            <span class="field-label">{{ t("panel.bruteforce.banMinutes") }}</span>
            <input v-model.number="bruteForm.banMinutes" type="number" min="0" max="525600" />
          </label>
          <label>
            <span class="field-label">{{ t("panel.bruteforce.whitelist") }}</span>
            <input v-model="bruteWhitelistText" placeholder="203.0.113.5, 198.51.100.7" />
          </label>
        </div>
        <div class="row-actions">
          <button class="compact" :disabled="busyAction === 'bruteforce'" @click="saveBruteForce">
            {{ busyAction === "bruteforce" ? t("panel.security.busy") : t("panel.common.save") }}
          </button>
        </div>
      </div>

      <div class="data-table">
        <div class="data-row table-head bruteforce-row">
          <span>{{ t("panel.bruteforce.ip") }}</span>
          <span>{{ t("panel.bruteforce.attempts") }}</span>
          <span>{{ t("panel.bruteforce.bannedAt") }}</span>
          <span>{{ t("panel.bruteforce.expiresAt") }}</span>
          <span>{{ t("panel.software.action") }}</span>
        </div>
        <div v-for="ban in bannedIps" :key="ban.ipAddress" class="data-row bruteforce-row">
          <strong>{{ ban.ipAddress }}</strong>
          <span>{{ ban.failedAttempts }}</span>
          <span class="cell-muted">{{ new Date(ban.bannedAtUtc).toLocaleString() }}</span>
          <span class="cell-muted">{{ ban.expiresAtUtc ? new Date(ban.expiresAtUtc).toLocaleString() : t("panel.bruteforce.permanent") }}</span>
          <button class="secondary compact row-button" :disabled="busyAction === 'unban'" @click="unban(ban.ipAddress)">
            {{ t("panel.bruteforce.unban") }}
          </button>
        </div>
        <p v-if="loading && bannedIps.length === 0" class="empty-state">{{ t("panel.common.loading") }}</p>
        <p v-else-if="bannedIps.length === 0" class="empty-state">{{ t("panel.bruteforce.empty") }}</p>
      </div>
    </template>
  </section>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from "vue";
import { useI18n } from "vue-i18n";
import { allowPort, blockPort, getFirewallRules, type FirewallRule } from "../api/firewall";
import {
  applyAcl,
  enableBasicRules,
  getBruteForce,
  getSiteSecurity,
  isolateSite,
  setRateLimit,
  unbanIp,
  updateBruteForce,
  type BannedIp,
  type BruteForceSettings,
  type SiteSecurityConfiguration
} from "../api/security";
import {
  getAntivirusStatus,
  installAntivirus,
  scanAntivirus,
  updateAntivirusDefinitions,
  type AntivirusStatus
} from "../api/antivirus";
import { extractApiDetails, extractApiMessage } from "../stores/auth";
import { useSitesStore } from "../stores/sites";

const { t } = useI18n();
const sitesStore = useSitesStore();
const securityConfig = ref<SiteSecurityConfiguration | null>(null);
const firewallRules = ref<FirewallRule[]>([]);
const notice = ref("");
const noticeError = ref(false);
const busyAction = ref("");
const loading = ref(false);
const activeSection = ref<"site" | "firewall" | "bruteforce" | "antivirus">("site");
// ClamAV antivirüs (Güvenlik > Antivirüs). Durum sekme ilk açıldığında yüklenir.
const avStatus = ref<AntivirusStatus | null>(null);
const avSiteName = ref("");
const showFirewallForm = ref(false);
const showSiteForm = ref(false);
const aclForm = reactive({ principal: "", rights: "M" });
const rateForm = reactive({ requestsPerMinute: 120 });
const firewallForm = reactive({ ruleName: "", port: 8890, protocol: "TCP" });
const bannedIps = ref<BannedIp[]>([]);
const bruteWhitelistText = ref("");
const bruteForm = reactive<BruteForceSettings>({
  enabled: true,
  maxFailedAttempts: 10,
  windowMinutes: 15,
  banMinutes: 60,
  whitelistedIps: []
});

const selectedSiteName = computed({
  get: () => sitesStore.selectedSiteName,
  set: (value: string) => sitesStore.selectSite(value)
});

onMounted(async () => {
  try {
    if (sitesStore.sites.length === 0) await sitesStore.fetchSites();
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
  await refreshAll();
});

watch(selectedSiteName, refreshAll);

watch(activeSection, async (section) => {
  if (section === "antivirus" && avStatus.value === null) {
    await refreshAvStatus();
  }
});

async function refreshAvStatus() {
  try {
    avStatus.value = await getAntivirusStatus();
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
}

async function installAv() {
  busyAction.value = "av";
  try {
    avStatus.value = await installAntivirus();
    showNotice(t("panel.antivirus.installed"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    busyAction.value = "";
  }
}

async function updateAvDefs() {
  busyAction.value = "av";
  try {
    avStatus.value = await updateAntivirusDefinitions();
    showNotice(t("panel.antivirus.defsDone"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    busyAction.value = "";
  }
}

async function runAvScan() {
  if (!avSiteName.value) return;
  busyAction.value = "av";
  try {
    const result = await scanAntivirus(avSiteName.value);
    await refreshAvStatus();
    showNotice(
      result.status === "infected"
        ? t("panel.antivirus.scanInfected", { count: result.infectedCount })
        : t("panel.antivirus.scanClean"),
      result.status === "infected");
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    busyAction.value = "";
  }
}

async function refreshAll() {
  loading.value = true;
  try {
    firewallRules.value = await getFirewallRules();
    await refreshBruteForce();
    if (selectedSiteName.value) {
      securityConfig.value = await getSiteSecurity(selectedSiteName.value);
      aclForm.principal = securityConfig.value.principal || aclForm.principal;
      rateForm.requestsPerMinute = securityConfig.value.requestsPerMinute || rateForm.requestsPerMinute;
    }
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    loading.value = false;
  }
}

async function refreshBruteForce() {
  const status = await getBruteForce();
  applyBruteForce(status.settings, status.bannedIps);
}

function applyBruteForce(settings: BruteForceSettings, bans: BannedIp[]) {
  Object.assign(bruteForm, settings);
  bruteWhitelistText.value = settings.whitelistedIps.join(", ");
  bannedIps.value = bans;
}

async function saveBruteForce() {
  busyAction.value = "bruteforce";
  try {
    const whitelist = bruteWhitelistText.value
      .split(/[\s,]+/)
      .map((value) => value.trim())
      .filter((value) => value.length > 0);
    const status = await updateBruteForce({ ...bruteForm, whitelistedIps: whitelist });
    applyBruteForce(status.settings, status.bannedIps);
    showNotice(t("panel.bruteforce.saved"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    busyAction.value = "";
  }
}

async function unban(ipAddress: string) {
  busyAction.value = "unban";
  try {
    const status = await unbanIp(ipAddress);
    applyBruteForce(status.settings, status.bannedIps);
    showNotice(t("panel.bruteforce.unbanned"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    busyAction.value = "";
  }
}

async function runSiteAction(action: "isolate" | "acl" | "rate" | "rules") {
  if (!selectedSiteName.value) return;
  busyAction.value = action;
  try {
    if (action === "isolate") securityConfig.value = await isolateSite(selectedSiteName.value);
    else if (action === "acl") securityConfig.value = await applyAcl(selectedSiteName.value, aclForm.principal, aclForm.rights);
    else if (action === "rate") securityConfig.value = await setRateLimit(selectedSiteName.value, rateForm.requestsPerMinute, 30);
    else securityConfig.value = await enableBasicRules(selectedSiteName.value);
    showNotice(t("panel.security.saved"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    busyAction.value = "";
  }
}

async function saveFirewall(allow: boolean) {
  busyAction.value = allow ? "allow" : "block";
  try {
    if (allow) await allowPort(firewallForm.port, firewallForm.protocol, firewallForm.ruleName);
    else await blockPort(firewallForm.port, firewallForm.protocol, firewallForm.ruleName);
    firewallRules.value = await getFirewallRules();
    showFirewallForm.value = false;
    showNotice(t("panel.firewall.saved"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    busyAction.value = "";
  }
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
.section-head {
  display: flex;
  justify-content: space-between;
  gap: 10px;
  align-items: flex-start;
  flex-wrap: wrap;
}

.site-select {
  width: min(280px, 52vw);
}

.security-summary {
  grid-template-columns: repeat(4, minmax(0, 1fr));
}

.summary-item strong.status-pill {
  font-size: 0.8rem;
}

.firewall-row {
  grid-template-columns: minmax(240px, 1.5fr) 0.6fr 0.7fr 0.7fr;
}

.bruteforce-row {
  grid-template-columns: minmax(160px, 1.2fr) 0.6fr 1fr 1fr auto;
}

.toggle-field {
  display: flex;
  align-items: center;
  gap: 8px;
  color: var(--text-soft);
}

.toggle-field input {
  width: auto;
}

.row-button {
  width: auto;
}

@media (max-width: 900px) {
  .security-summary {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }
}

.av-finding-row {
  grid-template-columns: minmax(280px, 2fr) 1fr;
}

.av-path {
  overflow-wrap: anywhere;
}
</style>
