<template>
  <section class="htaccess-panel">
    <div class="section-head">
      <div>
        <h4>{{ t("panel.htaccess.title") }}</h4>
        <p class="meta">{{ t("panel.htaccess.subtitle") }}</p>
      </div>
      <button class="secondary compact" :disabled="!siteName || busy" @click="load">
        {{ t("panel.htaccess.refresh") }}
      </button>
    </div>

    <!-- Şerit base.css'te ekrana sabitlidir (sağ alt köşe), yani kullanıcı nereye kaydırırsa
         kaydırsın görünür. Kapatma düğmesi Dosyalar ekranıyla AYNI kalıptır: iki ekran aynı
         panelin içinde farklı davranmasın. -->
    <p v-if="notice" :class="['notice', 'page-notice', { error: noticeError }]" :role="noticeError ? 'alert' : 'status'">
      <span class="notice-text">{{ notice }}</span>
      <button
        type="button"
        class="notice-dismiss"
        :aria-label="t('panel.common.dismissNotice')"
        :title="t('panel.common.dismissNotice')"
        @click="notice = ''">×</button>
    </p>

    <p v-if="!siteName" class="empty-state">{{ t("panel.php.selectSite") }}</p>

    <!-- Rapor gelmeden hicbir anahtar cizilmiyor: kapali gorunen bir anahtar "kapali" demektir,
         oysa henuz OKUNMAMISTIR. Bu ayrimi gizlemek panelin en pahali yalanlarindan biri. -->
    <p v-else-if="!report" class="empty-state" role="status" aria-live="polite">
      {{ t("panel.common.loading") }}
    </p>

    <template v-else>
      <!-- ============================ DÜRÜSTLÜK ŞERİDİ ============================
           Sirasi ONEM sirasidir: en ustte "hicbir sey olmaz" diyen engel durur. Kullanici
           once en guclu sebebi gormeli, yoksa alttaki kucuk uyariyi cozup yine hicbir sey
           olmadigini gorurdu.

           YOĞUNLUK SORUNU ÖLÇÜLDÜ: bu şeritte aynı anda ALTI blok birden çıkabiliyor
           (engelBlockers + destek + sunucu kapalı + dosya var/destek kapalı + kökte dosya yok).
           Metinler kısa değil: `warn.noManagedCode` 400, `warn.serverOff` 330 karaktere yakın.
           Yani ekranın ilk yarısı, hiçbir denetime ulaşmadan bir uyarı duvarına dönüyordu — ve
           bir duvarda hiçbir uyarı okunmaz.

           NE YAPILDI, NE YAPILMADI: hiçbir uyarı SİLİNMEDİ, gizlenmedi, yumuşatılmadı. Yalnızca
           iki gruba ayrıldı ve ayrımın ölçüsü EKRANIN KENDİ SINIFLANDIRMASIDIR — `notice error` /
           `role="alert"` olanlar (özelliğin ÇALIŞMAMASININ sebebi) her zaman açık; `role="status"`
           olanlar (bilgi düzeyi) katlanabilir bir blokta. Severity yeniden karar verilmedi,
           var olanı kullanıldı.

           KATLI BLOK NE ZAMAN AÇIK: ekranda hiç engel yoksa AÇIK gelir — o zaman bilgi
           uyarılarıyla yarışan bir şey yoktur. Engel varsa KAPALI gelir ki asıl sebep gürültüde
           kaybolmasın; başlığı kaç uyarı olduğunu SAYIYLA yazar, yani var oldukları saklanmaz. -->
      <div class="truth-strip">
        <p v-if="report.engineBlockers.length > 0" class="notice error" role="alert">
          <strong>{{ t("panel.htaccess.warn.engineNotLoaded") }}</strong>
          <span v-for="key in report.engineBlockers" :key="key"> {{ t(key) }}</span>
        </p>

        <p v-if="report.support === 'no_managed_code'" class="notice error" role="alert">
          {{ t("panel.htaccess.warn.noManagedCode") }}
        </p>
        <p v-else-if="report.support === 'runtime_version'" class="notice error" role="alert">
          {{ t("panel.htaccess.warn.runtimeVersion", { version: report.managedRuntimeVersion }) }}
        </p>

        <!-- ASIL SIKAYET: dosya duruyor, destek kapali, kimse sebebini soylemiyor. -->
        <p v-if="report.hasFiles && report.mode === 'off'" class="notice error" role="alert">
          {{ t("panel.htaccess.warn.filesButDisabled", { count: report.files.length }) }}
        </p>

        <details v-if="bilgiUyariSayisi > 0" class="truth-more" :open="engelUyariSayisi === 0">
          <summary>{{ t("panel.htaccess.warn.more", { count: bilgiUyariSayisi }) }}</summary>

          <p v-if="report.support === 'unknown'" class="notice" role="status">
            {{ t("panel.htaccess.warn.supportUnknown") }}
          </p>

          <p v-if="report.serverMode === 'off'" class="notice" role="status">
            {{ t("panel.htaccess.warn.serverOff") }}
          </p>

          <p v-if="report.hasFiles && !report.hasRootFile" class="notice" role="status">
            {{ t("panel.htaccess.warn.noRootFile") }}
          </p>

          <p v-if="!report.hasFiles" class="notice" role="status">
            {{ t("panel.htaccess.warn.noFiles") }}
          </p>
        </details>
      </div>

      <!-- ============================ KİP ============================ -->
      <section class="htaccess-section">
        <div class="section-head">
          <div>
            <h4>{{ t("panel.htaccess.mode.title") }}</h4>
            <p class="meta">{{ t("panel.htaccess.mode.hint") }}</p>
          </div>
        </div>

        <div class="mode-choices" role="radiogroup" :aria-label="t('panel.htaccess.mode.title')">
          <label v-for="choice in modeChoices" :key="choice.id" class="mode-choice">
            <input v-model="selectedMode" type="radio" :value="choice.id" name="htaccess-mode" />
            <span class="mode-text">
              <strong>{{ choice.label }}</strong>
              <small>{{ choice.hint }}</small>
            </span>
          </label>
        </div>

        <label class="setting-row">
          <span>{{ t("panel.htaccess.mode.note") }}</span>
          <input v-model="note" :placeholder="t('panel.htaccess.mode.notePlaceholder')" maxlength="200" />
          <small>{{ t("panel.htaccess.mode.noteHelp") }}</small>
        </label>

        <div class="action-row">
          <button v-bind="writeGate(busy || selectedMode === report.mode)" @click="saveMode">
            {{ busy ? t("panel.htaccess.saving") : t("panel.htaccess.mode.save") }}
          </button>
          <span class="meta">{{ t("panel.htaccess.mode.current", { mode: modeLabel(report.mode) }) }}</span>
        </div>

        <!--
          SUNUCU GENELİ ANAHTAR — yalnız yöneticiye görünür.

          Site kipi tek başına hiçbir şey yapmaz: etkin kip, sunucu ile site anahtarının EN
          DÜŞÜĞÜDÜR. Bu bölüm gelene kadar sunucu anahtarının panelde karşılığı yoktu ve arayüz
          kullanıcıya anahtar dosyasını ELLE açmasını söylüyordu; yani özellik panelden
          açılamıyordu. Düğme buraya konuldu çünkü kullanıcı sorunu tam olarak BURADA görüyor.
        -->
        <div v-if="isAdmin" class="server-switch">
          <strong>{{ t("panel.htaccess.server.title") }}</strong>
          <p class="meta">{{ t("panel.htaccess.server.hint") }}</p>

          <p v-if="serverNotice" :class="['notice', { error: serverNoticeError }]" :role="serverNoticeError ? 'alert' : 'status'">
            {{ serverNotice }}
          </p>

          <!-- ŞU ANKİ KİP YALNIZ "DEVRE DIŞI OLMAKLA" ANLATILIYORDU. Üç düğmeden biri soluk
               görünüyordu, ama solukluk panelde "basamazsın" demektir — "seçili olan bu" demek
               değil; askıdaki abonelikte de ÜÇÜ birden soluklaşıyor ve o hâlde hangisinin etkin
               olduğu ekrandan HİÇ okunamıyordu. Durum artık üç ayrı kanaldan söyleniyor:
               `aria-pressed` (ekran okuyucu), belirgin çerçeve/dolgu (renk körü kullanıcı için
               yalnız renk değil KENARLIK farkı) ve yanındaki metin. -->
          <div class="action-row mode-switch-row" role="group" :aria-label="t('panel.htaccess.server.title')">
            <button
              v-for="secim in modeChoices"
              :key="`server-${secim.id}`"
              class="secondary compact"
              :aria-pressed="serverSwitch?.mode === secim.id"
              v-bind="writeGate(serverBusy || serverSwitch?.mode === secim.id)"
              @click="saveServerMode(secim.id)">
              {{ secim.label }}
            </button>
            <span class="meta">
              {{ serverSwitch
                ? t("panel.htaccess.server.current", { mode: modeLabel(serverSwitch.mode) })
                : t("panel.htaccess.server.unknown") }}
            </span>
          </div>

          <!-- Acil durum kurtarma yolu BİLEREK duruyor: panel açılmıyorsa anahtar dosyadan
               çevrilebilmeli. Artık TEK yol değil, yalnız yedek yol. -->
          <small v-if="serverSwitch">{{ t("panel.htaccess.server.file", { path: serverSwitch.switchFilePath }) }}</small>
        </div>
      </section>

      <!-- ============================ DOSYALAR ============================ -->
      <section class="htaccess-section">
        <div class="section-head">
          <div>
            <h4>{{ t("panel.htaccess.files.title") }}</h4>
            <p class="meta">{{ t("panel.htaccess.files.hint") }}</p>
          </div>
        </div>

        <p v-if="report.files.length === 0" class="empty-state">{{ t("panel.htaccess.files.none") }}</p>
        <!--
          GERÇEK `<table>` DOĞRU SEÇİM, ama tek başına biçimsizdi: base.css'te hiçbir
          `table`/`th`/`td` kuralı yok (arandı, yok) ve `.data-table` bir BLOK için yazılmış
          (`overflow-x: auto` bir tablo kutusunda hiçbir şey yapmaz). Yani bu liste kenarlıklı bir
          çerçevenin içinde, hücre boşluğu olmayan çıplak bir tarayıcı tablosu olarak çiziliyordu.
          Kaydırma kabuğu dışarıdaki `div`e, hücre biçimi de tabloya ait; ikisi karışmasın.

          `scope="col"` EKLENDİ: ekran okuyucu hücreyi okurken sütun adını söyleyebilsin. Boş
          başlık da adını `sr-only` ile alır — görsel olarak boş kalması doğru, adsız kalması değil.
        -->
        <div v-else class="data-table">
          <table class="file-table">
            <thead>
              <tr>
                <th scope="col">{{ t("panel.htaccess.files.path") }}</th>
                <th scope="col" class="num-col">{{ t("panel.htaccess.files.size") }}</th>
                <th scope="col">{{ t("panel.htaccess.files.modified") }}</th>
                <th scope="col"><span class="sr-only">{{ t("panel.files.actions") }}</span></th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="file in report.files" :key="file.relativeDirectory">
                <td><code>{{ file.virtualPath }}.htaccess</code></td>
                <!-- Bayt biçimi panelin TEK kaynağından gelir (utils/bytes.ts). Burada ham sayı ve
                     koda gömülü bir "B" vardı: hem 12 KiB'lık bir dosya "12043 B" diye okunuyordu
                     hem de birim İngilizce/Türkçe ayrımını hiç görmüyordu. -->
                <td class="num-col">{{ boyutMetni(file.sizeBytes) }}</td>
                <td class="date-col">{{ formatDate(file.lastWriteUtc) }}</td>
                <td class="action-col">
                  <button
                    class="secondary compact"
                    :disabled="inspecting"
                    @click="inspect(file.relativeDirectory)">
                    {{ t("panel.htaccess.files.inspect") }}
                  </button>
                </td>
              </tr>
            </tbody>
          </table>
        </div>
      </section>

      <!-- ============================ İNCELEME ============================ -->
      <section class="htaccess-section">
        <div class="section-head">
          <div>
            <h4>{{ t("panel.htaccess.inspect.title", { path: inspection?.virtualPath ?? "/" }) }}</h4>
            <p class="meta">{{ t("panel.htaccess.inspect.hint") }}</p>
          </div>
        </div>

        <template v-if="inspection">
          <div class="summary-strip">
            <article class="summary-item">
              <span class="tile-label">{{ t("panel.htaccess.inspect.rewriteEngine") }}</span>
              <strong>{{ inspection.rewriteEngineEnabled ? t("panel.status.enabled") : t("panel.status.disabled") }}</strong>
            </article>
            <article class="summary-item">
              <span class="tile-label">{{ t("panel.htaccess.inspect.ruleCount") }}</span>
              <strong>{{ inspection.rewriteRuleCount }}</strong>
            </article>
            <article class="summary-item">
              <span class="tile-label">{{ t("panel.htaccess.inspect.fileCount") }}</span>
              <strong>{{ inspection.files.length }}</strong>
            </article>
            <article class="summary-item">
              <span class="tile-label">{{ t("panel.htaccess.inspect.unsupportedCount") }}</span>
              <strong>{{ inspection.unsupported.length }}</strong>
            </article>
          </div>

          <!-- RewriteEngine kapaliyken kural saymak yaniltir: kurallar VAR ama HICBIRI calismaz. -->
          <p v-if="!inspection.rewriteEngineEnabled && inspection.rewriteRuleCount > 0" class="notice error" role="alert">
            {{ t("panel.htaccess.inspect.rewriteEngineOff") }}
          </p>

          <p v-if="redosFindings.length > 0" class="notice error" role="alert">
            {{ t("panel.htaccess.inspect.redosWarning", { count: redosFindings.length }) }}
          </p>

          <p v-if="inspection.diagnostics.length === 0" class="empty-state">
            {{ t("panel.htaccess.inspect.noFindings") }}
          </p>
          <ul v-else class="finding-list">
            <li v-for="(finding, index) in sortedDiagnostics" :key="`${finding.code}-${index}`" class="finding">
              <span :class="['finding-badge', diagnosticLevelName(finding.level)]">
                {{ t(`panel.htaccess.level.${diagnosticLevelName(finding.level)}`) }}
              </span>
              <div class="finding-body">
                <strong>{{ finding.directive || finding.code }}</strong>
                <!-- MESAJ SUNUCUDAN OLDUGU GIBI GELIR. Motorun katalogunda her desteklenmeyen
                     direktifin GEREKCESI yazili; arayuz burada kendi metnini uydurursa iki
                     anlatim olusur ve biri guncellenmeden kalir. -->
                <p>{{ finding.message }}</p>
                <small>
                  <code>{{ finding.code }}</code>
                  <template v-if="finding.file"> · {{ shortFile(finding.file) }}</template>
                  <template v-if="finding.line > 0">:{{ finding.line }}</template>
                </small>
              </div>
            </li>
          </ul>
        </template>
        <p v-else class="empty-state">{{ t("panel.htaccess.inspect.pick") }}</p>
      </section>

      <!-- ============================ URL BENZETİMİ ============================
           SIRA DEĞİŞTİ: bu bölüm eskiden "Söz dizimi denetimi"nin ALTINDAYDI, yani kullanıcı
           dosyayı KAYDETTİKTEN sonra deneyebiliyordu. Ekranın izlediği yol "dosyam var mı →
           doğru mu → çalışıyor mu → yaz"dır ve üç bölüm de aynı kaynağı okur:
           Dosyalar/İnceleme/Benzetim SİTENİN DİSKTEKİ dosyalarına bakar, Söz dizimi denetimi ise
           kullanıcının HENÜZ YAZMADIĞI taslağına. Okuma bölümleri artık bir arada, yazma bölümü
           en sonda; kaydetmeden önce "şu an ne oluyor" sorusunun cevabı elde olur. -->
      <section class="htaccess-section">
        <div class="section-head">
          <div>
            <h4>{{ t("panel.htaccess.simulate.title") }}</h4>
            <p class="meta">{{ t("panel.htaccess.simulate.hint") }}</p>
          </div>
        </div>

        <label class="setting-row">
          <span>{{ t("panel.htaccess.simulate.path") }}</span>
          <input v-model="simulatePath" spellcheck="false" placeholder="/urun/5" />
          <small>{{ t("panel.htaccess.simulate.pathHelp") }}</small>
        </label>

        <label class="setting-row">
          <span>{{ t("panel.htaccess.simulate.method") }}</span>
          <select v-model="simulateMethod">
            <option v-for="verb in methods" :key="verb" :value="verb">{{ verb }}</option>
          </select>
        </label>

        <label class="setting-row checkbox-row">
          <span>{{ t("panel.htaccess.simulate.https") }}</span>
          <input v-model="simulateHttps" type="checkbox" />
          <small>{{ t("panel.htaccess.simulate.httpsHelp") }}</small>
        </label>

        <div class="action-row">
          <button class="secondary" :disabled="simulating || simulatePath.trim().length === 0" @click="runSimulate">
            {{ simulating ? t("panel.htaccess.simulate.running") : t("panel.htaccess.simulate.run") }}
          </button>
        </div>

        <div v-if="simulation" class="summary-strip">
          <article class="summary-item">
            <span class="tile-label">{{ t("panel.htaccess.simulate.result") }}</span>
            <strong>{{ t(`panel.htaccess.simulate.action.${simulation.action}`) }}</strong>
          </article>
          <article v-if="simulation.target" class="summary-item">
            <span class="tile-label">{{ t("panel.htaccess.simulate.target") }}</span>
            <strong><code>{{ simulation.target }}</code></strong>
          </article>
          <article v-if="simulation.statusCode > 0" class="summary-item">
            <span class="tile-label">{{ t("panel.htaccess.simulate.status") }}</span>
            <strong>{{ simulation.statusCode }}</strong>
          </article>
          <article class="summary-item">
            <span class="tile-label">{{ t("panel.htaccess.simulate.appliedFiles") }}</span>
            <strong>{{ simulation.detail.appliedFiles.length }}</strong>
          </article>
        </div>

        <!-- Benzetim CANLI DEGILDIR: motor IIS icinde yuklu degilse gercek istek bu karari
             ALMAZ. Bunu her sonucun yaninda soylemek zorundayiz. -->
        <p v-if="simulation && !report.engineLoadedInIis" class="notice" role="status">
          {{ t("panel.htaccess.simulate.notLive") }}
        </p>
      </section>

      <!-- ============================ SÖZ DİZİMİ DENETİMİ ============================ -->
      <section class="htaccess-section">
        <div class="section-head">
          <div>
            <h4>{{ t("panel.htaccess.validate.title") }}</h4>
            <p class="meta">{{ t("panel.htaccess.validate.hint") }}</p>
          </div>
        </div>

        <label class="setting-row align-start">
          <span>{{ t("panel.htaccess.validate.content") }}</span>
          <textarea v-model="draft" rows="10" spellcheck="false" :placeholder="validatePlaceholder"></textarea>
          <small>{{ t("panel.htaccess.validate.contentHelp") }}</small>
        </label>

        <label class="setting-row">
          <span>{{ t("panel.htaccess.validate.virtualPath") }}</span>
          <input v-model="draftPath" spellcheck="false" placeholder="/" />
          <small>{{ t("panel.htaccess.validate.virtualPathHelp") }}</small>
        </label>

        <div class="action-row">
          <button class="secondary" :disabled="validating || draft.trim().length === 0" @click="runValidate">
            {{ validating ? t("panel.htaccess.validate.running") : t("panel.htaccess.validate.run") }}
          </button>
          <!-- KAYDETME buraya konuldu: kullanıcı metni ZATEN burada yazıp denetliyordu, ama
               kaydetmek için Dosyalar ekranına gidip elle yapıştırmak zorundaydı. Kaydetme
               kendi doğrulamasını çalıştırır; ölümcül bulguda bilinçli onay ister. -->
          <button v-bind="writeGate(saving || validating || draft.trim().length === 0)" @click="saveDraft">
            {{ saving ? t("panel.htaccess.saving") : t("panel.htaccess.save.run") }}
          </button>
          <span class="meta">{{ t("panel.htaccess.save.hint") }}</span>
        </div>

        <template v-if="validation">
          <p v-if="validation.usable" class="notice" role="status">
            {{ t("panel.htaccess.validate.usable") }}
          </p>
          <p v-else class="notice error" role="alert">
            {{ t("panel.htaccess.validate.notUsable") }}
          </p>

          <ul v-if="validation.diagnostics.length > 0" class="finding-list">
            <li v-for="(finding, index) in validation.diagnostics" :key="`v-${index}`" class="finding">
              <span :class="['finding-badge', diagnosticLevelName(finding.level)]">
                {{ t(`panel.htaccess.level.${diagnosticLevelName(finding.level)}`) }}
              </span>
              <div class="finding-body">
                <strong>{{ finding.directive || finding.code }}</strong>
                <p>{{ finding.message }}</p>
                <small><code>{{ finding.code }}</code><template v-if="finding.line > 0"> · {{ t("panel.htaccess.line", { line: finding.line }) }}</template></small>
              </div>
            </li>
          </ul>
          <p v-else class="empty-state">{{ t("panel.htaccess.validate.noFindings") }}</p>
        </template>
      </section>

      <!-- ============================ BİLİNÇLİ SAPMALAR ============================ -->
      <section class="htaccess-section">
        <div class="section-head">
          <div>
            <h4>{{ t("panel.htaccess.deviations.title") }}</h4>
            <p class="meta">{{ t("panel.htaccess.deviations.hint") }}</p>
          </div>
        </div>
        <ul class="deviation-list">
          <li v-for="key in report.deviations" :key="key">{{ t(key) }}</li>
        </ul>
      </section>
    </template>
  </section>
</template>

<script setup lang="ts">
/*
 * SITE BASINA .htaccess EKRANI.
 *
 * NEDEN AYRI BIR GORUNUM, SiteHostingView'in ICINE GOMULU DEGIL:
 * SiteHostingView zaten 1270 satir ve on bir bolumu var. Bu ekran tek basina bes bolum
 * (kip, dosyalar, inceleme, dogrulama, benzetim) ve kendi yukleme/hata durumlarini tasiyor;
 * icine gomseydik o dosya yariya yakin buyur ve iki farkli konunun durumu ayni script blogunda
 * karisirdi. Ayri bir gorunum olarak, SiteHostingView'in sol menusune TEK bolum olarak
 * baglaniyor (WebsiteWorkspaceView'in gorunumleri cocuk olarak kullanmasiyla ayni desen).
 *
 * NEDEN YENI BIR ARAC SEKMESI DEGIL: .htaccess site basina bir BARINDIRMA ayaridir; PHP surumu
 * ve varsayilan belgelerle ayni yerde aranir. Ayri bir arac satiri, kullaniciyi "Barindirma"
 * icinde aramaktan alikoymazdi ve WebsiteWorkspaceView (baska bir is kolunun dosyasi)
 * degistirilmek zorunda kalirdi.
 */
import { computed, ref, watch } from "vue";
import { useI18n } from "vue-i18n";
import {
  diagnosticLevelName,
  getHtaccessReport,
  getServerHtaccessMode,
  inspectHtaccess,
  setHtaccessMode,
  setServerHtaccessMode,
  simulateHtaccess,
  validateHtaccess,
  type HtaccessDiagnostic,
  type HtaccessInspectionResult,
  type HtaccessMode,
  type HtaccessParseResult,
  type HtaccessServerSwitchState,
  type HtaccessSimulationResult,
  type HtaccessSiteReport
} from "../api/htaccess";
import { getFileWorkspace, saveFile } from "../api/files";
import { useConfirm } from "../components/ConfirmDialog.vue";
import { extractApiMessage, useAuthStore } from "../stores/auth";
import { useSitesStore } from "../stores/sites";
import { useSubscriptionGate } from "../composables/useSubscriptionGate";
import { formatBytes } from "../utils/bytes";
import { formatDateTime } from "../utils/datetime";

const { t } = useI18n();
// Askidaki abonelikte YAZMA dugmesi bastan devre disi + sebebi title/aria ile soylenir.
// Okuma dugmeleri (incele, denetle, benzet) kapiya girmez.
const { writeGate } = useSubscriptionGate();
const { confirm } = useConfirm();
const sitesStore = useSitesStore();
const authStore = useAuthStore();
const siteName = computed(() => sitesStore.selectedSiteName);

// Sunucu geneli anahtar YÖNETİCİ işidir: sunucudaki her siteyi aynı anda etkiler, dolayısıyla
// bayinin de kapsamı dışındadır. Uç zaten `[Authorize(Roles = "Administrator")]` ile kapalı;
// buradaki denetim gizleme amaçlı, KORUMA DEĞİL — koruma sunucudadır.
const isAdmin = computed(() => authStore.session?.user?.role === "Administrator");

const serverSwitch = ref<HtaccessServerSwitchState | null>(null);
const serverBusy = ref(false);
const serverNotice = ref("");
const serverNoticeError = ref(false);

const report = ref<HtaccessSiteReport | null>(null);
const inspection = ref<HtaccessInspectionResult | null>(null);
const validation = ref<HtaccessParseResult | null>(null);
const simulation = ref<HtaccessSimulationResult | null>(null);

const busy = ref(false);
const saving = ref(false);
const inspecting = ref(false);
const validating = ref(false);
const simulating = ref(false);
const notice = ref("");
const noticeError = ref(false);

const selectedMode = ref<HtaccessMode>("off");
const note = ref("");
const draft = ref("");
const draftPath = ref("/");
const simulatePath = ref("/");
const simulateMethod = ref("GET");
const simulateHttps = ref(false);

const methods = ["GET", "POST", "HEAD", "PUT", "DELETE"];

const validatePlaceholder = "RewriteEngine On\nRewriteRule ^urun/([0-9]+)$ /index.php?id=$1 [L]";

const modeChoices = computed(() => [
  { id: "off" as const, label: t("panel.htaccess.mode.off"), hint: t("panel.htaccess.mode.offHint") },
  { id: "observe" as const, label: t("panel.htaccess.mode.observe"), hint: t("panel.htaccess.mode.observeHint") },
  { id: "enforce" as const, label: t("panel.htaccess.mode.enforce"), hint: t("panel.htaccess.mode.enforceHint") }
]);

function modeLabel(mode: HtaccessMode): string {
  return t(`panel.htaccess.mode.${mode}`);
}

/**
 * DÜRÜSTLÜK ŞERİDİNDEKİ UYARI SAYILARI.
 *
 * İki sayaç da şablondaki `v-if` koşullarının BİREBİR aynısını okur; ikinci bir kural yazılsaydı
 * (ör. "dosya yoksa iki uyarı vardır") sayı ile ekranda görünen blok sayısı zamanla ayrışır ve
 * başlıkta yanlış bir sayı yazardı — tam da gizlemekle suçlanacağımız yer.
 *
 * `engelUyariSayisi` yalnız ENGEL sayar (özellik neden çalışmıyor); `bilgiUyariSayisi` bilgi
 * düzeyindekileri. Katlanan blok yalnız ikincisini taşır.
 */
const engelUyariSayisi = computed(() => {
  const rapor = report.value;
  if (!rapor) return 0;

  return (rapor.engineBlockers.length > 0 ? 1 : 0) +
    (rapor.support === "no_managed_code" || rapor.support === "runtime_version" ? 1 : 0) +
    (rapor.hasFiles && rapor.mode === "off" ? 1 : 0);
});

const bilgiUyariSayisi = computed(() => {
  const rapor = report.value;
  if (!rapor) return 0;

  return (rapor.support === "unknown" ? 1 : 0) +
    (rapor.serverMode === "off" ? 1 : 0) +
    (rapor.hasFiles && !rapor.hasRootFile ? 1 : 0) +
    (!rapor.hasFiles ? 1 : 0);
});

/**
 * ReDoS riski TAŞIYAN desenler. Kod METNE değil KODA bakar: motor mesajı iyileştirse ya da
 * çevirse bile bu süzgeç çalışmaya devam eder (motorun kendi kuralı da bu).
 */
const redosFindings = computed<HtaccessDiagnostic[]>(() =>
  (inspection.value?.diagnostics ?? []).filter((d) => d.code === "RISKLI_DUZENLI_IFADE")
);

/**
 * Bulgular AĞIRLIĞA göre sıralanır (ölümcül önce).
 *
 * Motorun döndürdüğü sıra DOSYA sırasıdır; kullanıcıya olduğu gibi göstermek, kökteki bir
 * "bilgi" satırını alt dizindeki ölümcül hatanın ÜSTÜNE koyardı. Sıralama kopyalanmıyor:
 * `slice()` ile yeni dizi üretiliyor, yoksa `sort` gelen diziyi yerinde değiştirir ve
 * `redosFindings` gibi diğer türetilmiş değerler beklenmedik sırayla yeniden hesaplanırdı.
 */
const sortedDiagnostics = computed<HtaccessDiagnostic[]>(() =>
  (inspection.value?.diagnostics ?? []).slice().sort((a, b) => b.level - a.level)
);

function shortFile(path: string): string {
  const parts = path.replace(/\\/g, "/").split("/");
  return parts.slice(-2).join("/");
}

/** Tarih biçimi panelin TEK kaynağından (utils/datetime.ts); ekran kendi kopyasını taşımaz. */
function formatDate(value: string): string {
  return formatDateTime(value, "-");
}

/** Bayt biçimi panelin geri kalanıyla AYNI (bkz. utils/bytes.ts); ekran kendi birimini uydurmaz. */
function boyutMetni(value: number): string {
  return formatBytes(value, t);
}

function fail(error: unknown, fallbackKey: string) {
  notice.value = extractApiMessage(error) || t(fallbackKey);
  noticeError.value = true;
}

/**
 * Sunucu geneli anahtarı OKU.
 *
 * Uç yalnız Administrator'a açık, bu yüzden kiracıda hiç ÇAĞRILMIYOR — çağırıp 403'ü yutmak,
 * konsolu her açılışta yanlış bir hatayla doldururdu. Yönetici değilse bölüm zaten görünmüyor.
 */
async function loadServerSwitch() {
  if (!isAdmin.value) {
    serverSwitch.value = null;
    return;
  }

  try {
    serverSwitch.value = await getServerHtaccessMode();
  } catch (error) {
    // Okunamadıysa DURUM UYDURULMAZ: `null` kalır ve arayüz "bilinmiyor" der. "off" varsaymak,
    // açık bir sunucuyu kapalı göstermek olurdu.
    serverSwitch.value = null;
    serverNotice.value = extractApiMessage(error) || t("panel.htaccess.server.loadFailed");
    serverNoticeError.value = true;
  }
}

async function saveServerMode(mode: HtaccessMode) {
  serverBusy.value = true;
  serverNotice.value = "";
  serverNoticeError.value = false;

  try {
    serverSwitch.value = await setServerHtaccessMode(mode, note.value.trim());
    serverNotice.value = t("panel.htaccess.server.saved", { mode: modeLabel(serverSwitch.value.mode) });

    // Sitenin raporu da TAZELENİR: etkin kip sunucu ile sitenin en düşüğü olduğu için, sunucu
    // anahtarı değişince bu ekrandaki uyarılar da değişir. Tazelemezsek kullanıcı anahtarı
    // açtığı hâlde ekranda hâlâ "sunucu geneli KAPALI" yazardı.
    await load();
  } catch (error) {
    serverNotice.value = extractApiMessage(error) || t("panel.htaccess.server.saveFailed");
    serverNoticeError.value = true;
  } finally {
    serverBusy.value = false;
  }
}

async function load() {
  if (!siteName.value) {
    report.value = null;
    return;
  }

  busy.value = true;
  notice.value = "";
  try {
    const loaded = await getHtaccessReport(siteName.value);
    report.value = loaded;
    selectedMode.value = loaded.mode;
    inspection.value = loaded.inspection;
    // Taslak, KOK dosyanin sanal yoluyla baslar: kullanicinin en sik duzenledigi dosya odur.
    draftPath.value = "/";
  } catch (error) {
    report.value = null;
    fail(error, "panel.htaccess.loadFailed");
  } finally {
    busy.value = false;
  }
}

async function saveMode() {
  if (!siteName.value || !report.value) return;

  busy.value = true;
  notice.value = "";
  try {
    const saved = await setHtaccessMode(siteName.value, selectedMode.value, note.value.trim());
    report.value = saved;
    selectedMode.value = saved.mode;
    inspection.value = saved.inspection;
    notice.value = t("panel.htaccess.mode.saved", { mode: modeLabel(saved.mode) });
    noticeError.value = false;
  } catch (error) {
    // Kaydedilemedi -> secim SUNUCUDAKI degere geri alinir. Aksi halde ekranda "enforce"
    // gorunur, sunucuda "off" olurdu: panelin soyleyebilecegi en pahali yalan.
    selectedMode.value = report.value.mode;
    fail(error, "panel.htaccess.saveFailed");
  } finally {
    busy.value = false;
  }
}

async function inspect(directory: string) {
  if (!siteName.value) return;

  inspecting.value = true;
  notice.value = "";
  try {
    inspection.value = await inspectHtaccess(siteName.value, directory);
  } catch (error) {
    fail(error, "panel.htaccess.inspectFailed");
  } finally {
    inspecting.value = false;
  }
}

async function runValidate() {
  if (!siteName.value) return;

  validating.value = true;
  notice.value = "";
  try {
    validation.value = await validateHtaccess(siteName.value, draft.value, draftPath.value.trim() || "/");
  } catch (error) {
    validation.value = null;
    fail(error, "panel.htaccess.validate.failed");
  } finally {
    validating.value = false;
  }
}

/**
 * TASLAĞI DOSYAYA YAZ.
 *
 * NEDEN YENİ BİR UÇ YOK: `.htaccess` sıradan bir metin dosyasıdır ve panelin dosya yazma yolu
 * (`PUT /files`) zaten sanal alan denetimi, kota, kiracı kapsamı ve DENETİM KAYDI taşıyor. Bu iş
 * için ikinci bir yazma yolu açmak, o dört korumayı ikinci kez (ve eksik) yazmak olurdu.
 *
 * SIRA ÖNEMLİ:
 *  1. Doğrula. Ölümcül bulgu varsa DOĞRUDAN yazılmaz — bozuk bir `.htaccess` sitenin TAMAMINI
 *     çökertebilir. Ama kullanıcı büsbütün kilitlenmez: bulgular gösterilip bilinçli onay
 *     ("yine de kaydet") istenir. Motor ölümcül dosyayı zaten atlıyor, yani karar kullanıcının.
 *  2. Hedef yolu ÖLÇEREK kur (bkz. hedefYolAsync) — uydurma.
 *  3. Yazdıktan sonra raporu ve incelemeyi TAZELE, yoksa ekran kaydedilmiş içeriği değil eski
 *     durumu göstermeye devam eder.
 */
async function saveDraft() {
  if (!siteName.value || saving.value) return;

  const sanalYol = draftPath.value.trim() || "/";
  const goreli = goreliDizin(sanalYol);
  if (goreli === null) {
    notice.value = t("panel.htaccess.save.badPath");
    noticeError.value = true;
    return;
  }

  saving.value = true;
  notice.value = "";
  try {
    const sonuc = await validateHtaccess(siteName.value, draft.value, sanalYol);
    validation.value = sonuc;

    if (!sonuc.usable) {
      const olumcul = sonuc.diagnostics.filter((bulgu) => diagnosticLevelName(bulgu.level) === "fatal");
      const onay = await confirm({
        title: t("panel.htaccess.save.fatalTitle"),
        message: t("panel.htaccess.save.fatalMessage"),
        // Bulgular ONAY KUTUSUNDA da görünür: kullanıcı neyi göze aldığını okumadan onaylamasın.
        impact: olumcul.map((bulgu) => `${bulgu.directive || bulgu.code}: ${bulgu.message}`),
        tone: "neutral",
        confirmLabel: t("panel.htaccess.save.anyway")
      });

      if (!onay) {
        notice.value = t("panel.htaccess.save.cancelled");
        noticeError.value = true;
        return;
      }
    }

    const hedef = await hedefYolAsync(goreli);
    if (hedef === null) {
      notice.value = t("panel.htaccess.save.rootMismatch");
      noticeError.value = true;
      return;
    }

    await saveFile(hedef, draft.value);

    await load();
    // `load` taslak yolunu köke çeker; kullanıcının çalıştığı dizin geri konur, sonra o dizin
    // yeniden incelenir — aksi halde ekran az önce yazılan dosyayı hiç göstermezdi.
    draftPath.value = sanalYol;
    await inspect(goreli);

    // Başarı mesajı, TAZELEMENİN hatasının ÜSTÜNE yazılmaz: dosya yazıldı ama rapor okunamadıysa
    // kullanıcı ekrandaki bilginin bayat olduğunu bilmeli.
    if (!noticeError.value) {
      notice.value = t("panel.htaccess.save.saved", { path: hedef });
      noticeError.value = false;
    }
  } catch (error) {
    fail(error, "panel.htaccess.save.failed");
  } finally {
    saving.value = false;
  }
}

/**
 * Sanal yolu (`/`, `/blog`) site köküne GÖRELİ dizine çevirir. Yukarı çıkma denemesi
 * (<c>..</c>) burada `null` döner: arka uç da reddederdi ama kullanıcı sebebi istek gitmeden
 * görmeli.
 */
function goreliDizin(virtualPath: string): string | null {
  const parcalar = virtualPath.replace(/\\/g, "/").split("/").filter((parca) => parca.length > 0);
  if (parcalar.some((parca) => parca === "." || parca === "..")) {
    return null;
  }

  return parcalar.join("/");
}

/**
 * DOSYA YÖNETİCİSİNİN sanal yolunu kurar: `sites/<site>/<...>/.htaccess`.
 *
 * ÖLÇÜLDÜ, VARSAYILMADI: dosya yöneticisinin kökü sitenin UYGULAMA kökü
 * (`ApplicationRootPath`, yoksa `PhysicalPath`), `.htaccess` motorunun kökü ise BELGE köküdür
 * (`PhysicalPath`). Laravel benzeri yerleşimlerde belge kökü uygulama kökünün ALTINDADIR
 * (`.../public`), yani ikisi aynı olmayabilir. İki kök doğrudan sunucudan okunup farkları
 * hesaplanır; sabit bir `public` varsayımı yazsaydık dosya yanlış klasöre yazılırdı.
 *
 * Belge kökü dosya yöneticisinin sanal alanının İÇİNDE değilse `null` döner: yolu tahmin edip
 * rastgele bir yere yazmaktansa kaydetmemek doğrudur.
 */
async function hedefYolAsync(goreliDizinYolu: string): Promise<string | null> {
  const siteKoku = `sites/${siteName.value}`;
  const calismaAlani = await getFileWorkspace(siteKoku);
  const onEk = belgeKokuOneki(calismaAlani.physicalPath, report.value?.documentRoot ?? "");
  if (onEk === null) {
    return null;
  }

  return [siteKoku, onEk, goreliDizinYolu, ".htaccess"].filter((parca) => parca.length > 0).join("/");
}

/** İki mutlak yol arasındaki fark; eşitse boş dize, kapsamıyorsa `null`. */
function belgeKokuOneki(dosyaKoku: string, belgeKoku: string): string | null {
  const normalize = (yol: string) => yol.replace(/\\/g, "/").replace(/\/+$/, "");
  const kok = normalize(dosyaKoku);
  const belge = normalize(belgeKoku);
  if (kok.length === 0 || belge.length === 0) {
    return null;
  }

  if (kok.toLowerCase() === belge.toLowerCase()) {
    return "";
  }

  return belge.toLowerCase().startsWith(`${kok.toLowerCase()}/`) ? belge.slice(kok.length + 1) : null;
}

async function runSimulate() {
  if (!siteName.value) return;

  simulating.value = true;
  notice.value = "";
  try {
    simulation.value = await simulateHtaccess(siteName.value, {
      requestPath: simulatePath.value.trim(),
      method: simulateMethod.value,
      https: simulateHttps.value
    });
  } catch (error) {
    simulation.value = null;
    fail(error, "panel.htaccess.simulate.failed");
  } finally {
    simulating.value = false;
  }
}

watch(siteName, load, { immediate: true });

// Sunucu anahtarı SİTEYE bağlı değildir; site değiştikçe yeniden okumak gereksiz istek olurdu.
// Oturumun rolü belirlendiği anda bir kez okunur (rol başta `undefined` olabilir: oturum
// bilgisi ayrı bir istekle geliyor; `immediate` ile o an okuyup boş dönmek "bilinmiyor"u
// kalıcılaştırırdı).
watch(isAdmin, loadServerSwitch, { immediate: true });
</script>

<style scoped>
.htaccess-panel {
  display: flex;
  flex-direction: column;
  gap: 1rem;
}

/*
 * BÖLÜM KABUĞU — ÖLÇÜLMÜŞ BİR BOŞLUK DOLDURULUYOR.
 *
 * Bu ekranın altı bölümü `class="settings-card"` taşıyordu ve o sınıf YALNIZCA
 * SiteHostingView'in `<style scoped>` bloğunda tanımlı. Vue'da kapsamlı stil bir alt bileşenin
 * KÖK ögesine geçer, İÇİNDEKİLERE geçmez: bu görünümün kökü `.htaccess-panel`, altı bölüm ise
 * onun çocukları. Yani altı bölümün hiçbiri bir kart görünümü ALMIYORDU — ne kenarlık, ne dolgu,
 * ne zemin. Ekran, başlıkları olan tek parça bir metin duvarıydı.
 *
 * KART İÇİNDE KART YAPILMADI: bu görünümün kendisi ana ekranda zaten bir `.settings-card`ın
 * içinde çiziliyor (SiteHostingView, "htaccess" bölümü). İç içe iki çerçeve derinlik değil
 * gürültü üretir. Bunun yerine bölümler ÜST ÇİZGİ + nefes payıyla ayrılıyor: aynı kart içinde
 * ritim kuran, Plesk'in ayar sayfalarında da kullanılan ayrım.
 */
.htaccess-section {
  display: flex;
  flex-direction: column;
  gap: 0.75rem;
  padding-top: 1.25rem;
  border-top: 1px solid var(--panel-border);
}

.section-head {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
  flex-wrap: wrap;
}

/* `.action-row` da bu görünümde hiç tanımlı değildi (FilesView/SiteHostingView kendi kapsamlı
   kopyalarını taşıyor): düğme ile yanındaki açıklama metni satır içinde, boşluksuz akıyordu. */
.action-row {
  display: flex;
  align-items: center;
  gap: 10px;
  flex-wrap: wrap;
}

.action-row button {
  width: auto;
}

/* Bildirim şeridinin İÇ düzeni (konumu base.css'te): metin ile kapatma düğmesi yan yana. */
.page-notice {
  display: flex;
  align-items: flex-start;
  gap: 10px;
}

.notice-text {
  flex: 1;
  min-width: 0;
}

.notice-dismiss {
  flex: 0 0 auto;
  width: 22px;
  height: 22px;
  min-height: 0;
  padding: 0;
  border: 0;
  border-radius: var(--radius-sm);
  background: transparent;
  color: inherit;
  font-size: 1rem;
  line-height: 1;
  opacity: 0.7;
  box-shadow: none;
}

.notice-dismiss:not(:disabled):hover {
  background: color-mix(in srgb, currentcolor 12%, transparent);
  opacity: 1;
  transform: none;
  filter: none;
  box-shadow: none;
}

.truth-strip {
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
}

/* KATLANAN BİLGİ UYARILARI. Başlık bir düğme gibi görünür (tıklanabilirliği belli olsun) ama
   uyarı kutusu gibi ağır değildir; içindekiler açıldığında normal uyarı biçimini korur. */
.truth-more > summary {
  padding: 7px 11px;
  border: 1px dashed var(--panel-border);
  border-radius: var(--radius-sm);
  color: var(--text-soft);
  font-weight: 600;
  cursor: pointer;
  list-style-position: inside;
}

.truth-more > summary:hover {
  border-color: color-mix(in srgb, var(--accent-1) 45%, transparent);
  color: var(--text-strong);
}

.truth-more > summary:focus-visible {
  outline: 2px solid color-mix(in srgb, var(--accent-1) 70%, transparent);
  outline-offset: 2px;
}

.truth-more[open] > summary {
  margin-bottom: 0.5rem;
}

.truth-more > :not(summary) {
  margin-bottom: 0.5rem;
}

.truth-more > :last-child {
  margin-bottom: 0;
}

/*
 * DOSYA TABLOSU. base.css'te hiçbir `table`/`th`/`td` kuralı yok; `.data-table` kabuğu blok
 * listeler için yazılmış (kenarlık + yatay kaydırma). Tablo o kabuğun İÇİNE alındı, hücre biçimi
 * burada verildi: başlık satırı panelin öbür listeleriyle aynı ağırlıkta (`.data-row.table-head`
 * ile eşleşen zemin ve punto), satır ayırıcıları aynı kenarlık renginde.
 */
.file-table {
  width: 100%;
  min-width: 520px;
  border-collapse: collapse;
}

.file-table th,
.file-table td {
  padding: 9px 11px;
  text-align: left;
  vertical-align: middle;
  border-bottom: 1px solid var(--panel-border);
  overflow-wrap: anywhere;
}

.file-table thead th {
  background: var(--surface-2);
  color: var(--text-soft);
  font-size: 0.82rem;
  font-weight: 600;
}

.file-table tbody tr:last-child td {
  border-bottom: 0;
}

.file-table tbody tr:hover td {
  background: color-mix(in srgb, var(--accent-1) 4%, transparent);
}

/* Sayı sütunu sağa hizalı ve eşit genişlikte rakamlarla: iki dosyanın boyutunu karşılaştırmak
   için okumak değil bakmak yetsin. */
.file-table .num-col {
  text-align: right;
  white-space: nowrap;
  font-variant-numeric: tabular-nums;
}

.file-table .date-col {
  white-space: nowrap;
  color: var(--text-muted);
  font-variant-numeric: tabular-nums;
}

.file-table .action-col {
  width: 1%;
  text-align: right;
  white-space: nowrap;
}

.file-table .action-col button {
  width: auto;
}

/* ETKİN SUNUCU KİPİ, yalnız "soluk düğme" ile değil görünür bir işaretle söylenir. Kenarlık
   kalınlığı + zemin birlikte çalışır: yalnız renge dayanan bir işaret renk körü kullanıcıda
   hiçbir şey anlatmaz. */
.mode-switch-row button[aria-pressed="true"] {
  border-color: var(--accent-1);
  background: color-mix(in srgb, var(--accent-1) 16%, var(--surface-3));
  color: var(--text-strong);
  box-shadow: inset 0 -2px var(--accent-1);
  opacity: 1;
}

.mode-choices {
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
  margin-bottom: 1rem;
}

/*
 * RENK BELİRTEÇLERİ DÜZELTİLDİ. Bu dosyanın üç kuralı `var(--border, #d8dee9)` yazıyordu ama
 * panelde `--border` diye bir belirteç YOK (base.css'te kenarlık belirteci `--panel-border`).
 * Yani üç yerde de daima yedek değer, yani AÇIK GRİ bir çizgi kullanılıyordu — panelin
 * varsayılan koyu temasında bu, çevresindeki bütün kenarlıklardan belirgin biçimde parlak bir
 * çizgi demekti ve bu bölümler "seçili/vurgulu" gibi görünüyordu. Doğru belirteç kullanılınca
 * kenarlıklar hem koyu hem açık temada panelin geri kalanıyla aynı tonda kalıyor.
 */
.mode-choice {
  display: flex;
  align-items: flex-start;
  gap: 0.6rem;
  padding: 0.6rem 0.75rem;
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-sm);
  background: var(--surface-1);
  cursor: pointer;
}

.mode-choice:hover {
  border-color: color-mix(in srgb, var(--accent-1) 45%, transparent);
}

/* SEÇİLİ KİP yalnız küçük radyo noktasıyla değil, kutunun tamamıyla söylenir: üç seçenekten
   hangisinin açık olduğu bir bakışta görünmeliydi. */
.mode-choice:has(input:checked) {
  border-color: var(--accent-1);
  background: color-mix(in srgb, var(--accent-1) 10%, var(--surface-1));
}

.mode-choice:focus-within {
  outline: 2px solid color-mix(in srgb, var(--accent-1) 70%, transparent);
  outline-offset: 2px;
}

/* Sunucu geneli anahtar site ayarından GÖRSEL OLARAK da ayrılır: kapsamı bu sitenin değil,
   sunucunun tamamıdır. Üstteki ayırıcı çizgi bu sınırı gösterir. */
.server-switch {
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
  margin-top: 1rem;
  padding-top: 1rem;
  border-top: 1px solid var(--panel-border);
}

.server-switch small {
  opacity: 0.75;
}

.mode-text {
  display: flex;
  flex-direction: column;
}

.mode-text small {
  opacity: 0.75;
}

.finding-list {
  list-style: none;
  margin: 0.75rem 0 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
}

.finding {
  display: flex;
  gap: 0.6rem;
  align-items: flex-start;
  padding: 0.5rem 0.6rem;
  border-left: 3px solid var(--panel-border);
  background: var(--surface-2);
  border-radius: var(--radius-sm);
}

.finding-body p {
  margin: 0.2rem 0;
}

.finding-body small {
  opacity: 0.7;
}

/*
 * Rozet renkleri AGIRLIGA gore. "unsupported" bilerek uyari renginde: kullanicinin kurali
 * CALISMIYOR ve bunu bir bilgi satiri gibi gostermek, tam da gizlemeye calistigimiz sey olurdu.
 *
 * BÜYÜK HARFE ÇEVİRME KALDIRILDI — Türkçe yerel tuzağı. `index.html` `<html lang="tr">` yazıyor
 * ve dil değiştirildiğinde bu öznitelik GÜNCELLENMİYOR; tarayıcı `text-transform: uppercase`i
 * belgenin diline göre uyguladığı için İngilizce arayüzde "Info" rozeti "İNFO" diye çiziliyordu
 * (Türkçe noktalı büyük İ). Rozet zaten kalın ve renkli; okunurluk için büyük harf gerekmiyor,
 * ama yanlış harf gerçek bir hata. Renk TEK BAŞINA taşıyıcı değil: her rozetin METNİ de var
 * (`panel.htaccess.level.*`), yani ayrım renk körü kullanıcıda da okunur.
 *
 * Renkler artık panelin durum belirteçlerinden geliyor; metin rengi de birlikte veriliyor,
 * yoksa açık temada soluk bir zemin üstünde gövde rengiyle yazılıyordu ve ağırlık ayrımı
 * yalnızca zemin tonuna kalıyordu.
 */
.finding-badge {
  flex: 0 0 auto;
  min-width: 6.5rem;
  text-align: center;
  font-size: 0.75rem;
  font-weight: 700;
  padding: 0.15rem 0.4rem;
  border: 1px solid transparent;
  border-radius: var(--radius-sm);
}

.finding-badge.info {
  background: color-mix(in srgb, var(--accent-1) 16%, transparent);
  border-color: color-mix(in srgb, var(--accent-1) 30%, transparent);
  color: var(--accent-text);
}

.finding-badge.unsupported,
.finding-badge.warning {
  background: var(--warn-bg);
  border-color: color-mix(in srgb, var(--warn-1) 30%, transparent);
  color: var(--warn-text);
}

.finding-badge.error {
  background: var(--danger-bg);
  border-color: color-mix(in srgb, var(--danger-1) 30%, transparent);
  color: var(--danger-text);
}

/* "Ölümcül" ile "Hata" AYIRT EDİLEBİLMELİ: ikisi de kırmızı ailesinde ama ölümcül olan dolu
   zeminle öne çıkar — motor o dosyayı TAMAMEN atlar, sonucu daha ağırdır. */
.finding-badge.fatal {
  background: var(--danger-1);
  border-color: var(--danger-1);
  color: #fff1f1;
}

.deviation-list {
  margin: 0;
  padding-left: 1.2rem;
  display: flex;
  flex-direction: column;
  gap: 0.35rem;
}
</style>
