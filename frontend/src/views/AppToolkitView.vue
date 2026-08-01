<template>
  <section class="panel module-shell toolkit">
    <div class="module-head">
      <div>
        <h3>{{ t("panel.toolkit.title") }}</h3>
        <p class="meta">{{ t("panel.toolkit.subtitle") }}</p>
      </div>
    </div>

    <p v-if="!siteName" class="meta">{{ t("panel.php.selectSite") }}</p>

    <template v-else>
      <div class="segmented-tabs" role="tablist">
        <button :class="{ active: tab === 'composer' }" @click="tab = 'composer'">{{ t("panel.toolkit.tabs.composer") }}</button>
        <button :class="{ active: tab === 'artisan' }" @click="tab = 'artisan'">{{ t("panel.toolkit.tabs.artisan") }}</button>
        <button :class="{ active: tab === 'node' }" @click="tab = 'node'">{{ t("panel.toolkit.tabs.node") }}</button>
        <button :class="{ active: tab === 'wordpress' }" @click="selectWordPress">{{ t("panel.toolkit.tabs.wordpress") }}</button>
        <button :class="{ active: tab === 'wpcli' }" @click="tab = 'wpcli'">{{ t("panel.toolkit.tabs.wpcli") }}</button>
        <button :class="{ active: tab === 'queue' }" @click="selectQueue">{{ t("panel.toolkit.tabs.queue") }}</button>
      </div>

      <CommandConsole
        v-if="tab === 'composer'"
        :title="t('panel.toolkit.tabs.composer')"
        :hint="t('panel.toolkit.composerHint')"
        placeholder="update, require monolog/monolog, dump-autoload"
        :presets="['install --no-dev --optimize-autoloader', 'update', 'require ', 'remove ', 'dump-autoload -o', 'show']"
        :runner="composerRunner" />

      <CommandConsole
        v-else-if="tab === 'artisan'"
        :title="t('panel.toolkit.tabs.artisan')"
        :hint="t('panel.toolkit.artisanHint')"
        placeholder="migrate, cache:clear, key:generate"
        :presets="['migrate', 'migrate --force', 'cache:clear', 'config:cache', 'route:list', 'key:generate', 'optimize']"
        :runner="artisanRunner" />

      <CommandConsole
        v-else-if="tab === 'node'"
        :title="t('panel.toolkit.tabs.node')"
        :hint="t('panel.toolkit.nodeHint')"
        placeholder="install, run build, run dev"
        :presets="['install', 'ci', 'run build', 'run dev', '-v']"
        :runner="nodeRunner" />

      <CommandConsole
        v-else-if="tab === 'wpcli'"
        :title="t('panel.toolkit.tabs.wpcli')"
        :hint="t('panel.toolkit.wpcliHint')"
        placeholder="plugin list, core update, cache flush"
        :presets="['core version', 'plugin list', 'plugin update --all', 'theme list', 'cache flush', 'core update']"
        :runner="wpcliRunner" />

      <div v-else-if="tab === 'wordpress'" class="wp-panel">
        <p v-if="wpNotice" :class="['notice', { error: wpNoticeError }]">{{ wpNotice }}</p>
        <p v-if="wpLoading" class="meta">{{ t("panel.wpkit.loading") }}</p>
        <p v-else-if="wp && !wp.isWordPress" class="notice warn">{{ t("panel.wpkit.notWordpress") }}</p>

        <template v-else-if="wp">
          <div class="wp-status">
            <span class="pill"><span class="state-dot"></span>WordPress {{ wp.version || "?" }}</span>
            <span v-if="wp.updateAvailable" class="pill stopped">{{ t("panel.wpkit.updateAvailable", { v: wp.latestVersion }) }}</span>
            <span v-else class="pill">{{ t("panel.wpkit.upToDate") }}</span>
            <a v-if="wp.adminUrl" :href="wp.adminUrl" target="_blank" rel="noopener" class="wp-admin-link">{{ t("panel.wpkit.openAdmin") }} ↗</a>
          </div>

          <div class="wp-actions">
            <button class="compact" :disabled="!!wpBusy" @click="wpLogin">{{ wpBusy === 'login' ? t("panel.wpkit.working") : t("panel.wpkit.login") }}</button>
            <button class="secondary compact" :disabled="!!wpBusy" @click="wpUpdate">{{ wpBusy === 'update' ? t("panel.wpkit.working") : t("panel.wpkit.update") }}</button>
            <button class="secondary compact" :disabled="!!wpBusy" @click="wpHarden">{{ wpBusy === 'harden' ? t("panel.wpkit.working") : t("panel.wpkit.harden") }}</button>
            <button class="secondary compact" :disabled="!!wpBusy" @click="wpToggleMaintenance">{{ wp.maintenanceMode ? t("panel.wpkit.maintenanceOff") : t("panel.wpkit.maintenanceOn") }}</button>
          </div>

          <div class="wp-form">
            <span class="field-label">{{ t("panel.wpkit.securityStatus") }}</span>
            <div class="wp-list">
              <div class="wp-list-row"><span class="wp-list-name">{{ t("panel.wpkit.st_file_edit") }}</span><span :class="['pill', wp.security.fileEditorDisabled ? '' : 'stopped']">{{ wp.security.fileEditorDisabled ? t("panel.wpkit.secOn") : t("panel.wpkit.secOff") }}</span></div>
              <div class="wp-list-row"><span class="wp-list-name">{{ t("panel.wpkit.st_version") }}</span><span :class="['pill', wp.security.versionHidden ? '' : 'stopped']">{{ wp.security.versionHidden ? t("panel.wpkit.secOn") : t("panel.wpkit.secOff") }}</span></div>
              <div class="wp-list-row"><span class="wp-list-name">{{ t("panel.wpkit.st_xmlrpc") }}</span><span :class="['pill', wp.security.xmlRpcPingbackDisabled ? '' : 'stopped']">{{ wp.security.xmlRpcPingbackDisabled ? t("panel.wpkit.secOn") : t("panel.wpkit.secOff") }}</span></div>
              <div class="wp-list-row"><span class="wp-list-name">{{ t("panel.wpkit.st_author") }}</span><span :class="['pill', wp.security.authorScanBlocked ? '' : 'stopped']">{{ wp.security.authorScanBlocked ? t("panel.wpkit.secOn") : t("panel.wpkit.secOff") }}</span></div>
            </div>
          </div>

          <div class="wp-form">
            <span class="field-label">{{ t("panel.wpkit.autoUpdates") }}</span>
            <div class="wp-row">
              <button class="secondary compact" :disabled="!!wpBusy" @click="wpAutoUpdate(true)">{{ wpBusy === 'autoOn' ? t("panel.wpkit.working") : t("panel.wpkit.autoUpdatesOn") }}</button>
              <button class="secondary compact" :disabled="!!wpBusy" @click="wpAutoUpdate(false)">{{ wpBusy === 'autoOff' ? t("panel.wpkit.working") : t("panel.wpkit.autoUpdatesOff") }}</button>
            </div>
            <small class="hint">{{ t("panel.wpkit.autoUpdatesHint") }}</small>
          </div>

          <div class="wp-form">
            <span class="field-label">{{ t("panel.wpkit.installPlugin") }}</span>
            <div class="wp-row">
              <input v-model.trim="wpForm.pluginSlug" placeholder="akismet" :disabled="!!wpBusy" />
              <button class="secondary compact" :disabled="!!wpBusy || !wpForm.pluginSlug" @click="wpInstallPlugin">{{ wpBusy === 'plugin' ? t("panel.wpkit.working") : t("panel.wpkit.install") }}</button>
            </div>
            <small class="hint">{{ t("panel.wpkit.installPluginHint") }}</small>
          </div>

          <div v-if="wp.plugins.length" class="wp-form">
            <span class="field-label">{{ t("panel.wpkit.plugins") }} ({{ wp.plugins.length }})</span>
            <div class="wp-list">
              <div v-for="p in wp.plugins" :key="p.slug" class="wp-list-row">
                <span class="wp-list-name"><span v-if="p.active" class="state-dot"></span>{{ p.name }} <small v-if="p.version">v{{ p.version }}</small></span>
                <span class="wp-list-actions">
                  <button v-if="p.active" class="secondary compact" :disabled="!!wpBusy" @click="wpComponent('plugin', 'deactivate', p.slug)">{{ t("panel.wpkit.deactivate") }}</button>
                  <button v-else class="secondary compact" :disabled="!!wpBusy" @click="wpComponent('plugin', 'activate', p.slug)">{{ t("panel.wpkit.activate") }}</button>
                  <button class="secondary compact" :disabled="!!wpBusy" @click="wpComponent('plugin', 'update', p.slug)">{{ t("panel.wpkit.compUpdate") }}</button>
                  <button v-if="!p.active" class="secondary compact" :disabled="!!wpBusy" @click="wpComponent('plugin', 'delete', p.slug)">{{ t("panel.wpkit.compDelete") }}</button>
                </span>
              </div>
            </div>
          </div>

          <div v-if="wp.themes.length" class="wp-form">
            <span class="field-label">{{ t("panel.wpkit.themes") }} ({{ wp.themes.length }})</span>
            <div class="wp-list">
              <div v-for="th in wp.themes" :key="th.slug" class="wp-list-row">
                <span class="wp-list-name"><span v-if="th.active" class="state-dot"></span>{{ th.name }} <small v-if="th.version">v{{ th.version }}</small></span>
                <span class="wp-list-actions">
                  <span v-if="th.active" class="pill">{{ t("panel.wpkit.activeLabel") }}</span>
                  <button v-else class="secondary compact" :disabled="!!wpBusy" @click="wpComponent('theme', 'activate', th.slug)">{{ t("panel.wpkit.activate") }}</button>
                  <button class="secondary compact" :disabled="!!wpBusy" @click="wpComponent('theme', 'update', th.slug)">{{ t("panel.wpkit.compUpdate") }}</button>
                  <button v-if="!th.active" class="secondary compact" :disabled="!!wpBusy" @click="wpComponent('theme', 'delete', th.slug)">{{ t("panel.wpkit.compDelete") }}</button>
                </span>
              </div>
            </div>
          </div>

          <div class="wp-form">
            <span class="field-label">{{ t("panel.wpkit.searchReplace") }}</span>
            <div class="wp-row">
              <input v-model.trim="wpForm.srFrom" placeholder="http://eski.com" :disabled="!!wpBusy" />
              <input v-model.trim="wpForm.srTo" placeholder="https://yeni.com" :disabled="!!wpBusy" />
              <button class="secondary compact" :disabled="!!wpBusy || !wpForm.srFrom || !wpForm.srTo" @click="wpSearchReplace">{{ wpBusy === 'sr' ? t("panel.wpkit.working") : t("panel.wpkit.replace") }}</button>
            </div>
            <small class="hint">{{ t("panel.wpkit.searchReplaceHint") }}</small>
          </div>

          <div class="wp-form">
            <span class="field-label">{{ t("panel.wpkit.resetPassword") }}</span>
            <div class="wp-row">
              <input v-model.trim="wpForm.adminUser" placeholder="admin" :disabled="!!wpBusy" />
              <button class="secondary compact" :disabled="!!wpBusy || !wpForm.adminUser" @click="wpResetPassword">{{ wpBusy === 'pass' ? t("panel.wpkit.working") : t("panel.wpkit.reset") }}</button>
            </div>
            <div v-if="wpNewPassword" class="notice wp-cred">{{ t("panel.wpkit.newPassword") }}: <code>{{ wpNewPassword }}</code><br /><small>{{ t("panel.wpkit.newPasswordHint") }}</small></div>
          </div>

          <div class="wp-form">
            <span class="field-label">{{ t("panel.wpkit.clone") }}</span>
            <div class="wp-row">
              <input v-model.trim="wpClone.targetSiteName" :placeholder="t('panel.wpkit.cloneSiteName')" :disabled="!!wpBusy" />
              <input v-model.trim="wpClone.targetHostName" placeholder="staging.ornek.com" :disabled="!!wpBusy" />
              <button class="secondary compact" :disabled="!!wpBusy || !wpClone.targetSiteName || !wpClone.targetHostName" @click="wpDoClone">{{ wpBusy === 'clone' ? t("panel.wpkit.working") : t("panel.wpkit.cloneBtn") }}</button>
            </div>
            <small class="hint">{{ t("panel.wpkit.cloneHint") }}</small>
            <div v-if="wpCloneResult" class="notice wp-cred">
              <a :href="wpCloneResult.targetUrl" target="_blank" rel="noopener">{{ wpCloneResult.targetUrl }}</a>
              · DB: <code>{{ wpCloneResult.targetDatabase }}</code>
              · {{ t("panel.wpkit.cloneUser") }}: <code>{{ wpCloneResult.databaseUser }}</code>
              · {{ t("panel.wpkit.clonePass") }}: <code>{{ wpCloneResult.databasePassword }}</code>
            </div>
          </div>

          <div class="wp-form">
            <span class="field-label">{{ t("panel.wpkit.copyData") }}</span>
            <div class="wp-row">
              <input v-model.trim="wpCopy.targetSiteName" :placeholder="t('panel.wpkit.copyTarget')" :disabled="!!wpBusy" />
              <label class="wp-check"><input v-model="wpCopy.files" type="checkbox" :disabled="!!wpBusy" /> {{ t("panel.wpkit.copyFiles") }}</label>
              <label class="wp-check"><input v-model="wpCopy.database" type="checkbox" :disabled="!!wpBusy" /> {{ t("panel.wpkit.copyDatabase") }}</label>
              <button class="secondary compact" :disabled="!!wpBusy || !wpCopy.targetSiteName || (!wpCopy.files && !wpCopy.database)" @click="wpDoCopy">{{ wpBusy === 'copy' ? t("panel.wpkit.working") : t("panel.wpkit.copyBtn") }}</button>
            </div>
            <small class="hint">⚠ {{ t("panel.wpkit.copyHint") }}</small>
            <div v-if="wpCopyResult" class="notice wp-cred">{{ t("panel.wpkit.copyDone") }}<br /><small>{{ t("panel.wpkit.copyBackup") }}: <code>{{ wpCopyResult.backupPath }}</code></small></div>
          </div>

          <pre v-if="wpOutput" class="wp-output">{{ wpOutput }}</pre>
        </template>
      </div>

      <div v-else class="queue-panel">
        <h4>{{ t("panel.toolkit.queue.title") }}</h4>
        <p class="meta">{{ t("panel.toolkit.queue.desc") }}</p>

        <p v-if="queueNotice" :class="['notice', { error: queueNoticeError }]">{{ queueNotice }}</p>

        <div class="queue-status">
          <span :class="['pill', { stopped: !queueRunning }]">
            <span class="state-dot"></span>
            {{ queueRunning ? t("panel.toolkit.queue.statusRunning") : t("panel.toolkit.queue.statusStopped") }}
          </span>
          <button v-if="!queueRunning" class="compact" :disabled="queueBusy" @click="startQueue">
            {{ queueBusy ? t("panel.toolkit.queue.working") : t("panel.toolkit.queue.start") }}
          </button>
          <button v-else class="secondary compact" :disabled="queueBusy" @click="stopQueue">
            {{ queueBusy ? t("panel.toolkit.queue.working") : t("panel.toolkit.queue.stop") }}
          </button>
        </div>

        <p class="hint">{{ t("panel.toolkit.queue.hint") }}</p>

        <h4 class="sched-head">{{ t("panel.toolkit.scheduler.title") }}</h4>
        <p class="meta">{{ t("panel.toolkit.scheduler.desc") }}</p>
        <div class="queue-status">
          <span :class="['pill', { stopped: !schedulerRunning }]">
            <span class="state-dot"></span>
            {{ schedulerRunning ? t("panel.toolkit.queue.statusRunning") : t("panel.toolkit.queue.statusStopped") }}
          </span>
          <button v-if="!schedulerRunning" class="compact" :disabled="schedulerBusy" @click="startScheduler">
            {{ schedulerBusy ? t("panel.toolkit.queue.working") : t("panel.toolkit.queue.start") }}
          </button>
          <button v-else class="secondary compact" :disabled="schedulerBusy" @click="stopScheduler">
            {{ schedulerBusy ? t("panel.toolkit.queue.working") : t("panel.toolkit.queue.stop") }}
          </button>
        </div>
        <p class="hint">{{ t("panel.toolkit.scheduler.hint") }}</p>
      </div>
    </template>
  </section>
</template>

<script setup lang="ts">
import { computed, reactive, ref } from "vue";
import { useI18n } from "vue-i18n";
import CommandConsole from "../components/CommandConsole.vue";
import {
  getLaravelScheduler,
  getQueueWorker,
  runArtisanCommand,
  runComposerCommand,
  runNodeCommand,
  runWpCliCommand,
  startLaravelScheduler,
  startQueueWorker,
  stopLaravelScheduler,
  stopQueueWorker
} from "../api/siteHosting";
import { getWordPress, setWordPressMaintenance, createWordPressLoginLink, cloneWordPress, hardenWordPress, copyWordPressData, type WordPressSite, type WordPressCloneResult, type WordPressCopyDataResult } from "../api/wordpress";
import { useConfirm } from "../components/ConfirmDialog.vue";
import { extractApiMessage } from "../stores/auth";
import { useSitesStore } from "../stores/sites";

type ToolkitTab = "composer" | "artisan" | "node" | "wpcli" | "wordpress" | "queue";

const { t } = useI18n();
const { confirm } = useConfirm();
const sitesStore = useSitesStore();
const siteName = computed(() => sitesStore.selectedSiteName);
const tab = ref<ToolkitTab>("composer");

// Akıllı yönlendirme: kullanıcı hangi sekmede olursa olsun, komutun başına göre doğru çalıştırıcıya gönderilir.
// (ör. Composer sekmesinde "php artisan migrate" yazılsa bile Artisan'a gider.) Önek yoksa sekmenin
// kendi çalıştırıcısı kullanılır. Backend önekleri (composer/php artisan/npm) zaten kırpıyor.
type Runner = (site: string, command: string) => Promise<{ exitCode: number; output: string; error: string }>;
function routed(fallback: Runner) {
  return (command: string) => {
    const text = command.trim();
    if (/^composer\b/i.test(text)) return runComposerCommand(siteName.value, text);
    if (/^(php\s+artisan|artisan)\b/i.test(text)) return runArtisanCommand(siteName.value, text);
    if (/^npm\b/i.test(text)) return runNodeCommand(siteName.value, text);
    if (/^wp\b/i.test(text)) return runWpCliCommand(siteName.value, text);
    return fallback(siteName.value, text);
  };
}

const composerRunner = routed(runComposerCommand);
const artisanRunner = routed(runArtisanCommand);
const nodeRunner = routed(runNodeCommand);
const wpcliRunner = routed(runWpCliCommand);

// --- WordPress Toolkit (Plesk benzeri tek-tık aksiyonlar; mevcut wp-cli motorunu kullanır) ---
const wp = ref<WordPressSite | null>(null);
const wpLoading = ref(false);
const wpLoaded = ref(false);
const wpBusy = ref<"" | "login" | "update" | "harden" | "plugin" | "sr" | "pass" | "maint" | "autoOn" | "autoOff" | "clone" | "comp" | "copy">("");
const wpOutput = ref("");
const wpNotice = ref("");
const wpNoticeError = ref(false);
const wpNewPassword = ref("");
const wpForm = reactive({ pluginSlug: "", srFrom: "", srTo: "", adminUser: "admin" });
const wpClone = reactive({ targetSiteName: "", targetHostName: "" });
const wpCloneResult = ref<WordPressCloneResult | null>(null);
const wpCopy = reactive({ targetSiteName: "", files: true, database: true });
const wpCopyResult = ref<WordPressCopyDataResult | null>(null);

function selectWordPress() {
  tab.value = "wordpress";
  if (!wpLoaded.value) {
    void loadWp();
  }
}

async function loadWp() {
  if (!siteName.value) {
    return;
  }
  wpLoading.value = true;
  try {
    wp.value = await getWordPress(siteName.value);
    wpLoaded.value = true;
  } catch (error) {
    showWpNotice(resolveApiMessage(error), true);
  } finally {
    wpLoading.value = false;
  }
}

function showWpNotice(message: string, isError: boolean) {
  wpNotice.value = message;
  wpNoticeError.value = isError;
}

// Tek bir wp-cli komutu çalıştırır, çıktıyı gösterir, exitCode==0 başarı sayılır.
async function runWp(command: string): Promise<boolean> {
  const result = await runWpCliCommand(siteName.value, command);
  wpOutput.value = `$ wp ${command}\n${result.output || ""}${result.error ? "\n" + result.error : ""}`.trim();
  return result.exitCode === 0;
}

// panel "Giriş yap": tek kullanımlık sihirli bağlantı alır, yeni sekmede açar (doğrudan wp-admin).
async function wpLogin() {
  if (!siteName.value) {
    return;
  }
  // Sekmeyi TIK anında aç (kullanıcı hareketi içinde); URL'yi await sonrası ata. Aksi halde tarayıcı popup'ı engeller.
  const tab = window.open("", "_blank");
  wpBusy.value = "login";
  wpNotice.value = "";
  try {
    const link = await createWordPressLoginLink(siteName.value);
    if (tab) {
      tab.location.href = link.url;
    }
    showWpNotice(t("panel.wpkit.loginOpened"), false);
  } catch (error) {
    if (tab) {
      tab.close();
    }
    showWpNotice(resolveApiMessage(error), true);
  } finally {
    wpBusy.value = "";
  }
}

// panel kurulu eklenti/tema yönetimi: tek bileşeni etkinleştir/devre dışı bırak/güncelle/sil (wp-cli), sonra listeyi tazele.
async function wpComponent(kind: "plugin" | "theme", action: "activate" | "deactivate" | "update" | "delete", slug: string) {
  if (!slug) {
    return;
  }
  if (action === "delete") {
    // Normal onay: tek bir eklenti/tema kaldırılır (yeniden kurulabilir) → ad yazdırmaya gerek yok.
    const approved = await confirm({
      title: t("panel.confirm.deleteWpComponentTitle", { name: slug }),
      message: t("panel.wpkit.confirmDelete", { name: slug }),
      impact: [`${t(kind === "plugin" ? "panel.wpkit.plugins" : "panel.wpkit.themes")}: ${slug}`],
    });
    if (!approved) {
      return;
    }
  }
  wpBusy.value = "comp";
  wpNotice.value = "";
  try {
    const ok = await runWp(`${kind} ${action} ${slug}`);
    showWpNotice(ok ? t("panel.wpkit.compDone") : t("panel.wpkit.failed"), !ok);
    if (ok) {
      await loadWp();
    }
  } catch (error) {
    showWpNotice(resolveApiMessage(error), true);
  } finally {
    wpBusy.value = "";
  }
}

// panel "Veri Kopyala": kaynağı VAR OLAN hedefe taşır (staging→canlı). YIKICI → önce onay; backend önce hedefi yedekler.
async function wpDoCopy() {
  if (!siteName.value || !wpCopy.targetSiteName || (!wpCopy.files && !wpCopy.database)) {
    return;
  }
  const impact: string[] = [`${t("panel.wpkit.copyTarget")}: ${wpCopy.targetSiteName}`];
  if (wpCopy.files) {
    impact.push(t("panel.wpkit.copyFiles"));
  }
  if (wpCopy.database) {
    impact.push(t("panel.wpkit.copyDatabase"));
  }

  // Yüksek riskli onay: hedef sitenin ÜZERİNE yazılır → hedef site adı yazılmadan devam edilemez.
  await confirm({
    title: t("panel.confirm.copyDataTitle", { name: wpCopy.targetSiteName }),
    message: t("panel.wpkit.copyConfirm", { target: wpCopy.targetSiteName }),
    impact,
    requireText: wpCopy.targetSiteName,
    confirmLabel: t("panel.wpkit.copyBtn"),
    busyLabel: t("panel.wpkit.working"),
    action: async () => {
      wpBusy.value = "copy";
      wpNotice.value = "";
      wpCopyResult.value = null;
      try {
        wpCopyResult.value = await copyWordPressData(siteName.value, {
          targetSiteName: wpCopy.targetSiteName,
          files: wpCopy.files,
          database: wpCopy.database,
        });
        showWpNotice(t("panel.wpkit.copyDone"), false);
      } catch (error) {
        showWpNotice(resolveApiMessage(error), true);
      } finally {
        wpBusy.value = "";
      }
    },
  });
}

// panel "Klonla": kaynak WP'yi yeni bir staging sitesine kopyalar (yalnız yönetici). Backend tüm işi orkestre eder.
async function wpDoClone() {
  if (!siteName.value || !wpClone.targetSiteName || !wpClone.targetHostName) {
    return;
  }
  wpBusy.value = "clone";
  wpNotice.value = "";
  wpCloneResult.value = null;
  try {
    wpCloneResult.value = await cloneWordPress(siteName.value, {
      targetSiteName: wpClone.targetSiteName,
      targetHostName: wpClone.targetHostName,
    });
    showWpNotice(t("panel.wpkit.cloneDone"), false);
  } catch (error) {
    showWpNotice(resolveApiMessage(error), true);
  } finally {
    wpBusy.value = "";
  }
}

// panel "Otomatik güncellemeler": WP-native ayarlar (çekirdek sabiti + eklenti/tema auto-updates). Harici görev yok.
async function wpAutoUpdate(enable: boolean) {
  wpBusy.value = enable ? "autoOn" : "autoOff";
  wpNotice.value = "";
  wpOutput.value = "";
  try {
    const verb = enable ? "enable" : "disable";
    const commands = [
      `config set WP_AUTO_UPDATE_CORE ${enable ? "true" : "false"} --raw`,
      `plugin auto-updates ${verb} --all`,
      `theme auto-updates ${verb} --all`,
    ];
    let combined = "";
    let ok = true;
    for (const command of commands) {
      const result = await runWpCliCommand(siteName.value, command);
      combined += `$ wp ${command}\n${result.output || ""}${result.error ? "\n" + result.error : ""}\n\n`;
      if (result.exitCode !== 0) {
        ok = false;
      }
    }
    wpOutput.value = combined.trim();
    showWpNotice(
      ok ? t(enable ? "panel.wpkit.autoUpdatesEnabled" : "panel.wpkit.autoUpdatesDisabled") : t("panel.wpkit.failed"),
      !ok
    );
  } catch (error) {
    showWpNotice(resolveApiMessage(error), true);
  } finally {
    wpBusy.value = "";
  }
}

async function wpUpdate() {
  wpBusy.value = "update";
  wpNotice.value = "";
  wpOutput.value = "";
  try {
    let combined = "";
    for (const command of ["core update", "plugin update --all", "theme update --all"]) {
      const result = await runWpCliCommand(siteName.value, command);
      combined += `$ wp ${command}\n${result.output || ""}${result.error ? "\n" + result.error : ""}\n\n`;
    }
    wpOutput.value = combined.trim();
    showWpNotice(t("panel.wpkit.updated"), false);
    await loadWp();
  } catch (error) {
    showWpNotice(resolveApiMessage(error), true);
  } finally {
    wpBusy.value = "";
  }
}

// panel "Güvenlik": önlem demetini backend'de uygular, uygulanan önlemleri etiketleyip listeler.
async function wpHarden() {
  if (!siteName.value) {
    return;
  }
  wpBusy.value = "harden";
  wpNotice.value = "";
  try {
    const result = await hardenWordPress(siteName.value);
    wpOutput.value = result.applied.map((code) => `✓ ${t("panel.wpkit.sec_" + code)}`).join("\n");
    showWpNotice(t("panel.wpkit.hardened"), false);
  } catch (error) {
    showWpNotice(resolveApiMessage(error), true);
  } finally {
    wpBusy.value = "";
  }
}

async function wpToggleMaintenance() {
  if (!wp.value) {
    return;
  }
  wpBusy.value = "maint";
  wpNotice.value = "";
  try {
    wp.value = await setWordPressMaintenance(siteName.value, !wp.value.maintenanceMode);
    showWpNotice(t("panel.wpkit.maintenanceUpdated"), false);
  } catch (error) {
    showWpNotice(resolveApiMessage(error), true);
  } finally {
    wpBusy.value = "";
  }
}

async function wpInstallPlugin() {
  if (!wpForm.pluginSlug) {
    return;
  }
  wpBusy.value = "plugin";
  wpNotice.value = "";
  try {
    const ok = await runWp(`plugin install ${wpForm.pluginSlug} --activate`);
    showWpNotice(ok ? t("panel.wpkit.installed") : t("panel.wpkit.failed"), !ok);
    if (ok) {
      wpForm.pluginSlug = "";
      await loadWp();
    }
  } catch (error) {
    showWpNotice(resolveApiMessage(error), true);
  } finally {
    wpBusy.value = "";
  }
}

async function wpSearchReplace() {
  if (!wpForm.srFrom || !wpForm.srTo) {
    return;
  }
  wpBusy.value = "sr";
  wpNotice.value = "";
  try {
    const ok = await runWp(`search-replace ${wpForm.srFrom} ${wpForm.srTo} --all-tables --report-changes-only`);
    showWpNotice(ok ? t("panel.wpkit.replaced") : t("panel.wpkit.failed"), !ok);
  } catch (error) {
    showWpNotice(resolveApiMessage(error), true);
  } finally {
    wpBusy.value = "";
  }
}

function genPassword(): string {
  const bytes = new Uint8Array(12);
  crypto.getRandomValues(bytes);
  return "Wp!" + Array.from(bytes).map((value) => value.toString(16).padStart(2, "0")).join("");
}

async function wpResetPassword() {
  const user = (wpForm.adminUser || "admin").trim();
  if (!user) {
    return;
  }
  wpBusy.value = "pass";
  wpNotice.value = "";
  wpNewPassword.value = "";
  try {
    const password = genPassword();
    const ok = await runWp(`user update ${user} --user_pass=${password} --skip-email`);
    if (ok) {
      wpNewPassword.value = password;
      showWpNotice(t("panel.wpkit.passwordReset"), false);
    } else {
      showWpNotice(t("panel.wpkit.failed"), true);
    }
  } catch (error) {
    showWpNotice(resolveApiMessage(error), true);
  } finally {
    wpBusy.value = "";
  }
}

const queueRunning = ref(false);
const queueBusy = ref(false);
const queueLoaded = ref(false);
const queueNotice = ref("");
const queueNoticeError = ref(false);
const schedulerRunning = ref(false);
const schedulerBusy = ref(false);

function selectQueue() {
  tab.value = "queue";
  if (!queueLoaded.value) {
    void loadQueue();
    void loadScheduler();
  }
}

async function loadQueue() {
  if (!siteName.value) {
    return;
  }
  try {
    const status = await getQueueWorker(siteName.value);
    queueRunning.value = status.running;
    queueLoaded.value = true;
  } catch {
    // Durum okunamazsa sessiz geç; kullanıcı başlat/durdur ile yine yönetebilir.
  }
}

async function startQueue() {
  queueBusy.value = true;
  queueNotice.value = "";
  try {
    const status = await startQueueWorker(siteName.value);
    queueRunning.value = status.running;
    showQueueNotice(t("panel.toolkit.queue.started"), false);
  } catch (error) {
    showQueueNotice(resolveApiMessage(error), true);
  } finally {
    queueBusy.value = false;
  }
}

async function stopQueue() {
  queueBusy.value = true;
  queueNotice.value = "";
  try {
    const status = await stopQueueWorker(siteName.value);
    queueRunning.value = status.running;
    showQueueNotice(t("panel.toolkit.queue.stopped"), false);
  } catch (error) {
    showQueueNotice(resolveApiMessage(error), true);
  } finally {
    queueBusy.value = false;
  }
}

async function loadScheduler() {
  if (!siteName.value) {
    return;
  }
  try {
    const status = await getLaravelScheduler(siteName.value);
    schedulerRunning.value = status.running;
  } catch {
    // Durum okunamazsa sessiz geç.
  }
}

async function startScheduler() {
  schedulerBusy.value = true;
  queueNotice.value = "";
  try {
    const status = await startLaravelScheduler(siteName.value);
    schedulerRunning.value = status.running;
    showQueueNotice(t("panel.toolkit.scheduler.started"), false);
  } catch (error) {
    showQueueNotice(resolveApiMessage(error), true);
  } finally {
    schedulerBusy.value = false;
  }
}

async function stopScheduler() {
  schedulerBusy.value = true;
  queueNotice.value = "";
  try {
    const status = await stopLaravelScheduler(siteName.value);
    schedulerRunning.value = status.running;
    showQueueNotice(t("panel.toolkit.scheduler.stopped"), false);
  } catch (error) {
    showQueueNotice(resolveApiMessage(error), true);
  } finally {
    schedulerBusy.value = false;
  }
}

function resolveApiMessage(error: unknown) {
  const key = extractApiMessage(error);
  const translated = t(key);
  return translated === key ? key : translated;
}

function showQueueNotice(message: string, isError: boolean) {
  queueNotice.value = message;
  queueNoticeError.value = isError;
}
</script>

<style scoped>
.toolkit {
  display: grid;
  gap: 12px;
}

.sched-head {
  margin: 16px 0 0;
  padding-top: 14px;
  border-top: 1px solid var(--panel-border);
}

.queue-panel {
  display: grid;
  gap: 10px;
  padding: 14px 16px;
  border: 1px solid var(--panel-border);
  border-radius: 12px;
  background: var(--surface-1);
}

.queue-panel h4 {
  margin: 0;
}

.queue-status {
  display: flex;
  align-items: center;
  gap: 12px;
  flex-wrap: wrap;
}

.queue-status button {
  width: auto;
}

.pill {
  display: inline-flex;
  gap: 6px;
  align-items: center;
  border-radius: 999px;
  padding: 5px 12px;
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

.hint {
  color: var(--text-muted);
  font-size: 0.8rem;
}

.wp-panel {
  display: grid;
  gap: 14px;
  padding: 14px 16px;
  border: 1px solid var(--panel-border);
  border-radius: 12px;
  background: var(--surface-1);
}
.wp-status {
  display: flex;
  align-items: center;
  gap: 10px;
  flex-wrap: wrap;
}
.wp-admin-link {
  margin-left: auto;
  font-size: 0.85rem;
  font-weight: 600;
}
.wp-actions {
  display: flex;
  gap: 8px;
  flex-wrap: wrap;
}
.wp-actions button {
  width: auto;
}
.wp-form {
  display: grid;
  gap: 6px;
  padding-top: 12px;
  border-top: 1px solid var(--panel-border);
}
.wp-row {
  display: flex;
  gap: 8px;
  flex-wrap: wrap;
}
.wp-row input {
  flex: 1 1 160px;
  width: auto;
}
.wp-row button {
  width: auto;
  flex: none;
}
.wp-cred code {
  font-family: ui-monospace, "Cascadia Code", Consolas, monospace;
  background: color-mix(in srgb, var(--accent-1) 16%, transparent);
  color: var(--accent-1);
  padding: 2px 8px;
  border-radius: 6px;
  user-select: all;
}
.wp-output {
  margin: 0;
  padding: 12px;
  border-radius: 8px;
  background: var(--surface-0, #0b1418);
  border: 1px solid var(--panel-border);
  font-family: ui-monospace, "Cascadia Code", Consolas, monospace;
  font-size: 0.8rem;
  white-space: pre-wrap;
  word-break: break-word;
  max-height: 280px;
  overflow: auto;
}
.wp-list {
  display: grid;
  gap: 4px;
  max-height: 220px;
  overflow: auto;
}
.wp-list-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  padding: 6px 8px;
  border-radius: 6px;
  background: color-mix(in srgb, var(--panel-border) 30%, transparent);
}
.wp-list-name {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.wp-list-name small {
  opacity: 0.6;
}
.wp-list-actions {
  display: flex;
  gap: 6px;
  flex: none;
}
.wp-check {
  display: flex;
  align-items: center;
  gap: 4px;
  white-space: nowrap;
  flex: none;
}
.wp-check input {
  width: auto;
  flex: none;
}
</style>
