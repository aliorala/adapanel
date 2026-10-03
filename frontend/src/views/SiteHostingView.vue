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
          <button class="secondary compact" :disabled="!siteName || busy" @click="load()">{{ t("panel.hosting.refresh") }}</button>
        </div>

        <p v-if="notice" :class="['notice', 'page-notice', { error: noticeError }]" :role="noticeError ? 'alert' : 'status'">{{ notice }}</p>
        <p v-if="!siteName" class="empty-state">{{ t("panel.hosting.selectSiteHint") }}</p>

        <!-- Veri gelmeden ayar anahtarlarını göstermiyoruz: kapalı görünen bir anahtar,
             "kapalı" demektir — oysa henüz okunmamıştır. -->
        <p v-else-if="!settingsLoaded" class="empty-state" role="status" aria-live="polite">
          {{ t(busy ? "panel.common.loading" : "panel.common.loadFailed") }}
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
              <!-- Kaydetme eylemi ile yardımcı araçlar AYRI kümelerde: ikisi de düğme ama biri
                   yukarıdaki alanı işler, üçü ise alanı/dosyaları hazırlar. Ayrı kutu, aradaki
                   ayracın CSS numarası olmadan gerçek bir yapıya dayanmasını sağlar. -->
              <div class="setting-row doc-root-actions">
                <button class="compact" v-bind="writeGate(busy || !documentRoot.trim())" @click="saveDocumentRoot">
                  {{ busy ? t("panel.hosting.saving") : t("panel.hosting.saveDocumentRoot") }}
                </button>
                <div class="doc-root-tools">
                  <button type="button" class="secondary compact" v-bind="writeGate(busy)" @click="useLaravelPublic">
                    {{ t("panel.hosting.useLaravelPublic") }}
                  </button>
                  <button type="button" class="secondary compact" v-bind="writeGate(busy)" @click="enableRewrite">
                    {{ t("panel.hosting.enableRewrite") }}
                  </button>
                  <button type="button" class="secondary compact" v-bind="writeGate(busy)" @click="repairPermissions">
                    {{ t("panel.hosting.repairPermissions") }}
                  </button>
                </div>
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
              <button v-bind="writeGate(busy)" @click="save">{{ t("panel.hosting.save") }}</button>
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
              <button v-bind="writeGate(busy)" @click="save">{{ t("panel.hosting.save") }}</button>
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
              <button v-bind="writeGate(busy)" @click="save">{{ t("panel.hosting.save") }}</button>
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
              <button v-bind="writeGate(busy)" @click="save">{{ t("panel.hosting.save") }}</button>
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
              <button v-bind="writeGate(busy)" @click="save">{{ t("panel.hosting.save") }}</button>
            </div>
          </section>

          <section v-else-if="activeSection === 'errorPages'" class="settings-card">
            <div class="settings-card-head">
              <div>
                <h4>{{ t("panel.hosting.errorPages") }}</h4>
                <p class="meta">{{ t("panel.hosting.errorPagesHint") }}</p>
              </div>
            </div>

            <!-- ÇIKMAZ SOKAK DEĞİL: ayrıntılı hatalar açıkken bu sayfalar HİÇ görünmez; kullanıcı
                 "kaydettim ama çalışmıyor" demeden önce sebebini ve nereye gideceğini burada görür. -->
            <p v-if="form.detailedErrorsEnabled" class="notice error" role="alert">
              {{ t("panel.hosting.errorPagesDetailedWarning") }}
            </p>

            <div class="settings-rows">
              <label class="setting-row align-start">
                <span>{{ t("panel.hosting.errorPages") }}</span>
                <textarea v-model="errorPagesText" rows="6" :placeholder="t('panel.hosting.errorPagesPlaceholder')"></textarea>
                <small>{{ t("panel.hosting.errorPagesHelp") }}</small>
              </label>
              <p class="meta">{{ t("panel.hosting.errorPagesFileHint") }}</p>
            </div>

            <div class="action-row">
              <button v-bind="writeGate(busy)" @click="save">{{ t("panel.hosting.save") }}</button>
            </div>
          </section>

          <section v-else-if="activeSection === 'mime'" class="settings-card">
            <div class="settings-card-head">
              <div>
                <h4>{{ t("panel.hosting.mime") }}</h4>
                <p class="meta">{{ t("panel.hosting.mimeHint") }}</p>
              </div>
            </div>

            <div class="settings-rows">
              <label class="setting-row align-start">
                <span>{{ t("panel.hosting.mime") }}</span>
                <textarea v-model="mimeTypesText" rows="6" :placeholder="t('panel.hosting.mimePlaceholder')"></textarea>
                <small>{{ t("panel.hosting.mimeHelp") }}</small>
              </label>
              <p class="meta">{{ t("panel.hosting.mimeWhy") }}</p>
            </div>

            <div class="action-row">
              <button v-bind="writeGate(busy)" @click="save">{{ t("panel.hosting.save") }}</button>
            </div>
          </section>

          <section v-else-if="activeSection === 'hsts'" class="settings-card">
            <div class="settings-card-head">
              <div>
                <h4>{{ t("panel.hosting.hsts") }}</h4>
                <p class="meta">{{ t("panel.hosting.hstsHint") }}</p>
              </div>
            </div>

            <!-- Geri alınması zor bir karar: uyarı açmadan ÖNCE görünür olmalı. -->
            <p class="notice error" role="alert">{{ t("panel.hosting.hstsWarning") }}</p>

            <div class="settings-rows">
              <label class="setting-row checkbox-row">
                <span>{{ t("panel.hosting.hstsEnabled") }}</span>
                <input v-model="form.hstsEnabled" type="checkbox" />
                <small>{{ t("panel.hosting.hstsEnabledHelp") }}</small>
              </label>
              <label class="setting-row">
                <span>{{ t("panel.hosting.hstsMaxAge") }}</span>
                <input v-model.number="hstsMaxAgeDays" type="number" min="0" max="730" :disabled="!form.hstsEnabled" />
                <small>{{ t("panel.hosting.hstsMaxAgeHelp") }}</small>
              </label>
              <label class="setting-row checkbox-row">
                <span>{{ t("panel.hosting.hstsIncludeSubDomains") }}</span>
                <input v-model="form.hstsIncludeSubDomains" type="checkbox" :disabled="!form.hstsEnabled" />
                <small>{{ t("panel.hosting.hstsIncludeSubDomainsHelp") }}</small>
              </label>
              <label class="setting-row checkbox-row">
                <span>{{ t("panel.hosting.hstsPreload") }}</span>
                <input v-model="form.hstsPreload" type="checkbox" :disabled="!form.hstsEnabled" />
                <small>{{ t("panel.hosting.hstsPreloadHelp") }}</small>
              </label>
            </div>

            <div class="action-row">
              <button v-bind="writeGate(busy)" @click="save">{{ t("panel.hosting.save") }}</button>
            </div>
          </section>

          <section v-else-if="activeSection === 'quota'" class="settings-card">
            <div class="settings-card-head">
              <div>
                <h4>{{ t("panel.hosting.quota") }}</h4>
                <p class="meta">{{ t("panel.hosting.quotaHint") }}</p>
              </div>
              <button class="secondary compact" v-bind="writeGate(busy || !quota?.quotaEnabled)" @click="removeSiteQuota">
                {{ t("panel.hosting.quotaRemove") }}
              </button>
            </div>

            <!-- ÇIKMAZ SOKAK DEĞİL: rol eksikse kurma düğmesi de burada. -->
            <div v-if="quota && !quota.fsrmAvailable" class="notice error fsrm-missing" role="alert">
              <span>{{ t("panel.hosting.quotaUnavailable") }}</span>
              <button class="secondary compact" v-bind="writeGate(busy)" @click="installFsrmRole">
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
                <strong>{{ formatQuotaBytes(quota?.usedBytes ?? 0) }}</strong>
              </div>
              <div class="summary-item">
                <span>{{ t("panel.hosting.quotaLimit") }}</span>
                <strong>{{ quota?.quotaEnabled ? formatQuotaBytes(quota?.limitBytes ?? 0) : t("panel.security.unlimited") }}</strong>
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
              <button v-bind="writeGate(busy)" @click="saveSiteQuota">{{ t("panel.hosting.save") }}</button>
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
                  <strong>{{ stats.totalRequests.toLocaleString(getFormattingLocale()) }}</strong>
                </div>
                <div class="summary-item">
                  <span>{{ t("panel.hosting.statsVisitors") }}</span>
                  <strong>{{ stats.uniqueVisitors.toLocaleString(getFormattingLocale()) }}{{ stats.uniqueVisitorsApproximate ? "+" : "" }}</strong>
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
                    <span class="stat-count">{{ page.count.toLocaleString(getFormattingLocale()) }}</span>
                  </div>
                </div>
                <div>
                  <h5>{{ t("panel.hosting.statsTopReferrers") }}</h5>
                  <p v-if="stats.topReferrers.length === 0" class="meta">{{ t("panel.hosting.statsNoReferrers") }}</p>
                  <div v-for="ref in stats.topReferrers" :key="ref.key" class="stat-bar">
                    <span class="stat-label" :title="ref.key">{{ ref.key }}</span>
                    <span class="stat-count">{{ ref.count.toLocaleString(getFormattingLocale()) }}</span>
                  </div>
                </div>
              </div>

              <div>
                <h5>{{ t("panel.hosting.statsDaily") }}</h5>
                <div v-for="day in stats.dailyHits" :key="day.key" class="stat-bar">
                  <span class="stat-label">{{ day.key }}</span>
                  <span class="stat-track"><span class="stat-fill" :style="{ width: dailyWidth(day.count) }"></span></span>
                  <span class="stat-count">{{ day.count.toLocaleString(getFormattingLocale()) }}</span>
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
              <button class="compact" v-bind="writeGate(busy || !git?.configured)" @click="deploySite">
                {{ t("panel.hosting.gitDeploy") }}
              </button>
            </div>

            <SectionLoadError v-if="gitLoad.message.value" :message="gitLoad.message.value" :busy="busy" @retry="loadGit" />

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
              <button v-bind="writeGate(busy)" @click="saveGit()">{{ t("panel.hosting.save") }}</button>
              <button class="secondary compact" v-bind="writeGate(busy || !git?.configured)" @click="regenerateSecret">
                {{ t("panel.hosting.gitRegenerate") }}
              </button>
            </div>

            <div v-if="git?.configured" class="settings-rows">
              <label class="setting-row">
                <span>{{ t("panel.hosting.gitLastDeploy") }}</span>
                <input :value="git.lastDeployUtc ? `${new Date(git.lastDeployUtc).toLocaleString(getFormattingLocale())} (${git.lastCommit})` : '-'" readonly />
                <small>{{ git.lastDeployStatus || "-" }}</small>
              </label>
              <label class="setting-row align-start">
                <span>{{ t("panel.hosting.gitWebhook") }}</span>
                <textarea :value="webhookUrl" rows="2" readonly></textarea>
                <small>{{ t("panel.hosting.gitWebhookHelp") }}</small>
              </label>
              <label class="setting-row align-start">
                <span>{{ t("panel.hosting.gitWebhookHeader") }}</span>
                <textarea :value="webhookHeaderLine" rows="2" readonly></textarea>
                <small>{{ t("panel.hosting.gitWebhookHeaderHelp") }}</small>
              </label>
              <div class="setting-row align-start">
                <span>{{ t("panel.hosting.gitWebhookLegacy") }}</span>
                <div>
                  <button type="button" class="secondary compact" @click="showLegacyWebhook = !showLegacyWebhook">
                    {{ showLegacyWebhook ? t("panel.hosting.gitWebhookLegacyHide") : t("panel.hosting.gitWebhookLegacyShow") }}
                  </button>
                  <textarea v-if="showLegacyWebhook" :value="legacyWebhookUrl" rows="2" readonly></textarea>
                </div>
                <small>{{ t("panel.hosting.gitWebhookLegacyHelp") }}</small>
              </div>
            </div>

            <pre v-if="gitOutput" class="composer-output">{{ gitOutput }}</pre>
          </section>

          <!-- .htaccess: site basina BARINDIRMA ayaridir (PHP surumu ve varsayilan belgelerle
               ayni yerde aranir), bu yuzden ayri bir arac sekmesi degil bu menunun bir bolumu.
               Icerigi kendi gorunumunde: bu dosya zaten 1270 satir. -->
          <section v-else-if="activeSection === 'htaccess'" class="settings-card">
            <SiteHtaccessView />
          </section>

          <section v-else class="settings-card">
            <div class="settings-card-head">
              <div>
                <h4>{{ t("panel.hosting.tools") }}</h4>
                <p class="meta">{{ t("panel.hosting.toolsHint") }}</p>
              </div>
              <button class="secondary compact" v-bind="writeGate(busy)" @click="runComposer">{{ t("panel.hosting.composer") }}</button>
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
import { getFormattingLocale } from '../utils/locale';
import { computed, onBeforeUnmount, reactive, ref, watch } from "vue";
import { useI18n } from "vue-i18n";
import SiteAliases from "../components/SiteAliases.vue";
// .htaccess bolumu AYRI bir gorunumde: gerekcesi o dosyanin script blogunda yazili.
import SiteHtaccessView from "./SiteHtaccessView.vue";
import SectionLoadError from "../components/SectionLoadError.vue";
import { useSectionError } from "../composables/useNotice";
import {
  getSiteHostingSettings,
  runComposerInstall,
  updateSiteHostingSettings,
  type SiteErrorPage,
  type SiteMimeType
} from "../api/siteHosting";
import { getQuota, removeQuota, setQuota, type SiteDiskQuota, installFsrm } from "../api/quota";
import { configureGitDeployment, deployGit, getGitDeployment, type GitDeployment } from "../api/git";
import { getSiteStatistics, type WebStatistics } from "../api/statistics";
import { extractApiDetails, extractApiMessage } from "../stores/auth";
import { useSitesStore } from "../stores/sites";
import { updateDocumentRoot, enableFrameworkRewrite, repairSitePermissions } from "../api/sites";
import { useSubscriptionGate } from "../composables/useSubscriptionGate";
import { formatBytes as sharedFormatBytes } from "../utils/bytes";
import { getSessionEpoch } from "../api/client";

const { t } = useI18n();
// Askidaki abonelikte YAZMA dugmeleri bastan devre disi + sebebi title/aria ile soylenir
// (bkz. composables/useSubscriptionGate.ts). Okuma dugmeleri kapiya girmez.
const { writeGate } = useSubscriptionGate();
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
let workspaceVersion = 0;
let disposed = false;
onBeforeUnmount(() => { disposed = true; workspaceVersion++; });

function hostingContext() {
  const site = siteName.value, version = workspaceVersion, epoch = getSessionEpoch();
  return { site, current: () => !disposed && siteName.value === site && workspaceVersion === version && getSessionEpoch() === epoch };
}

async function mutate<T>(action: (context: ReturnType<typeof hostingContext>) => Promise<T>, apply: (result: T) => void, installing = false) {
  if (!siteName.value || disposed || busy.value || !settingsLoaded.value) return;
  const context = hostingContext();
  busy.value = true;
  if (installing) { installingFsrm.value = true; quotaError.value = ""; }
  try {
    const result = await action(context);
    if (context.current()) apply(result);
  } catch (error) {
    if (!context.current()) return;
    if (installing) quotaError.value = resolveApiMessage(error);
    else showNotice(resolveApiMessage(error), true);
  } finally {
    if (context.current()) { busy.value = false; if (installing) installingFsrm.value = false; }
  }
}
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
// WEBHOOK ADRESI ARTIK JETON TASIMIYOR.
//
// Eski hali `...?token=<gizli>` uretiyordu ve o adres saglayiciya yapistirildiginda jeton HER
// tetiklemede IIS/W3C erisim gunlugune yaziliyordu -- panelin kendi gunluk goruntuleyicisi
// oldugu icin dagitim anahtari gunlugu okuyabilen herkese gorunur haldeydi.
const webhookUrl = computed(() => {
  if (!git.value?.configured) return "";
  return `${window.location.origin}${git.value.webhookPath}`;
});

// Jetonun konacagi baslik satiri, saglayicinin "secret/header" alanina yapistirilmak uzere.
const webhookHeaderLine = computed(() => {
  if (!git.value?.configured) return "";
  return `${git.value.webhookHeaderName}: ${git.value.webhookSecret}`;
});

// ESKI BICIM GIZLENMIYOR, UYARIYLA GOSTERILIYOR.
//
// Kullanicilarin saglayicida DURAN adresleri bu bicimde ve sunucu onlari kabul etmeye DEVAM
// ediyor (geriye donuk uyum). Bicimi ekrandan tamamen kaldirmak, calisan bir kancayi ariza
// saniip yeniden kurmaya ya da -- daha kotusu -- gunluge sizdigini hic ogrenmemeye yol acardi.
const legacyWebhookUrl = computed(() => {
  if (!git.value?.configured) return "";
  return `${window.location.origin}${git.value.webhookPath}?token=${git.value.webhookSecret}`;
});
const showLegacyWebhook = ref(false);
const form = reactive({
  maximumBandwidthBytesPerSecond: 0,
  maximumConnections: 0,
  redirectUrl: "",
  directoryBrowsingEnabled: false,
  detailedErrorsEnabled: false,
  staticCompressionEnabled: true,
  dynamicCompressionEnabled: false,
  maximumUploadBytes: 31_457_280,
  hotlinkProtectionEnabled: false,
  hstsEnabled: false,
  hstsMaxAgeSeconds: 31_536_000,
  hstsIncludeSubDomains: false,
  hstsPreload: false
});
const maximumUploadMegabytes = computed({
  get: () => Math.max(1, Math.round(form.maximumUploadBytes / 1_048_576)),
  set: (value: number) => {
    form.maximumUploadBytes = Math.max(1, value || 1) * 1_048_576;
  }
});
// Kullanıcı gün düşünür, protokol saniye konuşur. 0 GEÇERLİ bir değerdir: HSTS'i geri almanın
// tek yolu max-age=0 göndermektir (başlığı silmek tarayıcının hafızasını temizlemez).
const hstsMaxAgeDays = computed({
  get: () => Math.round(form.hstsMaxAgeSeconds / 86_400),
  set: (value: number) => {
    form.hstsMaxAgeSeconds = Math.max(0, value || 0) * 86_400;
  }
});
// Satır tabanlı düzenleme: "404 /hatalar/404.html" ve ".woff2 font/woff2". Girdi biçimi burada
// yalnız AYRIŞTIRILIR; geçerlilik kararını sunucu verir (tek doğruluk kaynağı orası).
const errorPagesText = ref("");
const mimeTypesText = ref("");

function parseErrorPages(): SiteErrorPage[] {
  return splitLines(errorPagesText.value).map((line) => {
    const [code, ...rest] = line.split(/\s+/);
    return { statusCode: Number(code) || 0, path: rest.join(" ") };
  });
}

function parseMimeTypes(): SiteMimeType[] {
  return splitLines(mimeTypesText.value).map((line) => {
    const [extension, ...rest] = line.split(/\s+/);
    return { extension, mimeType: rest.join(" ") };
  });
}

const sections = computed(() => [
  { id: "general", label: t("panel.hosting.general"), hint: t("panel.hosting.generalHint") },
  { id: "documents", label: t("panel.hosting.defaultDocuments"), hint: t("panel.hosting.defaultDocumentsHint") },
  { id: "htaccess", label: t("panel.htaccess.title"), hint: t("panel.htaccess.navHint") },
  { id: "traffic", label: t("panel.hosting.traffic"), hint: t("panel.hosting.trafficHint") },
  { id: "access", label: t("panel.hosting.access"), hint: t("panel.hosting.accessHint") },
  { id: "errors", label: t("panel.hosting.errorsPerformance"), hint: t("panel.hosting.errorsPerformanceHint") },
  { id: "errorPages", label: t("panel.hosting.errorPages"), hint: t("panel.hosting.errorPagesHint") },
  { id: "mime", label: t("panel.hosting.mime"), hint: t("panel.hosting.mimeHint") },
  { id: "hsts", label: t("panel.hosting.hsts"), hint: t("panel.hosting.hstsHint") },
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

watch(activeSection, (section) => {
  if (section === "stats" && !stats.value) {
    void loadStats();
  }
});

async function load(siteChanged = false) {
  if (disposed || (!siteChanged && busy.value)) return;
  workspaceVersion++;
  settingsLoaded.value = false;
  notice.value = "";
  noticeError.value = false;
  if (siteChanged) {
    quota.value = null;
    quotaError.value = "";
    Object.assign(quotaForm, { limitMegabytes: 1024, softLimit: false });
    git.value = null;
    gitLoad.clear();
    Object.assign(gitForm, { repositoryUrl: "", branch: "main" });
    showLegacyWebhook.value = false;
    composerOutput.value = "";
    gitOutput.value = "";
    stats.value = null;
    installingFsrm.value = false;
    busy.value = false;
  }
  if (!siteName.value) return;
  const context = hostingContext();
  busy.value = true;
  try {
    const settings = await getSiteHostingSettings(context.site);
    if (!context.current()) return;
    form.maximumBandwidthBytesPerSecond = settings.maximumBandwidthBytesPerSecond;
    form.maximumConnections = settings.maximumConnections;
    form.redirectUrl = settings.redirectUrl;
    form.directoryBrowsingEnabled = settings.directoryBrowsingEnabled;
    form.detailedErrorsEnabled = settings.detailedErrorsEnabled;
    form.staticCompressionEnabled = settings.staticCompressionEnabled;
    form.dynamicCompressionEnabled = settings.dynamicCompressionEnabled;
    form.maximumUploadBytes = settings.maximumUploadBytes;
    form.hotlinkProtectionEnabled = settings.hotlinkProtectionEnabled;
    form.hstsEnabled = settings.hstsEnabled;
    form.hstsMaxAgeSeconds = settings.hstsMaxAgeSeconds;
    form.hstsIncludeSubDomains = settings.hstsIncludeSubDomains;
    form.hstsPreload = settings.hstsPreload;
    errorPagesText.value = settings.customErrorPages.map((page) => `${page.statusCode} ${page.path}`).join("\n");
    mimeTypesText.value = settings.mimeTypes.map((mime) => `${mime.extension} ${mime.mimeType}`).join("\n");
    defaultDocumentsText.value = settings.defaultDocuments.join("\n");
    deniedIps.value = settings.deniedIpAddresses.join("\n");
    allowedReferrers.value = settings.allowedReferrerHosts.join("\n");
    protectedExtensions.value = settings.protectedExtensions.join(", ");
    await loadQuota(context);
    if (!context.current()) return;
    await loadGitFor(context);
    if (!context.current()) return;
    settingsLoaded.value = true;
  } catch (error) {
    if (context.current()) showNotice(resolveApiMessage(error), true);
  } finally {
    if (context.current()) busy.value = false;
  }
}

async function save() {
  await mutate(context => updateSiteHostingSettings(context.site, {
      ...form,
      defaultDocuments: splitLines(defaultDocumentsText.value),
      deniedIpAddresses: splitLines(deniedIps.value),
      allowedReferrerHosts: splitLines(allowedReferrers.value),
      protectedExtensions: protectedExtensions.value.split(",").map(item => item.trim()).filter(Boolean),
      customErrorPages: parseErrorPages(),
      mimeTypes: parseMimeTypes()
    }), saved => {
    // Sunucu normalleştirilmiş hâli döner ("woff2" -> ".woff2", sıralı liste). Ekran onunla
    // tazelenir ki kullanıcı SUNUCUDA DURANI görsün, yazdığını değil.
    errorPagesText.value = saved.customErrorPages.map((page) => `${page.statusCode} ${page.path}`).join("\n");
    mimeTypesText.value = saved.mimeTypes.map((mime) => `${mime.extension} ${mime.mimeType}`).join("\n");
    // SADE YESIL "kaydedildi" ARTIK KOSULLU. Yazma bozuksa ya da IIS reddettiyse istek zaten
    // hata dondurur (onceki icerik geri konur). Buraya duseni de OLCUME gore anlatiriz:
    // IIS'e sorulamadiysa "kaydedildi" demeyiz.
    if (saved.webConfigWrite && !saved.webConfigWrite.iisVerified) {
      showNotice(t("panel.hosting.savedIisUnverified"), true);
    } else {
      showNotice(t("panel.hosting.saved"), false);
    }
  });
}

// #1 ⭐ Belge kökü / web root kaydet (Laravel/Symfony için ...\public). IIS'te kalıcı; panel revert etmez.
async function saveDocumentRoot() {
  const path = documentRoot.value.trim();
  if (!path) return;
  await mutate(async context => {
    await updateDocumentRoot(context.site, path);
    if (!context.current()) return;
    await sitesStore.fetchSites();
  }, () => {
    showNotice(t("panel.hosting.documentRootSaved"), false);
  });
}

// Mevcut yolun altına \public ekle (Laravel/Symfony belge kökü). Kullanıcı kaydetmeden önce görür/düzenler.
function useLaravelPublic() {
  const base = (selectedSite.value?.physicalPath ?? "").replace(/[\\/]+$/, "");
  documentRoot.value = base.length > 0 ? `${base}\\public` : "public";
}

// #2 — pretty-URL (index.php front controller) yönlendirmesini etkinleştir. URL Rewrite modülü yoksa net hata.
async function enableRewrite() {
  await mutate(context => enableFrameworkRewrite(context.site), () => {
    showNotice(t("panel.hosting.rewriteEnabled"), false);
  });
}

// #5 — mevcut siteye yazma iznini yeniden uygula.
//
// SONUC SORULUR, VARSAYILMAZ. Eskiden burada kosulsuz "Yazma izinleri onarildi" yaziliyordu:
// ucun basarisiz DONMESI imkansizdi (servis tarafi en iyi cabaydi), yani kullanici izin hic
// degismese bile HER ZAMAN yesil bir bildirim goruyordu. Panelin sitesine yazamayan musteri,
// 500 hatasinin sebebini bu yuzden baska yerde ariyordu.
//
// Uc deger AYRI gosterilir. "Dogrulanamadi"yi "basarisiz" ile birlestirmek, okunamayan bir
// DACL yuzunden calisan bir sunucuyu bozuk gostermek olurdu; ikisini "tamam" ile birlestirmek
// ise duzeltilen yalanin ta kendisi.
async function repairPermissions() {
  await mutate(context => repairSitePermissions(context.site), result => {
    if (result.state === "granted") {
      showNotice(t("panel.hosting.permissionsRepaired"), false);
    } else if (result.state === "not_granted") {
      showNotice(`${t("panel.hosting.permissionsNotRepaired")} ${result.detail}`.trim(), true);
    } else {
      showNotice(`${t("panel.hosting.permissionsUnverified")} ${result.detail}`.trim(), true);
    }
  });
}

async function runComposer() {
  await mutate(context => runComposerInstall(context.site), result => {
    composerOutput.value = [result.output, result.error].filter(Boolean).join("\n");
    showNotice(result.exitCode === 0 ? t("panel.hosting.composerDone") : t("panel.hosting.composerFailed"), result.exitCode !== 0);
  });
}

/*
 * ÖLÜ KOD SİLİNDİ — serbest Composer/Artisan komut kutusu.
 * `runComposerCmd`, `runArtisanCmd` ve bunların tuttuğu `composerCommand` / `artisanCommand` /
 * `artisanOutput` durumları ŞABLONDA HİÇ GEÇMİYORDU: o araç kutusu `AppToolkitView`e taşındı
 * (orada `runComposerCommand` / `runArtisanCommand` gerçekten çağrılıyor). Burada kalan hâli
 * hiçbir düğmeye bağlı değildi, yani hiç çalışmıyordu ama iki API çağrısını, iki i18n anahtarını
 * ve okuyanın kafasını meşgul ediyordu. `runComposer` (tek tuşla "composer install") DURUYOR:
 * o şablondaki düğmeye bağlı ve `composerOutput` onun çıktısını gösteriyor.
 */
const quotaError = ref("");
const installingFsrm = ref(false);

async function installFsrmRole() {
  await mutate(context => installFsrm(context.site), result => {
    quota.value = result;
    showNotice(t("panel.hosting.quotaInstalled"), false);
  }, true);
}

async function loadQuota(context: ReturnType<typeof hostingContext>) {
  quotaError.value = "";
  try {
    const result = await getQuota(context.site);
    if (!context.current()) return;
    quota.value = result;
    if (quota.value.quotaEnabled) {
      quotaForm.limitMegabytes = Math.max(1, Math.round(quota.value.limitBytes / 1_048_576));
      quotaForm.softLimit = quota.value.softLimit;
    } else {
      Object.assign(quotaForm, { limitMegabytes: 1024, softLimit: false });
    }
  } catch (error) {
    if (!context.current()) return;
    // ÖLÇEMEDİK ≠ KAPALI. Önceden yalnız quota=null yapılıyordu ve kart "Durum: Kapalı,
    // Kullanılan: 0 B, Sınır: Sınırsız" gösteriyordu — bunlar ölçüm değil, null'ın varsayılanı.
    // Kullanıcı kotanın gerçekten kapalı olduğuna inanıyordu.
    quota.value = null;
    quotaError.value = resolveApiMessage(error);
  }
}

async function saveSiteQuota() {
  await mutate(context => setQuota(context.site, quotaForm.limitMegabytes, quotaForm.softLimit), result => {
    quota.value = result;
    quotaError.value = "";
    showNotice(t("panel.hosting.quotaSaved"), false);
  });
}

async function removeSiteQuota() {
  await mutate(context => removeQuota(context.site), result => {
    quota.value = result;
    quotaError.value = "";
    showNotice(t("panel.hosting.quotaRemoved"), false);
  });
}

/** Bayt bicimi panelin geri kalaniyla AYNI (bkz. utils/bytes.ts); ekran kendi bicimini uydurmaz. */
function formatBytes(value: number) {
  return sharedFormatBytes(value, t);
}

/**
 * KOTA rakamlari ayri hassasiyette (bkz. utils/bytes.ts, "gauge"): burada okunan sey buyukluk
 * degil SINIRA YAKINLIK. Disk kotasi tipik olarak 10-100 GiB araligindadir ve ortak kuralin
 * ondalik atmasi tam o araligi kaba yapar ("16 GiB / 20 GiB"). SubscriptionCard'daki trafik
 * kotasiyla AYNI davranmasi bilinctir: bir kota ondalikli, digeri ondaliksiz gorunemez.
 * Istatistik/bant genisligi rakamlari bir kota DEGIL, bu yuzden ustteki `formatBytes`ta kalir.
 */
function formatQuotaBytes(value: number) {
  return sharedFormatBytes(value, t, "gauge");
}

async function loadGit() {
  await mutate(context => loadGitFor(context), () => {});
}

async function loadGitFor(context: ReturnType<typeof hostingContext>) {
  try {
    const result = await getGitDeployment(context.site);
    if (!context.current()) return;
    git.value = result;
    if (git.value.configured) {
      gitForm.repositoryUrl = git.value.repositoryUrl;
      gitForm.branch = git.value.branch;
    } else {
      Object.assign(gitForm, { repositoryUrl: "", branch: "main" });
    }
    gitLoad.clear();
  } catch (error) {
    if (!context.current()) return;
    // Hata YUTULMUYOR: boş form "git kurulu değil" sanılıyordu; kullanıcı var olan
    // dağıtımı yeniden kurmaya kalkabilirdi.
    git.value = null;
    gitLoad.capture(error);
  }
}

async function saveGit(regenerate = false) {
  await mutate(context => configureGitDeployment(context.site, gitForm.repositoryUrl, gitForm.branch, regenerate), result => {
    git.value = result;
    showNotice(t("panel.hosting.gitSaved"), false);
  });
}

async function regenerateSecret() {
  await saveGit(true);
}

async function deploySite() {
  await mutate(context => deployGit(context.site), result => {
    git.value = result.deployment;
    gitOutput.value = result.output;
    showNotice(result.success ? t("panel.hosting.gitDeployed") : t("panel.hosting.gitDeployFailed"), !result.success);
  });
}

async function loadStats() {
  await mutate(context => getSiteStatistics(context.site, statsDays.value), result => { stats.value = result; });
}

// Başlangıç yüklemesi, kota/Git durumları da oluşturulduktan sonra başlatılır.
watch(siteName, () => { void load(true); }, { immediate: true, flush: "sync" });

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
  margin-top: var(--sp-12);
  padding-top: var(--sp-12);
  border-top: 1px solid var(--panel-border);
}

.fsrm-missing {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--sp-10);
  flex-wrap: wrap;
}

/* `.settings-rows` BU LİSTEDEN ÇIKARILDI: ortak tanımı base.css'te ve BİREBİR aynı üç
   bildirimi veriyor (grid / --sp-12 / min-width: 0). Kopya, aynı adı kullanan komşu
   bileşenlerin (SubscriptionCard) neden biçimsiz kaldığının da sebebiydi. Görünen çıktı
   DEĞİŞMEZ. */
.hosting-workspace,
.hosting-main,
.settings-card {
  display: grid;
  gap: var(--sp-12);
  min-width: 0;
}

.hosting-shell {
  display: grid;
  grid-template-columns: minmax(210px, 0.24fr) minmax(0, 1fr);
  gap: var(--sp-12);
  align-items: start;
}

/* KARTLARIN ZEMİNİ `--surface-2` DEĞİL `--surface-0` — GEREKÇE VARSAYILAN TEMA.
   ÖLÇÜLDÜ, açık tema (main.ts'e göre VARSAYILAN): kart `--surface-2` = #f5f7fa, sayfa zemini
   `--page-bg` = #eef1f6, aradaki fark 1.05:1. Yani bu ekranın bütün kartları beyaz sayfanın
   üstünde pratikte hiç yükselmiyordu; kartı ayakta tutan tek şey gölgesiydi. `--surface-0`
   ile fark 1.13:1'e çıkar ve kart panelin her yerindeki kartla AYNI beyaz olur — burası,
   base.css'in `.panel` kararının dışında kalan tek ekrandı.
   DÜRÜST OLMAK GEREKİRSE koyu temada yön TERS: orada surface-2 farkı 1.24, surface-0 farkı
   1.09'dur, yani koyu temada kart bir tık daha az yükselir. Yine de surface-0 seçildi: kartın
   sınırını taşıyan asıl şey `--panel-border` + `--panel-shadow` ve koyu temada ikisi de güçlü;
   buna karşılık açık temadaki kayıp, VARSAYILAN temada her kullanıcının gördüğü kayıptı.
   Ayrıca eski değer iki durağı BİREBİR aynı olan bir `linear-gradient` idi: gradyan değil düz
   bir dolgu, yani okuyanı yanıltan bir yazım. */
.hosting-nav,
.settings-card {
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-lg);
  background: var(--surface-0);
  box-shadow: var(--panel-shadow);
}

.hosting-nav {
  position: sticky;
  top: var(--sp-12);
  display: grid;
  gap: var(--sp-6);
  padding: var(--sp-12);
}

.site-badge {
  display: grid;
  gap: var(--sp-4);
  padding: var(--sp-12);
  margin-bottom: var(--sp-4);
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
  font-size: var(--fs-2xs);
  font-weight: 850;
  letter-spacing: 0.08em;
  text-transform: uppercase;
}

.hosting-nav-item {
  display: grid;
  gap: var(--sp-4);
  width: 100%;
  padding: var(--sp-10) var(--sp-12);
  border-left: 3px solid transparent;
  background: transparent;
  color: var(--text-soft);
  text-align: left;
}

/* Seçili bölümün yazısı `--accent-text` ile: bu belirteç tam olarak "vurgu renginin ZEMİN
   ÜZERİNE yazılan hâli" için ölçülmüştür (base.css). ÖLÇÜLDÜ, açık tema: eski `--accent-1`
   (#1474e5) `--surface-3` (#eaf0f6) üzerinde 3.93:1 — AA eşiği 4.5, altında kalıyordu ve bu
   menü ögesi kalın değil normal ağırlıkta. Yeni `--accent-text` (#0f62c4) 5.13:1.
   Aynı kararı `.segmented-tabs button.active` de veriyor; iki yerde aynı olsun. */
.hosting-nav-item.active {
  border-left-color: var(--accent-1);
  background: var(--surface-3);
  color: var(--accent-text);
}

/* `.section-head` bu listeden ÇIKARILDI: artık base.css'te ortak tanımı var ve buradaki kopya
   onu eziyordu. Kopya `flex-wrap: wrap` içermiyordu, yani dar ekranda başlık ile yanındaki
   düğme sarmıyor, taşıyordu — ortak tanıma geçmek bunu da düzeltir. Kalan iki seçici bu
   ekrana özgü ve base'de karşılığı yok, o yüzden burada kalıyor. */
.settings-card-head,
.action-row {
  display: flex;
  gap: var(--sp-12);
  align-items: flex-start;
  justify-content: space-between;
}

.section-head button,
.settings-card-head button,
.action-row button {
  width: auto;
}

.settings-card {
  padding: var(--sp-16);
}

/*
  BÖLÜM BAŞLIKLARINA HİÇBİR KURAL ULAŞMIYORDU — TARAYICIDA ÖLÇÜLDÜ.
  base.css'in başlık kuralları `.panel h2/h3/h4` diye KAPILI. Bu ekranın kökü ise
  `<section class="hosting-workspace">`; hiçbir atasında `.panel` YOK. Yani on iki bölümün
  `<h4>` başlığı tarayıcı varsayılanıyla çiziliyordu: `margin-top: 17.955px` VE
  `margin-bottom: 17.955px` (ölçüldü). Toplam 35.9px, hiçbir kararın ürünü olmayan boşluk;
  üstelik alttaki, ızgaranın kendi 12px'ine EKLENİYORDU. Başlık kartın içinde yüzüyordu.
  BOYUT BİLEREK DEĞİŞMEDİ: `--fs-lg` de 1rem = 13.5px, yani `.panel h3` de gövde metniyle
  aynı boyutta. Panelin tipografisi boyutla değil AĞIRLIKLA ayrışıyor ve ölçek kararı
  base.css'e ait. Burada yapılan, o kararı bu ekrana da UYGULAMAK: `.panel h3` ile birebir
  aynı ağırlık, satır yüksekliği ve harf aralığı; kart başlığı panelin her yerinde aynı rütbede.
*/
.settings-card h4 {
  margin: 0;
  font-size: var(--fs-lg);
  font-weight: 640;
  line-height: 1.2;
  letter-spacing: -0.015em;
}

/*
  BAŞLIK ALTI İPUÇLARININ TARAYICI VARSAYILANI TEMİZLENDİ.
  base.css `.meta` yalnız rengi ve satır yüksekliğini verir; MARGIN'i sıfırlayan kural
  (`.module-head p`) bu ekranın `.settings-card-head`ini KAPSAMIYORDU. Sonuç ölçülebilir:
  her bölüm başlığının altındaki tek satırlık ipucu tarayıcının varsayılan 1em üst + 1em alt
  boşluğunu taşıyordu (13.5px taban -> 13.5px + 13.5px), üstelik alttaki boşluk ızgaranın
  kendi 12px'ine EKLENİYORDU. Yani her bölümün başı ile içeriği arasında 25.5px'lik,
  hiçbir kararın ürünü olmayan bir delik vardı — on iki bölümün hepsinde.
  Boşluk artık ızgaradan geliyor; başlık ile ipucu arasında ölçekten tek adım kalıyor.
*/
.hosting-main p {
  margin: 0;
}

.section-head p,
.settings-card-head p {
  margin-top: var(--sp-4);
}

/* BELGE KÖKÜ EYLEM SATIRI — burada dört düğme var ve düzeni BOZUKTU.
   `.setting-row` paylaşılan bir ÜÇ SÜTUNLU ızgaradır (190px | 0.52fr | minmax(220px,1fr)),
   çünkü işi "etiket | denetim | ipucu" dizmektir. Bu satırda ise etiket de ipucu da yok, dört
   düğme var: ilk üçü üç ayrı sütuna dağılıyor ve ızgara çocuğu varsayılan olarak GERİLDİĞİ
   için 190px / değişken / 220px+ genişliğinde üç farklı düğme çıkıyordu; dördüncüsü de alt
   satıra düşüp yine 190px'e geriliyordu. Yani aynı işlevdeki dört düğme dört farklı boyda.
   Esnek satıra alındı: düğmeler kendi metinleri kadar yer kaplar.
   HİYERARŞİ: satırdaki tek BİRİNCİL düğme "Web root'u kaydet" (alanı işleyen tek eylem);
   diğer üçü yardımcı araç ve ince bir ayraçla ondan ayrılır — böylece "kaydet" ile "yolu
   \public yap" aynı ağırlıkta okunmaz. */
.doc-root-actions {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--sp-8);
}

.doc-root-actions button {
  width: auto;
}

/* Ayraç: yardımcı araçlar kümesi, kaydetme eyleminden ince bir çizgiyle ayrılır. */
.doc-root-tools {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--sp-8);
  padding-inline-start: var(--sp-12);
  border-inline-start: 1px solid var(--panel-border);
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
  gap: var(--sp-10);
}

/*
  KOTA/İSTATİSTİK ÖZET KUTULARININ KENDİ GÖRÜNÜMÜ SİLİNDİ — base.css'teki `.summary-item`
  KULLANILIYOR.
  Buradaki eski kural iki KOYU TEMA sabitini koda gömüyordu: `rgba(22, 29, 41, 0.5)` bir zemin
  ve `rgba(255, 255, 255, 0.06)` bir kenar. AÇIK TEMA varsayılan olduğu için (main.ts)
  kullanıcıların çoğu şunu görüyordu: beyaz bir kartın içinde, beyazla birleşince #8b8e94'e
  düşen (beyaza karşı 3.28:1 ölçüldü) üç adet koyu gri-mavi kutu — panelde başka hiçbir yerde
  olmayan bir görünüm; kenarları da (beyaz üzerine %6 beyaz) hiç çizilmiyordu.
  Sabit renk tema değiştirmez; belirteç değiştirir. Kural tümüyle kaldırıldığında base.css'in
  `.summary-item` tanımı devreye giriyor (`--surface-0` zemin + `--panel-border` kenar +
  `--sp-12` dolgu) ve bu üç kutu panelin diğer bütün özet şeritleriyle AYNI görünüyor.
  Etiketin `uppercase`i de bilerek gitti: aynı bilgi panelin başka yerlerinde küçük harfle
  yazılıyor; iki farklı "özet etiketi" biçimi tutmanın bir gerekçesi yok.
*/

@media (max-width: 760px) {
  .quota-summary {
    grid-template-columns: 1fr;
  }
}

.stats-summary {
  grid-template-columns: repeat(4, minmax(0, 1fr));
}

/* `.compact-select` KOPYASI SİLİNDİ (ortak tanım base.css'te, `min-width: 140px`).
   GÖRÜNEN FARK: buradaki istatistik gün seçicisi 130px -> 140px, yani 10px genişler.
   Ortak değer bilerek büyük olan: sekiz çağrı yerinden biri "example.com" gibi bir alan adı
   taşıyor ve 130px onu kırpıyordu. Bu ekrandaki içerik ("Son 30 gün") zaten sığıyor. */

.stats-columns {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: var(--sp-16);
}

.stats-columns h5,
.settings-card h5 {
  margin: 0 0 var(--sp-8);
  color: var(--text-soft);
  font-size: var(--fs-xs);
}

.stat-bar {
  display: grid;
  grid-template-columns: minmax(0, 1fr) auto;
  gap: var(--sp-8);
  align-items: center;
  padding: var(--sp-4) 0;
  border-top: 1px solid var(--panel-border);
}

.stat-bar:first-of-type {
  border-top: 0;
}

.stat-label {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  color: var(--text-soft);
  font-size: var(--fs-2xs);
}

.stat-count {
  color: var(--accent-text);
  font-variant-numeric: tabular-nums;
  font-weight: 700;
  text-align: right;
}

/* `--ring-track` tam olarak bunun için var: bir ölçüm çubuğunun DOLMAYAN kısmı. Eskiden
   `rgba(255,255,255,0.06)` yazıyordu, yani açık temada beyaz üzerine beyaz — günlük grafiğinin
   rayı görünmüyordu ve dolan kısım havada duruyordu. */
.stat-track {
  grid-column: 1 / 2;
  height: 7px;
  border-radius: 4px;
  background: var(--ring-track);
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

/* Üç `rgba(255,255,255,…)` kenar daha belirtece bağlandı: açık temada beyaz üzerine beyaz
   çizildikleri için bu tablonun çerçevesi ve satır ayraçları HİÇ görünmüyordu. */
.composer-table {
  display: grid;
  overflow: hidden;
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-md);
}

.composer-row {
  display: grid;
  grid-template-columns: 150px minmax(0, 1fr) 140px;
  gap: var(--sp-12);
  align-items: center;
  padding: var(--sp-10) var(--sp-12);
  border-top: 1px solid var(--panel-border);
}

.composer-row:first-child {
  border-top: 0;
}

/* Başlık satırı panelin diğer tablo başlıklarıyla aynı zemini alır (base.css
   `.data-row.table-head`); eskiden yalnız yazı biçimi değişiyordu, zemini yoktu ve tek
   veri satırından ayrılmıyordu. 900 ağırlık da 600'e indi: ölçekte o basamak yok ve
   panelin bütün tablo başlıkları 600. */
.composer-row.head {
  color: var(--text-soft);
  font-size: var(--fs-xs);
  font-weight: 600;
  background: var(--surface-2);
}

.composer-output {
  max-height: 320px;
  overflow: auto;
  margin: 0;
  padding: var(--sp-12);
  border-radius: var(--radius-sm);
  /* Koyu terminal yüzeyi BİLEREK sabit: base.css `pre`/`.code-viewer` için aynı kararı
     veriyor ve açık temada bu ögeyi zaten kendi koyu değerine çeviyor (öge bir <pre>).
     Kabuk çıktısı açık zeminde okunmaz; belirtece bağlanMAZ. */
  background: #0a0e16;
  color: var(--accent-text);
  white-space: pre-wrap;
}

.help-list {
  display: grid;
  gap: var(--sp-6);
  margin: 0;
  padding-left: var(--sp-20);
}

.empty-state {
  margin: 0;
  padding: var(--sp-16);
  /* Kesikli kenar da belirtece bağlandı: `rgba(255,255,255,0.12)` açık temada görünmüyordu,
     yani "site seçin" / "yükleniyor" kutusunun sınırı yoktu ve metin havada duruyordu. */
  border: 1px dashed var(--panel-border);
  border-radius: var(--radius-md);
  color: var(--text-muted);
}

@media (max-width: 1080px) {
  .hosting-shell,
  .setting-row,
  .composer-row {
    grid-template-columns: 1fr;
  }

  /*
    DAR EKRANDA MENÜ ARTIK SAYFAYI TIKAMIYOR.
    ÖLÇÜLDÜ: `.hosting-shell` burada tek sütuna iniyor, yani 12 bölümlük kenar menüsü içeriğin
    ÜSTÜNE tam genişlikte geçiyordu. Her öge iki satır (başlık + ipucu) ve ~52px; 12 x 52 + site
    rozeti = ~700px. Kullanıcı 900px'lik bir ekranda ayarlara ulaşmak için önce yaklaşık bir tam
    ekran boyu menü kaydırıyordu.
    HİÇBİR ÖGE VE HİÇBİR İPUCU GİZLENMEDİ: menü yalnızca çok sütunlu bir ızgaraya alındı.
    900px'te satır başına 4 öge sığar (auto-fit, taban 190px) -> 3 satır, ~160px. Site rozeti
    bütün satırı kaplar ki hangi sitede olduğun menünün başında kalsın.
  */
  .hosting-nav {
    position: static;
    grid-template-columns: repeat(auto-fit, minmax(190px, 1fr));
  }

  .site-badge {
    grid-column: 1 / -1;
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
