<template>
  <section class="panel module-shell migration-view">
    <header class="module-head">
      <div>
        <h2>{{ t("panel.migration.title") }}</h2>
        <p>{{ t("panel.migration.subtitle") }}</p>
      </div>
    </header>

    <!-- Sonuç ekrana SABİTLENİR (`page-notice`, bkz. styles/base.css): göç ekranı çok uzun
         (kaynak sekmeleri + ön kontroller + toplu liste + canlı akış günlüğü) ve kullanıcı
         "Taşı"ya bastığında sonuç şeridi sayfanın en tepesinde kalıyordu — görülmeyen bir sonuç,
         ikinci kez basılan bir düğme demektir. Göçte ikinci basış siteyi yeniden taşımaktır. -->
    <p v-if="notice" :class="['inline-notice', 'page-notice', noticeError ? 'error' : 'ok']" :role="noticeError ? 'alert' : 'status'">{{ notice }}</p>

    <!-- SAYFA YENİLENDİĞİNDE "GÖÇ SÜRÜYOR" KAYBOLMUYOR.
         Canlı adım akışı (SSE) yalnız o sekmeye aitti: yenileme hem adımları hem "sürüyor"
         bilgisini siliyordu, oysa göç sunucuda saatlerce devam ediyordu. Şerit sunucudaki
         çalışan-iş kaydından beslenir; panel iş sürerken yeniden başlamışsa "yarıda kaldı"
         uyarısı da buradan çıkar. -->
    <RunningTaskBanner
      :running="bannerRunning"
      :label="bannerLabel"
      :elapsed-text="bannerElapsedText"
      :hint="bannerHint"
      :interrupted-label="serverMigration.interruptedText.value"
      @dismiss="serverMigration.dismissInterrupted" />

    <div class="form-surface migration-how">
      <h3 class="section-label">{{ t("panel.migration.howTitle") }}</h3>
      <p class="meta create-hint">{{ t("panel.migration.howWhat") }}</p>
      <ol class="migration-steps">
        <li>{{ t("panel.migration.howStep1") }}</li>
        <li>{{ t("panel.migration.howStep2") }}</li>
        <li>{{ t("panel.migration.howStep3") }}</li>
        <li>{{ t("panel.migration.howStep4") }}</li>
        <li>{{ t("panel.migration.howStep5") }}</li>
      </ol>
    </div>

    <div class="form-surface plesk-discover">
      <h3 class="section-label">{{ t("panel.migration.discoverTitle") }}</h3>
      <p class="meta create-hint">{{ t("panel.migration.discoverHint") }}</p>

      <!-- Sekme etiketleri i18n'den gelir. Sabit kodlu metin, çeviri parite denetiminden kaçar ve
           dil değişince Türkçe kalır. Kaynak panelin ADI burada işlevseldir: kullanıcı taşımayı
           hangi panelden yaptığını seçebilmeli. -->
      <div class="segmented-tabs source-tabs" role="tablist">
        <button role="tab" :aria-selected="source === 'plesk'" type="button" :class="{ active: source === 'plesk' }" @click="source = 'plesk'">
          {{ t("panel.migration.sourcePlesk") }}
        </button>
        <button role="tab" :aria-selected="source === 'cpanel'" type="button" :class="{ active: source === 'cpanel' }" @click="source = 'cpanel'">
          {{ t("panel.migration.sourceCpanel") }}
        </button>
      </div>

      <div v-if="source === 'plesk'" class="form-grid">
        <label>
          <span class="field-label">{{ t("panel.migration.pleskHost") }}</span>
          <input v-model="plesk.host" placeholder="plesk.example.com" spellcheck="false" />
        </label>
        <label>
          <span class="field-label">{{ t("panel.migration.ftp.port") }}</span>
          <input v-model.number="plesk.port" type="number" />
        </label>
        <label>
          <span class="field-label">{{ t("panel.migration.pleskApiKey") }}</span>
          <PasswordField v-model="plesk.apiKey" spellcheck="false" autocomplete="off" />
        </label>
        <label>
          <span class="field-label">{{ t("panel.migration.pleskUser") }}</span>
          <input v-model="plesk.userName" placeholder="admin" spellcheck="false" autocomplete="off" />
        </label>
        <label>
          <span class="field-label">{{ t("panel.migration.pleskPassword") }}</span>
          <PasswordField v-model="plesk.password" autocomplete="off" />
        </label>
      </div>
      <p v-if="source === 'plesk'" class="meta create-hint">{{ t("panel.migration.pleskAuthHint") }}</p>

      <div v-else class="form-grid">
        <label>
          <span class="field-label">{{ t("panel.migration.cpanelHost") }}</span>
          <input v-model="cpanel.host" placeholder="whm.example.com" spellcheck="false" />
        </label>
        <label>
          <span class="field-label">{{ t("panel.migration.ftp.port") }}</span>
          <input v-model.number="cpanel.port" type="number" />
        </label>
        <label>
          <span class="field-label">{{ t("panel.migration.cpanelUser") }}</span>
          <input v-model="cpanel.userName" placeholder="root" spellcheck="false" />
        </label>
        <label>
          <span class="field-label">{{ t("panel.migration.cpanelApiToken") }}</span>
          <PasswordField v-model="cpanel.apiToken" spellcheck="false" />
        </label>
      </div>

      <div class="row-actions">
        <button class="secondary compact" :disabled="discovering || !canDiscover" @click="discover">
          {{ discovering ? t("panel.migration.discovering") : t("panel.migration.discover") }}
        </button>
      </div>

      <!--
        ÜÇ DURUM AYRI: taranıyor / hiç alan adı yok / okunamadı.
        Önceden yalnız "dolu liste" çiziliyordu; tarama sürerken de, kaynak panelde hiç alan adı
        yokken de, bağlantı reddedildiğinde de ekranda AYNI hiçlik vardı — kullanıcı düğmenin
        çalışıp çalışmadığını bile bilemiyordu.
      -->
      <p v-if="discovering" class="empty-state" role="status" aria-live="polite">
        {{ t("panel.migration.discovering") }}
      </p>

      <SectionLoadError v-else-if="discoverError.message.value" :message="discoverError.message.value" @retry="discover" />

      <p v-else-if="discoverRan && discoveredDomains.length === 0" class="empty-state">
        {{ t("panel.migration.noDomains") }}
      </p>

      <div v-else-if="discoveredDomains.length" class="discovered-list">
        <p class="meta">{{ t("panel.migration.discovered", { count: discoveredDomains.length }) }}</p>
        <div class="domain-rows">
          <div v-for="d in discoveredDomains" :key="d.name" class="domain-row">
            <label class="domain-pick">
              <input type="checkbox" :value="d.name" v-model="selectedDomains" />
              <span class="domain-name">{{ d.name }}</span>
              <span v-if="d.hostingType" class="meta">{{ d.hostingType }}</span>
            </label>
            <button type="button" class="secondary compact" @click="pickDomain(d.name)">{{ t("panel.migration.useSingle") }}</button>
          </div>
        </div>

        <div class="bulk-box">
          <label class="stacked-field">
            <span class="field-label">{{ t("panel.migration.bulkPathTemplate") }}</span>
            <input v-model="bulkPathTemplate" spellcheck="false" />
            <small class="field-hint">{{ t("panel.migration.bulkPathHint") }}</small>
          </label>
          <p class="meta">{{ t("panel.migration.bulkHint") }}</p>
          <div class="row-actions">
            <button class="compact" :disabled="bulkBusy || selectedDomains.length === 0 || !discoverHost" @click="runBulk">
              {{ bulkBusy ? t("panel.migration.bulkRunning") : t("panel.migration.bulkStart", { count: selectedDomains.length }) }}
            </button>
          </div>
        </div>
      </div>
    </div>

    <div class="form-surface">
      <p class="meta create-hint">{{ t("panel.migration.intro") }}</p>

      <!-- Hedef site -->
      <h3 class="section-label">{{ t("panel.migration.siteSection") }}</h3>
      <div class="form-grid">
        <label>
          <span class="field-label">{{ t("panel.migration.siteName") }}</span>
          <input v-model="form.siteName" :placeholder="t('panel.migration.siteNamePlaceholder')" spellcheck="false" />
        </label>
        <label>
          <span class="field-label">{{ t("panel.sites.physicalPath") }}</span>
          <input v-model="form.physicalPath" spellcheck="false" />
        </label>
      </div>
      <label class="checkbox">
        <input v-model="form.createSite" type="checkbox" />
        <span>{{ t("panel.migration.createSite") }}</span>
      </label>

      <!-- Dosya kaynağı -->
      <label class="checkbox section-toggle">
        <input v-model="form.pullFiles" type="checkbox" />
        <span><strong>{{ t("panel.migration.pullFiles") }}</strong></span>
      </label>
      <div v-if="form.pullFiles" class="form-grid">
        <label>
          <span class="field-label">{{ t("panel.migration.ftp.protocol") }}</span>
          <select v-model="form.fileProtocol" @change="syncFilePort">
            <option value="sftp">SFTP</option>
            <option value="ftps">FTPS</option>
            <option value="ftp">FTP</option>
          </select>
        </label>
        <label>
          <span class="field-label">{{ t("panel.migration.ftp.host") }}</span>
          <input v-model="form.fileHost" spellcheck="false" />
        </label>
        <label>
          <span class="field-label">{{ t("panel.migration.ftp.port") }}</span>
          <input v-model.number="form.filePort" type="number" />
        </label>
        <label>
          <span class="field-label">{{ t("panel.migration.ftp.user") }}</span>
          <input v-model="form.fileUserName" spellcheck="false" />
        </label>
        <label>
          <span class="field-label">{{ t("panel.migration.ftp.password") }}</span>
          <PasswordField v-model="form.filePassword" />
        </label>
        <label>
          <span class="field-label">{{ t("panel.migration.ftp.remotePath") }}</span>
          <input v-model="form.remotePath" spellcheck="false" placeholder="/httpdocs" />
        </label>
      </div>

      <!-- Veritabanı kaynağı -->
      <label class="checkbox section-toggle">
        <input v-model="form.transferDatabase" type="checkbox" />
        <span><strong>{{ t("panel.migration.transferDb") }}</strong></span>
      </label>
      <div v-if="form.transferDatabase" class="form-grid">
        <label>
          <span class="field-label">{{ t("panel.migration.dbEngine") }}</span>
          <select v-model="form.databaseEngine">
            <option value="mariadb">MariaDB</option>
            <option value="mysql">MySQL</option>
          </select>
        </label>
        <label>
          <span class="field-label">{{ t("panel.migration.ftp.host") }}</span>
          <input v-model="form.dbHost" spellcheck="false" placeholder="127.0.0.1" />
        </label>
        <label>
          <span class="field-label">{{ t("panel.migration.ftp.port") }}</span>
          <input v-model.number="form.dbPort" type="number" />
        </label>
        <label>
          <span class="field-label">{{ t("panel.migration.ftp.user") }}</span>
          <input v-model="form.dbUserName" spellcheck="false" placeholder="root" />
        </label>
        <label>
          <span class="field-label">{{ t("panel.migration.ftp.password") }}</span>
          <PasswordField v-model="form.dbPassword" />
        </label>
        <label>
          <span class="field-label">{{ t("panel.migration.sourceDb") }}</span>
          <input v-model="form.sourceDatabase" spellcheck="false" />
        </label>
        <label>
          <span class="field-label">{{ t("panel.migration.targetDb") }}</span>
          <input v-model="form.targetDatabase" spellcheck="false" :placeholder="form.sourceDatabase" />
        </label>
        <label>
          <span class="field-label">{{ t("panel.migration.targetDbUser") }}</span>
          <input v-model="form.targetDatabaseUser" spellcheck="false" autocomplete="off" />
        </label>
        <label>
          <span class="field-label">{{ t("panel.migration.targetDbPassword") }}</span>
          <!-- `type="text"` idi: ayni sayfadaki kaynak parolalari gizliyken hedef veritabani
               parolasi ekranda aciktı. Kural her parola alaninda ayni olmali. -->
          <PasswordField v-model="form.targetDatabasePassword" autocomplete="off" />
        </label>
      </div>
      <p v-if="form.transferDatabase" class="meta create-hint db-user-hint">{{ t("panel.migration.targetDbUserHint") }}</p>

      <div class="row-actions">
        <button class="secondary compact" :disabled="validating || !form.siteName.trim()" @click="validate">
          {{ validating ? t("panel.migration.validating") : t("panel.migration.validate") }}
        </button>
        <button class="compact" :disabled="busy || !form.siteName.trim()" @click="run">
          {{ busy ? t("panel.migration.running") : t("panel.migration.start") }}
        </button>
      </div>
    </div>

    <section v-if="checks.length || validating" class="form-surface">
      <h3 class="section-label">{{ t("panel.migration.checksTitle") }}</h3>
      <p v-if="validating" class="empty-state" role="status" aria-live="polite">{{ t("panel.migration.validating") }}</p>
      <div v-for="(check, index) in checks" :key="index" class="data-row">
        <span :class="['status-pill', check.ok ? 'ok' : 'danger']">{{ check.ok ? "✓" : "✗" }}</span>
        <span class="cell-muted">{{ t(check.message) }}</span>
      </div>
    </section>

    <section v-if="liveSteps.length || busy" class="form-surface">
      <h3 class="section-label">{{ t("panel.migration.liveTitle") }}</h3>
      <p v-if="busy" class="meta create-hint">{{ t("panel.migration.liveHint") }}</p>
      <!-- Sunucudan ilk adım listesi gelene kadar bölüm tamamen boştu: göç başlamamış sanılıyordu. -->
      <p v-if="busy && liveSteps.length === 0" class="empty-state" role="status" aria-live="polite">
        {{ t("panel.common.loading") }}
      </p>
      <div v-for="(step, index) in liveSteps" :key="index" class="data-row live-step" :class="step.status">
        <span class="status-pill" :class="stepPillClass(step.status)">{{ stepPillIcon(step.status) }}</span>
        <div class="live-step-body">
          <strong>{{ t(`panel.migration.stepName.${step.step}`) }}</strong>
          <span class="cell-muted">{{ stepStatusText(step) }}</span>
          <small v-if="step.detail" class="step-detail" :class="step.status === 'fail' ? 'err' : 'ok'">{{ step.detail }}</small>
        </div>
      </div>
    </section>

    <section v-if="bulkLog.length" class="form-surface">
      <h3 class="section-label">{{ t("panel.migration.bulkLogTitle") }}</h3>
      <div v-for="(entry, index) in bulkLog" :key="index" class="bulk-entry">
        <div class="bulk-entry-head">
          <span :class="['status-pill', entry.overallSuccess ? 'ok' : 'danger']">{{ entry.overallSuccess ? "✓" : "✗" }}</span>
          <strong>{{ entry.domain }}</strong>
        </div>
        <div v-for="(step, sub) in entry.steps" :key="sub" class="data-row bulk-step">
          <span :class="['status-pill', step.success ? 'ok' : 'danger']">{{ step.success ? "✓" : "✗" }}</span>
          <span>{{ t(`panel.migration.stepName.${step.step}`) }}</span>
          <span class="cell-muted">{{ t(step.message) }}</span>
          <small v-if="!step.success && step.detail" class="step-detail err">{{ step.detail }}</small>
        </div>
      </div>
    </section>
  </section>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from "vue";
import { useI18n } from "vue-i18n";
import PasswordField from "../components/PasswordField.vue";
import {
  getMigrationRunningJobs,
  migrateSite,
  migrateSiteStream,
  discoverPlesk,
  discoverCpanel,
  validateMigration,
  type MigrationStep,
  type PleskDomain,
  type MigrationCheck
} from "../api/migration";
import { extractApiMessage, extractApiDetails } from "../stores/auth";
import RunningTaskBanner from "../components/RunningTaskBanner.vue";
import SectionLoadError from "../components/SectionLoadError.vue";
import { runningJobLabel, useRunningTask, useServerRunningTask } from "../composables/useRunningTask";
import { useSectionError } from "../composables/useNotice";

const { t } = useI18n();

/*
 * "GÖÇ SÜRÜYOR" ARTIK SAYFA YENİLEMEYE DAYANIYOR.
 *
 * ÖNCESİ: canlı adım akışı (SSE) yalnız bu sekmeye aitti. Kullanıcı yüzlerce megabaytlık bir göç
 * sürerken sayfayı yenilediğinde hem adım listesi hem "sürüyor" bilgisi siliniyor, göç ise
 * sunucuda devam ediyordu — ve kullanıcı aynı siteye ikinci bir göç başlatabiliyordu.
 *
 * ŞİMDİ: sunucu göç süresince kaydı `/migration/running` ucundan veriyor; ekran açılır açılmaz
 * okunuyor. Sunucu ayrıca aynı anda ikinci bir göçü 409 ile reddediyor.
 *
 * YÜZDE YOK: uzak taraftaki dosya sayısı çekilmeden bilinmiyor. Adım listesi zaten GERÇEK
 * adımları gösteriyor; onun üstüne uydurma bir yüzde koymak, ölçülmüş bilgiyi ölçülmemişle
 * kirletmek olurdu.
 */
const localMigration = useRunningTask();
const serverMigration = useServerRunningTask({
  fetchStatus: getMigrationRunningJobs,
  label: (job) => runningJobLabel(t, job),
  interruptedLabel: (job) =>
    t("panel.runningJob.interrupted", {
      job: runningJobLabel(t, job),
      date: new Date(job.startedAtUtc).toLocaleString()
    })
});

const bannerRunning = computed(() => localMigration.running.value || serverMigration.running.value);
const bannerLabel = computed(() =>
  localMigration.running.value ? localMigration.label.value : serverMigration.label.value);
const bannerElapsedText = computed(() =>
  localMigration.running.value ? localMigration.elapsedText.value : serverMigration.elapsedText.value);
// Sunucudan okunan göç bu sekmede başlatılmadı: "sayfadan ayrılmayın" demek yanlış olurdu.
const bannerHint = computed(() =>
  localMigration.running.value ? "" : t("panel.runningJob.resumedHint"));

// Sunucuda süren göç varsa şerit ekran açılır açılmaz dolar (yenilenmiş sayfa dahil).
onMounted(serverMigration.start);

const form = reactive({
  siteName: "",
  createSite: true,
  physicalPath: "",
  pullFiles: true,
  fileProtocol: "sftp",
  fileHost: "",
  filePort: 22,
  fileUserName: "",
  filePassword: "",
  remotePath: "/",
  transferDatabase: true,
  databaseEngine: "mariadb",
  dbHost: "",
  dbPort: 3306,
  dbUserName: "root",
  dbPassword: "",
  sourceDatabase: "",
  targetDatabase: "",
  targetDatabaseUser: "",
  targetDatabasePassword: ""
});

const busy = ref(false);
type LiveStepStatus = "pending" | "running" | "ok" | "fail";
type LiveStep = { step: string; status: LiveStepStatus; message?: string; detail?: string | null };
const liveSteps = ref<LiveStep[]>([]);
const notice = ref("");
const noticeError = ref(false);
let physicalPathEdited = false;

// Keşif (opsiyonel kolaylık): Plesk REST veya cPanel/WHM.
const source = ref<"plesk" | "cpanel">("plesk");
const plesk = reactive({ host: "", port: 8443, apiKey: "", userName: "", password: "" });
const cpanel = reactive({ host: "", port: 2087, userName: "root", apiToken: "" });
const discovering = ref(false);
const discoveredDomains = ref<PleskDomain[]>([]);
// "Hiç taranmadı" ile "tarandı, sonuç boş" ve "okunamadı" birbirinden ayrı tutulur.
const discoverRan = ref(false);
const discoverError = useSectionError();
const discoverHost = computed(() => (source.value === "cpanel" ? cpanel.host : plesk.host).trim());

// Keşfedebilmek için host + kimlik gerekir. Plesk: API anahtarı YA DA (kullanıcı + parola). cPanel: API token.
const canDiscover = computed(() => {
  if (!discoverHost.value) return false;
  if (source.value === "plesk") {
    return plesk.apiKey.trim().length > 0 || (plesk.userName.trim().length > 0 && plesk.password.length > 0);
  }
  return cpanel.apiToken.trim().length > 0;
});

// Toplu göç + canlı günlük
const selectedDomains = ref<string[]>([]);
const bulkBusy = ref(false);
const bulkPathTemplate = ref("/var/www/vhosts/{domain}/httpdocs");
const bulkLog = ref<{ domain: string; overallSuccess: boolean; steps: MigrationStep[] }[]>([]);

// Göç öncesi doğrulama
const checks = ref<MigrationCheck[]>([]);
const validating = ref(false);

// Kaynak değişince uzak-yol şablonunu o panelin tipik düzenine ayarla.
watch(source, () => {
  bulkPathTemplate.value = source.value === "cpanel"
    ? "/home/{user}/public_html"
    : "/var/www/vhosts/{domain}/httpdocs";
});

function expandTemplate(template: string, domain: string, user: string): string {
  return template.replace(/\{domain\}/g, domain).replace(/\{user\}/g, user || domain);
}

// Site adından varsayılan fiziksel yol türet (kullanıcı elle değiştirmediyse).
watch(
  () => form.siteName,
  (name) => {
    if (!physicalPathEdited) {
      form.physicalPath = name.trim() ? `C:\\inetpub\\wwwroot\\${name.trim()}` : "";
    }
  }
);
watch(
  () => form.physicalPath,
  () => {
    // kullanıcı elle yazdıysa otomatik türetmeyi durdur
    if (form.physicalPath && form.siteName && form.physicalPath !== `C:\\inetpub\\wwwroot\\${form.siteName.trim()}`) {
      physicalPathEdited = true;
    }
  }
);

// Protokole göre varsayılan portu güncelle (SFTP=22, FTP/FTPS=21).
function syncFilePort() {
  form.filePort = form.fileProtocol === "sftp" ? 22 : 21;
}

// Plesk'e bağlan → domain listesi çek (best-effort; hata → uyarı, manuel form çalışır).
async function discover() {
  if (!discoverHost.value) return;
  discovering.value = true;
  notice.value = "";
  discoveredDomains.value = [];
  selectedDomains.value = [];
  discoverRan.value = false;
  discoverError.clear();
  try {
    const result = source.value === "cpanel"
      ? await discoverCpanel({
          host: cpanel.host.trim(),
          port: cpanel.port || 2087,
          userName: cpanel.userName.trim() || "root",
          apiToken: cpanel.apiToken.trim()
        })
      : await discoverPlesk({
          host: plesk.host.trim(),
          port: plesk.port || 8443,
          apiKey: plesk.apiKey.trim(),
          userName: plesk.userName.trim(),
          password: plesk.password
        });
    discoveredDomains.value = result.domains;
    // "Tarandı ve hiçbir şey çıkmadı" ile "hiç taranmadı" ayrı: boş liste artık kendi
    // metniyle görünür, şeritteki geçici uyarıya bağlı değil.
    discoverRan.value = true;
  } catch (error) {
    // Hata hem şeritte hem listenin yerinde: boş alan "kaynakta alan adı yok" sanılmasın.
    discoverError.capture(error);
    notice.value = apiErrorText(error);
    noticeError.value = true;
  } finally {
    discovering.value = false;
  }
}

// Hata bildirimi: anahtarı çevir (eskiden ham "migration.plesk.api_error" görünüyordu) + gerçek tekn. nedeni ekle
// (HTTP kodu + sunucu yanıtı = Details). Böylece "neden başarısız" net görünür.
function apiErrorText(error: unknown): string {
  const key = extractApiMessage(error);
  const translated = t(key);
  const message = translated === key ? key : translated;
  const details = extractApiDetails(error);
  return details ? `${message} — ${details}` : message;
}

// Keşfedilen domain'i seç → sihirbazı doldur (parolalar yine elle girilir). Uzak yol = kaynağın tipik şablonu.
function pickDomain(name: string) {
  form.siteName = name;
  const domain = discoveredDomains.value.find((item) => item.name === name);
  form.remotePath = expandTemplate(bulkPathTemplate.value, name, domain?.hostingType ?? "");
  if (discoverHost.value) {
    form.fileHost = discoverHost.value;
    form.dbHost = discoverHost.value;
  }
}

// Göç ÖNCESİ doğrulama: site adı uygunluğu + dosya/DB kaynağı erişilebilirliği (göç başlatmadan).
async function validate() {
  if (!form.siteName.trim()) return;
  validating.value = true;
  notice.value = "";
  checks.value = [];
  try {
    const result = await validateMigration({ ...form, siteName: form.siteName.trim() });
    checks.value = result.checks;
    notice.value = t(result.allPassed ? "panel.migration.allChecksOk" : "panel.migration.someChecksFailed");
    noticeError.value = !result.allPassed;
  } catch (error) {
    notice.value = apiErrorText(error);
    noticeError.value = true;
  } finally {
    validating.value = false;
  }
}

// Toplu göç: seçilen domain'leri SIRAYLA göç et (site + dosya); her domain sonrası canlı günlüğe ekle.
// DB toplu modda atlanır (hesap başına kimlik farklı → DB'yi tek tek sihirbazdan yap). Dosya kaynağı
// kimlik bilgileri (protokol/kullanıcı/parola) aşağıdaki "Dosya kaynağı" alanından alınır.
async function runBulk() {
  if (selectedDomains.value.length === 0 || !discoverHost.value) return;
  bulkBusy.value = true;
  notice.value = "";
  bulkLog.value = [];
  for (const name of selectedDomains.value) {
    const domain = discoveredDomains.value.find((item) => item.name === name);
    try {
      const result = await migrateSite({
        ...form,
        siteName: name,
        createSite: true,
        physicalPath: `C:\\inetpub\\wwwroot\\${name}`,
        pullFiles: true,
        fileHost: discoverHost.value,
        remotePath: expandTemplate(bulkPathTemplate.value, name, domain?.hostingType ?? ""),
        transferDatabase: false
      });
      bulkLog.value = [...bulkLog.value, { domain: name, overallSuccess: result.overallSuccess, steps: result.steps }];
    } catch (error) {
      bulkLog.value = [
        ...bulkLog.value,
        { domain: name, overallSuccess: false, steps: [{ step: "site", success: false, message: extractApiMessage(error) }] }
      ];
    }
  }
  bulkBusy.value = false;
  notice.value = t("panel.migration.bulkDone", { count: bulkLog.value.length });
  noticeError.value = false;
}

// Canlı göç: her aşama (plan/başla/sonuç/bitti) SSE ile gelir → checklist anlık güncellenir, kullanıcı
// "hangi adımda ne yapılıyor"u görür. Başarısız adımda gerçek teknik neden (detail) satırın altında çıkar.
async function run() {
  if (!form.siteName.trim()) return;
  busy.value = true;
  notice.value = "";
  liveSteps.value = [];
  // Adım listesi "hangi adımda"yı söyler ama GEÇEN SÜREYİ söylemez; büyük bir göçte tek bir adım
  // yarım saat sürebilir ve ekran donmuş görünür.
  localMigration.begin(t("panel.runningJob.migration", { title: form.siteName.trim() }));
  try {
    await migrateSiteStream({ ...form, siteName: form.siteName.trim() }, (event) => {
      if (event.phase === "plan") {
        liveSteps.value = (event.steps ?? []).map((step): LiveStep => ({ step, status: "pending" }));
      } else if (event.phase === "start" && event.step) {
        upsertStep(event.step, "running");
      } else if (event.phase === "result" && event.step) {
        upsertStep(event.step, event.success ? "ok" : "fail", event.message, event.detail);
      } else if (event.phase === "done") {
        notice.value = t(event.overallSuccess ? "panel.migration.overallOk" : "panel.migration.overallPartial");
        noticeError.value = !event.overallSuccess;
      }
    });
  } catch (error) {
    notice.value = t("panel.migration.streamFailed");
    noticeError.value = true;
  } finally {
    busy.value = false;
    localMigration.end();
  }
}

// Adımı kimliğine göre güncelle (yoksa ekle) — userini gibi plana konmayan adımlar canlı eklensin diye.
function upsertStep(step: string, status: LiveStepStatus, message?: string, detail?: string | null) {
  const existing = liveSteps.value.find((item) => item.step === step);
  if (existing) {
    existing.status = status;
    if (message !== undefined) existing.message = message;
    if (detail !== undefined) existing.detail = detail;
  } else {
    liveSteps.value = [...liveSteps.value, { step, status, message, detail }];
  }
}

function stepPillClass(status: LiveStepStatus): string {
  return status === "ok" ? "ok" : status === "fail" ? "danger" : status === "running" ? "run" : "idle";
}

function stepPillIcon(status: LiveStepStatus): string {
  return status === "ok" ? "✓" : status === "fail" ? "✗" : status === "running" ? "⟳" : "•";
}

function stepStatusText(item: LiveStep): string {
  if (item.status === "running") return t("panel.migration.statusRunning");
  if (item.status === "pending") return t("panel.migration.statusPending");
  return item.message ? t(item.message) : "";
}
</script>

<style scoped>
.section-label {
  margin: 18px 0 8px;
  font-size: 0.92rem;
  opacity: 0.8;
}
.migration-how .section-label {
  margin-top: 0;
}
.migration-steps {
  margin: 8px 0 0;
  padding-left: 1.3rem;
  display: grid;
  gap: 7px;
}
.migration-steps li {
  font-size: 0.86rem;
  line-height: 1.45;
  padding-left: 4px;
}
.migration-steps li::marker {
  color: var(--accent-1);
  font-weight: 700;
}
.section-toggle {
  margin-top: 16px;
}
.migration-view .data-row {
  display: flex;
  gap: 10px;
  align-items: center;
  padding: 8px 0;
}
.inline-notice {
  padding: 8px 12px;
  border-radius: 8px;
}
.inline-notice.ok {
  background: rgba(46, 160, 67, 0.12);
}
.inline-notice.error {
  background: rgba(218, 54, 51, 0.12);
}
.source-tabs {
  margin: 12px 0;
}
.domain-rows {
  display: grid;
  gap: 6px;
  margin-top: 8px;
}
.domain-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
  padding: 8px 12px;
  border: 1px solid var(--panel-border);
  border-radius: 10px;
  background: var(--surface-1);
}
.domain-pick {
  display: flex;
  align-items: center;
  gap: 10px;
  min-width: 0;
  cursor: pointer;
}
.domain-pick .domain-name {
  font-weight: 600;
}
.bulk-box {
  margin-top: 14px;
  display: grid;
  gap: 8px;
  padding-top: 12px;
  border-top: 1px solid var(--panel-border);
}
.bulk-entry {
  padding: 8px 0;
  border-bottom: 1px solid var(--panel-border);
}
.bulk-entry-head {
  display: flex;
  align-items: center;
  gap: 10px;
  margin-bottom: 4px;
}
.migration-view .data-row.bulk-step {
  padding: 4px 0 4px 14px;
  font-size: 0.86rem;
  flex-wrap: wrap;
}
.migration-view .data-row.live-step {
  align-items: flex-start;
}
.live-step-body {
  display: flex;
  flex-direction: column;
  gap: 2px;
  min-width: 0;
}
.live-step-body strong {
  font-size: 0.92rem;
}
.step-detail {
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  font-size: 0.78rem;
  padding: 3px 8px;
  border-radius: 6px;
  margin-top: 2px;
  word-break: break-word;
  white-space: pre-wrap;
}
.step-detail.err {
  color: var(--danger, #f85149);
  background: rgba(218, 54, 51, 0.1);
}
.step-detail.ok {
  color: var(--accent-1, #5fe39a);
  background: rgba(56, 139, 253, 0.1);
}
.db-user-hint {
  margin-top: 6px;
}
/* Canlı durum rozetleri: çalışıyor (dönen) + sırada (soluk). ok/danger global stilden gelir. */
.migration-view .status-pill.run {
  background: rgba(56, 139, 253, 0.16);
  color: var(--accent-1, #388bfd);
  display: inline-block;
  animation: migrate-spin 1s linear infinite;
}
.migration-view .status-pill.idle {
  background: rgba(125, 133, 144, 0.14);
  color: var(--text-muted, #8b949e);
}
@keyframes migrate-spin {
  to {
    transform: rotate(360deg);
  }
}
@media (prefers-reduced-motion: reduce) {
  .migration-view .status-pill.run {
    animation: none;
  }
}
</style>
