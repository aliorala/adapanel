<template>
  <section class="panel backup-view">

    <!-- Ortak `.section-head` devralindi: kapsamli `.bk-header` kopyasi ayni isi yapiyordu ama
         `align-items: center` ile. Baslik blogu UC satir (ustbaslik + baslik + site adi),
         yanindaki dugme tek satir; ortalama dugmeyi uc satirin ortasina koyuyor ve goz onu
         hicbir seyle hizali bulamiyordu. Ortak kural ustten hizalar (base.css'te 25/31 kopya
         boyle olculdu) ve `flex-wrap` ekler. -->
    <div class="section-head">
      <div>
        <p class="section-kicker">{{ t("panel.backup.siteKicker") }}</p>
        <h2>{{ t("panel.backup.siteTitle") }}</h2>
        <p class="meta">{{ siteName || t("panel.php.selectSite") }}</p>
      </div>
      <div class="row-actions">
        <button v-if="!isAdmin" class="secondary compact" @click="router.push({ query: { section: 'backup' } })">{{ t("panel.backup.manageScoped") }}</button>
        <button class="secondary compact" :disabled="loading" @click="refresh">{{ t("panel.backup.refresh") }}</button>
      </div>
    </div>

    <p v-if="notice" :class="['notice', { error: noticeError }]" :role="noticeError ? 'alert' : 'status'">{{ notice }}</p>

    <section v-if="createResult" class="form-surface create-result" aria-labelledby="backup-result-title">
      <div class="section-head">
        <h3 id="backup-result-title">{{ t("panel.backup.lastCreateResult") }}</h3>
        <span :class="['status-pill', { warn: createResult.status === 'created_with_errors' || createResult.status === 'created_remote_failed' || createResult.warnings.length > 0 }]">
          {{ t(`panel.backup.status.${createResult.status}`) }}
        </span>
      </div>
      <strong class="backup-name">{{ createResult.name }}</strong>
      <p class="meta">{{ formatDate(createResult.createdAtUtc) }} · {{ formatSize(createResult.size) }}</p>
      <ul v-if="createResult.warnings.length" class="backup-warnings">
        <li v-for="(warning, index) in createResult.warnings" :key="index">
          <strong>{{ t(warning.messageKey, { target: warning.target }) }}</strong>
          <span v-if="warning.target && !t(warning.messageKey, { target: warning.target }).includes(warning.target)">{{ warning.target }}</span>
          <small v-if="warning.detail">{{ warning.detail }}</small>
        </li>
      </ul>
    </section>

    <div v-if="siteName" class="form-surface bk-create">
      <h3>{{ t("panel.backup.createTitle") }}</h3>
      <div class="scope-heading">
        <h4>{{ t("panel.backup.scopeTitle") }}</h4>
        <p class="field-hint">{{ t("panel.backup.scopeHint") }}</p>
      </div>
      <!-- `chk` -> ortak `toggle-field`: kapsamli kopya birebir ayni uc ozelligi yaziyordu
           (flex + center + 8px). Gorunen fark iki tane: etiket rengi ortak `--text-soft`e
           gelir ve boyu 0.86rem yerine govde boyunda (13.5px) olur — ayni formdaki oteki
           etiketlerle ayni. -->
      <div class="scope-grid">
        <label class="toggle-field"><input v-model="scope.includeFiles" type="checkbox" /> {{ t("panel.backup.scopeFiles") }}</label>
        <label class="toggle-field"><input v-model="scope.includeConfig" type="checkbox" /> {{ t("panel.backup.scopeConfig") }}</label>
        <label class="toggle-field"><input v-model="scope.includeMail" type="checkbox" /> {{ t("panel.backup.scopeMail") }}</label>
      </div>

      <SectionLoadError
        v-if="databasesLoad.message.value"
        :message="databasesLoad.message.value"
        @retry="loadDatabases" />
      <p v-if="databasesLoading" class="empty-state" role="status">{{ t("panel.common.loading") }}</p>
      <p v-else-if="!canReadDatabases" class="field-hint">{{ t("panel.backup.databasePermissionHint") }}</p>
      <p v-else-if="!databasesLoad.message.value && availableDatabases.length === 0" class="field-hint">{{ t("panel.backup.databaseEmptyHint") }}</p>

      <div v-if="availableDatabases.length > 0" class="db-block">
        <div class="section-head">
          <span class="field-label">{{ t("panel.backup.scopeDatabases") }}</span>
          <span class="meta">{{ t("panel.backup.databaseSelection", { count: selectedDbKeys.length }) }}</span>
        </div>
        <div class="scope-grid database-options">
          <label v-for="db in availableDatabases" :key="`${db.engine}:${db.name}`" class="toggle-field">
            <input v-model="selectedDbKeys" type="checkbox" :value="`${db.engine}:${db.name}`" />
            {{ db.name }} <small>({{ db.engine }})</small>
          </label>
        </div>
      </div>

      <details class="backup-options">
        <summary>{{ t("panel.backup.advancedOptions") }}</summary>
        <div class="backup-options-body">
      <!--
        ARTIMSAL / TAM seçimi. Sunucu bunu ZATEN destekliyordu (`BackupSiteRequest` →
        `BackupArchiveRequest.Incremental` + `FullBackupEveryDays`, aralık 1..365) ve
        `api/backup.ts` alanları gönderiyordu; bu ekran seçeneği GÖSTERMİYORDU, yani kullanıcı
        yalnız tam yedek alabiliyordu. "Mümkünse artımsal": ilk yedek, tam yedek periyodu
        dolduğunda ve temel indeks okunamadığında yine TAM alınır.
      -->
      <div class="scope-grid incremental-row">
        <label class="toggle-field">
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
      <!-- UYARI, UYARI GIBI GORUNMELI: bu iki serit duz `.notice` idi, yani panelin MAVI bilgi
           kutusuyla birebir ayni ciziliyordu — "parolayi kaybedersen yedegin tamami gider"
           mesaji bir bilgi notundan ayirt edilemiyordu. Ortak `.warn-banner` kehribar cizer;
           durum boylece RENKTEN BASKA bir kanalda da (kutu tipi) anlatilmis olur. -->
      <p v-if="encryptionPassword" class="notice warn-banner encryption-loss" role="status">
        {{ t("panel.backup.encryptionLossWarning") }}
      </p>

      <!--
        ŞİFRELİ + ARTIMSAL: parola kaybı TEK HALKAYI değil ZİNCİRİN TAMAMINI götürür (tam yedek
        dahil). Uyarı, kararın VERİLDİĞİ yerde durur: artımsal işaretliyken görünür. Parola
        alanının boş olması riski kaldırmaz — panelde KAYITLI bir parola varsa zincir yine
        şifreli alınır ve bu ekran o kaydı okuyamaz (uç yalnız yöneticiye açık), bu yüzden uyarı
        parolaya bakılmadan gösterilir.
      -->
      <p v-if="scope.incremental" class="notice warn-banner encryption-loss" role="status">
        {{ t("panel.backup.encryptionChainWarning") }}
      </p>
        </div>
      </details>

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

      <!-- Gonder dugmesi kendi KAPANIS SATIRINDA. `.bk-create` bir izgara oldugu icin dugme
           kartin TAM GENISLIGINE geriliyordu: sayfayi bastan sona kesen mavi bir bant, panelin
           baska hicbir yerinde olmayan bir agirlik. -->
      <div class="form-actions">
        <button class="compact" v-bind="writeGate(!canCreateBackup)" @click="createBackup">
          {{ busy ? t("panel.backup.creating") : t("panel.backup.createButton") }}
        </button>
      </div>
    </div>

    <div class="bk-list">
      <div class="section-head">
        <h3>{{ t("panel.backup.listTitle") }}</h3>
        <span class="meta">{{ listLoaded ? siteBackups.length : loading ? "…" : "—" }}</span>
      </div>
      <p v-if="!isAdmin" class="field-hint">{{ t("panel.backup.siteRestoreHint") }}</p>
      <SectionLoadError v-if="listLoad.message.value" :message="listLoad.message.value" @retry="refresh" />

      <!--
        GÜN GRUPLARI (Yedekler ekranıyla AYNI kaynaktan: composables/useDayGroups.ts). Gruplama
        `createdAtUtc`ten KULLANICININ YEREL gününe çevrilerek yapılır — dosya yolu
        ayrıştırılmaz, böylece gün klasörü olmadan alınmış eski yedekler de doğru günde durur.
      -->
      <div v-if="siteBackups.length > 0" class="bk-days">
        <section v-for="group in dayGroups" :key="group.key" class="bk-day">
          <header class="bk-day-head">
            <!-- GÜN, BİR BÖLÜM BAŞLIĞIDIR — RÜTBESİ DE ÖYLE OLMALI.
                 Önceden `<strong>` idi: gövde metniyle AYNI boyda (13.5px), yalnız kalınlıkla
                 ayrılıyordu. Oysa bu satır listeyi bölen tek yapıdır ve kapalı bir günden
                 ekranda geriye kalan TEK şeydir; ayrıca ekran okuyucunun gezinme ağacında hiç
                 görünmüyordu (bölüm başlığı `<h3>`, altındaki günler başlıksızdı).
                 `<h4>` DIŞARIDA, düğme İÇERİDE: `<button>`ın içerik modeli sözcük öbeğidir,
                 içine `<h4>` konamaz. Bu sırayla hem rütbe (ladder: --fs-h4 = 14.3px/640) hem
                 de "başlığın tamamı tıklanır" davranışı korunur — düğme `font: inherit`
                 taşıdığı için boyu başlıktan gelir. -->
            <h4 class="bk-day-title">
              <button
                type="button"
                class="day-toggle"
                :aria-expanded="isDayOpen(group.key)"
                @click="toggleDay(group.key)">
                <span class="day-caret" aria-hidden="true">{{ isDayOpen(group.key) ? "▾" : "▸" }}</span>
                {{ group.label }}
              </button>
            </h4>
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
                  <!--
                    DOSYASI YOK ROZETİ — BU EKRANIN EN PAHALI YALANIYDI.

                    ÖLÇÜLDÜ (kaynaktan): `BackupService.GetBackupsAsync` her listelemede
                    `record.LocalMissing = IsLocalFileMissing(record)` yazıyor (kayıt başına bir
                    `File.Exists`; ağ isteği yok, bu yüzden her listelemede yapılıyor) ve alan
                    `api/backup.ts`te TİPLİ. Yedekler ekranı (`BackupView`) bu ölçümü kırmızı bir
                    rozetle gösteriyor; SİTE yedek listesi HİÇ göstermiyordu.

                    Görünen sonuç: dosyası elle silinmiş bir yedek bu ekranda sapasağlam
                    görünüyor, kullanıcı "Geri yükle"ye basıp site adını da yazıyor ve dosyanın
                    olmadığını ancak sunucunun hatasından öğreniyordu — yani tam da felaket
                    anında. Ölçüm VARDI, teslim yolu kopuktu (bu depoda ölçülmüş bir hata sınıfı).

                    Rozetler, anahtarlar ve gerekçeleri `BackupView` ile AYNI; yeni anahtar yok.
                  -->
                  <span
                    v-if="record.localMissing"
                    class="status-pill danger"
                    :title="t('panel.backup.missingLocalHint')">
                    {{ t("panel.backup.missingLocalBadge") }}
                  </span>
                  <!-- Uzak kopya YALNIZCA "Doğrula" çalıştıysa ölçülmüştür; ölçülmemiş bir kaydı
                       "uzakta yok" göstermek yalan olurdu, bu yüzden `remoteVerifiedUtc` şart. -->
                  <span
                    v-if="record.remoteMissing && record.remoteVerifiedUtc"
                    class="status-pill danger"
                    :title="t('panel.backup.missingRemoteHint')">
                    {{ t("panel.backup.missingRemoteBadge") }}
                  </span>
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
                <span v-if="record.status === 'created_with_errors' || record.status === 'created_remote_failed'" class="status-pill warn backup-outcome">
                  {{ t(`panel.backup.status.${record.status}`) }}
                </span>
                <details v-if="record.warnings?.length" class="backup-row-warnings">
                  <summary>{{ t("panel.backup.warningsBadge", { count: record.warnings.length }) }}</summary>
                  <ul class="backup-warnings">
                    <li v-for="(warning, index) in record.warnings" :key="index">
                      <strong>{{ t(warning.messageKey, { target: warning.target }) }}</strong>
                      <span v-if="warning.target && !t(warning.messageKey, { target: warning.target }).includes(warning.target)">{{ warning.target }}</span>
                      <small v-if="warning.detail">{{ warning.detail }}</small>
                    </li>
                  </ul>
                </details>

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
                    <span v-else class="meta">{{ t(isAdmin ? "panel.backup.location.unknown" : "panel.backup.location.scopedHidden") }}</span>
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
              <!-- "GERI YUKLE" YIKICI BIR EYLEM: canli sitenin dosyalarinin uzerine yazar.
                   Yan yana duran "Indir" ile BIREBIR ayni goruntudeydi (ikisi de `secondary`),
                   yani en zararsiz eylem ile en yikici eylem ayni agirliktaydi ve aralarinda
                   yalniz 8px vardi. Artik panelin geri kalaninda oldugu gibi tehlike rengiyle
                   ve bir ayracin arkasinda, satirin SONUNDA duruyor — yeri satirdan satira
                   degismez. Yuksek riskli onay (site adini yazdirma) AYNEN yerinde. -->
              <div class="bk-actions">
                <button class="secondary compact" :disabled="downloadingId === record.id" @click="download(record)">
                  {{ downloadingId === record.id ? t("panel.backup.downloading") : t("panel.backup.download") }}
                </button>
                <span v-if="isAdmin" class="action-divider" aria-hidden="true"></span>
                <button class="danger compact" v-bind="writeGate(restoringId === record.id)" v-if="isAdmin" @click="restore(record)">
                  {{ restoringId === record.id ? t("panel.backup.restoring") : t("panel.backup.restore") }}
                </button>
              </div>
            </article>
          </div>
        </section>
      </div>
      <!-- Yedek listesi gelmeden "yedek yok" yazıyordu; var olan yedekler yokmuş gibi görünüyordu. -->
      <p v-else-if="loading && !listLoaded" class="empty-state" role="status" aria-live="polite">
        {{ t("panel.common.loading") }}
      </p>
      <p v-else-if="listLoaded && !listLoad.message.value" class="empty-state">{{ t("panel.backup.empty") }}</p>
    </div>
  </section>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from "vue";
import { useI18n } from "vue-i18n";
import PasswordField from "../components/PasswordField.vue";
import { useBackupRestore, restoreResult } from "../composables/useBackupRestore";
import { useAuthStore } from "../stores/auth";
import router from "../router";
import { useSitesStore } from "../stores/sites";
import {
  backupSite,
  downloadBackup,
  getBackupRunningJobs,
  getBackups,
  type BackupRecord
} from "../api/backup";
import { getDatabaseEngines, getDatabaseWorkspace } from "../api/databases";
import { getMyDatabases } from "../api/myDatabases";
import { useConfirm } from "../components/ConfirmDialog.vue";
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
import { getSessionEpoch } from "../api/client";

const { t } = useI18n();
const { confirm } = useConfirm();
// Askidaki abonelikte YAZMA dugmeleri basilmadan once pasiflesir ve sebebini soyler
// (bkz. composables/useSubscriptionGate.ts). Okuma dugmeleri (yenile, indir) kapiya girmez.
const { writeGate, locked } = useSubscriptionGate();
const sitesStore = useSitesStore();
const siteName = computed(() => sitesStore.selectedSiteName);

const loading = ref(false);
// Yüklenmemiş liste "yedek yok" olarak görünüyordu.
const listLoaded = ref(false);
const busy = ref(false);
const downloadingId = ref("");
const fullRestore = useBackupRestore();
const restoringId = fullRestore.restoringId;
const authStore = useAuthStore();
const isAdmin = computed(() => authStore.session?.user?.role === "Administrator");
// TEK KAYNAK: bu ekran ham `extractApiMessage` basıyordu (4 nokta) — kullanıcı çevrilmemiş
// anahtar görüyordu.
const { notice, noticeError, showNotice, resolveApiMessage } = useNotice();
const backups = ref<BackupRecord[]>([]);
const createResult = ref<BackupRecord | null>(null);
const listLoad = useSectionError();
const availableDatabases = ref<{ engine: string; name: string }[]>([]);
// Boş liste "yedeklenecek veritabanı yok" demektir; okunamadıysa AYRI söylenir — aksi hâlde
// kullanıcı veritabanı seçmeden yedek alıp yedeğini eksik sanmadan taşırdı.
const databasesLoad = useSectionError();
const selectedDbKeys = ref<string[]>([]);
const databasesLoading = ref(false);
let disposed = false, siteGeneration = 0, listRequest = 0, databaseRequest = 0, passwordRevision = 0;
const session = getSessionEpoch();
const originalSession = authStore.session, actorId = originalSession?.user?.id, actorRole = originalSession?.user?.role;
const isCurrent = () => !disposed && session === getSessionEpoch() && authStore.session === originalSession && authStore.session?.isAuthenticated === true &&
  authStore.session.user?.id === actorId && authStore.session.user?.role === actorRole;
const canReadDatabases = computed(() => isCurrent() && (isAdmin.value ||
  (["Customer", "Reseller"].includes(actorRole ?? "") && (!authStore.session?.isSubUser ||
    (authStore.session.allowedSections ?? []).some(section => section.toLowerCase() === "database")))));
const canCreateBackup = computed(() => isCurrent() && !!siteName.value && !busy.value && !databasesLoading.value && !locked.value &&
  selectedDbKeys.value.every(key => canReadDatabases.value && availableDatabases.value.some(db => `${db.engine}:${db.name}` === key)));

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
watch(encryptionPassword, () => { passwordRevision++; }, { flush: "sync" });
onBeforeUnmount(() => { disposed = true; encryptionPassword.value = ""; });

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
watch(siteName, () => {
  ++siteGeneration;
  encryptionPassword.value = "";
  selectedDbKeys.value = [];
  notice.value = "";
  createResult.value = null;
  expandedLocation.value = "";
  dayDefaultsApplied.value = false;
  collapsedDays.value = new Set();
});

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
  await Promise.all([refresh(), loadDatabases()]);
  // Sunucuda süren iş varsa şerit ekran açılır açılmaz dolar (yenilenmiş sayfa dahil).
  if (isCurrent()) await serverBackup.start();
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
  if (!isCurrent()) return false;
  const request = ++listRequest;
  loading.value = true;
  try {
    const result = await getBackups();
    if (!isCurrent() || request !== listRequest) return false;
    backups.value = result;
    listLoaded.value = true;
    listLoad.clear();
    return true;
  } catch (error) {
    if (isCurrent() && request === listRequest) listLoad.capture(error);
    return false;
  } finally {
    if (isCurrent() && request === listRequest) loading.value = false;
  }
}

watch(canReadDatabases, allowed => {
  databaseRequest++; availableDatabases.value = []; selectedDbKeys.value = [];
  databasesLoading.value = false; databasesLoad.clear();
  if (allowed) void loadDatabases();
}, { flush: "sync" });

async function loadDatabases() {
  if (!isCurrent() || databasesLoading.value || !canReadDatabases.value) return;
  const request = ++databaseRequest;
  const current = () => isCurrent() && request === databaseRequest && canReadDatabases.value;
  databasesLoading.value = true;
  try {
    if (!isAdmin.value) {
      const rows = await getMyDatabases();
      if (current()) { availableDatabases.value = rows; databasesLoad.clear(); }
      return;
    }
    const engines = await getDatabaseEngines();
    if (!current()) return;
    const list: { engine: string; name: string }[] = [];
    const failed: string[] = [];
    for (const engine of engines.filter((item) => item.installed)) {
      if (!current()) return;
      try {
        const workspace = await getDatabaseWorkspace(engine.engine);
        for (const db of workspace.databases ?? []) {
          list.push({ engine: engine.engine, name: db.name });
        }
      } catch {
        failed.push(engine.displayName || engine.engine);
      }
    }
    if (!current()) return;
    availableDatabases.value = list;
    if (failed.length) databasesLoad.message.value = t("panel.backup.databasesPartial", { engines: failed.join(", ") });
    else databasesLoad.clear();
  } catch (error) {
    if (!current()) return;
    availableDatabases.value = [];
    databasesLoad.capture(error);
  } finally {
    if (current()) databasesLoading.value = false;
  }
}

async function createBackup() {
  if (!isCurrent() || !canCreateBackup.value) {
    return;
  }
  busy.value = true;
  const site = siteName.value, generation = siteGeneration;
  const submittedPasswordRevision = passwordRevision;
  createResult.value = null;
  const current = () => isCurrent() && siteName.value === site && generation === siteGeneration;
  runningBackup.begin(`${t("panel.backup.running.creating")}: ${siteName.value}`);
  try {
    const databases = selectedDbKeys.value.map((key) => {
      const [engine, ...rest] = key.split(":");
      return { engine, databaseName: rest.join(":") };
    });
    const result = await backupSite(site, {
      includeFiles: scope.includeFiles,
      includeConfig: scope.includeConfig,
      includeMail: scope.includeMail,
      databases,
      // "Mümkünse artımsal": sunucu ilk yedekte, tam yedek periyodu dolduğunda ve temel indeks
      // okunamadığında yine TAM yedek alır.
      incremental: scope.incremental,
      fullBackupEveryDays: scope.fullBackupEveryDays
    }, { encryptionPassword: encryptionPassword.value });
    if (!current()) return;
    // Parola tek seferliktir; formda asılı kalıp bir sonraki yedeğe sessizce taşınmaz.
    if (passwordRevision === submittedPasswordRevision) encryptionPassword.value = "";
    createResult.value = result;
    const warnings = result.warnings ?? [];
    const partial = result.status === "created_with_errors" || result.status === "created_remote_failed" || warnings.length > 0;
    const messageKey = result.status === "created_remote_failed" ? "panel.backup.createdRemoteFailed" :
      result.status === "created_with_errors" ? "panel.backup.createdWithErrors" : warnings.length ? "panel.backup.createdWithWarnings" : "panel.backup.created";
    const lines = [t(messageKey)];
    const refreshed = await refresh();
    if (!current()) return;
    if (!refreshed && listLoad.message.value) lines.push(listLoad.message.value);
    showNotice(lines.join("\n"), partial || !refreshed);
  } catch (error) {
    if (current()) showNotice(resolveApiMessage(error), true);
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
  await fullRestore.restore(record, (result) => {
    const report = restoreResult(result, t);
    showNotice(report.message, report.failed);
  }, (error) => showNotice(error instanceof Error && !("isAxiosError" in error) ? error.message : resolveApiMessage(error), true), refresh);
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
/*
 * BU DOSYADAN SILINEN KOPYALAR (gorunen etkileriyle):
 *  - `.bk-header`   -> ortak `.section-head`; uc satirlik baslik blogu artik dugmeyle USTTEN
 *                      hizali (oncesinde `center` ile ortadan hizaliydi) ve satir sariyor.
 *  - `.chk`         -> ortak `.toggle-field` (birebir ayni tanim).
 *  - `.field-label` -> ORTAK SINIFIN UZERINE YAZIYORDU: 0.74rem/`--text-muted`/700 diyordu,
 *                      ortak tanim ise 0.92rem/`--text-soft`/600. Yani panelde her yerde ayni
 *                      gorunen alan etiketi YALNIZ bu ekranda kucuk ve soluktu. Kopya silindi.
 *  - `.meta`        -> ortak tanimla ayni renk; tek fark ortak surumun `line-height: 1.5`i.
 *  - `.location-line button { width: auto }` -> hicbir kural o dugmeyi germiyordu (olu).
 */

.backup-view,
.bk-create,
.bk-list {
  display: grid;
  gap: var(--sp-12);
}

.bk-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--sp-12);
}

.scope-grid {
  display: flex;
  flex-wrap: wrap;
  gap: var(--sp-12);
}

.db-block {
  display: grid;
  gap: var(--sp-8);
}

.scope-heading h4,
.scope-heading p,
.create-result p {
  margin: 0;
}

.scope-heading,
.create-result,
.backup-options-body {
  display: grid;
  gap: var(--sp-8);
  min-width: 0;
}

.database-options {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(min(100%, 220px), 1fr));
  gap: var(--sp-8);
}

.database-options .toggle-field {
  padding: var(--sp-10);
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-md);
  background: var(--surface-1);
  overflow-wrap: anywhere;
}

.database-options .toggle-field:has(input:checked) {
  border-color: var(--accent-1);
  background: var(--surface-2);
}

.database-options input {
  flex-shrink: 0;
}

.backup-options {
  border-block: 1px solid var(--panel-border);
  padding-block: var(--sp-10);
}

.backup-options summary,
.backup-row-warnings summary {
  cursor: pointer;
  font-weight: 600;
  color: var(--text-soft);
  overflow-wrap: anywhere;
}

.backup-options[open] summary {
  margin-bottom: var(--sp-12);
}

.backup-warnings {
  margin: 0;
  padding-left: 1.25em;
}

.backup-warnings li {
  padding-block: var(--sp-4);
  overflow-wrap: anywhere;
}

.backup-warnings li > * {
  display: block;
}

.backup-name,
.bk-id > strong {
  overflow-wrap: anywhere;
}

.backup-outcome {
  justify-self: start;
  white-space: normal;
}

/* Formun KAPANIS SATIRI: gonder dugmesi saga hizali, kart genisligine gerilmez. */
.form-actions {
  display: flex;
  justify-content: flex-end;
}

.bk-days {
  display: grid;
  gap: var(--sp-12);
}

.bk-day {
  display: grid;
  gap: var(--sp-8);
}

.bk-day-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--sp-10);
  flex-wrap: wrap;
  padding-bottom: var(--sp-6);
  border-bottom: 1px solid var(--panel-border);
}

/* Başlık artık gerçek bir `<h4>`; ortak merdivenin verdiği üst/alt boşluk burada istenmiyor,
   çünkü satırın dikey ritmini `.bk-day-head` (flex + `--sp-6` alt dolgu) belirliyor. */
.bk-day-title {
  margin: 0;
  min-width: 0;
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

/* Gun ozeti SAYI tasir (kac yedek, toplam boyut) ve gunler alt alta karsilastiriliyor:
   basamaklar esit genislikte olmazsa "12" ile "9" ayni sutunda hizasiz gorunur. */
.bk-day-summary {
  display: flex;
  align-items: center;
  gap: var(--sp-6);
  flex-wrap: wrap;
  font-variant-numeric: tabular-nums;
}

.bk-rows {
  display: flex;
  flex-direction: column;
  gap: var(--sp-8);
}

.location-toggle {
  width: auto;
  justify-self: start;
  margin-top: var(--sp-4);
}

.location-detail {
  display: grid;
  gap: var(--sp-6);
  margin-top: var(--sp-6);
}

.location-line {
  display: flex;
  align-items: center;
  gap: var(--sp-8);
  flex-wrap: wrap;
}

/* Tam yol KISALTILMAZ (kopyalanıp yapıştırılacak) ama satırı da bozmaz: kutu KENDİ içinde
   yatay kayar. `nowrap` bilinçli — sarılmış bir yol yanlış okunuyor. */
/* Tam yol KISALTILMAZ (kopyalanıp yapıştırılacak) ama satırı da bozmaz: kutu KENDİ içinde
   yatay kayar. `nowrap` bilinçli — sarılmış bir yol yanlış okunuyor.
   Yazi tipi ortak `--font-mono` belirtecinden: burada elle yazilan liste panelin oteki
   konsol yuzeylerinden farkliydi (base.css bu belirteci tam bu yuzden tanimliyor). */
.path-box {
  flex: 1 1 240px;
  min-width: 0;
  max-width: 100%;
  overflow-x: auto;
  padding: var(--sp-4) var(--sp-8);
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-sm);
  background: var(--surface-2);
  font-family: var(--font-mono);
  font-size: var(--fs-xs);
  white-space: nowrap;
}

.incremental-row {
  align-items: end;
}

.full-every {
  display: grid;
  gap: var(--sp-4);
  max-width: 200px;
}

.bk-row {
  padding: var(--sp-10) var(--sp-12);
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-lg);
  background: var(--surface-1);
}

.bk-id {
  display: grid;
  gap: var(--sp-2);
  min-width: 0;
}

/* Tarih ve boyut ("18.08.2026 14:03 · 1.24 GB") satirlar boyunca alt alta okunuyor. */
.bk-id .meta {
  font-variant-numeric: tabular-nums;
}

.bk-actions {
  display: flex;
  align-items: center;
  gap: var(--sp-8);
  flex-shrink: 0;
}

/* YIKICI BOLGE AYRACI: "Indir" ile "Geri yukle" arasindaki gorsel sinir. */
.action-divider {
  align-self: stretch;
  width: 1px;
  margin: 0 var(--sp-2);
  background: var(--panel-border);
}

/* Parola kaybı geri dönüşsüz: uyarı satırı gövde metninden ayrışsın. */
.encryption-loss {
  font-weight: 600;
}

/* DAR EKRAN: yedek satiri iki blok halinde (kimlik + eylemler) yan yana duruyordu ve
   telefonda eylem dugmeleri ada 8px kala sikisiyordu. Alt alta gecerler, eylem seridi de
   saga yaslanir ki "Geri yukle" yine sagda ve ayracin arkasinda kalsin. */
@media (max-width: 720px) {
  .bk-row {
    flex-direction: column;
    align-items: stretch;
  }

  .bk-actions {
    justify-content: flex-end;
  }
}
</style>
