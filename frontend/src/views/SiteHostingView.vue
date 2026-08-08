<template>
  <section class="hosting-workspace">
    <div class="hosting-shell">
      <aside class="hosting-nav">
        <div class="site-badge">
          <span>{{ t("panel.hosting.currentSite") }}</span>
          <strong>{{ selectedSite?.name || t("panel.php.selectSite") }}</strong>
          <small>{{ primaryBinding }}</small>
        </div>

        <button
          v-for="section in sections"
          :key="section.id"
          :class="['hosting-nav-item', { active: activeSection === section.id }]"
          @click="activeSection = section.id">
          <strong>{{ section.label }}</strong>
          <small>{{ section.hint }}</small>
        </button>
      </aside>

      <div class="hosting-main">
        <div class="section-head">
          <div>
            <h3>{{ t("panel.hosting.title") }}</h3>
            <p class="meta">{{ t("panel.hosting.subtitle") }}</p>
          </div>
          <button class="secondary compact" :disabled="!siteName || busy" @click="load">{{ t("panel.hosting.refresh") }}</button>
        </div>

        <p v-if="notice" :class="['notice', 'page-notice', { error: noticeError }]" :role="noticeError ? 'alert' : 'status'">{{ notice }}</p>
        <p v-if="!siteName" class="empty-state">{{ t("panel.hosting.selectSiteHint") }}</p>

        <!-- Veri gelmeden ayar anahtarlarını göstermiyoruz: kapalı görünen bir anahtar,
             "kapalı" demektir — oysa henüz okunmamıştır. -->
        <p v-else-if="!settingsLoaded" class="empty-state" role="status" aria-live="polite">
          {{ t("panel.common.loading") }}
        </p>

        <template v-else>
          <section v-if="activeSection === 'general'" class="settings-card">
            <div class="settings-card-head">
              <div>
                <h4>{{ t("panel.hosting.general") }}</h4>
                <p class="meta">{{ t("panel.hosting.generalHint") }}</p>
              </div>
            </div>

            <div class="settings-rows">
              <label class="setting-row">
                <span>{{ t("panel.hosting.documentRoot") }}</span>
                <input v-model="documentRoot" :placeholder="selectedSite?.physicalPath || ''" spellcheck="false" />
                <small>{{ t("panel.hosting.documentRootHelp") }}</small>
              </label>
              <div class="setting-row doc-root-actions">
                <button class="compact" :disabled="busy || !documentRoot.trim()" @click="saveDocumentRoot">
                  {{ busy ? t("panel.hosting.saving") : t("panel.hosting.saveDocumentRoot") }}
                </button>
                <button type="button" class="secondary compact" :disabled="busy" @click="useLaravelPublic">
                  {{ t("panel.hosting.useLaravelPublic") }}
                </button>
                <button type="button" class="secondary compact" :disabled="busy" @click="enableRewrite">
                  {{ t("panel.hosting.enableRewrite") }}
                </button>
                <button type="button" class="secondary compact" :disabled="busy" @click="repairPermissions">
                  {{ t("panel.hosting.repairPermissions") }}
                </button>
              </div>

              <label class="setting-row">
                <span>{{ t("panel.sites.appPool") }}</span>
                <input :value="selectedSite?.applicationPoolName || '-'" readonly />
                <small>{{ t("panel.hosting.poolHelp") }}</small>
              </label>

              <label class="setting-row">
                <span>{{ t("panel.hosting.redirect") }}</span>
                <input v-model="form.redirectUrl" placeholder="https://example.com" />
                <small>{{ t("panel.hosting.redirectHelp") }}</small>
              </label>

              <label class="setting-row checkbox-row">
                <span>{{ t("panel.hosting.directoryBrowsing") }}</span>
                <input v-model="form.directoryBrowsingEnabled" type="checkbox" />
                <small>{{ t("panel.hosting.directoryBrowsingHelp") }}</small>
              </label>
            </div>

            <div class="action-row">
              <button :disabled="busy" @click="save">{{ t("panel.hosting.save") }}</button>
            </div>

            <!-- ALAN ADI TAKMA ADLARI: ".com + .com.tr aynı siteye" en sık gelen talep ve panelde
                 hiç yoktu (kullanıcı ikinci bir site açmak zorunda kalıyordu). Genel bölümde,
                 belge kökünün yanında: alan adıyla ilgili iş zaten burada yapılıyor. -->
            <div class="alias-block">
              <SiteAliases :site-name="siteName" />
            </div>
          </section>

          <section v-else-if="activeSection === 'documents'" class="settings-card">
            <div class="settings-card-head">
              <div>
                <h4>{{ t("panel.hosting.defaultDocuments") }}</h4>
                <p class="meta">{{ t("panel.hosting.defaultDocumentsHint") }}</p>
              </div>
            </div>
            <label class="setting-row align-start">
              <span>{{ t("panel.hosting.defaultDocuments") }}</span>
              <textarea v-model="defaultDocumentsText" rows="7"></textarea>
              <small>{{ t("panel.hosting.defaultDocumentsHelp") }}</small>
            </label>
            <div class="action-row">
              <button :disabled="busy" @click="save">{{ t("panel.hosting.save") }}</button>
            </div>
          </section>

          <section v-else-if="activeSection === 'traffic'" class="settings-card">
            <div class="settings-card-head">
              <div>
                <h4>{{ t("panel.hosting.traffic") }}</h4>
                <p class="meta">{{ t("panel.hosting.trafficHint") }}</p>
              </div>
            </div>
            <div class="settings-rows">
              <label class="setting-row">
                <span>{{ t("panel.hosting.maxConnections") }}</span>
                <input v-model.number="form.maximumConnections" type="number" min="0" />
                <small>{{ t("panel.hosting.maxConnectionsHelp") }}</small>
              </label>

              <label class="setting-row">
                <span>{{ t("panel.hosting.bandwidth") }}</span>
                <input v-model.number="form.maximumBandwidthBytesPerSecond" type="number" min="0" />
                <small>{{ t("panel.hosting.bandwidthHelp") }}</small>
              </label>
            </div>

            <div class="action-row">
              <button :disabled="busy" @click="save">{{ t("panel.hosting.save") }}</button>
            </div>
          </section>

          <section v-else-if="activeSection === 'access'" class="settings-card">
            <div class="settings-card-head">
              <div>
                <h4>{{ t("panel.hosting.access") }}</h4>
                <p class="meta">{{ t("panel.hosting.accessHint") }}</p>
              </div>
            </div>

            <div class="settings-rows">
              <label class="setting-row align-start">
                <span>{{ t("panel.hosting.deniedIps") }}</span>
                <textarea v-model="deniedIps" rows="4" placeholder="203.0.113.10&#10;198.51.100.0"></textarea>
                <small>{{ t("panel.hosting.deniedIpsHelp") }}</small>
              </label>

              <label class="setting-row align-start">
                <span>{{ t("panel.hosting.allowedReferrers") }}</span>
                <textarea v-model="allowedReferrers" rows="4" placeholder="example.com&#10;cdn.example.com"></textarea>
                <small>{{ t("panel.hosting.referrersHelp") }}</small>
              </label>

              <label class="setting-row">
                <span>{{ t("panel.hosting.extensions") }}</span>
                <input v-model="protectedExtensions" placeholder="jpg, png, webp, mp4" />
                <small>{{ t("panel.hosting.extensionsHelp") }}</small>
              </label>

              <label class="setting-row checkbox-row">
                <span>{{ t("panel.hosting.hotlink") }}</span>
                <input v-model="form.hotlinkProtectionEnabled" type="checkbox" />
                <small>{{ t("panel.hosting.hotlinkHelp") }}</small>
              </label>
            </div>

            <div class="action-row">
              <button :disabled="busy" @click="save">{{ t("panel.hosting.save") }}</button>
            </div>
          </section>

          <section v-else-if="activeSection === 'errors'" class="settings-card">
            <div class="settings-card-head">
              <div>
                <h4>{{ t("panel.hosting.errorsPerformance") }}</h4>
                <p class="meta">{{ t("panel.hosting.errorsPerformanceHint") }}</p>
              </div>
            </div>
            <div class="settings-rows">
              <label class="setting-row checkbox-row">
                <span>{{ t("panel.hosting.detailedErrors") }}</span>
                <input v-model="form.detailedErrorsEnabled" type="checkbox" />
                <small>{{ t("panel.hosting.detailedErrorsHelp") }}</small>
              </label>
              <label class="setting-row checkbox-row">
                <span>{{ t("panel.hosting.staticCompression") }}</span>
                <input v-model="form.staticCompressionEnabled" type="checkbox" />
                <small>{{ t("panel.hosting.staticCompressionHelp") }}</small>
              </label>
              <label class="setting-row checkbox-row">
                <span>{{ t("panel.hosting.dynamicCompression") }}</span>
                <input v-model="form.dynamicCompressionEnabled" type="checkbox" />
                <small>{{ t("panel.hosting.dynamicCompressionHelp") }}</small>
              </label>
              <label class="setting-row">
                <span>{{ t("panel.hosting.maximumUpload") }}</span>
                <input v-model.number="maximumUploadMegabytes" type="number" min="1" max="4095" />
                <small>{{ t("panel.hosting.maximumUploadHelp") }}</small>
              </label>
            </div>
            <div class="action-row">
              <button :disabled="busy" @click="save">{{ t("panel.hosting.save") }}</button>
            </div>
          </section>

          <section v-else-if="activeSection === 'quota'" class="settings-card">
            <div class="settings-card-head">
              <div>
                <h4>{{ t("panel.hosting.quota") }}</h4>
                <p class="meta">{{ t("panel.hosting.quotaHint") }}</p>
              </div>
              <button class="secondary compact" :disabled="busy || !quota?.quotaEnabled" @click="removeSiteQuota">
                {{ t("panel.hosting.quotaRemove") }}
              </button>
            </div>

            <!-- ÇIKMAZ SOKAK DEĞİL: rol eksikse kurma düğmesi de burada. -->
            <div v-if="quota && !quota.fsrmAvailable" class="notice error fsrm-missing" role="alert">
              <span>{{ t("panel.hosting.quotaUnavailable") }}</span>
              <button class="secondary compact" :disabled="installingFsrm" @click="installFsrmRole">
                {{ installingFsrm ? t("panel.hosting.quotaInstalling") : t("panel.hosting.quotaInstall") }}
              </button>
            </div>
            <!-- Okunamadıysa ÖZET GÖSTERİLMEZ: sıfırlar ölçüm sanılıyordu. -->
            <p v-if="quotaError" class="notice error" role="alert">{{ quotaError }}</p>

            <div v-if="!quotaError" class="summary-strip quota-summary">
              <div class="summary-item">
                <span>{{ t("panel.hosting.quotaStatus") }}</span>
                <strong :class="['status-pill', quota?.quotaEnabled ? 'ok' : 'warn']">
                  {{ quota?.quotaEnabled ? t("panel.status.enabled") : t("panel.status.disabled") }}
                </strong>
              </div>
              <div class="summary-item">
                <span>{{ t("panel.hosting.quotaUsed") }}</span>
                <strong>{{ formatBytes(quota?.usedBytes ?? 0) }}</strong>
              </div>
              <div class="summary-item">
                <span>{{ t("panel.hosting.quotaLimit") }}</span>
                <strong>{{ quota?.quotaEnabled ? formatBytes(quota?.limitBytes ?? 0) : t("panel.security.unlimited") }}</strong>
              </div>
            </div>

            <div class="settings-rows">
              <label class="setting-row">
                <span>{{ t("panel.hosting.quotaLimitMb") }}</span>
                <input v-model.number="quotaForm.limitMegabytes" type="number" min="1" />
                <small>{{ t("panel.hosting.quotaLimitMbHelp") }}</small>
              </label>
              <label class="setting-row checkbox-row">
                <span>{{ t("panel.hosting.quotaSoft") }}</span>
                <input v-model="quotaForm.softLimit" type="checkbox" />
                <small>{{ t("panel.hosting.quotaSoftHelp") }}</small>
              </label>
            </div>

            <div class="action-row">
              <button :disabled="busy" @click="saveSiteQuota">{{ t("panel.hosting.save") }}</button>
            </div>
          </section>

          <section v-else-if="activeSection === 'stats'" class="settings-card">
            <div class="settings-card-head">
              <div>
                <h4>{{ t("panel.hosting.stats") }}</h4>
                <p class="meta">{{ t("panel.hosting.statsHint") }}</p>
              </div>
              <select v-model.number="statsDays" class="compact-select" @change="loadStats">
                <option :value="1">{{ t("panel.hosting.statsToday") }}</option>
                <option :value="7">{{ t("panel.hosting.statsWeek") }}</option>
                <option :value="30">{{ t("panel.hosting.statsMonth") }}</option>
              </select>
            </div>

            <p v-if="stats && !stats.hasData" class="empty-state">{{ t("panel.hosting.statsEmpty") }}</p>

            <template v-if="stats && stats.hasData">
              <div class="summary-strip quota-summary stats-summary">
                <div class="summary-item">
                  <span>{{ t("panel.hosting.statsRequests") }}</span>
                  <strong>{{ stats.totalRequests.toLocaleString() }}</strong>
                </div>
                <div class="summary-item">
                  <span>{{ t("panel.hosting.statsVisitors") }}</span>
                  <strong>{{ stats.uniqueVisitors.toLocaleString() }}{{ stats.uniqueVisitorsApproximate ? "+" : "" }}</strong>
                </div>
                <div class="summary-item">
                  <span>{{ t("panel.hosting.statsBandwidth") }}</span>
                  <strong>{{ formatBytes(stats.totalBytes) }}</strong>
                </div>
                <div class="summary-item">
                  <span>2xx / 3xx / 4xx / 5xx</span>
                  <strong>{{ stats.status2xx }} / {{ stats.status3xx }} / {{ stats.status4xx }} / {{ stats.status5xx }}</strong>
                </div>
              </div>

              <div class="stats-columns">
                <div>
                  <h5>{{ t("panel.hosting.statsTopPages") }}</h5>
                  <div v-for="page in stats.topPages" :key="page.key" class="stat-bar">
                    <span class="stat-label" :title="page.key">{{ page.key }}</span>
                    <span class="stat-count">{{ page.count.toLocaleString() }}</span>
                  </div>
                </div>
                <div>
                  <h5>{{ t("panel.hosting.statsTopReferrers") }}</h5>
                  <p v-if="stats.topReferrers.length === 0" class="meta">{{ t("panel.hosting.statsNoReferrers") }}</p>
                  <div v-for="ref in stats.topReferrers" :key="ref.key" class="stat-bar">
                    <span class="stat-label" :title="ref.key">{{ ref.key }}</span>
                    <span class="stat-count">{{ ref.count.toLocaleString() }}</span>
                  </div>
                </div>
              </div>

              <div>
                <h5>{{ t("panel.hosting.statsDaily") }}</h5>
                <div v-for="day in stats.dailyHits" :key="day.key" class="stat-bar">
                  <span class="stat-label">{{ day.key }}</span>
                  <span class="stat-track"><span class="stat-fill" :style="{ width: dailyWidth(day.count) }"></span></span>
                  <span class="stat-count">{{ day.count.toLocaleString() }}</span>
                </div>
              </div>
            </template>
          </section>

          <section v-else-if="activeSection === 'git'" class="settings-card">
            <div class="settings-card-head">
              <div>
                <h4>{{ t("panel.hosting.git") }}</h4>
                <p class="meta">{{ t("panel.hosting.gitHint") }}</p>
              </div>
              <button class="compact" :disabled="busy || !git?.configured" @click="deploySite">
                {{ t("panel.hosting.gitDeploy") }}
              </button>
            </div>

            <SectionLoadError v-if="gitLoad.message.value" :message="gitLoad.message.value" @retry="loadGit" />

            <div class="settings-rows">
              <label class="setting-row">
                <span>{{ t("panel.hosting.gitRepo") }}</span>
                <input v-model="gitForm.repositoryUrl" placeholder="https://github.com/owner/repo.git" />
                <small>{{ t("panel.hosting.gitRepoHelp") }}</small>
              </label>
              <label class="setting-row">
                <span>{{ t("panel.hosting.gitBranch") }}</span>
                <input v-model="gitForm.branch" placeholder="main" />
                <small>{{ t("panel.hosting.gitBranchHelp") }}</small>
              </label>
            </div>

            <div class="action-row">
              <button :disabled="busy" @click="saveGit()">{{ t("panel.hosting.save") }}</button>
              <button class="secondary compact" :disabled="busy || !git?.configured" @click="regenerateSecret">
                {{ t("panel.hosting.gitRegenerate") }}
              </button>
            </div>

            <div v-if="git?.configured" class="settings-rows">
              <label class="setting-row">
                <span>{{ t("panel.hosting.gitLastDeploy") }}</span>
                <input :value="git.lastDeployUtc ? `${new Date(git.lastDeployUtc).toLocaleString()} (${git.lastCommit})` : '-'" readonly />
                <small>{{ git.lastDeployStatus || "-" }}</small>
              </label>
              <label class="setting-row align-start">
                <span>{{ t("panel.hosting.gitWebhook") }}</span>
                <textarea :value="webhookUrl" rows="2" readonly></textarea>
                <small>{{ t("panel.hosting.gitWebhookHelp") }}</small>
              </label>
            </div>

            <pre v-if="gitOutput" class="composer-output">{{ gitOutput }}</pre>
          </section>

          <section v-else class="settings-card">
            <div class="settings-card-head">
              <div>
                <h4>{{ t("panel.hosting.tools") }}</h4>
                <p class="meta">{{ t("panel.hosting.toolsHint") }}</p>
              </div>
              <button class="secondary compact" :disabled="busy" @click="runComposer">{{ t("panel.hosting.composer") }}</button>
            </div>

            <div class="composer-table">
              <div class="composer-row head">
                <span>{{ t("panel.hosting.tool") }}</span>
                <span>{{ t("panel.hosting.scope") }}</span>
                <span>{{ t("panel.hosting.status") }}</span>
              </div>
              <div class="composer-row">
                <strong>Composer</strong>
                <span>{{ selectedSite?.physicalPath || "-" }}</span>
                <span>{{ composerOutput ? t("panel.hosting.outputReady") : t("panel.hosting.ready") }}</span>
              </div>
            </div>

            <pre v-if="composerOutput" class="composer-output">{{ composerOutput }}</pre>
            <p class="meta composer-cmd-hint">{{ t("panel.hosting.toolkitMoved") }}</p>

            <ul class="help-list">
              <li>{{ t("panel.hosting.composerHelp1") }}</li>
              <li>{{ t("panel.hosting.composerHelp2") }}</li>
            </ul>
          </section>
        </template>
      </div>
    </div>
  </section>
</template>

<script setup lang="ts">
import { computed, reactive, ref, watch } from "vue";
import { useI18n } from "vue-i18n";
import SiteAliases from "../components/SiteAliases.vue";
import SectionLoadError from "../components/SectionLoadError.vue";
import { useSectionError } from "../composables/useNotice";
import { getSiteHostingSettings, runArtisanCommand, runComposerCommand, runComposerInstall, updateSiteHostingSettings } from "../api/siteHosting";
import { getQuota, removeQuota, setQuota, type SiteDiskQuota, installFsrm } from "../api/quota";
import { configureGitDeployment, deployGit, getGitDeployment, type GitDeployment } from "../api/git";
import { getSiteStatistics, type WebStatistics } from "../api/statistics";
import { extractApiDetails, extractApiMessage } from "../stores/auth";
import { useSitesStore } from "../stores/sites";
import { updateDocumentRoot, enableFrameworkRewrite, repairSitePermissions } from "../api/sites";

const { t } = useI18n();
const sitesStore = useSitesStore();
const siteName = computed(() => sitesStore.selectedSiteName);
const selectedSite = computed(() => sitesStore.selectedSite);
const documentRoot = ref("");
watch(selectedSite, (site) => { documentRoot.value = site?.physicalPath ?? ""; }, { immediate: true });
const activeSection = ref("general");
const busy = ref(false);
// Ayarlar SUNUCUDAN gelene kadar anahtarlar varsayılan (kapalı) durumda render ediliyordu; kullanıcı
// "her şey kapalı" sanıp yanlış karar veriyordu. Bu bayrak, "henüz bilmiyorum" ile "kapalı" arasındaki
// farkı ekranda görünür kılar.
const settingsLoaded = ref(false);
const notice = ref("");
const noticeError = ref(false);
const deniedIps = ref("");
const allowedReferrers = ref("");
const protectedExtensions = ref("jpg, jpeg, png, gif, webp, svg, mp4");
const defaultDocumentsText = ref("index.html\nindex.php\nDefault.aspx\ndefault.asp");
const composerOutput = ref("");
const quota = ref<SiteDiskQuota | null>(null);
const quotaForm = reactive({ limitMegabytes: 1024, softLimit: false });
const git = ref<GitDeployment | null>(null);
// "Okunamadı" ile "kurulu değil" AYRI durumlar; ikincisi boş form, ilki uyarı + tekrar dene.
const gitLoad = useSectionError();
const gitForm = reactive({ repositoryUrl: "", branch: "main" });
const gitOutput = ref("");
const stats = ref<WebStatistics | null>(null);
const statsDays = ref(7);
const maxDailyHit = computed(() => Math.max(1, ...(stats.value?.dailyHits.map((day) => day.count) ?? [1])));
const webhookUrl = computed(() => {
  if (!git.value?.configured) return "";
  return `${window.location.origin}${git.value.webhookPath}?token=${git.value.webhookSecret}`;
});
const form = reactive({
  maximumBandwidthBytesPerSecond: 0,
  maximumConnections: 0,
  redirectUrl: "",
  directoryBrowsingEnabled: false,
  detailedErrorsEnabled: false,
  staticCompressionEnabled: true,
  dynamicCompressionEnabled: false,
  maximumUploadBytes: 31_457_280,
  hotlinkProtectionEnabled: false
});
const maximumUploadMegabytes = computed({
  get: () => Math.max(1, Math.round(form.maximumUploadBytes / 1_048_576)),
  set: (value: number) => {
    form.maximumUploadBytes = Math.max(1, value || 1) * 1_048_576;
  }
});

const sections = computed(() => [
  { id: "general", label: t("panel.hosting.general"), hint: t("panel.hosting.generalHint") },
  { id: "documents", label: t("panel.hosting.defaultDocuments"), hint: t("panel.hosting.defaultDocumentsHint") },
  { id: "traffic", label: t("panel.hosting.traffic"), hint: t("panel.hosting.trafficHint") },
  { id: "access", label: t("panel.hosting.access"), hint: t("panel.hosting.accessHint") },
  { id: "errors", label: t("panel.hosting.errorsPerformance"), hint: t("panel.hosting.errorsPerformanceHint") },
  { id: "quota", label: t("panel.hosting.quota"), hint: t("panel.hosting.quotaHint") },
  { id: "git", label: t("panel.hosting.git"), hint: t("panel.hosting.gitHint") },
  { id: "tools", label: t("panel.hosting.tools"), hint: t("panel.hosting.toolsHintShort") }
]);

const primaryBinding = computed(() => {
  const binding = selectedSite.value?.bindings[0];
  if (!binding) return t("panel.sites.bindingNone");
  return binding.hostName
    ? `${binding.protocol.toUpperCase()} ${binding.hostName}:${binding.port}`
    : `${binding.protocol.toUpperCase()} ${binding.ipAddress}:${binding.port}`;
});

watch(siteName, load, { immediate: true });
watch(activeSection, (section) => {
  if (section === "stats" && !stats.value) {
    loadStats();
  }
});

async function load() {
  if (!siteName.value) return;
  busy.value = true;
  try {
    const settings = await getSiteHostingSettings(siteName.value);
    form.maximumBandwidthBytesPerSecond = settings.maximumBandwidthBytesPerSecond;
    form.maximumConnections = settings.maximumConnections;
    form.redirectUrl = settings.redirectUrl;
    form.directoryBrowsingEnabled = settings.directoryBrowsingEnabled;
    form.detailedErrorsEnabled = settings.detailedErrorsEnabled;
    form.staticCompressionEnabled = settings.staticCompressionEnabled;
    form.dynamicCompressionEnabled = settings.dynamicCompressionEnabled;
    form.maximumUploadBytes = settings.maximumUploadBytes;
    form.hotlinkProtectionEnabled = settings.hotlinkProtectionEnabled;
    defaultDocumentsText.value = settings.defaultDocuments.join("\n");
    deniedIps.value = settings.deniedIpAddresses.join("\n");
    allowedReferrers.value = settings.allowedReferrerHosts.join("\n");
    protectedExtensions.value = settings.protectedExtensions.join(", ");
    await loadQuota();
    await loadGit();
    settingsLoaded.value = true;
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    busy.value = false;
  }
}

async function save() {
  if (!siteName.value) return;
  busy.value = true;
  try {
    await updateSiteHostingSettings(siteName.value, {
      ...form,
      defaultDocuments: splitLines(defaultDocumentsText.value),
      deniedIpAddresses: splitLines(deniedIps.value),
      allowedReferrerHosts: splitLines(allowedReferrers.value),
      protectedExtensions: protectedExtensions.value.split(",").map(item => item.trim()).filter(Boolean)
    });
    showNotice(t("panel.hosting.saved"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    busy.value = false;
  }
}

// #1 ⭐ Belge kökü / web root kaydet (Laravel/Symfony için ...\public). IIS'te kalıcı; panel revert etmez.
async function saveDocumentRoot() {
  if (!siteName.value || !documentRoot.value.trim()) return;
  busy.value = true;
  try {
    await updateDocumentRoot(siteName.value, documentRoot.value.trim());
    await sitesStore.fetchSites();
    showNotice(t("panel.hosting.documentRootSaved"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    busy.value = false;
  }
}

// Mevcut yolun altına \public ekle (Laravel/Symfony belge kökü). Kullanıcı kaydetmeden önce görür/düzenler.
function useLaravelPublic() {
  const base = (selectedSite.value?.physicalPath ?? "").replace(/[\\/]+$/, "");
  documentRoot.value = base.length > 0 ? `${base}\\public` : "public";
}

// #2 — pretty-URL (index.php front controller) yönlendirmesini etkinleştir. URL Rewrite modülü yoksa net hata.
async function enableRewrite() {
  if (!siteName.value) return;
  busy.value = true;
  try {
    await enableFrameworkRewrite(siteName.value);
    showNotice(t("panel.hosting.rewriteEnabled"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    busy.value = false;
  }
}

// #5 — mevcut siteye yazma iznini yeniden uygula.
async function repairPermissions() {
  if (!siteName.value) return;
  busy.value = true;
  try {
    await repairSitePermissions(siteName.value);
    showNotice(t("panel.hosting.permissionsRepaired"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    busy.value = false;
  }
}

async function runComposer() {
  if (!siteName.value) return;
  busy.value = true;
  try {
    const result = await runComposerInstall(siteName.value);
    composerOutput.value = [result.output, result.error].filter(Boolean).join("\n");
    showNotice(result.exitCode === 0 ? t("panel.hosting.composerDone") : t("panel.hosting.composerFailed"), result.exitCode !== 0);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    busy.value = false;
  }
}

const composerCommand = ref("");

async function runComposerCmd() {
  if (!siteName.value || !composerCommand.value.trim()) return;
  busy.value = true;
  try {
    const result = await runComposerCommand(siteName.value, composerCommand.value.trim());
    composerOutput.value = [result.output, result.error].filter(Boolean).join("\n");
    showNotice(result.exitCode === 0 ? t("panel.hosting.composerDone") : t("panel.hosting.composerFailed"), result.exitCode !== 0);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    busy.value = false;
  }
}

const artisanCommand = ref("");
const artisanOutput = ref("");

async function runArtisanCmd() {
  if (!siteName.value || !artisanCommand.value.trim()) return;
  busy.value = true;
  try {
    const result = await runArtisanCommand(siteName.value, artisanCommand.value.trim());
    artisanOutput.value = [result.output, result.error].filter(Boolean).join("\n");
    showNotice(result.exitCode === 0 ? t("panel.hosting.artisanDone") : t("panel.hosting.artisanFailed"), result.exitCode !== 0);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    busy.value = false;
  }
}

const quotaError = ref("");
const installingFsrm = ref(false);

async function installFsrmRole() {
  installingFsrm.value = true;
  quotaError.value = "";
  try {
    quota.value = await installFsrm(siteName.value);
    showNotice(t("panel.hosting.quotaInstalled"), false);
  } catch (error) {
    quotaError.value = resolveApiMessage(error);
  } finally {
    installingFsrm.value = false;
  }
}

async function loadQuota() {
  quotaError.value = "";
  if (!siteName.value) return;
  try {
    quota.value = await getQuota(siteName.value);
    if (quota.value.quotaEnabled) {
      quotaForm.limitMegabytes = Math.max(1, Math.round(quota.value.limitBytes / 1_048_576));
      quotaForm.softLimit = quota.value.softLimit;
    }
  } catch (error) {
    // ÖLÇEMEDİK ≠ KAPALI. Önceden yalnız quota=null yapılıyordu ve kart "Durum: Kapalı,
    // Kullanılan: 0 B, Sınır: Sınırsız" gösteriyordu — bunlar ölçüm değil, null'ın varsayılanı.
    // Kullanıcı kotanın gerçekten kapalı olduğuna inanıyordu.
    quota.value = null;
    quotaError.value = resolveApiMessage(error);
  }
}

async function saveSiteQuota() {
  if (!siteName.value) return;
  busy.value = true;
  try {
    quota.value = await setQuota(siteName.value, quotaForm.limitMegabytes, quotaForm.softLimit);
    showNotice(t("panel.hosting.quotaSaved"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    busy.value = false;
  }
}

async function removeSiteQuota() {
  if (!siteName.value) return;
  busy.value = true;
  try {
    quota.value = await removeQuota(siteName.value);
    showNotice(t("panel.hosting.quotaRemoved"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    busy.value = false;
  }
}

function formatBytes(value: number) {
  if (value < 1024) return `${value} B`;
  if (value < 1024 ** 2) return `${(value / 1024).toFixed(1)} KiB`;
  if (value < 1024 ** 3) return `${(value / 1024 ** 2).toFixed(1)} MiB`;
  return `${(value / 1024 ** 3).toFixed(2)} GiB`;
}

async function loadGit() {
  if (!siteName.value) return;
  try {
    git.value = await getGitDeployment(siteName.value);
    if (git.value.configured) {
      gitForm.repositoryUrl = git.value.repositoryUrl;
      gitForm.branch = git.value.branch;
    }
    gitLoad.clear();
  } catch (error) {
    // Hata YUTULMUYOR: boş form "git kurulu değil" sanılıyordu; kullanıcı var olan
    // dağıtımı yeniden kurmaya kalkabilirdi.
    git.value = null;
    gitLoad.capture(error);
  }
}

async function saveGit(regenerate = false) {
  if (!siteName.value) return;
  busy.value = true;
  try {
    git.value = await configureGitDeployment(siteName.value, gitForm.repositoryUrl, gitForm.branch, regenerate);
    showNotice(t("panel.hosting.gitSaved"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    busy.value = false;
  }
}

async function regenerateSecret() {
  await saveGit(true);
}

async function deploySite() {
  if (!siteName.value) return;
  busy.value = true;
  try {
    const result = await deployGit(siteName.value);
    git.value = result.deployment;
    gitOutput.value = result.output;
    showNotice(result.success ? t("panel.hosting.gitDeployed") : t("panel.hosting.gitDeployFailed"), !result.success);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    busy.value = false;
  }
}

async function loadStats() {
  if (!siteName.value) return;
  busy.value = true;
  try {
    stats.value = await getSiteStatistics(siteName.value, statsDays.value);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    busy.value = false;
  }
}

function dailyWidth(count: number) {
  return `${Math.round((count / maxDailyHit.value) * 100)}%`;
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
.alias-block {
  margin-top: 14px;
  padding-top: 14px;
  border-top: 1px solid var(--panel-border);
}

.fsrm-missing {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
  flex-wrap: wrap;
}

.hosting-workspace,
.hosting-main,
.settings-card,
.settings-rows {
  display: grid;
  gap: 14px;
  min-width: 0;
}

.hosting-shell {
  display: grid;
  grid-template-columns: minmax(210px, 0.24fr) minmax(0, 1fr);
  gap: 14px;
  align-items: start;
}

.hosting-nav,
.settings-card {
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-lg);
  background: linear-gradient(180deg, var(--surface-2), var(--surface-2));
  box-shadow: var(--panel-shadow);
}

.hosting-nav {
  position: sticky;
  top: 12px;
  display: grid;
  gap: 6px;
  padding: 12px;
}

.site-badge {
  display: grid;
  gap: 5px;
  padding: 12px;
  margin-bottom: 4px;
  border: 1px solid color-mix(in srgb, var(--accent-1) 13%, transparent);
  border-radius: var(--radius-md);
  background: color-mix(in srgb, var(--accent-1) 8%, transparent);
}

.site-badge span,
.site-badge small,
.hosting-nav-item small,
.setting-row small,
.help-list {
  color: var(--text-muted);
}

.site-badge span {
  font-size: 0.68rem;
  font-weight: 850;
  letter-spacing: 0.08em;
  text-transform: uppercase;
}

.hosting-nav-item {
  display: grid;
  gap: 4px;
  width: 100%;
  padding: 11px 12px;
  border-left: 3px solid transparent;
  background: transparent;
  color: var(--text-soft);
  text-align: left;
}

.hosting-nav-item.active {
  border-left-color: var(--accent-1);
  background: var(--surface-3);
  color: var(--accent-1);
}

.section-head,
.settings-card-head,
.action-row {
  display: flex;
  gap: 12px;
  align-items: flex-start;
  justify-content: space-between;
}

.section-head button,
.settings-card-head button,
.action-row button {
  width: auto;
}

.settings-card {
  padding: 16px;
}

.tab-strip {
  display: flex;
  flex-wrap: wrap;
  gap: 0;
  margin-bottom: 10px;
  border-bottom: 1px solid rgba(255, 255, 255, 0.08);
}

.tab-strip button {
  width: auto;
  min-width: 128px;
  border-radius: 10px 10px 0 0;
  border: 1px solid rgba(255, 255, 255, 0.08);
  border-bottom: 0;
  background: var(--surface-1);
  color: var(--text-soft);
}

.tab-strip button.active {
  color: var(--accent-1);
  background: color-mix(in srgb, var(--accent-2) 42%, transparent);
}

/* .setting-row duzeni base.css'te (paylasilan sinif, paylasilan tanim). */

.checkbox-row input {
  width: 18px;
  height: 18px;
}

input[readonly] {
  color: var(--text-muted);
}

.quota-summary {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: 10px;
}

.quota-summary .summary-item {
  display: grid;
  gap: 4px;
  padding: 10px 12px;
  border: 1px solid rgba(255, 255, 255, 0.06);
  border-radius: var(--radius-md);
  background: rgba(22, 29, 41, 0.5);
}

.quota-summary .summary-item span {
  font-size: 0.68rem;
  color: var(--text-muted);
  text-transform: uppercase;
  letter-spacing: 0.06em;
}

@media (max-width: 760px) {
  .quota-summary {
    grid-template-columns: 1fr;
  }
}

.stats-summary {
  grid-template-columns: repeat(4, minmax(0, 1fr));
}

.compact-select {
  width: auto;
  min-width: 130px;
}

.stats-columns {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 16px;
}

.stats-columns h5,
.settings-card h5 {
  margin: 0 0 8px;
  color: var(--text-soft);
  font-size: 0.8rem;
}

.stat-bar {
  display: grid;
  grid-template-columns: minmax(0, 1fr) auto;
  gap: 8px;
  align-items: center;
  padding: 5px 0;
  border-top: 1px solid rgba(255, 255, 255, 0.05);
}

.stat-bar:first-of-type {
  border-top: 0;
}

.stat-label {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  color: var(--text-soft);
  font-size: 0.78rem;
}

.stat-count {
  color: var(--accent-1);
  font-variant-numeric: tabular-nums;
  font-weight: 700;
}

.stat-track {
  grid-column: 1 / 2;
  height: 7px;
  border-radius: 4px;
  background: rgba(255, 255, 255, 0.06);
  overflow: hidden;
}

.stat-fill {
  display: block;
  height: 100%;
  border-radius: 4px;
  background: linear-gradient(90deg, var(--accent-1), color-mix(in srgb, var(--accent-1) 45%, transparent));
}

.stat-bar:has(.stat-track) {
  grid-template-columns: 96px minmax(0, 1fr) auto;
}

@media (max-width: 760px) {
  .stats-summary,
  .stats-columns {
    grid-template-columns: 1fr;
  }
}

.composer-table {
  display: grid;
  overflow: hidden;
  border: 1px solid rgba(255, 255, 255, 0.06);
  border-radius: var(--radius-md);
}

.composer-row {
  display: grid;
  grid-template-columns: 150px minmax(0, 1fr) 140px;
  gap: 12px;
  align-items: center;
  padding: 11px 12px;
  border-top: 1px solid rgba(255, 255, 255, 0.06);
}

.composer-row:first-child {
  border-top: 0;
}

.composer-row.head {
  color: var(--text-muted);
  font-size: 0.72rem;
  font-weight: 900;
  letter-spacing: 0.04em;
  text-transform: uppercase;
}

.composer-output {
  max-height: 320px;
  overflow: auto;
  margin: 0;
  padding: 12px;
  border-radius: var(--radius-sm);
  background: #0a0e16;
  color: var(--accent-text);
  white-space: pre-wrap;
}

.help-list {
  display: grid;
  gap: 7px;
  margin: 0;
  padding-left: 18px;
}

.empty-state {
  margin: 0;
  padding: 18px;
  border: 1px dashed rgba(255, 255, 255, 0.12);
  border-radius: var(--radius-md);
  color: var(--text-muted);
}

@media (max-width: 1080px) {
  .hosting-shell,
  .setting-row,
  .composer-row {
    grid-template-columns: 1fr;
  }

  .hosting-nav {
    position: static;
  }
}

@media (max-width: 760px) {
  .section-head,
  .settings-card-head,
  .action-row {
    flex-direction: column;
    align-items: flex-start;
  }
}
</style>
