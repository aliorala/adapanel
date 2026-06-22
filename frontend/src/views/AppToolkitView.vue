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
import { computed, ref } from "vue";
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
import { extractApiMessage } from "../stores/auth";
import { useSitesStore } from "../stores/sites";

type ToolkitTab = "composer" | "artisan" | "node" | "wpcli" | "queue";

const { t } = useI18n();
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
</style>
