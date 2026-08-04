<template>
  <section class="panel module-shell">
    <div class="module-head">
      <div>
        <h2>{{ t("panel.tasks.title") }}</h2>
        <p>{{ t("panel.tasks.subtitle") }}</p>
      </div>
      <button class="secondary compact" :disabled="loading" @click="refresh">
        {{ t("panel.tasks.refresh") }}
      </button>
    </div>

    <p v-if="notice" :class="['notice', { error: noticeError }]">{{ notice }}</p>

    <div class="module-toolbar">
      <div class="toolbar-group">
        <button @click="formOpen = !formOpen">
          {{ formOpen ? t("panel.tasks.closeForm") : t("panel.tasks.add") }}
        </button>
        <span class="status-pill ok">{{ tasks.length }} {{ t("panel.tasks.taskCount") }}</span>
      </div>
      <input v-model.trim="query" class="search-control" :placeholder="t('panel.tasks.search')" />
    </div>

    <section v-if="formOpen" class="form-surface">
      <div class="form-grid">
        <label><span class="field-label">{{ t("panel.tasks.name") }}</span><input v-model.trim="form.name" /></label>
        <label v-if="isAdmin">
          <span class="field-label">{{ t("panel.tasks.taskType") }}</span>
          <select v-model="form.taskType">
            <option value="command">{{ t("panel.tasks.typeCommand") }}</option>
            <option value="php">{{ t("panel.tasks.typePhp") }}</option>
            <option value="script">{{ t("panel.tasks.typeScript") }}</option>
            <option value="url">{{ t("panel.tasks.typeUrl") }}</option>
          </select>
        </label>
        <label v-if="form.taskType === 'url'" class="span-2">
          <span class="field-label">{{ t("panel.tasks.url") }}</span>
          <input v-model.trim="form.url" placeholder="https://ornek.com/cron/calistir?key=..." spellcheck="false" />
          <small class="field-hint">{{ t("panel.tasks.urlHint") }}</small>
        </label>

        <!-- Betik görevi yalnız yöneticide: sunucu betiği (yol serbest) mi, site betiği (yol o sitenin kökünde) mi. -->
        <label v-if="isScriptForm">
          <span class="field-label">{{ t("panel.tasks.scriptScope") }}</span>
          <select v-model="scriptScope">
            <option value="server">{{ t("panel.tasks.scriptScopeServer") }}</option>
            <option value="site">{{ t("panel.tasks.scriptScopeSite") }}</option>
          </select>
        </label>
        <label v-if="isScriptForm && scriptScope === 'site'">
          <span class="field-label">{{ t("panel.tasks.scriptSite") }}</span>
          <select v-model="form.siteName">
            <option value="">{{ t("panel.tasks.scriptSitePlaceholder") }}</option>
            <option v-for="site in sites" :key="site.name" :value="site.name">{{ site.name }}</option>
          </select>
          <small v-if="selectedSiteRoot" class="field-hint">{{ t("panel.tasks.scriptRootHint", { path: selectedSiteRoot }) }}</small>
        </label>
        <!-- Kimlik seçimi yalnız site modunda anlamlı: havuz hesabı bir SİTEYE aittir. -->
        <label v-if="isScriptForm && scriptScope === 'site'">
          <span class="field-label">{{ t("panel.tasks.runAs") }}</span>
          <select v-model="form.runAs">
            <option value="system">{{ t("panel.tasks.runAsSystem") }}</option>
            <option value="limited">{{ t("panel.tasks.runAsLimited") }}</option>
          </select>
          <small class="field-hint">{{ t("panel.tasks.runAsHint") }}</small>
        </label>

        <label v-if="form.taskType !== 'url'">
          <span class="field-label">{{ commandFieldLabel }}</span>
          <input v-model.trim="form.command" :placeholder="commandFieldPlaceholder" spellcheck="false" />
          <small v-if="form.taskType === 'php'" class="field-hint">{{ t("panel.tasks.phpHint") }}</small>
          <small v-else-if="form.taskType === 'script'" class="field-hint">{{ t("panel.tasks.scriptHint") }}</small>
        </label>
        <label v-if="form.taskType !== 'url'"><span class="field-label">{{ t("panel.tasks.arguments") }}</span><input v-model="form.arguments" /></label>
        <label v-if="form.taskType === 'command' || form.taskType === 'script'">
          <span class="field-label">{{ t("panel.tasks.workingDirectory") }}</span>
          <input v-model.trim="form.workingDirectory" :placeholder="form.taskType === 'script' ? t('panel.tasks.workingDirectoryScriptHint') : ''" />
        </label>
        <label>
          <span class="field-label">{{ t("panel.tasks.scheduleType") }}</span>
          <select v-model="form.scheduleType">
            <option value="hourly">{{ t("panel.tasks.hourly") }}</option>
            <option value="daily">{{ t("panel.tasks.daily") }}</option>
            <option value="minute">{{ t("panel.tasks.minutely") }}</option>
          </select>
        </label>
        <label><span class="field-label">{{ t("panel.tasks.interval") }}</span><input v-model.number="form.interval" type="number" min="1" /></label>
        <label v-if="form.scheduleType === 'daily'">
          <span class="field-label">{{ t("panel.tasks.timeOfDay") }}</span>
          <input v-model="form.timeOfDay" type="time" />
        </label>
      </div>
      <div class="row-actions">
        <button :disabled="saving || !form.name || !formComplete" @click="saveTask">
          {{ saving ? t("panel.tasks.saving") : t("panel.tasks.save") }}
        </button>
        <button class="secondary compact" @click="resetForm">{{ t("panel.tasks.reset") }}</button>
      </div>
    </section>

    <!-- Takılı görevler tablonun ÜSTÜNDE, adıyla ve çözümüyle birlikte. Satırdaki tek bir hata
         rozeti fark edilmiyordu: kullanıcı "Running" görüp sorun yok sanıyor, oysa görev fiilen
         ölmüş ve bütün turlar reddediliyor. -->
    <section v-if="stuckTasks.length" class="stuck-notice">
      <strong>{{ t("panel.tasks.stuckTitle") }}</strong>
      <p>{{ t("panel.tasks.stuckBody") }}</p>
      <ul>
        <li v-for="item in stuckTasks" :key="item.name">
          <span>{{ item.name }}</span>
          <button class="secondary compact" :disabled="stoppingName === item.name" @click="stopStuck(item)">
            {{ t("panel.tasks.stopStuck") }}
          </button>
        </li>
      </ul>
    </section>

    <div class="data-table">
      <div class="data-row table-head cron-row">
        <span>{{ t("panel.tasks.name") }}</span>
        <span>{{ t("panel.tasks.command") }}</span>
        <span>{{ t("panel.tasks.scheduleType") }}</span>
        <span>{{ t("panel.sites.state") }}</span>
        <span>{{ t("panel.tasks.lastResult") }}</span>
        <span>{{ t("panel.tasks.nextRun") }}</span>
        <span>{{ t("panel.tasks.lastRun") }}</span>
        <span>{{ t("panel.files.actions") }}</span>
      </div>
      <template v-for="item in filteredTasks" :key="item.name">
      <div class="data-row cron-row">
        <span class="cell-main">
          <strong>{{ item.name }}</strong>
          <small>{{ typeLabel(item.taskType) }}</small>
        </span>
        <span class="cell-main">
          <strong>{{ item.taskType === "url" ? item.url || item.arguments : item.command }}</strong>
          <small>{{ item.taskType === "url" ? "-" : item.arguments || "-" }}</small>
        </span>
        <span>{{ scheduleLabel(item.scheduleType) }}</span>
        <span :class="['status-pill', taskStateClass(item.state)]">{{ item.state || "-" }}</span>
        <!-- Kodun ne demek olduğu satırda yazılı; "neden ve ne yapmalı" ipucu üzerine gelince. -->
        <span :class="['status-pill', resultClass(item)]" :title="resultHint(item) || undefined">{{ resultLabel(item) }}</span>
        <span>{{ formatUtc(item.nextRunTimeUtc) }}</span>
        <span>{{ formatUtc(item.lastRunTimeUtc) }}</span>
        <span class="row-actions">
          <button class="secondary compact" :disabled="runningName === item.name" @click="runTask(item.name)">
            {{ runningName === item.name ? t("panel.tasks.running") : t("panel.tasks.runNow") }}
          </button>
          <button class="secondary compact" :disabled="togglingName === item.name" @click="toggleTask(item)">
            {{ isDisabled(item) ? t("panel.tasks.enableTask") : t("panel.tasks.disableTask") }}
          </button>
          <button class="danger compact" :disabled="deletingName === item.name" @click="removeTask(item.name)">
            {{ deletingName === item.name ? t("panel.database.removing") : t("panel.sites.delete") }}
          </button>
        </span>
      </div>

      <!-- Canlı bilgi, görevin KENDİ satırının hemen altında: kullanıcı gözünü tablonun başka bir
           yerine kaydırmadan ne olduğunu görür. -->
      <div v-if="runStates[item.name]" :class="['run-live', runClass(item.name)]">
        <span class="run-dot" :class="{ spinning: runStates[item.name].phase === 'running' || runStates[item.name].phase === 'starting' }" aria-hidden="true"></span>
        <span class="run-text">
          {{ runMessage(item.name) }}
          <!-- Görev Zamanlayıcı komut ÇIKTISINI saklamaz. Bunu söylememek, kullanıcının olmayan
               bir çıktıyı aramasına yol açardı. -->
          <small v-if="runStates[item.name].phase === 'done'">{{ t("panel.tasks.runNoOutput") }}</small>
        </span>
        <button class="ghost compact" @click="closeRunPanel(item.name)">{{ t("panel.tasks.runClose") }}</button>
      </div>
      </template>
      <p v-if="loading && filteredTasks.length === 0" class="empty-state">{{ t("panel.common.loading") }}</p>
      <p v-else-if="filteredTasks.length === 0" class="empty-state">{{ t("panel.tasks.empty") }}</p>
    </div>
  </section>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch, onUnmounted } from "vue";
import { useI18n } from "vue-i18n";
import { createScheduledTask, deleteScheduledTask, getScheduledTasks, getScheduledTaskStatus, runScheduledTask, setScheduledTaskEnabled, stopScheduledTask, type ScheduledTask } from "../api/cron";
import { getSites, type SiteRecord } from "../api/sites";
import { useConfirm } from "../components/ConfirmDialog.vue";
import { extractApiDetails, extractApiMessage, useAuthStore } from "../stores/auth";

const { t } = useI18n();
const { confirm } = useConfirm();
const authStore = useAuthStore();
// Müşteri/bayi yalnızca URL görevi kurar (backend de zorlar); tip seçici yalnız admin'de görünür.
// "script" tipi de bilinçli olarak yalnız yöneticide: planlı betik = sunucuda kod çalıştırmak.
const isAdmin = computed(() => authStore.session?.user?.role === "Administrator");
// Betik görevinin site/sunucu ayrımı yalnız formda yaşar; sunucuya "siteName dolu mu" olarak gider.
const scriptScope = ref<"server" | "site">("server");
const sites = ref<SiteRecord[]>([]);
const tasks = ref<ScheduledTask[]>([]);
const query = ref("");
const formOpen = ref(false);
const loading = ref(false);
const saving = ref(false);
const deletingName = ref("");
const runningName = ref("");
const togglingName = ref("");
const notice = ref("");
const noticeError = ref(false);
const form = reactive(defaultForm());
const isScriptForm = computed(() => isAdmin.value && form.taskType === "script");
const selectedSiteRoot = computed(() => {
  const site = sites.value.find((item) => item.name === form.siteName);
  return site ? site.applicationRootPath || site.physicalPath : "";
});
const commandFieldLabel = computed(() => {
  if (form.taskType === "php") return t("panel.tasks.scriptPath");
  if (form.taskType === "script") return t("panel.tasks.scriptFile");
  return t("panel.tasks.command");
});
const commandFieldPlaceholder = computed(() => {
  if (form.taskType === "php") return "C:\\inetpub\\wwwroot\\site\\httpdocs\\cron.php";
  // Site seçiliyse yolun o sitenin kökünden başlaması gerektiğini ipucu olarak göster.
  if (form.taskType === "script") return (selectedSiteRoot.value || "C:\\inetpub\\wwwroot\\site") + "\\cron.bat";
  return "powershell.exe";
});
const formComplete = computed(() => {
  if (form.taskType === "url") return Boolean(form.url);
  if (form.taskType === "script" && scriptScope.value === "site" && !form.siteName) return false;
  return Boolean(form.command);
});

// Tip değişince betiğe uymayan varsayılanları temizle: "powershell.exe" bir betik yolu değildir ve
// betik dışı tiplerde site/kimlik alanları gönderilmemeli (sunucu da yok sayar, ama istek temiz kalsın).
watch(() => form.taskType, (taskType) => {
  if (taskType === "script") {
    if (form.command === "powershell.exe") {
      form.command = "";
    }

    if (sites.value.length === 0 && isAdmin.value) {
      void loadSites();
    }

    return;
  }

  scriptScope.value = "server";
  form.siteName = "";
  form.runAs = "system";
});

// Sunucu betiğine dönülünce site ve havuz kimliği seçimi geçersizdir; sunucuya sızmasın.
watch(scriptScope, (scope) => {
  if (scope === "server") {
    form.siteName = "";
    form.runAs = "system";
  }
});

const filteredTasks = computed(() => {
  const needle = query.value.toLocaleLowerCase();
  return needle
    ? tasks.value.filter((item) => [item.name, item.command, item.arguments, item.state]
        .some((value) => value?.toLocaleLowerCase().includes(needle)))
    : tasks.value;
});

// Arama kutusu takılı görevi gizlemesin: uyarı TÜM görevlere bakar, süzülmüş listeye değil.
const stuckTasks = computed(() => tasks.value.filter(isStuck));

onMounted(() => {
  if (!isAdmin.value) {
    form.taskType = "url";
  }
  void refresh();
});

async function refresh() {
  loading.value = true;
  try {
    tasks.value = await getScheduledTasks();
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    loading.value = false;
  }
}

async function saveTask() {
  saving.value = true;
  try {
    await createScheduledTask({ ...form });
    await refresh();
    resetForm();
    formOpen.value = false;
    showNotice(t("panel.tasks.saved"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    saving.value = false;
  }
}

async function removeTask(name: string) {
  const task = tasks.value.find((item) => item.name === name);
  const impact = [`${t("panel.tasks.name")}: ${name}`];
  if (task?.command) {
    impact.push(`${t("panel.tasks.command")}: ${task.command}`);
  }

  // Normal onay: tek bir zamanlanmış görev silinir, veri/dosya kaybı yok → ad yazdırmaya gerek yok.
  await confirm({
    title: t("panel.confirm.deleteTaskTitle", { name }),
    message: t("panel.tasks.confirmDelete"),
    impact,
    action: async () => {
      deletingName.value = name;
      try {
        await deleteScheduledTask(name);
        await refresh();
        showNotice(t("panel.tasks.deleted"), false);
      } catch (error) {
        showNotice(resolveApiMessage(error), true);
      } finally {
        deletingName.value = "";
      }
    },
  });
}

function defaultForm() {
  return {
    name: "",
    taskType: "command",
    command: "powershell.exe",
    arguments: "",
    workingDirectory: "",
    url: "",
    phpVersion: "",
    siteName: "",
    runAs: "system",
    scheduleType: "hourly",
    interval: 1,
    timeOfDay: "00:00"
  };
}

function resetForm() {
  Object.assign(form, defaultForm());
  scriptScope.value = "server";
}

// Site listesi yalnız betik görevinin site modu için gerekir; form açılana kadar istek atma.
async function loadSites() {
  try {
    sites.value = await getSites();
  } catch {
    // Site listesi alınamazsa betik formu yine kullanılabilir (yol elle yazılır); sessiz geç.
  }
}

// Görevi hemen tetikle; birkaç saniye sonra listeyi yenile ki "son çalışma" sütunu sonucu göstersin.
/**
 * "Şimdi çalıştır" sonrası CANLI bilgi.
 *
 * Öncesinde düğmeye basmak sessizdi: görev tetikleniyor, sayfa 4 saniye sonra bir kez tazeleniyordu.
 * 10 saniye süren bir görevde kullanıcı hiçbir şey görmüyor, bittiğini de nereden anlayacağını
 * bilmiyordu. Artık görevin kendi satırının altında "çalışıyor → bitti: <sonuç>" yazıyor.
 */
type RunState = {
  phase: "starting" | "running" | "done" | "unknown";
  seconds: number;
  resultCode: number | null;
};

const runStates = reactive<Record<string, RunState>>({});

// Aynı görev iki kez başlatılırsa ESKİ takip durmalı: yoksa iki döngü aynı satıra yazar.
const runTimers: Record<string, number> = {};

function stopWatching(name: string) {
  if (runTimers[name]) {
    window.clearInterval(runTimers[name]);
    delete runTimers[name];
  }
}

function closeRunPanel(name: string) {
  stopWatching(name);
  delete runStates[name];
}

async function runTask(name: string) {
  runningName.value = name;
  stopWatching(name);
  runStates[name] = { phase: "starting", seconds: 0, resultCode: null };

  try {
    await runScheduledTask(name);
    watchRun(name);
  } catch (error) {
    delete runStates[name];
    showNotice(resolveApiMessage(error), true);
  } finally {
    runningName.value = "";
  }
}

/**
 * Görev bitene kadar durumunu izler.
 *
 * Sonsuza kadar sormaz: bir üst sınır vardır ve dolduğunda "hâlâ çalışıyor" der. Süresiz bir
 * yoklama, sekme açık kaldığı sürece sunucuda saniyede bir PowerShell çalıştırmak demekti.
 */
function watchRun(name: string) {
  const POLL_MS = 2500;
  const MAX_SECONDS = 300;
  const basladi = Date.now();
  let sorguSuruyor = false;

  runTimers[name] = window.setInterval(() => {
    const gecen = Math.round((Date.now() - basladi) / 1000);
    const durum = runStates[name];
    if (!durum) {
      stopWatching(name);
      return;
    }

    durum.seconds = gecen;

    if (gecen >= MAX_SECONDS) {
      durum.phase = "running";
      stopWatching(name);
      void refresh();
      return;
    }

    // Bir önceki sorgu hâlâ sürüyorsa yenisini açma: yavaş bir sunucuda sorgular üst üste binerdi.
    if (sorguSuruyor) return;
    sorguSuruyor = true;

    void getScheduledTaskStatus(name)
      .then((status) => {
        if (!runStates[name]) return;
        if (status.isRunning) {
          runStates[name].phase = "running";
          return;
        }

        runStates[name].phase = "done";
        runStates[name].resultCode = status.lastTaskResult;
        stopWatching(name);
        void refresh();
      })
      .catch(() => {
        // Durum okunamadı: "bitti, başarılı" demek YANLIŞ olurdu — bilmediğimizi söyleriz.
        if (!runStates[name]) return;
        runStates[name].phase = "unknown";
        stopWatching(name);
      })
      .finally(() => {
        sorguSuruyor = false;
      });
  }, POLL_MS);
}

/** Canlı satırda gösterilecek metin. */
function runMessage(name: string) {
  const durum = runStates[name];
  if (!durum) return "";
  if (durum.phase === "starting") return t("panel.tasks.runStarting");
  if (durum.phase === "unknown") return t("panel.tasks.runUnknown");
  if (durum.phase === "running") {
    return durum.seconds >= 300
      ? t("panel.tasks.runStillGoing", { seconds: durum.seconds })
      : t("panel.tasks.runLive", { seconds: durum.seconds });
  }

  // Bitiş: kodu ham göstermek yerine, tablodakiyle AYNI sözlükten geçir.
  const sonuc = resultLabelForCode(durum.resultCode, tasks.value.find((item) => item.name === name)?.taskType ?? "command");
  return t("panel.tasks.runFinished", { seconds: durum.seconds, result: sonuc });
}

function runClass(name: string) {
  const durum = runStates[name];
  if (!durum) return "";
  if (durum.phase === "done") return durum.resultCode === 0 ? "ok" : "warn";
  if (durum.phase === "unknown") return "warn";
  return "";
}

onUnmounted(() => {
  // Sayfadan çıkılınca yoklama sürmemeli.
  Object.keys(runTimers).forEach(stopWatching);
});

function isDisabled(item: ScheduledTask) {
  return item.state.toLocaleLowerCase().includes("disabled");
}

async function toggleTask(item: ScheduledTask) {
  togglingName.value = item.name;
  try {
    await setScheduledTaskEnabled(item.name, isDisabled(item));
    await refresh();
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    togglingName.value = "";
  }
}

// Kullanıcının GERÇEKTEN gördüğü kodlar. Ham onaltılık bir sayı göstermek, sorunu kullanıcının
// arama motoruna havale etmektir: "Hata (0x800710E0)" satırı hiçbir şey anlatmıyordu.
const SKIPPED = 2148007648; // 0x800710E0 — önceki tur hâlâ sürerken yeni tur reddedildi
const TERMINATED = 267014; // 0x41306 — süre sınırı ya da elle durdurma

// curl çıkış kodları: SADECE "url" tipinde geçerlidir. Aynı küçük sayılar bir .bat ya da .exe
// görevinde o programın kendi çıkış kodudur ve curl'ün anlamıyla okumak yanlış olur.
const CURL_CODES: Record<number, string> = {
  6: "panel.tasks.resultUrlDns",
  7: "panel.tasks.resultUrlConnect",
  22: "panel.tasks.resultUrlHttp",
  28: "panel.tasks.resultUrlTimeout",
  35: "panel.tasks.resultUrlTls",
  60: "panel.tasks.resultUrlTls"
};

// Son çalışma sonucu: 0 = başarılı; 267009/267011/267045 = koşuyor/hiç çalışmadı/kuyrukta (nötr).
//
// TEK sözlük: tablodaki rozet de "şimdi çalıştır" sonrası canlı satır da buradan geçer. İki ayrı
// yerde çevirmek, birini güncelleyip diğerini unutmanın kestirme yoludur.
function resultLabelForCode(code: number | null | undefined, taskType: string) {
  if (code === null || code === undefined || code === 267011) return "-";
  if (code === 0) return t("panel.tasks.resultOk");
  if (code === 267009 || code === 267045) return t("panel.tasks.running");
  if (code === SKIPPED) return t("panel.tasks.resultSkipped");
  if (code === TERMINATED) return t("panel.tasks.resultTerminated");
  if (code === 2 || code === 3) return t("panel.tasks.resultNotFound");
  if (taskType === "url" && CURL_CODES[code]) return t(CURL_CODES[code]);
  return t("panel.tasks.resultFail", { code: "0x" + code.toString(16).toUpperCase() });
}

function resultLabel(item: ScheduledTask) {
  return resultLabelForCode(item.lastTaskResult, item.taskType);
}

/** Satırın altında gösterilen "peki ne yapmalıyım" cümlesi. Yoksa boş döner. */
function resultHint(item: ScheduledTask) {
  const code = item.lastTaskResult;
  if (code === SKIPPED) return t("panel.tasks.resultSkippedHint");
  if (code === 2 || code === 3) return t("panel.tasks.resultNotFoundHint");
  return "";
}

/**
 * Görev TAKILI KALMIŞ mı: hâlâ "çalışıyor" görünüyor ve son sonucu "atlandı".
 *
 * Bu ikisi bir arada, tek bir turun bitmediğini ve o yüzden sonraki bütün turların reddedildiğini
 * söyler — görev fiilen ölmüştür ama panel bunu "çalışıyor" diye gösterdiği için sorun sağlıklı
 * görünür. Kullanıcının canlı sunucusunda bir .bat görevi tam olarak bu durumdaydı.
 */
function isStuck(item: ScheduledTask) {
  return item.lastTaskResult === SKIPPED && item.state?.toLocaleLowerCase() === "running";
}

function resultClass(item: ScheduledTask) {
  const code = item.lastTaskResult;
  if (code === 0) return "ok";
  if (code === null || code === undefined || code === 267011 || code === 267009 || code === 267045) return "";
  return "warn";
}

// Takılı çalışmayı durdur: görev bir sonraki zamanında normal çalışmaya döner.
const stoppingName = ref("");
async function stopStuck(item: ScheduledTask) {
  stoppingName.value = item.name;
  try {
    await stopScheduledTask(item.name);
    showNotice(t("panel.tasks.stopStuckDone"), false);
    await refresh();
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    stoppingName.value = "";
  }
}

function typeLabel(value: string) {
  const key = value === "url"
    ? "panel.tasks.typeUrl"
    : value === "php"
      ? "panel.tasks.typePhp"
      : value === "script"
        ? "panel.tasks.typeScript"
        : "panel.tasks.typeCommand";
  return t(key);
}

function scheduleLabel(value: string) {
  const key = value === "minute" ? "panel.tasks.minutely" : value === "daily" ? "panel.tasks.daily" : "panel.tasks.hourly";
  return t(key);
}

function taskStateClass(value: string) {
  const state = value.toLocaleLowerCase();
  return state.includes("ready") || state.includes("running") ? "ok" : state.includes("disabled") ? "warn" : "";
}

function formatUtc(value: string | null) {
  return value ? new Date(value).toLocaleString() : "-";
}

function showNotice(message: string, isError: boolean) {
  notice.value = message;
  noticeError.value = isError;
}

function resolveApiMessage(error: unknown) {
  const key = extractApiMessage(error);
  const translated = t(key);
  const message = translated === key ? key : translated;
  // Gerçek teknik nedeni (backend Details) da göster: "kaydedilemedi" tek başına teşhis edilemiyor.
  const details = extractApiDetails(error);
  return details ? `${message} — ${details}` : message;
}
</script>

<style scoped>
.cron-row {
  grid-template-columns: 1.1fr 1.6fr 0.65fr 0.7fr 0.8fr 0.95fr 0.95fr 1.25fr;
}

.span-2 {
  grid-column: span 2;
}

.field-hint {
  color: var(--text-muted);
  font-size: 0.78rem;
}

.search-control {
  width: min(300px, 50vw);
}

.stuck-notice {
  border: 1px solid var(--warn-border, #e0b400);
  border-radius: 8px;
  padding: 0.7rem 1rem;
  margin-bottom: 0.9rem;
  background: var(--warn-bg, rgba(224, 180, 0, 0.08));
}

.stuck-notice p {
  margin: 0.35rem 0 0;
  font-size: 0.86rem;
}

.stuck-notice ul {
  margin: 0.6rem 0 0;
  padding: 0;
  list-style: none;
  display: grid;
  gap: 0.4rem;
}

.run-live {
  display: flex;
  align-items: flex-start;
  gap: 0.6rem;
  padding: 0.5rem 0.9rem;
  border-left: 3px solid var(--accent-1, #4b7bec);
  background: color-mix(in srgb, var(--accent-1, #4b7bec) 7%, transparent);
  font-size: 0.86rem;
}

.run-live.ok {
  border-left-color: var(--success-1, #2e9e5b);
  background: color-mix(in srgb, var(--success-1, #2e9e5b) 8%, transparent);
}

.run-live.warn {
  border-left-color: var(--warn-border, #e0b400);
  background: var(--warn-bg, rgba(224, 180, 0, 0.08));
}

.run-text {
  flex: 1;
}

.run-text small {
  display: block;
  opacity: 0.75;
  margin-top: 0.2rem;
}

.run-dot {
  width: 0.62rem;
  height: 0.62rem;
  margin-top: 0.32rem;
  border-radius: 50%;
  background: currentColor;
  opacity: 0.55;
  flex: none;
}

/* Dönen halka yalnızca çalışırken; bittiğinde hareket durur ki "bitti" gözle anlaşılsın. */
.run-dot.spinning {
  background: none;
  border: 2px solid currentColor;
  border-top-color: transparent;
  opacity: 0.8;
  animation: run-spin 0.9s linear infinite;
}

@keyframes run-spin {
  to { transform: rotate(360deg); }
}

/* Hareketi azalt ayarı açıksa dönme yerine yanıp sönme bile yok: sabit halka. */
@media (prefers-reduced-motion: reduce) {
  .run-dot.spinning { animation: none; }
}

.stuck-notice li {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 1rem;
  flex-wrap: wrap;
}
</style>
