<template>
  <section class="panel wide sites-panel">
    <div class="sites-header">
      <div>
        <p class="section-kicker">{{ t("panel.sites.management") }}</p>
        <h2>{{ t("panel.sites.title") }}</h2>
        <p class="meta">{{ t("panel.sites.subtitle") }}</p>
      </div>

      <div class="sites-actions">
        <button class="secondary compact" :disabled="sitesStore.loading" @click="refreshSites">
          {{ t("panel.sites.refresh") }}
        </button>
        <button v-if="isAdmin" class="secondary compact" :disabled="sitesStore.importing" @click="importExistingSites">
          {{ t("panel.sites.import") }}
        </button>
        <button v-if="isAdmin" class="compact" @click="showCreateForm = !showCreateForm">
          {{ showCreateForm ? t("panel.sites.cancelCreate") : t("panel.sites.create") }}
        </button>
      </div>
    </div>

    <p v-if="notice" :class="['notice', { error: noticeError }]">
      {{ notice }}
    </p>

    <section v-if="generatedCredentials.length" class="credentials-card">
      <div>
        <h3>{{ t("panel.sites.credentials.title") }}</h3>
        <p class="meta">{{ t("panel.sites.credentials.hint") }}</p>
      </div>
      <div class="credentials-list">
        <div v-for="credential in generatedCredentials" :key="credential.service" class="credential-row">
          <strong>{{ credential.service }}</strong>
          <code>{{ credential.userName }}</code>
          <code>{{ credential.password }}</code>
          <button class="secondary compact" type="button" @click="copyCredential(credential)">
            {{ t("panel.sites.credentials.copy") }}
          </button>
        </div>
      </div>
    </section>

    <form v-if="showCreateForm && isAdmin" class="create-card" @submit.prevent="submitCreateSite">
      <div class="create-card-head">
        <div>
          <h3>{{ t("panel.sites.createTitle") }}</h3>
          <p class="meta">{{ t("panel.sites.createHint") }}</p>
        </div>
        <span class="ready-chip">{{ t("panel.sites.starterIncluded") }}</span>
      </div>

      <div class="site-create-grid">
        <label class="domain-field">
          <span>{{ t("panel.sites.domainName") }}</span>
          <input v-model.trim="createForm.hostName" maxlength="255" placeholder="example.com" required />
          <small class="field-hint">{{ t("panel.sites.domainNameHint") }}</small>
        </label>

        <aside class="automatic-plan">
          <strong>{{ t("panel.sites.automatic.title") }}</strong>
          <span>{{ t("panel.sites.automatic.runtime") }}</span>
          <span>{{ t("panel.sites.automatic.dns") }}</span>
          <span>{{ t("panel.sites.automatic.services") }}</span>
        </aside>

        <template v-if="showAdvanced">
          <label>
            <span>{{ t("panel.sites.name") }}</span>
            <input v-model="createForm.name" minlength="2" maxlength="128" required />
            <small class="field-hint">{{ t("panel.sites.internalNameHint") }}</small>
          </label>

          <label>
            <span>{{ t("panel.sites.physicalPath") }}</span>
            <input v-model="createForm.physicalPath" required @input="physicalPathEdited = true" />
          </label>

          <label>
            <span>{{ t("panel.sites.runtimeProfile") }}</span>
            <select v-model="createForm.runtimeProfile">
              <option value="auto">{{ t("panel.sites.runtime.auto") }}</option>
              <option value="static">{{ t("panel.sites.runtime.static") }}</option>
              <option value="node">{{ t("panel.sites.runtime.node") }}</option>
              <option value="python">{{ t("panel.sites.runtime.python") }}</option>
            </select>
            <small>{{ t("panel.sites.runtimeHint") }}</small>
          </label>

          <label v-if="createForm.runtimeProfile === 'auto'">
            <span>{{ t("panel.sites.phpVersion") }}</span>
            <select v-model="createForm.runtimeComponentId">
              <option value="">{{ t("panel.sites.runtimeNewest") }}</option>
              <option v-for="component in phpComponents" :key="component.id" :value="component.id">
                {{ component.displayName }}
              </option>
            </select>
          </label>

          <label v-if="createForm.runtimeProfile === 'node' || createForm.runtimeProfile === 'python'">
            <span>{{ t("panel.apps.startupFile") }}</span>
            <input v-model="createForm.startupFile" />
          </label>

        </template>
      </div>

      <button type="button" class="secondary compact advanced-toggle" @click="showAdvanced = !showAdvanced">
        {{ showAdvanced ? t("panel.sites.hideAdvanced") : t("panel.sites.showAdvanced") }}
      </button>

      <div class="form-surface companion-section">
        <label class="checkbox">
          <input v-model="createForm.createDatabase" type="checkbox" />
          <span>{{ t("panel.sites.companion.createDb") }}</span>
        </label>
        <div v-if="createForm.createDatabase" class="form-grid companion-grid">
          <label>
            <span class="field-label">{{ t("panel.database.databaseName") }}</span>
            <input v-model="createForm.databaseName" :placeholder="t('panel.database.optional')" />
          </label>
          <label>
            <span class="field-label">{{ t("panel.database.userName") }}</span>
            <input v-model="createForm.databaseUserName" :placeholder="t('panel.database.optional')" />
          </label>
          <label>
            <span class="field-label">{{ t("panel.database.userPassword") }}</span>
            <input v-model="createForm.databasePassword" type="password" :placeholder="t('panel.database.optional')" />
          </label>
        </div>
      </div>

      <div class="create-actions">
        <p class="meta">{{ t("panel.sites.automatic.submitHint") }}</p>
        <button type="submit" :disabled="sitesStore.creating">
          {{ sitesStore.creating ? t("panel.sites.creating") : t("panel.sites.createAndPublish") }}
        </button>
      </div>
    </form>

    <section class="domain-list">
      <div class="section-head">
        <h3>{{ t("panel.sites.title") }}</h3>
        <span class="meta">{{ sitesStore.sites.length }}</span>
      </div>

      <div v-if="sitesStore.sites.length > 0" class="domain-rows">
        <article
          v-for="site in sitesStore.sites"
          :key="site.id"
          :class="['domain-row', { active: site.name === sitesStore.selectedSiteName }]">
          <div class="domain-row-main" @click="openTool(site.name, 'pano')">
            <span class="domain-glyph">WWW</span>
            <div class="domain-id-copy">
              <strong>{{ site.name }}</strong>
              <a
                class="domain-url"
                :href="siteUrl(site)"
                target="_blank"
                rel="noopener"
                @click.stop>{{ siteUrl(site) }}</a>
              <span class="domain-path">{{ site.physicalPath }}</span>
            </div>
          </div>

          <span :class="['pill', { stopped: !isRunning(site) }]">
            <span class="state-dot"></span>
            {{ translateState(site.state) }}
          </span>

          <nav class="domain-tools" :aria-label="site.name">
            <button
              v-for="tool in quickTools"
              :key="tool.id"
              class="tool-chip"
              :title="tool.label"
              @click="openTool(site.name, tool.id)">
              <span class="chip-glyph">{{ tool.glyph }}</span>
            </button>
          </nav>

          <div class="domain-row-actions">
            <a class="secondary compact link-btn" :href="siteUrl(site)" target="_blank" rel="noopener">
              {{ t("panel.sites.open") }}
            </a>
            <button
              v-if="!isRunning(site)"
              class="secondary compact"
              :disabled="sitesStore.busySiteName === site.name"
              @click="handleStartSite(site.name)">
              {{ t("panel.sites.start") }}
            </button>
            <button
              v-else
              class="secondary compact"
              :disabled="sitesStore.busySiteName === site.name"
              @click="handleStopSite(site.name)">
              {{ t("panel.sites.stop") }}
            </button>
            <button
              class="danger compact"
              :disabled="sitesStore.busySiteName === site.name"
              @click="handleDeleteSite(site.name)">
              {{ t("panel.sites.delete") }}
            </button>
          </div>
        </article>
      </div>

      <div v-else class="empty-onboard">
        <svg viewBox="0 0 24 24" class="empty-icon" aria-hidden="true">
          <circle cx="12" cy="12" r="9" />
          <path d="M3.5 12h17M12 3c2.5 2.5 2.5 15.5 0 18M12 3c-2.5 2.5-2.5 15.5 0 18" />
        </svg>
        <h3>{{ t("panel.sites.noSites") }}</h3>
        <p class="meta">{{ t("panel.sites.noSitesHint") }}</p>
        <button v-if="isAdmin" class="compact" @click="showCreateForm = true">{{ t("panel.sites.create") }}</button>
      </div>
    </section>

  </section>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from "vue";
import { useI18n } from "vue-i18n";
import type { SiteBindingPayload, SiteDefaultsResult, SiteRecord } from "../api/sites";
import { getSiteDeletionPlan } from "../api/sites";
import { getPanelPreferences } from "../api/panelSettings";
import { extractApiDetails, extractApiMessage, useAuthStore } from "../stores/auth";
import { useSitesStore } from "../stores/sites";
import { useSoftwareStore } from "../stores/software";

const { t } = useI18n();
const sitesStore = useSitesStore();
const softwareStore = useSoftwareStore();
const authStore = useAuthStore();
// Site oluşturma/içe aktarma yalnız admin (bayi/müşteri mevcut sitelerini yönetir, oluşturamaz).
const isAdmin = computed(() => authStore.session?.user?.role === "Administrator");

// Plesk benzeri domain kartlarindaki hizli eylemler -> ilgili site araci sekmesine git.
const emit = defineEmits<{ (event: "open-tool", payload: { siteName: string; tool: string }): void }>();

const quickTools = computed(() => [
  { id: "hosting", glyph: "⚙", label: t("panel.sites.tools.hosting") },
  { id: "files", glyph: "FL", label: t("panel.sites.tools.files") },
  { id: "php", glyph: "P", label: t("panel.sites.tools.php") },
  { id: "mail", glyph: "@", label: t("panel.sites.tools.mail") },
  { id: "ssl", glyph: "SSL", label: t("panel.sites.tools.ssl") },
  // webdb server-level (admin-only) → bayi/müşteride gizli.
  ...(isAdmin.value ? [{ id: "webdb", glyph: "DB", label: t("panel.sites.tools.webdb") }] : []),
  { id: "ftp", glyph: "FT", label: t("panel.sites.tools.ftp") },
  { id: "dns", glyph: "DNS", label: t("panel.sites.tools.dns") }
]);

function openTool(siteName: string, tool: string) {
  sitesStore.selectSite(siteName);
  emit("open-tool", { siteName, tool });
}

function isRunning(site: SiteRecord): boolean {
  return site.state.toLowerCase() === "started";
}

function siteUrl(site: SiteRecord): string {
  const binding = site.bindings[0];
  if (!binding) {
    return "#";
  }
  const proto = (binding.protocol || "http").toLowerCase();
  const host = binding.hostName || window.location.hostname;
  const standard = (proto === "http" && binding.port === 80) || (proto === "https" && binding.port === 443);
  const portPart = standard ? "" : `:${binding.port}`;
  return `${proto}://${host}${portPart}`;
}

const notice = ref("");
const noticeError = ref(false);
const defaultsReport = ref<SiteDefaultsResult | null>(null);
const generatedCredentials = ref<Array<{ service: string; userName: string; password: string }>>([]);
const showCreateForm = ref(false);
const showAdvanced = ref(false);
const physicalPathEdited = ref(false);
const defaultSiteRoot = ref("C:\\inetpub\\wwwroot");
const createForm = reactive({
  name: "",
  physicalPath: "C:\\inetpub\\wwwroot\\new-site",
  applicationPoolName: "",
  hostName: "",
  ipAddress: "*",
  port: 80,
  protocol: "http",
  autoStart: true,
  runtimeProfile: "auto",
  runtimeComponentId: "",
  installRuntimeIfMissing: true,
  startupFile: "",
  provisionDefaultServices: true,
  createDatabase: false,
  databaseEngine: "",
  databaseName: "",
  databaseUserName: "",
  databasePassword: "",
  createMailDomain: false,
  createFtpUser: false,
  ftpUserName: "",
  ftpPassword: ""
});

const phpComponents = computed(() =>
  (softwareStore.catalog?.manifest ?? []).filter((component) => component.id.startsWith("php-")));

watch(() => createForm.hostName, (hostName) => {
  const name = sanitizeSiteName(hostName);
  createForm.name = name;
  if (!physicalPathEdited.value) {
    createForm.physicalPath = `${defaultSiteRoot.value}\\${sanitizeSiteName(name || "new-site")}`;
  }
});

watch(() => createForm.runtimeProfile, (profile) => {
  createForm.runtimeComponentId = "";
  createForm.startupFile = profile === "node" ? "server.js" : profile === "python" ? "app.py" : "";
});

watch(() => sitesStore.selectedSiteName, () => {
  defaultsReport.value = null;
});

onMounted(async () => {
  await getPanelPreferences()
    .then((settings) => {
      defaultSiteRoot.value = settings.defaultSiteRoot.replace(/[\\/]+$/, "");
      createForm.runtimeProfile = settings.defaultRuntimeProfile || "auto";
      createForm.installRuntimeIfMissing = settings.installRuntimeIfMissing;
      if (!physicalPathEdited.value) {
        createForm.physicalPath = `${defaultSiteRoot.value}\\${sanitizeSiteName(createForm.name || "new-site")}`;
      }
    })
    .catch(() => undefined);

  if (!softwareStore.catalog && !softwareStore.loading) {
    await softwareStore.fetchCatalog().catch(() => undefined);
  }

  if (sitesStore.sites.length > 0 || sitesStore.loading) {
    return;
  }

  try {
    await sitesStore.fetchSites();
  } catch (error) {
    showSiteLoadError(error);
  }
});

async function refreshSites() {
  try {
    await sitesStore.fetchSites();
    showNotice(t("panel.sites.refreshed"), false);
  } catch (error) {
    showSiteLoadError(error);
  }
}

async function importExistingSites() {
  try {
    await sitesStore.importExistingSites();
    if (sitesStore.selectedSiteName) {
      defaultsReport.value = await sitesStore.prepareSiteDefaults(sitesStore.selectedSiteName).catch(() => null);
    }
    showNotice(t("panel.sites.imported"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
}

async function handleAddSubdomain(label: string) {
  const parent = sitesStore.selectedSiteName;
  if (!parent || !label) {
    return;
  }
  const host = `${label}.${parent}`.toLowerCase();
  try {
    await sitesStore.createSite({
      name: host,
      hostName: host,
      physicalPath: `${defaultSiteRoot.value}\\${host}`,
      applicationPoolName: "",
      ipAddress: "*",
      port: 80,
      protocol: "http",
      autoStart: true,
      runtimeProfile: "auto",
      runtimeComponentId: "",
      installRuntimeIfMissing: true,
      startupFile: "",
      provisionDefaultServices: false
    });
    await sitesStore.fetchSites().catch(() => undefined);
    showNotice(t("panel.sites.subdomainCreated", { host }), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
}

async function submitCreateSite() {
  try {
    const requestedPort = createForm.port;
    const result = await sitesStore.createSite({ ...createForm });
    const site = result.site;
    generatedCredentials.value = result.generatedCredentials ?? [];
    defaultsReport.value = await sitesStore.prepareSiteDefaults(site.name).catch(() => null);
    const createdPort = site.bindings[0]?.port ?? requestedPort;
    createForm.name = "";
    createForm.hostName = "";
    createForm.applicationPoolName = "";
    createForm.ipAddress = "*";
    createForm.port = 80;
    createForm.runtimeProfile = "auto";
    createForm.runtimeComponentId = "";
    createForm.installRuntimeIfMissing = true;
    createForm.startupFile = "";
    createForm.provisionDefaultServices = true;
    createForm.createDatabase = false;
    createForm.createMailDomain = false;
    createForm.createFtpUser = false;
    createForm.ftpUserName = "";
    createForm.ftpPassword = "";
    createForm.physicalPath = `${defaultSiteRoot.value}\\new-site`;
    physicalPathEdited.value = false;
    showCreateForm.value = false;

    const base = createdPort !== requestedPort
      ? t("panel.sites.createdWithPort", { port: createdPort })
      : t("panel.sites.created");
    const extras = result.created.length
      ? ` ${t("panel.sites.companion.created")}: ${result.created.join(", ")}`
      : "";
    if (result.warnings.length) {
      showNotice(`${base}${extras} — ${t("panel.sites.companion.warnings")}: ${result.warnings.join(" • ")}`, true);
    } else {
      showNotice(`${base}${extras}`, false);
    }
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
}

async function handleStartSite(siteName: string) {
  try {
    await sitesStore.startSite(siteName);
    showNotice(t("panel.sites.started"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
}

async function handlePrepareDefaults(siteName: string) {
  try {
    const result = await sitesStore.prepareSiteDefaults(siteName);
    defaultsReport.value = result;
    showNotice(
      result.isComplete
        ? t("panel.sites.defaultsPrepared")
        : t("panel.sites.defaultsPartial", { features: result.failedFeatures.join(", ") }),
      !result.isComplete);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
}

async function handleStopSite(siteName: string) {
  try {
    await sitesStore.stopSite(siteName);
    showNotice(t("panel.sites.stopped"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
}

async function handleDeleteSite(siteName: string) {
  // Plesk gibi: silmeden önce siteye bağlı tüm kaynakları göster, sonra cascade sil.
  let lines: string[] = [];
  try {
    const plan = await getSiteDeletionPlan(siteName);
    const items: string[] = [];
    if (plan.mailDomains.length) {
      items.push(`${t("panel.sites.deletePlan.mail")}: ${plan.mailDomains.join(", ")}`);
    }
    if (plan.databases.length) {
      items.push(`${t("panel.sites.deletePlan.databases")}: ${plan.databases.join(", ")}`);
    }
    if (plan.ftpUsers.length) {
      items.push(`${t("panel.sites.deletePlan.ftp")}: ${plan.ftpUsers.join(", ")}`);
    }
    if (plan.hasDnsZone) {
      items.push(t("panel.sites.deletePlan.dns"));
    }
    if (plan.hasReverseProxy) {
      items.push(t("panel.sites.deletePlan.proxy"));
    }
    if (plan.hasDiskQuota) {
      items.push(t("panel.sites.deletePlan.quota"));
    }
    if (plan.hasGitDeployment) {
      items.push(t("panel.sites.deletePlan.git"));
    }
    if (plan.customerAssignments > 0) {
      items.push(`${t("panel.sites.deletePlan.customers")}: ${plan.customerAssignments}`);
    }
    items.push(`${t("panel.sites.deletePlan.files")}: ${plan.applicationRootPath || plan.physicalPath}`);
    lines = items;
  } catch {
    // Plan alınamazsa yine de basit onayla devam edilir.
  }

  const detail = lines.length
    ? `\n\n${t("panel.sites.deletePlan.intro")}\n- ${lines.join("\n- ")}`
    : "";
  if (!window.confirm(`${t("panel.sites.confirmDelete")}${detail}`)) {
    return;
  }

  try {
    // Plesk gibi cascade: siteyle aynı adı taşıyan veritabanları da silinir (plan yukarıda gösterildi/onaylandı).
    const result = await sitesStore.deleteSite(siteName, { deleteDatabases: true });
    if (result && result.warnings.length) {
      showNotice(`${t("panel.sites.deletedWithWarnings")}: ${result.warnings.join(" • ")}`, true);
    } else {
      showNotice(t("panel.sites.deleted"), false);
    }
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
}

async function handleAddBinding(payload: { siteName: string; binding: SiteBindingPayload }) {
  try {
    await sitesStore.addBinding(payload.siteName, payload.binding);
    showNotice(t("panel.sites.bindingAdded"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
}

async function handleRemoveBinding(payload: { siteName: string; binding: SiteBindingPayload }) {
  try {
    await sitesStore.removeBinding(payload.siteName, payload.binding);
    showNotice(t("panel.sites.bindingRemoved"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
}

function translateState(state: string): string {
  const key = `panel.sites.runtime.${state.toLowerCase()}`;
  const translated = t(key);
  return translated === key ? state : translated;
}

function resolveApiMessage(error: unknown): string {
  const key = extractApiMessage(error);
  const translated = t(key);
  const message = translated === key ? key : translated;
  const details = extractApiDetails(error);
  return details ? `${message}\n\n${details}` : message;
}

function showSiteLoadError(error: unknown) {
  const key = extractApiMessage(error);
  if (key === "sites.iis_not_available" || key === "sites.iis_install_required") {
    showNotice(t("panel.sites.iisPreparationHint"), false);
    return;
  }

  showNotice(resolveApiMessage(error), true);
}

function sanitizeSiteName(value: string): string {
  return value.trim().toLowerCase().replace(/[^a-z0-9._-]+/g, "-") || "new-site";
}

function showNotice(message: string, isError: boolean) {
  notice.value = message;
  noticeError.value = isError;
}

async function copyCredential(credential: { service: string; userName: string; password: string }) {
  await navigator.clipboard.writeText(
    `${credential.service}\n${t("panel.database.userName")}: ${credential.userName}\n${t("panel.form.password")}: ${credential.password}`);
  showNotice(t("panel.sites.credentials.copied"), false);
}
</script>

<style scoped>
.sites-panel,
.create-card,
.site-create-grid,
.credentials-card,
.credentials-list {
  display: grid;
  gap: 14px;
}

.sites-header,
.sites-actions,
.section-head,
.create-card-head,
.create-actions {
  display: flex;
  gap: 12px;
}

.sites-header,
.section-head {
  justify-content: space-between;
  align-items: flex-start;
}

.sites-actions {
  align-items: center;
  flex-wrap: wrap;
}

.sites-actions button {
  width: auto;
}

.site-create-grid {
  grid-template-columns: minmax(0, 1.35fr) minmax(260px, 0.65fr);
}

.create-card {
  padding: 16px;
  border: 1px solid color-mix(in srgb, var(--accent-1) 20%, transparent);
  border-radius: var(--radius-lg);
  background:
    linear-gradient(120deg, color-mix(in srgb, var(--accent-2) 13%, transparent), transparent 42%),
    var(--surface-2);
}

.create-card-head,
.create-actions {
  justify-content: space-between;
  align-items: center;
}

.create-actions button {
  width: auto;
  min-width: 190px;
}

.section-kicker {
  margin: 0 0 4px;
  color: var(--accent-1);
  font-size: 0.68rem;
  font-weight: 800;
  letter-spacing: 0.12em;
  text-transform: uppercase;
}

.ready-chip {
  padding: 5px 9px;
  border-radius: 999px;
  background: color-mix(in srgb, var(--accent-1) 12%, transparent);
  color: var(--accent-1);
  font-size: 0.72rem;
  font-weight: 700;
}

.automatic-plan,
.credentials-card {
  padding: 13px 14px;
  border: 1px solid color-mix(in srgb, var(--accent-1) 18%, var(--panel-border));
  border-radius: var(--radius-md);
  background: color-mix(in srgb, var(--accent-1) 6%, var(--surface-1));
}

.automatic-plan {
  display: grid;
  gap: 6px;
}

.automatic-plan span::before {
  content: "✓";
  margin-right: 7px;
  color: var(--accent-1);
  font-weight: 800;
}

.credentials-card {
  border-color: color-mix(in srgb, var(--warn-1) 30%, var(--panel-border));
}

.credential-row {
  display: grid;
  grid-template-columns: minmax(140px, 0.8fr) minmax(120px, 1fr) minmax(160px, 1.2fr) auto;
  gap: 10px;
  align-items: center;
  padding: 9px 0;
  border-top: 1px solid var(--panel-border);
}

.credential-row code {
  overflow: hidden;
  text-overflow: ellipsis;
}

.domain-list {
  display: grid;
  gap: 12px;
}

.empty-onboard {
  display: grid;
  justify-items: center;
  gap: 8px;
  padding: 40px 20px;
  text-align: center;
  border: 1px dashed var(--panel-border);
  border-radius: var(--radius-lg);
  background: var(--surface-1);
}

.empty-onboard h3 {
  margin: 6px 0 0;
}

.empty-onboard .empty-icon {
  width: 44px;
  height: 44px;
  fill: none;
  stroke: var(--accent-1);
  stroke-width: 1.4;
  stroke-linecap: round;
  stroke-linejoin: round;
  opacity: 0.85;
}

.empty-onboard button {
  margin-top: 8px;
}

/* Plesk benzeri tam genişlikte satır listesi (kare kartlar yerine). */
.domain-rows {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.domain-row {
  display: flex;
  align-items: center;
  gap: 16px;
  padding: 12px 16px;
  border-radius: var(--radius-lg);
  border: 1px solid var(--panel-border);
  background: var(--surface-1);
  transition: border-color 0.15s ease, box-shadow 0.15s ease;
}

.domain-row:hover {
  border-color: color-mix(in srgb, var(--accent-1) 30%, transparent);
}

.domain-row.active {
  border-color: color-mix(in srgb, var(--accent-1) 45%, transparent);
  box-shadow: 0 0 0 1px color-mix(in srgb, var(--accent-1) 30%, transparent);
}

.domain-row-main {
  display: flex;
  align-items: center;
  gap: 12px;
  min-width: 0;
  flex: 1 1 280px;
  cursor: pointer;
}

.domain-path {
  color: var(--text-muted);
  font-size: 0.72rem;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.domain-row-actions {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  flex-shrink: 0;
}

.domain-row-actions .link-btn {
  display: inline-flex;
  align-items: center;
  text-decoration: none;
}

.domain-glyph {
  display: grid;
  place-items: center;
  width: 38px;
  height: 38px;
  border-radius: 10px;
  background: color-mix(in srgb, var(--accent-1) 14%, transparent);
  color: var(--accent-1);
  font-size: 0.62rem;
  font-weight: 800;
  letter-spacing: 0.04em;
  flex-shrink: 0;
}

.domain-id-copy {
  display: grid;
  gap: 2px;
  min-width: 0;
}

.domain-id-copy strong {
  font-size: 0.96rem;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.domain-url {
  color: var(--accent-1);
  font-size: 0.78rem;
  text-decoration: none;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.domain-url:hover {
  text-decoration: underline;
}

/* Hızlı araç simgeleri — satırda kompakt ikon dizisi (Plesk benzeri). */
.domain-tools {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  flex-shrink: 0;
}

.domain-tools .tool-chip {
  display: inline-grid;
  place-items: center;
  width: 32px;
  height: 32px;
  border-radius: 8px;
  border: 1px solid var(--panel-border);
  background: var(--surface-2);
  color: var(--text-strong);
  cursor: pointer;
}

.domain-tools .tool-chip:hover {
  border-color: color-mix(in srgb, var(--accent-1) 40%, transparent);
  color: var(--accent-1);
}

.chip-glyph {
  font-size: 0.6rem;
  font-weight: 800;
  color: var(--accent-1);
}

@media (max-width: 900px) {
  .domain-row {
    flex-wrap: wrap;
  }
  .domain-row-main {
    flex: 1 1 100%;
  }
}

.meta {
  color: var(--text-muted);
}

.meta {
  margin: 0;
}

.pill {
  display: inline-flex;
  gap: 6px;
  align-items: center;
  border-radius: 999px;
  padding: 4px 10px;
  background: color-mix(in srgb, var(--accent-1) 14%, transparent);
  color: var(--accent-1);
  font-size: 0.82rem;
  font-weight: 700;
}

.pill.stopped {
  background: color-mix(in srgb, var(--warn-1) 12%, transparent);
  color: var(--warn-text);
}

.state-dot {
  width: 6px;
  height: 6px;
  border-radius: 999px;
  background: currentColor;
}

label {
  display: grid;
  gap: 8px;
}

.checkbox {
  grid-template-columns: auto 1fr;
  align-items: center;
}

.checkbox input {
  width: 17px;
  height: 17px;
  margin: 0;
}

input,
button {
  width: 100%;
  border-radius: var(--radius-sm);
  font: inherit;
}

input {
  border: 1px solid var(--panel-border);
  padding: 12px 14px;
  background: var(--input-bg);
  color: var(--text-strong);
}

button.compact {
  padding: 10px 12px;
}

button:disabled {
  opacity: 0.65;
  cursor: wait;
}

@media (max-width: 1080px) {
  .site-create-grid,
  .sites-columns,
  .credential-row {
    grid-template-columns: 1fr;
  }
}

@media (max-width: 760px) {
  .sites-header,
  .sites-actions,
  .section-head,
  .site-item-top,
  .create-card-head,
  .create-actions {
    flex-direction: column;
    align-items: flex-start;
  }

  .create-actions button {
    width: 100%;
  }
}
</style>
