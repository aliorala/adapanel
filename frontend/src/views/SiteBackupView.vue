<template>
  <section class="panel backup-view">
    <div class="bk-header">
      <div>
        <p class="section-kicker">{{ t("panel.backup.siteKicker") }}</p>
        <h2>{{ t("panel.backup.siteTitle") }}</h2>
        <p class="meta">{{ siteName || t("panel.php.selectSite") }}</p>
      </div>
      <button class="secondary compact" :disabled="loading" @click="refresh">{{ t("panel.backup.refresh") }}</button>
    </div>

    <p v-if="notice" :class="['notice', 'page-notice', { error: noticeError }]" :role="noticeError ? 'alert' : 'status'">{{ notice }}</p>

    <div v-if="siteName" class="form-surface bk-create">
      <h3>{{ t("panel.backup.createTitle") }}</h3>
      <div class="scope-grid">
        <label class="chk"><input v-model="scope.includeFiles" type="checkbox" /> {{ t("panel.backup.scopeFiles") }}</label>
        <label class="chk"><input v-model="scope.includeConfig" type="checkbox" /> {{ t("panel.backup.scopeConfig") }}</label>
        <label class="chk"><input v-model="scope.includeMail" type="checkbox" /> {{ t("panel.backup.scopeMail") }}</label>
      </div>

      <SectionLoadError
        v-if="databasesLoad.message.value"
        :message="databasesLoad.message.value"
        @retry="loadDatabases" />

      <div v-if="availableDatabases.length > 0" class="db-block">
        <span class="field-label">{{ t("panel.backup.scopeDatabases") }}</span>
        <div class="scope-grid">
          <label v-for="db in availableDatabases" :key="`${db.engine}:${db.name}`" class="chk">
            <input v-model="selectedDbKeys" type="checkbox" :value="`${db.engine}:${db.name}`" />
            {{ db.name }} <small>({{ db.engine }})</small>
          </label>
        </div>
      </div>

      <!--
        ARTIMSAL / TAM seçimi. Sunucu bunu ZATEN destekliyordu (`BackupSiteRequest` →
        `BackupArchiveRequest.Incremental` + `FullBackupEveryDays`, aralık 1..365) ve
        `api/backup.ts` alanları gönderiyordu; bu ekran seçeneği GÖSTERMİYORDU, yani kullanıcı
        yalnız tam yedek alabiliyordu. "Mümkünse artımsal": ilk yedek, tam yedek periyodu
        dolduğunda ve temel indeks okunamadığında yine TAM alınır.
      -->
      <div class="scope-grid incremental-row">
        <label class="chk">
          <input v-model="scope.incremental" type="checkbox" /> {{ t("panel.backup.incremental") }}
        </label>
        <label v-if="scope.incremental" class="stacked-field full-every">
          <span class="field-label">{{ t("panel.backup.schedule.fullEvery") }}</span>
          <input v-model.number="scope.fullBackupEveryDays" type="number" min="1" max="365" />
        </label>
      </div>
      <p v-if="scope.incremental" class="field-hint">{{ t("panel.backup.incrementalHint") }}</p>

      <!-- Bu yedeğe özel şifreleme parolası. Boş = panelde kayıtlı parola; o da yoksa şifresiz.
           UYARI parola BELİRLENİRKEN görünür, geri yükleme anında değil. -->
      <label class="stacked-field">
        <span class="field-label">{{ t("panel.backup.perBackupPassword") }}</span>
        <PasswordField v-model="encryptionPassword" autocomplete="new-password" />
        <small class="field-hint">{{ t("panel.backup.perBackupPasswordHint") }}</small>
      </label>
      <p v-if="encryptionPassword" class="notice encryption-loss" role="status">
        {{ t("panel.backup.encryptionLossWarning") }}
      </p>

      <!--
        ŞİFRELİ + ARTIMSAL: parola kaybı TEK HALKAYI değil ZİNCİRİN TAMAMINI götürür (tam yedek
        dahil). Uyarı, kararın VERİLDİĞİ yerde durur: artımsal işaretliyken görünür. Parola
        alanının boş olması riski kaldırmaz — panelde KAYITLI bir parola varsa zincir yine
        şifreli alınır ve bu ekran o kaydı okuyamaz (uç yalnız yöneticiye açık), bu yüzden uyarı
        parolaya bakılmadan gösterilir.
      -->
      <p v-if="scope.incremental" class="notice encryption-loss" role="status">
        {{ t("panel.backup.encryptionChainWarning") }}
      </p>

      <!-- Yedek ALINIRKEN ekran donmuş görünmesin: uç işi arka plana almıyor, istek uçuştayken
           yedek GERÇEKTEN sürüyor (bkz. composables/useRunningTask.ts).

           ŞERİT İKİ KAYNAKTAN BESLENİR: bu sekmede başlatılan iş (runningBackup) ve SUNUCUDA
           süren iş (/backup/running). İkincisi sayfa yenilendikten sonra da doludur — eskiden
           yenileme "sürüyor" bilgisini tamamen siliyordu. -->
      <RunningTaskBanner
        :running="bannerRunning"
        :label="bannerLabel"
        :elapsed-text="bannerElapsedText"
        :hint="bannerHint"
        :interrupted-label="serverBackup.interruptedText.value"
        @dismiss="serverBackup.dismissInterrupted" />

      <button class="compact" v-bind="writeGate(busy)" @click="createBackup">
        {{ busy ? t("panel.backup.creating") : t("panel.backup.createButton") }}
      </button>
    </div>

    <div class="bk-list">
      <div class="section-head">
        <h3>{{ t("panel.backup.listTitle") }}</h3>
        <span class="meta">{{ listLoaded ? siteBackups.length : "…" }}</span>
      </div>

      <!--
        GÜN GRUPLARI (Yedekler ekranıyla AYNI kaynaktan: composables/useDayGroups.ts). Gruplama
        `createdAtUtc`ten KULLANICININ YEREL gününe çevrilerek yapılır — dosya yolu
        ayrıştırılmaz, böylece gün klasörü olmadan alınmış eski yedekler de doğru günde durur.
      -->
      <div v-if="siteBackups.length > 0" class="bk-days">
        <section v-for="group in dayGroups" :key="group.key" class="bk-day">
          <header class="bk-day-head">
            <button
              type="button"
              class="day-toggle"
              :aria-expanded="isDayOpen(group.key)"
              @click="toggleDay(group.key)">
              <span class="day-caret" aria-hidden="true">{{ isDayOpen(group.key) ? "▾" : "▸" }}</span>
              <strong>{{ group.label }}</strong>
            </button>
            <span class="bk-day-summary">
              <span class="status-pill">{{ t("panel.backup.day.count", { count: group.items.length }) }}</span>
              <span class="status-pill">{{ formatSize(dayBytes(group.items)) }}</span>
            </span>
          </header>

          <div v-if="isDayOpen(group.key)" class="bk-rows">
            <article v-for="record in group.items" :key="record.id" class="bk-row">
              <div class="bk-id">
                <strong>
                  {{ record.name }}
                  <!-- Kilit rozeti: geri yüklerken parola isteneceği LİSTEDE görünsün. -->
                  <span v-if="record.encrypted" class="status-pill" :title="t('panel.backup.encryptedHint')">
                    {{ t("panel.backup.encryptedBadge") }}
                  </span>
                  <!-- Zincirdeki YER: artımsal halka mı, zincirin tam yedeği mi. Zinciri olmayan
                       tekil yedekte rozet YOKTUR (yanlış bir bağ hissi vermesin). -->
                  <span v-if="record.chainId && record.backupMode === 'incremental'" class="status-pill">
                    {{ t("panel.backup.chain.linkBadge", { index: record.chainIndex }) }}
                  </span>
                  <span v-else-if="record.chainId" class="status-pill">
                    {{ t("panel.backup.chain.headBadge") }}
                  </span>
                </strong>
                <span class="meta">{{ formatDate(record.createdAtUtc) }} · {{ formatSize(record.size) }}</span>

                <!-- Yedeğin DİSKTEKİ yeri. Tam yol satırı bozmasın diye açılır bir alanda ve
                     KENDİ İÇİNDE yatay kayan bir kutuda; kısaltılmaz, çünkü kullanıcı bu yolu
                     kopyalayıp Dosya Gezgini'ne/FTP'ye yapıştıracak. -->
                <button
                  type="button"
                  class="secondary compact location-toggle"
                  :aria-expanded="expandedLocation === record.id"
                  @click="toggleLocation(record.id)">
                  {{ t("panel.backup.location.toggle") }}
                </button>
                <div v-if="expandedLocation === record.id" class="location-detail">
                  <div class="location-line">
                    <span class="field-label">{{ t("panel.backup.location.local") }}</span>
                    <code v-if="record.path" class="path-box">{{ record.path }}</code>
                    <span v-else class="meta">{{ t("panel.backup.location.unknown") }}</span>
                    <button
                      v-if="record.path"
                      type="button"
                      class="secondary compact"
                      @click="copyPath(record.path)">
                      {{ t("panel.backup.location.copy") }}
                    </button>
                  </div>
                  <!-- UZAK kopyanın adresi KİMLİKSİZ + İMZASIZ basılır (bkz. utils/url.ts):
                       S3 "presigned" hedefinde imza sorgu dizesinde durur ve bir ekran
                       görüntüsüyle paylaşılırsa hedef depoyu devretmiş olurduk. -->
                  <div v-if="record.remoteUri" class="location-line">
                    <span class="field-label">{{ t("panel.backup.location.remote") }}</span>
                    <code v-if="safeRemote(record.remoteUri).text" class="path-box">
                      {{ safeRemote(record.remoteUri).text }}
                    </code>
                    <span v-else class="meta">{{ t("panel.backup.location.remoteHidden") }}</span>
                  </div>
                  <p v-if="record.remoteUri && safeRemote(record.remoteUri).masked" class="field-hint">
                    {{ t("panel.backup.location.remoteMasked") }}
                  </p>
                  <!-- Kayıt dosyanın diskte HÂLÂ durup durmadığını taşımıyor (uç ölçmüyor):
                       "yerinde" demek ölçmediğimiz şeye güvenmek olurdu. -->
                  <p class="field-hint">{{ t("panel.backup.location.existenceUnknown") }}</p>
                </div>
              </div>
              <div class="bk-actions">
                <button class="secondary compact" :disabled="downloadingId === record.id" @click="download(record)">
                  {{ downloadingId === record.id ? t("panel.backup.downloading") : t("panel.backup.download") }}
                </button>
                <button class="secondary compact" v-bind="writeGate(restoringId === record.id)" @click="restore(record)">
                  {{ restoringId === record.id ? t("panel.backup.restoring") : t("panel.backup.restore") }}
                </button>
              </div>
            </article>
          </div>
        </section>
      </div>
      <!-- Yedek listesi gelmeden "yedek yok" yazıyordu; var olan yedekler yokmuş gibi görünüyordu. -->
      <p v-else-if="!listLoaded" class="empty-state" role="status" aria-live="polite">
        {{ t("panel.common.loading") }}
      </p>
      <p v-else class="empty-state">{{ t("panel.backup.empty") }}</p>
    </div>
  </section>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from "vue";
import { useI18n } from "vue-i18n";
import PasswordField from "../components/PasswordField.vue";
import { useSitesStore } from "../stores/sites";
import {
  backupSite,
  downloadBackup,
  getBackupRunningJobs,
  getBackups,
  restoreBackup,
  type BackupRecord
} from "../api/backup";
import { getDatabaseEngines, getDatabaseWorkspace } from "../api/databases";
import { useConfirm } from "../components/ConfirmDialog.vue";
import { usePrompt } from "../components/PromptDialog.vue";
import RunningTaskBanner from "../components/RunningTaskBanner.vue";
import SectionLoadError from "../components/SectionLoadError.vue";
import { compareNewestFirst, localDayKey, useDayGroups } from "../composables/useDayGroups";
import { useNotice, useSectionError } from "../composables/useNotice";
import { useSubscriptionGate } from "../composables/useSubscriptionGate";
import { runningJobLabel, useRunningTask, useServerRunningTask } from "../composables/useRunningTask";
import { copyToClipboard } from "../utils/clipboard";
import { maskSecretsInUri } from "../utils/url";
import { formatBytes } from "../utils/bytes";
import { formatDateTime } from "../utils/datetime";

const { t } = useI18n();
const { confirm } = useConfirm();
const { prompt } = usePrompt();
// Askidaki abonelikte YAZMA dugmeleri basilmadan once pasiflesir ve sebebini soyler
// (bkz. composables/useSubscriptionGate.ts). Okuma dugmeleri (yenile, indir) kapiya girmez.
const { writeGate } = useSubscriptionGate();
const sitesStore = useSitesStore();
const siteName = computed(() => sitesStore.selectedSiteName);

const loading = ref(false);
// Yüklenmemiş liste "yedek yok" olarak görünüyordu.
const listLoaded = ref(false);
const busy = ref(false);
const downloadingId = ref("");
const restoringId = ref("");
// TEK KAYNAK: bu ekran ham `extractApiMessage` basıyordu (4 nokta) — kullanıcı çevrilmemiş
// anahtar görüyordu.
const { notice, noticeError, showNotice, resolveApiMessage } = useNotice();
const backups = ref<BackupRecord[]>([]);
const availableDatabases = ref<{ engine: string; name: string }[]>([]);
// Boş liste "yedeklenecek veritabanı yok" demektir; okunamadıysa AYRI söylenir — aksi hâlde
// kullanıcı veritabanı seçmeden yedek alıp yedeğini eksik sanmadan taşırdı.
const databasesLoad = useSectionError();
const selectedDbKeys = ref<string[]>([]);

/**
 * Yedek kapsamı + ARTIMSAL seçimi.
 *
 * `incremental` / `fullBackupEveryDays` sunucu sözleşmesinde ZATEN vardı
 * (`BackupSiteRequest : BackupArchiveRequest`; `FullBackupEveryDays` için `[Range(1, 365)]`) ve
 * `api/backup.ts` alanları gönderiyordu — eksik olan yalnızca bu ekrandaki seçimdi.
 */
const scope = reactive({
  includeFiles: true,
  includeConfig: true,
  includeMail: false,
  incremental: false,
  fullBackupEveryDays: 7
});
// Bu yedeğe özel şifreleme parolası (boş = panelde kayıtlı parola, o da yoksa şifresiz).
const encryptionPassword = ref("");

// Süren yedek GÖRÜNÜR: uç işi arka plana almadığı için istek uçuştayken yedek gerçekten sürüyor.
const runningBackup = useRunningTask();

/*
 * SAYFA YENİLENDİĞİNDE "YEDEK SÜRÜYOR" ARTIK KAYBOLMUYOR.
 *
 * ÖNCESİ: durum yalnız bu sekmenin belleğindeydi. Kullanıcı büyük bir siteyi yedeklemeye başlayıp
 * sayfayı yenilediğinde panel "iş yok" diyordu; yedek sunucuda sürerken ekran boş görünüyor,
 * kullanıcı ikinci bir yedek başlatmaya çalışıyordu.
 *
 * ŞİMDİ: sunucu yedek/geri yükleme süresince kaydı `/backup/running` ucundan veriyor; ekran
 * açılır açılmaz okunuyor ve şerit oradan besleniyor. Süre SUNUCUNUN verdiği başlangıç anından
 * hesaplanıyor, bu sekmenin `begin()` anından değil.
 */
const serverBackup = useServerRunningTask({
  fetchStatus: getBackupRunningJobs,
  label: (job) => runningJobLabel(t, job),
  interruptedLabel: (job) =>
    t("panel.runningJob.interrupted", { job: runningJobLabel(t, job), date: formatDate(job.startedAtUtc) })
});

// Bu sekmede başlatılan iş varsa ONUN sayacı gösterilir; yoksa sunucudan okunan iş.
const bannerRunning = computed(() => runningBackup.running.value || serverBackup.running.value);
const bannerLabel = computed(() =>
  runningBackup.running.value ? runningBackup.label.value : serverBackup.label.value);
const bannerElapsedText = computed(() =>
  runningBackup.running.value ? runningBackup.elapsedText.value : serverBackup.elapsedText.value);
// Sunucudan okunan iş bu sekmede başlatılmadı: "sayfadan ayrılmayın" demek yanlış olurdu.
const bannerHint = computed(() =>
  runningBackup.running.value ? "" : t("panel.runningJob.resumedHint"));
const { groupByDay } = useDayGroups();

/** Yolu açık olan kayıt (tek seferde bir tane): tam yollar uzun, hepsi birden açık kalmasın. */
const expandedLocation = ref("");

/**
 * Bu sitenin yedekleri, zamana göre AZALAN.
 *
 * Sıralama burada da yapılır: uç şu an `createdAtUtc` azalan döndürüyor, ama gün gruplamasının
 * doğruluğu (bir günün listede TEK bir grup olması) sunucunun sıralamasına bırakılamaz.
 */
const siteBackups = computed(() =>
  backups.value
    .filter((record) => record.type === "site" && record.source === siteName.value)
    .sort((left, right) => compareNewestFirst(left.createdAtUtc, right.createdAtUtc)));

const dayGroups = computed(() => groupByDay(siteBackups.value, (record) => record.createdAtUtc));

function dayBytes(records: BackupRecord[]) {
  return records.reduce((total, record) => total + record.size, 0);
}

/**
 * KAPALI gün anahtarları tutulur, açık olanlar değil: en yeni gün AÇIK başlamalı ve yarın
 * alınan ilk yedek de kendiliğinden açık bir günün altına düşmeli.
 */
const collapsedDays = ref(new Set<string>());
const dayDefaultsApplied = ref(false);

function isDayOpen(key: string) {
  return !collapsedDays.value.has(key);
}

function toggleDay(key: string) {
  // Set YERİNDE değiştirilirse `ref` tetiklenmez; yeni bir Set atanır.
  const next = new Set(collapsedDays.value);
  if (next.has(key)) {
    next.delete(key);
  } else {
    next.add(key);
  }
  collapsedDays.value = next;
}

// En yeni gün açık, geri kalanı kapalı — YALNIZ ilk yüklemede. Her yenilemede uygulanmak,
// kullanıcının elle açtığı eski günü her yenilemede kapatmak olurdu.
watch(siteBackups, (records) => {
  if (dayDefaultsApplied.value || records.length === 0) {
    return;
  }

  const keys = [...new Set(records.map((record) => localDayKey(record.createdAtUtc)))];
  collapsedDays.value = new Set(keys.slice(1));
  dayDefaultsApplied.value = true;
}, { immediate: true });

function toggleLocation(recordId: string) {
  expandedLocation.value = expandedLocation.value === recordId ? "" : recordId;
}

/** Uzak adresi kimliksiz + imzasız gösterime çevirir (bkz. utils/url.ts). */
function safeRemote(remoteUri: string) {
  return maskSecretsInUri(remoteUri);
}

async function copyPath(value: string) {
  if (!value) {
    return;
  }

  // Kopyalanmadıysa "kopyalandı" DEMEYİZ: `copyToClipboard` gerçekten kopyalayıp kopyalamadığını
  // döndürür (HTTP üzerinde pano izni verilmeyebilir).
  const copied = await copyToClipboard(value);
  showNotice(copied ? t("panel.common.copied") : t("panel.common.copyFailed"), !copied);
}

onMounted(async () => {
  await refresh();
  await loadDatabases();
  // Sunucuda süren iş varsa şerit ekran açılır açılmaz dolar (yenilenmiş sayfa dahil).
  await serverBackup.start();
});

/*
 * SUNUCUDAKİ İŞ BİTTİĞİNDE LİSTE TAZELENİR.
 *
 * Bu sekme işi kendisi başlatmadıysa (sayfa yenilenmişti) sonucu isteyecek açık bir isteği yok;
 * liste tazelenmezse yeni yedek ekranda HİÇ görünmez ve kullanıcı yedeğin alınmadığını sanır.
 */
watch(
  () => serverBackup.job.value,
  async (yeni, eski) => {
    if (eski && !yeni && !busy.value) {
      await refresh();
    }
  });

async function refresh() {
  loading.value = true;
  try {
    backups.value = await getBackups();
    listLoaded.value = true;
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    loading.value = false;
  }
}

async function loadDatabases() {
  try {
    const engines = await getDatabaseEngines();
    const list: { engine: string; name: string }[] = [];
    for (const engine of engines.filter((item) => item.installed)) {
      try {
        const workspace = await getDatabaseWorkspace(engine.engine);
        for (const db of workspace.databases ?? []) {
          list.push({ engine: engine.engine, name: db.name });
        }
      } catch {
        // motor erişilemezse atla
      }
    }
    availableDatabases.value = list;
    databasesLoad.clear();
  } catch (error) {
    availableDatabases.value = [];
    databasesLoad.capture(error);
  }
}

async function createBackup() {
  if (!siteName.value) {
    return;
  }
  busy.value = true;
  runningBackup.begin(`${t("panel.backup.running.creating")}: ${siteName.value}`);
  try {
    const databases = selectedDbKeys.value.map((key) => {
      const [engine, ...rest] = key.split(":");
      return { engine, databaseName: rest.join(":") };
    });
    await backupSite(siteName.value, {
      includeFiles: scope.includeFiles,
      includeConfig: scope.includeConfig,
      includeMail: scope.includeMail,
      databases,
      // "Mümkünse artımsal": sunucu ilk yedekte, tam yedek periyodu dolduğunda ve temel indeks
      // okunamadığında yine TAM yedek alır.
      incremental: scope.incremental,
      fullBackupEveryDays: scope.fullBackupEveryDays
    }, { encryptionPassword: encryptionPassword.value });
    // Parola tek seferliktir; formda asılı kalıp bir sonraki yedeğe sessizce taşınmaz.
    encryptionPassword.value = "";
    showNotice(t("panel.backup.created"), false);
    await refresh();
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    busy.value = false;
    runningBackup.end();
  }
}

async function download(record: BackupRecord) {
  downloadingId.value = record.id;
  try {
    await downloadBackup(record.id, record.name);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    downloadingId.value = "";
  }
}

async function restore(record: BackupRecord) {
  // Şifreli yedekte parola ÖNCE sorulur: yüksek riskli onayı geçtikten sonra "parola lazım"
  // demek, kullanıcıya site adını ikinci kez yazdırmak olurdu.
  let password = "";
  if (record.encrypted) {
    const entered = await prompt({
      title: t("panel.backup.restorePasswordTitle"),
      message: t("panel.backup.encryptionLossWarning"),
      label: t("panel.backup.encryptionPassword"),
      hint: t("panel.backup.restorePasswordHint"),
      password: true,
      allowEmpty: true,
      confirmLabel: t("panel.backup.restore")
    });
    if (entered === null) {
      return;
    }

    password = entered;
  }

  // Canlı siteyi geri yükleme yıkıcıdır — YÜKSEK RİSKLİ onay: üzerine yazılacak site adı yazılmadan devam edilemez.
  const target = record.source || siteName.value;
  const impact = [
    `${t("panel.files.siteLabel")}: ${target}`,
    `${t("panel.backup.name")}: ${record.name}`,
    `${t("panel.backup.size")}: ${formatSize(record.size)}`,
    `${t("panel.backup.createdAt")}: ${formatDate(record.createdAtUtc)}`
  ];

  await confirm({
    title: t("panel.confirm.restoreBackupTitle", { name: record.name }),
    message: t("panel.backup.restoreConfirm"),
    impact,
    requireText: target,
    confirmLabel: t("panel.backup.restore"),
    busyLabel: t("panel.backup.restoring"),
    action: async () => {
      restoringId.value = record.id;
      try {
        const restored = await restoreBackup(record.id, "", password);
        // Yarım kalan adımlar SÖYLENİR: koşulsuz "geri yüklendi" demek, veritabanı dönmemişken
        // kullanıcıya "tamam" dedirtirdi. Üzerine yazılan içeriğin taşındığı klasör de yazılır.
        const warnings = restored.warnings ?? [];
        const lines = [warnings.length > 0 ? t("panel.backup.restoredWithErrors") : t("panel.backup.restored")];
        if (warnings.length > 0) {
          lines.push(`${t("panel.backup.restoreWarningsTitle")}:`);
          for (const warning of warnings) {
            const translated = t(warning.messageKey);
            const message = translated === warning.messageKey ? warning.messageKey : translated;
            lines.push(warning.target ? `${warning.target}: ${message}` : message);
          }
        }
        if (restored.preservedPath) {
          lines.push(t("panel.backup.preservedPath", { path: restored.preservedPath }));
        }
        showNotice(lines.join("\n"), warnings.length > 0);
      } catch (error) {
        showNotice(resolveApiMessage(error), true);
      } finally {
        restoringId.value = "";
        password = "";
      }
    },
  });
}

/** Bayt bicimi panelin geri kalaniyla AYNI (bkz. utils/bytes.ts); ekran kendi bicimini uydurmaz. */
function formatSize(bytes: number): string {
  return formatBytes(bytes, t);
}

/** Tarih bicimi panelin geri kalaniyla AYNI (bkz. utils/datetime.ts). */
function formatDate(iso: string): string {
  return formatDateTime(iso);
}
</script>

<style scoped>
.backup-view,
.bk-create,
.bk-list {
  display: grid;
  gap: 14px;
}

.bk-header,
.bk-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
}

.scope-grid {
  display: flex;
  flex-wrap: wrap;
  gap: 12px;
}

.chk {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  font-size: 0.86rem;
}

.db-block {
  display: grid;
  gap: 8px;
}

.field-label {
  color: var(--text-muted);
  font-size: 0.74rem;
  font-weight: 700;
}

.bk-days {
  display: grid;
  gap: 12px;
}

.bk-day {
  display: grid;
  gap: 8px;
}

.bk-day-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
  flex-wrap: wrap;
  padding-bottom: 6px;
  border-bottom: 1px solid var(--panel-border);
}

/* Başlığın TAMAMI tıklanır: küçük bir oka isabet ettirmek dokunmatik ekranda zordu. */
.day-toggle {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  width: auto;
  padding: 2px 0;
  border: none;
  background: none;
  color: inherit;
  font: inherit;
  text-align: left;
  cursor: pointer;
}

.day-caret {
  width: 1em;
  color: var(--text-muted);
}

.bk-day-summary {
  display: flex;
  align-items: center;
  gap: 6px;
  flex-wrap: wrap;
}

.bk-rows {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.location-toggle {
  width: auto;
  justify-self: start;
  margin-top: 4px;
}

.location-detail {
  display: grid;
  gap: 6px;
  margin-top: 6px;
}

.location-line {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
}

.location-line button {
  width: auto;
}

/* Tam yol KISALTILMAZ (kopyalanıp yapıştırılacak) ama satırı da bozmaz: kutu KENDİ içinde
   yatay kayar. `nowrap` bilinçli — sarılmış bir yol yanlış okunuyor. */
.path-box {
  flex: 1 1 240px;
  min-width: 0;
  max-width: 100%;
  overflow-x: auto;
  padding: 4px 8px;
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-sm);
  background: var(--surface-2);
  font-family: ui-monospace, "Cascadia Code", Consolas, monospace;
  font-size: 0.8rem;
  white-space: nowrap;
}

.incremental-row {
  align-items: end;
}

.full-every {
  display: grid;
  gap: 4px;
  max-width: 200px;
}

.bk-row {
  padding: 10px 14px;
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-lg);
  background: var(--surface-1);
}

.bk-id {
  display: grid;
  gap: 2px;
  min-width: 0;
}

.bk-actions {
  display: flex;
  gap: 8px;
  flex-shrink: 0;
}

.meta {
  color: var(--text-muted);
}

/* Parola kaybı geri dönüşsüz: uyarı satırı gövde metninden ayrışsın. */
.encryption-loss {
  font-weight: 600;
}
</style>
