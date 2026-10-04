<template>
  <section class="panel module-shell server-health">
    <div class="module-head">
      <div>
        <!-- h3 -> h2. SAYILDI: alti sayfa panelinin ucu (Apps, Proxy, Cluster) sayfa basligini
             `h2` (19.2px), ucu (ServerHealth, Firewall, AppStore) `h3` (15.9px) yaziyordu.
             Ayni rutbe iki ayri boyda ciziliyor, yani sayfadan sayfaya gecerken baslik
             kuculup buyuyordu. Merdiven artik her yerde ayni: sayfa h2, bolum h3, alt blok h4. -->
        <h2>{{ t("panel.health.title") }}</h2>
        <p class="meta">{{ t("panel.health.subtitle") }}</p>
      </div>
      <button class="secondary compact" :disabled="!canReload" @click="load">{{ loading ? t('panel.common.loading') : t("panel.health.refresh") }}</button>
    </div>

    <p v-if="notice" :class="['notice', 'page-notice', { error: noticeError }]" :role="noticeError ? 'alert' : 'status'">{{ notice }}</p>

    <section v-if="dnsLoad.message.value || dns?.serviceStatus === 'Unknown'" class="health-read-error">
      <h3>{{ t('panel.health.dnsTitle') }}</h3>
      <SectionLoadError :message="dnsLoad.message.value || t('dns.health_unmeasurable')" :busy="!canReload" @retry="load" />
    </section>

    <!-- DNS erişilebilirliği: servisin "Running" olması YETMEZ, 53 portunun genel IP'de
         dinlemesi gerekir. Bu kart o farkı gösterir ve tek tıkla onarır. -->
    <!--
      "KURULU DEĞİL" DE BİR ÖLÇÜM SONUCUDUR.
      ÖNCESİ ÖLÇÜLDÜ: koşul `v-if="dns && dns.installed"` idi, yani Windows DNS Server rolü
      kurulu olmayan her sunucuda kart TÜMÜYLE KAYBOLUYORDU. Panel PowerShell çalıştırıp
      "rol yok" sonucunu ölçüyor, sonra kullanıcıya HİÇBİR ŞEY söylemiyordu. Kullanıcının
      gördüğü, "DNS erişilebilirliği" diye bir başlığın hiç var olmamasıydı — bu da iki ayrı
      soruyu cevapsız bırakıyordu: "panel DNS'i ölçtü mü?" ve "ölçtüyse ne buldu?".
      ÜÇÜNCÜ DURUM `idle` (kırmızı da yeşil de değil): kurulu olmaması bir ARIZA DEĞİL —
      alan adlarının DNS'ini dış sağlayıcıda tutan kurulumda tamamen normaldir. Ölçülmemiş
      bir arızaya kırmızı vermek, panelin en sık tekrarlanan yalanıdır.
    -->
    <article v-if="dns && dnsConfirmed && dns.serviceStatus !== 'Unknown'" :class="['dns-card', !dns.installed ? 'idle' : (dns.needsRepair ? 'bad' : dns.publiclyBound ? 'good' : 'idle')]">
      <div class="dns-head">
        <span :class="['state-dot', !dns.installed ? 'dot-idle' : (dns.needsRepair ? 'dot-danger' : dns.publiclyBound ? 'dot-ok' : 'dot-idle')]"></span>
        <div class="health-name">
          <strong>{{ t("panel.health.dnsTitle") }}</strong>
          <span class="svc-id">
            {{ !dns.installed ? t("panel.health.dnsNotInstalledHint") : (dns.publiclyBound ? t("panel.health.dnsGood") : t("panel.health.dnsBad")) }}
          </span>
        </div>
        <!-- Rol kuruluyken rozet "dışarıya yanıt veriyor mu"yu söyler; kurulu değilken o soru
             SORULAMAZ, dolayısıyla ne `ok` ne `danger` — rozet nötr kalır. -->
        <span v-if="!dns.installed" class="status-pill">{{ t("panel.health.dnsNotInstalled") }}</span>
        <span v-else :class="['status-pill', dns.needsRepair ? 'danger' : dns.publiclyBound ? 'ok' : '']">
          {{ dns.publiclyBound ? t("panel.health.dnsBound") : t("panel.health.dnsNotBound") }}
        </span>
        <!-- Onarım düğmesi YALNIZCA onarılacak bir şey varken görünür. Her zaman durduğunda
             kullanıcı "demek ki sorun var" diye tekrar tekrar basıyor, hiçbir şey değişmiyor
             (çünkü zaten sorun yok) ve panel bozukmuş gibi hissettiriyordu. -->
        <button v-if="dns.needsRepair" class="secondary compact" :disabled="!canRepairDns" @click="repairDns">
          {{ dnsBusy ? t("panel.health.dnsRepairing") : t("panel.health.dnsRepair") }}
        </button>
      </div>
      <!-- Rol kurulu değilken bu satırların HEPSİ boş/sıfır olurdu: "dinlenen adresler: —",
           "bölge sayısı: 0". Ölçülemeyen şeyi tire ile göstermek, ölçülüp boş çıkmış gibi
           okunur. Kurulu değilken kart tek cümleyle konuşur. -->
      <dl v-if="dns.installed" class="dns-facts">
        <div>
          <dt>{{ t("panel.health.dnsListening") }}</dt>
          <dd>{{ dns.listeningAddresses.length ? dns.listeningAddresses.join(", ") : "—" }}</dd>
        </div>
        <div>
          <dt>{{ t("panel.health.dnsExpected") }}</dt>
          <dd>{{ dns.expectedAddresses.length ? dns.expectedAddresses.join(", ") : "—" }}</dd>
        </div>
        <div v-if="dns.missingAddresses.length">
          <dt>{{ t("panel.health.dnsMissing") }}</dt>
          <!-- Kapsamli `.warn` sinifi SILINDI: adi "uyari" iken rengi `--danger-1` (kirmizi) idi,
               yani bu ekranda KEHRIBAR SEVIYE HIC YOKTU. Iki bulgu ayni kirmiziyla ciziliyordu:
               "dinlenmeyen adres" (gercek ariza) ve "gecikmeli baslatma kapali" (yalnizca uyari).
               Artik base.css'in paylasilan `.danger-text` / `.warn-text` sozlugu kullaniliyor. -->
          <dd class="danger-text">{{ dns.missingAddresses.join(", ") }}</dd>
        </div>
        <div>
          <dt>{{ t("panel.health.dnsZones") }}</dt>
          <dd>{{ dns.zoneCount }}</dd>
        </div>
        <div>
          <dt>{{ t("panel.health.dnsStartMode") }}</dt>
          <dd>
            <!-- Ham Windows değeri basılıyordu ("Auto" / "Manual"): Türkçe panelde de İngilizce,
                 üstelik "Auto" ile "Boot" arasındaki farkı bilmeyen için anlamsız. Bilinen
                 değerler çevrilir, BİLİNMEYEN OLDUĞU GİBİ kalır (bkz. startModeText). -->
            {{ startModeText(dns.startMode) }}
            <span v-if="dns.delayedAutoStart">({{ t("panel.health.dnsDelayedOn") }})</span>
            <span v-else class="warn-text">({{ t("panel.health.dnsDelayedOff") }})</span>
          </dd>
        </div>
        <div v-if="dns.lastAutoRepairUtc">
          <dt>{{ t("panel.health.dnsLastAutoRepair") }}</dt>
          <!-- Nobetci durumu sunucudan ANAHTAR (+ istege bagli teshis) olarak gelir; eskiden
               sabit Turkce cumleydi ve burada HAM basiliyordu. -->
          <!-- BAŞARISIZ ONARIM ARTIK BAŞARILI GİBİ OKUNMUYOR.
               ÖNCESİ ÖLÇÜLDÜ: bu satır NÖTR renkteydi. "Nöbetçi DNS'i yeniden başlattı ama
               hâlâ dinlemiyor" ile "onarım tuttu" ekranda birebir aynı gri satırdı.
               Renk kararı DİZGEDEN ÇIKARILMIYOR: metnin başına bakıp ("failed ile mi
               başlıyor") karar vermek, sunucu metni değiştirdiği gün sessizce bozulan bir
               yama olurdu. Sunucu artık AYRI bir alanla durumu söylüyor
               (`DnsServerHealthDto.LastAutoRepairSucceeded`) ve ekran yalnız onu okuyor.
               `=== false` KASITLI: alan üç değerlidir ve `null` "ölçülmedi" demektir —
               eski nöbetçi kaydı kırmızıya boyanmaz. -->
          <dd :class="{ 'danger-text': dns.lastAutoRepairSucceeded === false }">
            {{ formatDate(dns.lastAutoRepairUtc) }} — {{ serverText(dns.lastAutoRepairStatus) }}
          </dd>
        </div>
      </dl>
      <!--
        `details` İKİ AYRI ŞEY TAŞIYOR — ÖLÇÜLDÜ, İKİSİ AYRI GÖSTERİLİYOR.
        · Ölçüm TUTTUĞUNDA: makineye yazılmış bir teşhis dizgesi
          ("service=Running; startMode=Auto; listening=…; expected=…; zones=3").
          Çevrilemez ve çevrilmemeli; ama kartın en alt satırında kalıcı olarak durduğunda
          kullanıcının okuduğu son şey anlamadığı bir makine çıktısı oluyordu. `<details>`
          içine KATLANDI: bilgi SİLİNMİYOR (destek kaydına yapıştırılacak tek şey odur),
          yalnız istendiğinde açılıyor.
        · Ölçüm TUTMADIĞINDA: alan `dns.health_unmeasurable` ANAHTARINI taşır ve o, gerçek bir
          cümleye çevrilir. O cümle kartın CEVABIDIR; katlanmış bir kutuya saklanamaz.
        Ayrımı yapan kural yeni değil: panelin ortak `looksLikeMessageKey` +
        `splitCompositeMessage` sözlüğü (`utils/apiError`).
      -->
      <details v-if="dnsDetailsIsDiagnostic" class="dns-details-fold">
        <summary>{{ t("panel.health.dnsDetailsToggle") }}</summary>
        <p class="dns-details">{{ dns.details }}</p>
      </details>
      <p v-else-if="dns.details" class="dns-details-text">{{ serverText(dns.details) }}</p>
    </article>

    <!-- Alan adı tazeleme: panel güncellendiğinde YENİ varsayılanlar (DMARC, DKIM, MX…) yalnız yeni
         alan adlarına uygulanır. Eskiden eklenmiş alan adları eksik kalır ve bu eksiklik kendini
         aylar sonra "postalarım spam'e düşüyor" olarak belli eder. Panel eksiği kendisi ölçer;
         uygulama kararı kullanıcıya bırakılır. -->
    <!-- Yuklenirken kart NE IYI NE KOTU gorunur. Onceden veri gelmeden 'good' + yesil nokta
         basiliyordu: kullanici "her sey yolunda" diye bakip gecerken kart birden kirmiziya
         donuyordu. Olculmemis bir seyi "saglikli" gostermek, panelin en cok tekrarlanan hatasi. -->
    <article :class="['dns-card', domainsLoading || !domainsConfirmed || !domains ? 'idle' : (domainCheckFailed || onarilacakVar ? 'bad' : 'good')]">
      <div class="dns-head">
        <span :class="['state-dot', domainsLoading || !domainsConfirmed || !domains ? 'dot-muted' : (domainCheckFailed || onarilacakVar ? 'dot-danger' : 'dot-ok')]"></span>
        <div class="health-name">
          <strong>{{ t("panel.health.domainsTitle") }}</strong>
          <span class="svc-id">{{ t("panel.health.domainsHint") }}</span>
        </div>
        <span v-if="domainsLoading" class="status-pill">{{ t("panel.health.domainsLoading") }}</span>
        <span v-else-if="!domainsConfirmed || domainCheckFailed" class="status-pill">{{ t("panel.health.statusUnknown") }}</span>
        <span v-else-if="onarilacakVar && domains?.updatedSites === 0" class="status-pill warn">{{ t('panel.health.domainsNeedAttention') }}</span>
        <span v-else-if="domains" :class="['status-pill', onarilacakVar ? 'danger' : 'ok']">
          {{
            domainsRepaired
              ? t('panel.health.domainsDone', { updated: domains.updatedSites, total: domains.totalSites })
              : domains.updatedSites > 0
              ? t("panel.health.domainsStale", { count: domains.updatedSites, total: domains.totalSites })
              : t("panel.health.domainsCurrent", { total: domains.totalSites })
          }}
        </span>
        <!-- "Eksikleri tamamla" YALNIZCA gerçekten eksik varken görünür. Aksi hâlde kullanıcı
             hiç eksik yokken de düğmeyi görüp basıyor, hiçbir şey değişmiyor ve panel çalışmıyor
             sanıyordu. (İşlem zaten idempotent; sorun düğmenin varlığının yanlış sinyal vermesiydi.) -->
        <button
          v-if="domainsConfirmed && !domainsLoading && onarilacakVar"
          class="secondary compact"
          :disabled="!canRefreshDomains"
          @click="refreshAllDomains">
          {{ domainsBusy ? t("panel.health.domainsRefreshing") : t("panel.health.domainsRefresh") }}
        </button>

        <!-- KOSULSUZ CALISTIRMA.
             Dugme yalnizca panel bir eksik GORDUGUNDE cikiyor; panelin goremedigi bir eksik
             varsa kullanicinin elinde hicbir yol kalmiyordu. Islem zaten idempotent ve var olan
             kayitlara dokunmuyor, dolayisiyla calistirmanin zarari yok -- ama sessizce oturmak
             yerine denemek mumkun olmali. Sessiz bir baglanti olarak duruyor ki asil dugmeyle
             karistirilmasin. -->
        <button
          v-if="domainsConfirmed && !domainsLoading && !onarilacakVar"
          class="ghost compact"
          :disabled="!canRefreshDomains"
          @click="refreshAllDomains">
          {{ domainsBusy ? t("panel.health.domainsRefreshing") : t("panel.health.domainsForce") }}
        </button>
      </div>

      <!-- SUNUCU GENELI bulgular: kapali port, posta sunucusunun adi gibi tek bir alan adina
           ait olmayan sorunlar. Bunlar hicbir yerde yazmiyordu; kart yesil "N/N guncel" derken
           yaninda onarma dugmesi cikiyor, kullanici neyin eksik oldugunu goremiyordu. -->
      <!-- PANELIN NE OLCTUGU. Kara kutu olmamali: "eksik yok" derken neye baktigini
           gostermezse, yanlis olcum sessizce dogru sonuc gibi gorunur. -->
      <SectionLoadError v-if="domainsLoad.message.value" :message="domainsLoad.message.value" :busy="!canReload" @retry="load" />

      <p v-if="domainsConfirmed && !domainsLoading && domains" class="measure-line">
        {{ t("panel.health.domainsMeasured", {
          total: domains.totalSites,
          stale: domains.updatedSites,
          findings: (domains.serverFindings?.length ?? 0)
        }) }}
      </p>

      <!-- OLCULDU: `severity` yalniz "warn" | "fail" olabiliyor (api/health.ts), ama kapsamli
           stilde SADECE `li.fail` tanimliydi. Yani her iki uyari bulgusu duz govde metniyle,
           komsu satirdan ayirt EDILEMEDEN ciziliyordu. Iki seviye de base.css'in paylasilan
           durum renkleriyle anlatiliyor; metin zaten bulgunun kendisini yazdigi icin renk tek
           kanal degil, vurgudur. -->
      <ul v-if="domainsConfirmed && domains?.serverFindings?.length" class="server-findings">
        <li
          v-for="bulgu in domains.serverFindings"
          :key="bulgu.key + bulgu.detail"
          :class="bulgu.severity === 'fail' ? 'danger-text' : 'warn-text'">
          {{ t(bulgu.key) }}
          <small v-if="bulgu.detail">{{ bulgu.detail }}</small>
        </li>
      </ul>

      <!-- Ne yapıldığını kayıt kayıt göster: "onarıldı" demek tek başına güven vermez. -->
      <dl v-if="domainsConfirmed && domainReport.length" class="dns-facts">
        <div v-for="row in domainReport" :key="row.siteName">
          <dt>{{ row.siteName }}</dt>
          <dd :class="{ 'danger-text': row.error }">
            <template v-if="row.error">{{ row.error }}</template>
            <template v-else-if="row.alreadyCurrent && !row.findings.some(finding => !finding.repaired || finding.needsConfirmation)">{{ t("panel.health.domainsRowCurrent") }}</template>
            <template v-else>
              {{ row.addedDnsRecords.join(", ") || "—" }}
              <!-- Bulgular: eklenen kayit sayisiyla anlatilamayan sorunlar. Gosterilmezse
                   uretilseler bile kullaniciya HIC ULASMIYORLAR. -->
              <span v-if="row.appliedMailRecords">
                · {{ t("panel.health.domainsRowMail", { count: row.appliedMailRecords }) }}
              </span>
            </template>
            <span v-for="bulgu in row.findings ?? []" :key="bulgu.key" class="row-finding" :class="{ 'warn-text': !bulgu.repaired || bulgu.needsConfirmation }">
              · {{ t(bulgu.key) }}
            </span>
          </dd>
        </div>
      </dl>
    </article>

    <!-- SERVIS LISTESININ BASLIGI YOKTU. Ekranda sirayla: sayfa basligi (h2), DNS karti,
         alan adi karti, sonra HICBIR SEY DEMEDEN servis satirlari. Ustundeki karta ait bir
         devam listesi gibi okunuyordu; ayrica sayfanin baslik merdiveni bu bolumde KOPUYORDU
         (h2 -> hicbir sey). Ayni dosyada `module-head` icin zaten "sayfa h2, bolum h3" karari
         yazili -- eksik olan, bu bolumun o karara hic dahil edilmemis olmasiydi. -->
    <div class="section-head">
      <div>
        <h3>{{ t("panel.health.servicesTitle") }}</h3>
        <p class="meta">{{ t("panel.health.servicesHint") }}</p>
      </div>
    </div>

    <SectionLoadError v-if="servicesLoad.message.value" :message="servicesLoad.message.value" :busy="!canReload" @retry="load" />
    <p v-if="services.length && !servicesConfirmed" class="field-hint">{{ t('panel.health.lastKnownServices') }}</p>
    <p v-if="loading && !services.length" class="meta">{{ t("panel.common.loading") }}</p>

    <div v-else-if="services.length" class="health-list">
      <article v-for="svc in services" :key="svc.name" class="health-row">
        <div class="health-id">
          <span :class="['state-dot', servicesConfirmed ? dotClass(svc.status) : 'dot-muted']"></span>
          <div class="health-name">
            <strong>{{ serviceLabel(svc, t) }}</strong>
            <span class="svc-id">{{ svc.name }}</span>
          </div>
        </div>
        <span :class="['status-pill', servicesConfirmed ? pillClass(svc.status) : '']">{{ statusText(servicesConfirmed ? svc.status : 'Unknown') }}</span>
        <button
          v-if="svc.status !== 'NotInstalled'"
          class="secondary compact"
          :disabled="!canRestart(svc.name)"
          @click="restart(svc.name)">
          {{ busyName === svc.name ? t("panel.health.restarting") : t("panel.health.restart") }}
        </button>
        <span v-else class="na">—</span>
      </article>
    </div>
    <p v-else-if="loaded && servicesConfirmed" class="meta">{{ t("panel.health.empty") }}</p>
  </section>
</template>

<script setup lang="ts">
import { serviceLabel } from '../utils/serviceLabel';
import { computed, onMounted, onUnmounted, ref } from "vue";
import { useI18n } from "vue-i18n";
import {
  getServerHealth,
  inspectDomains,
  refreshDomains,
  restartService,
  type DomainRefreshResult,
  type DomainRefreshSummary,
  type ServiceHealth,
} from "../api/health";
import { getDnsServerHealth, repairDnsServer, type DnsServerHealth } from "../api/dns";
import SectionLoadError from "../components/SectionLoadError.vue";
import { useNotice, useSectionError, useServerText } from "../composables/useNotice";
import { looksLikeMessageKey, splitCompositeMessage } from "../utils/apiError";
import { formatDateTime } from "../utils/datetime";
import { getSessionEpoch } from "../api/client";
import { useAuthStore } from "../stores/auth";
import { useSubscriptionGate } from "../composables/useSubscriptionGate";

const { t } = useI18n();
const services = ref<ServiceHealth[]>([]);
const dns = ref<DnsServerHealth | null>(null);
const dnsLoad = useSectionError();
const dnsBusy = ref(false);
const loading = ref(false);
const loaded = ref(false);
const busyName = ref("");
const servicesLoad = useSectionError();
const servicesConfirmed = ref(false), dnsConfirmed = ref(false), domainsConfirmed = ref(false);
const auth = useAuthStore(), mountedEpoch = getSessionEpoch(), actor = auth.session?.user?.id, role = auth.session?.user?.role;
const { locked } = useSubscriptionGate();
let disposed = false;
const current = () => !disposed && mountedEpoch === getSessionEpoch() && auth.session?.user?.id === actor && auth.session?.user?.role === role;
const { notice, noticeError, showNotice, resolveApiMessage } = useNotice();
// Sunucudan gelen serbest metin (anahtar mi, olculmus teshis mi) TEK kuralla cozulur.
const { serverText } = useServerText();

/*
 * `dns.details` KATLANSIN MI: aynı alanın iki ayrı içeriğini birbirinden ayıran ölçüm.
 *
 * Alan ölçüm tuttuğunda makine çıktısı taşır ("service=Running; startMode=Auto; listening=…"),
 * tutmadığında `dns.health_unmeasurable` ANAHTARINI. Birincisi katlanır (bilgi durur, gürültü
 * gitmez ama öne çıkmaz), ikincisi kartın cevabıdır ve açıkta kalır.
 *
 * Karar için YENİ bir kural yazılmadı: panelin ortak sözlüğü kullanılıyor. `serverText` de
 * tam olarak bu ikisini ayırt eder, ama bir metin döndürür — hangisi olduğunu SÖYLEMEZ.
 */
const dnsDetailsIsDiagnostic = computed(() => {
  const raw = (dns.value?.details ?? "").trim();
  return raw.length > 0 && !looksLikeMessageKey(splitCompositeMessage(raw).key);
});

/*
 * WINDOWS BAŞLANGIÇ TÜRÜ — BİLİNENİ ÇEVİR, BİLİNMEYENİ OLDUĞU GİBİ BIRAK.
 *
 * ÖNCESİ: `Win32_Service.StartMode` değeri ekrana HAM basılıyordu, yani Türkçe panelde de
 * "Auto" / "Manual" yazıyordu.
 *
 * Sözlük Windows'un GERÇEKTEN döndürdüğü değer kümesidir (Win32_Service.StartMode:
 * Boot, System, Auto, Manual, Disabled) artı ölçüm başarısızlığında servisin kendi yazdığı
 * "Unknown" (`DnsService.ServerHealthProbe.StartMode` varsayılanı).
 *
 * BİLİNMEYEN DEĞER UYDURULMAZ: değeri anahtar sanıp `t()`'ye vermek, sözlükte olmadığında
 * ekrana `panel.health.dnsStartModeValue.xyz` gibi HAM ANAHTAR bastırırdı — ham Windows
 * değerinden çok daha kötü. Bilinmeyen değer olduğu gibi geçer (panelin `useServerText`
 * kuralıyla aynı ilke: ham anahtar basmaktansa ham değer basmak dürüsttür).
 */
const START_MODE_KEYS: Record<string, string> = {
  boot: "panel.health.dnsStartModeValue.boot",
  system: "panel.health.dnsStartModeValue.system",
  auto: "panel.health.dnsStartModeValue.auto",
  // WMI bazı sürümlerde uzun biçimi döndürür; ikisi de AYNI şeydir.
  automatic: "panel.health.dnsStartModeValue.auto",
  manual: "panel.health.dnsStartModeValue.manual",
  disabled: "panel.health.dnsStartModeValue.disabled",
  unknown: "panel.health.dnsStartModeValue.unknown"
};

function startModeText(value: string): string {
  const key = START_MODE_KEYS[(value ?? "").trim().toLowerCase()];
  return key ? t(key) : (value ?? "");
}
const domains = ref<DomainRefreshSummary | null>(null);
const domainsRepaired = ref(false);
const domainCheckFailed = computed(() => Boolean(domains.value &&
  (domains.value.failedSites > 0 || domains.value.results.some(row => Boolean(row.error)))));
const domainsLoad = useSectionError();
const domainReport = ref<DomainRefreshResult[]>([]);
const domainsBusy = ref(false);
// Ilk denetim bitene kadar true: kart bu sure boyunca notr gorunur.
const domainsLoading = ref(true);
const actionBusy = computed(() => !!busyName.value || dnsBusy.value || domainsBusy.value);
const canReload = computed(() => current() && !loading.value && !actionBusy.value);
const canWrite = computed(() => canReload.value && role === "Administrator" && !locked.value);
function canRestart(name: string) {
  return canWrite.value && servicesConfirmed.value && services.value.some(row => row.name === name && ["Running", "Stopped"].includes(row.status));
}
const canRepairDns = computed(() => canWrite.value && dnsConfirmed.value && dns.value?.installed === true && dns.value.serviceStatus !== "Unknown" && dns.value.needsRepair);
const canRefreshDomains = computed(() => canWrite.value && domainsConfirmed.value);
const hasUnresolvedFindings = computed(() => unresolvedFindings(domains.value));
function unresolvedFindings(summary: DomainRefreshSummary | null) {
  return !!summary && [...summary.serverFindings, ...summary.results.flatMap(row => row.findings)]
    .some(finding => !finding.repaired || finding.needsConfirmation);
}

/**
 * Onarmanın yapacağı bir iş var mı?
 *
 * Yalnız updatedSites'a bakmak yetmiyordu: sunucu geneli bulgular (posta sunucusunun adı,
 * kapalı güvenlik duvarı portu) hiçbir alan adına ait değil, dolayısıyla o sayıya girmiyor.
 * Girmeyince düğme çıkmıyor, düğme çıkmayınca onarma hiç çalışmıyordu — panelin düzeltebildiği
 * bir sorun kullanıcıya hiç ulaşmıyordu.
 */
const onarilacakVar = computed(
  () => (!domainsRepaired.value && (domains.value?.updatedSites ?? 0) > 0) || hasUnresolvedFindings.value
);

onMounted(load);
onUnmounted(() => { disposed = true; });

async function load() {
  if (!canReload.value) return;
  loading.value = true;
  domainsLoading.value = true;
  notice.value = "";
  // Read independently, while reserving the whole refresh against writes.
  await Promise.all([
    (async () => { try { const result = await getServerHealth(); if (!current()) return; services.value = result; loaded.value = true; servicesConfirmed.value = true; servicesLoad.clear(); }
      catch (error) { if (current()) { servicesConfirmed.value = false; servicesLoad.capture(error); } } })(),
    (async () => { try { const result = await getDnsServerHealth(); if (!current()) return; dns.value = result; dnsConfirmed.value = result.serviceStatus !== "Unknown"; dnsLoad.clear(); }
      catch (error) { if (current()) { dnsConfirmed.value = false; dnsLoad.capture(error); } } })(),
    (async () => { try { const result = await inspectDomains(); if (!current()) return; domains.value = result; domainsRepaired.value = false; domainsConfirmed.value = true; domainReport.value = result.results.filter(row => row.error || !row.alreadyCurrent || row.findings.length); domainsLoad.clear(); }
      catch (error) { if (current()) { domainsConfirmed.value = false; domainsLoad.capture(error); } } })()
  ]);
  if (current()) { loading.value = false; domainsLoading.value = false; }
}

async function refreshAllDomains() {
  if (!canRefreshDomains.value) return;
  domainsBusy.value = true;
  notice.value = "";
  try {
    const summary = await refreshDomains();
    if (!current()) return;
    domains.value = summary;
    domainsRepaired.value = true;
    domainsConfirmed.value = true;
    domainsLoad.clear();
    // Tazelemeden SONRA tüm satırlar gösterilir: hangi alan adına ne eklendiği (ya da hiçbir şey
    // eklenmediği) görünmeden işlem "yapıldı mı" belirsiz kalır.
    domainReport.value = summary.results;
    const incomplete = summary.failedSites > 0 || summary.results.some(row => !!row.error) || unresolvedFindings(summary);
    showNotice(incomplete ? t("panel.health.resultUnconfirmed") : t("panel.health.domainsDone", { updated: summary.updatedSites, total: summary.totalSites }), incomplete);
  } catch (error) {
    if (current()) { domainsConfirmed.value = false; showNotice(resolveApiMessage(error), true); }
  } finally {
    if (current()) domainsBusy.value = false;
  }
}

async function repairDns() {
  if (!canRepairDns.value) return;
  dnsBusy.value = true;
  notice.value = "";
  try {
    const result = await repairDnsServer(true);
    if (!current()) return;
    dns.value = result;
    dnsConfirmed.value = result.serviceStatus !== "Unknown";
    dnsLoad.clear();
    const repaired = result.installed && result.serviceStatus === "Running" && result.publiclyBound && !result.needsRepair;
    showNotice(
      repaired ? t("panel.health.dnsRepaired") : t("panel.health.dnsStillBad"),
      !repaired
    );
    try { const measuredServices = await getServerHealth(); if (!current()) return; services.value = measuredServices; servicesConfirmed.value = true; servicesLoad.clear(); }
    catch (error) { if (current()) { servicesConfirmed.value = false; servicesLoad.capture(error); } }
  } catch (error) {
    if (current()) { dnsConfirmed.value = false; showNotice(resolveApiMessage(error), true); }
  } finally {
    if (current()) dnsBusy.value = false;
  }
}

/** Tarih bicimi panelin geri kalaniyla AYNI (bkz. utils/datetime.ts). */
function formatDate(value: string) {
  return formatDateTime(value);
}

async function restart(name: string) {
  if (!canRestart(name)) return;
  busyName.value = name;
  notice.value = "";
  try {
    const updated = await restartService(name);
    if (!current()) return;
    const index = services.value.findIndex((svc) => svc.name.toLowerCase() === updated.name.toLowerCase());
    if (index >= 0) {
      services.value[index] = updated;
    }
    const restarted = updated.status === "Running";
    showNotice(restarted ? t("panel.health.restartedNotice", { name: updated.label }) : t("panel.health.resultUnconfirmed"), !restarted);
  } catch (error) {
    if (current()) { servicesConfirmed.value = false; showNotice(resolveApiMessage(error), true); }
  } finally {
    if (current()) busyName.value = "";
  }
}

function statusText(status: string) {
  if (status === "Running") return t("panel.health.statusRunning");
  if (status === "Stopped") return t("panel.health.statusStopped");
  if (status === "NotInstalled") return t("panel.health.statusNotInstalled");
  return t("panel.health.statusUnknown");
}

function pillClass(status: string) {
  if (status === "Running") return "ok";
  if (status === "Stopped") return "danger";
  return "";
}

function dotClass(status: string) {
  if (status === "Running") return "dot-ok";
  if (status === "Stopped") return "dot-danger";
  return "dot-muted";
}

</script>

<style scoped>
.server-health {
  display: grid;
  gap: var(--sp-12);
}
.health-read-error { display: grid; gap: var(--sp-8); }
.health-read-error h3 { margin: 0; }

.health-list {
  display: grid;
  gap: var(--sp-8);
}

/* PANELIN "IC KART" RECETESI — bu ekranda iki ad (servis satiri + DNS karti) ayni kutuyu
   iki ayri dolguyla yaziyordu (11px 14px karsi 13px 14px), artik tek tanim.
   OLCULDU (alti ekran taranarak): ayni rutbedeki ic kart DORT ayri kose yaricapiyla
   ciziliyordu — ServerHealth 11px, Cluster 10px, Apps `--radius-md` (9px), AppStore
   `--radius-sm` (6px) — ve UC ayri zeminle (surface-1 / surface-0 / input-bg). Ayni sey
   dort farkli kutu gibi gorunuyordu; kullanicinin "birbirine girdi" dedigi sey tam olarak bu.
   Tek recete: 1px kenar + `--radius-md` + `--surface-1` + `--sp-12` dolgu.
   `--surface-0` SECILMEDI cunku `.panel` zaten `--surface-0`: kart zeminiyle sayfa zemini
   ayni olunca kutuyu yalniz kenar tasiyor ve o kenar acik temada 1.35:1 (bkz. base.css). */
.health-row,
.dns-card {
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-md);
  background: var(--surface-1);
  padding: var(--sp-12);
}

.health-row {
  display: flex;
  align-items: center;
  gap: var(--sp-12);
}

.health-id {
  display: flex;
  align-items: center;
  gap: var(--sp-12);
  flex: 1 1 auto;
  min-width: 0;
}

.health-name {
  display: grid;
  gap: var(--sp-2);
  min-width: 0;
}

/* 0.74rem -> `--fs-2xs` (0.78rem). Yazi olceginde 0.74 diye bir adim yok; en yakin adim
   secildi. Servis kimligi (`W3SVC`, `MSSQLSERVER`) tek aralikli kaldi: gozle karsilastirilan
   bir kimlik, degisken genislikte yazildiginda satirdan satira kayiyor. */
.svc-id {
  color: var(--text-muted);
  font-size: var(--fs-2xs);
  font-family: var(--font-mono);
}

.health-row .status-pill {
  flex-shrink: 0;
}

.health-row button {
  width: auto;
  flex-shrink: 0;
}

/* Kurulu olmayan servisin bos eylem hucresi. Genislik SABIT tutuluyor (dugme genisligi
   mertebesinde) ki "Yeniden baslat" dugmeleri satirdan satira ayni dikey cizgide dursun;
   aksi halde tire isareti kolonu kaydiriyor ve liste tarak gibi gorunuyordu. */
.na {
  width: 84px;
  text-align: center;
  color: var(--text-muted);
  flex-shrink: 0;
}

.state-dot {
  width: 9px;
  height: 9px;
  border-radius: 999px;
  background: var(--text-muted);
  flex-shrink: 0;
}

.state-dot.dot-ok {
  background: var(--ok-1);
  box-shadow: 0 0 0 3px color-mix(in srgb, var(--ok-1) 22%, transparent);
}

/* METIN SOLDURMAK ICIN `opacity` KULLANILMIYOR — base.css'te olculmus ders:
   `opacity` metni VE zemini ayni anda birbirine yaklastirdigi icin kontrasti DUSURUR
   (devre disi dugme olcumu: 0.68 -> 3.15:1, 0.90 -> 2.97:1). Bu ekranda uc yerde
   opacity vardi (0.65 / 0.85 / 0.70); hepsi belirtece cevrildi: `--text-muted` koyu temada
   6.00:1, acik temada 5.24:1 verir. Gorunum ayni sakinlikte, okunurluk olculebilir sekilde
   daha iyi. Boyutlar da olcege alindi (0.8 / 0.88rem -> `--fs-xs` / `--fs-sm`). */
.measure-line {
  margin: var(--sp-4) 0 0;
  color: var(--text-muted);
  font-size: var(--fs-xs);
}

.row-finding {
  color: var(--text-muted);
}

.server-findings {
  margin: var(--sp-6) 0 0;
  padding-left: var(--sp-16);
  font-size: var(--fs-sm);
}

.server-findings small {
  display: block;
  color: var(--text-muted);
}

.state-dot.dot-idle {
  background: var(--text-muted);
  animation: dot-pulse 1.2s ease-in-out infinite;
}

@keyframes dot-pulse {
  0%, 100% { opacity: 0.35; }
  50% { opacity: 1; }
}

.state-dot.dot-danger {
  background: var(--danger-1);
  box-shadow: 0 0 0 3px color-mix(in srgb, var(--danger-1) 22%, transparent);
}

.state-dot.dot-muted {
  background: var(--text-muted);
}

.dns-card {
  display: grid;
  gap: var(--sp-10);
}

.dns-card.bad {
  border-color: color-mix(in srgb, var(--danger-1) 55%, var(--panel-border));
  background: color-mix(in srgb, var(--danger-1) 7%, var(--surface-1));
}

/* "SAGLIKLI" DURUMUN DA BIR GORUNUMU OLMALI.
   `good` sinifi sablonda yaziliyordu ama HICBIR KURALI YOKTU: iyi kart ile henuz olculmemis
   (`idle`) kart BIREBIR ayni ciziliyordu. Yani ekranin cevaplamasi gereken tek soru --
   "sunucum saglikli mi" -- en buyuk ogede sessiz kaliyordu. Kirmizinin dengi olarak yesil
   kenar veriliyor; dolgu DEGISMIYOR (iyi haber bagirmaz, yalniz teyit eder).
   Ton, AppsView'in `.runtime-status.app` kartiyla AYNI recete: iki ekranda "saglikli kart"
   artik ayni sey demek. */
.dns-card.good {
  border-color: color-mix(in srgb, var(--ok-1) 38%, var(--panel-border));
}

.dns-head {
  display: flex;
  align-items: center;
  gap: var(--sp-12);
}

.dns-head .health-name {
  flex: 1 1 auto;
}

.dns-head button {
  width: auto;
  flex-shrink: 0;
}

.dns-facts {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(190px, 1fr));
  gap: var(--sp-8) var(--sp-16);
  margin: 0;
}

/* Etiket DEGERDEN kucuk: hiyerarsinin kendisi bu (bkz. base.css `.tile-label`).
   0.74 -> `--fs-2xs`, 0.86 -> `--fs-sm`: ikisi de olcegin var olan adimlari. */
.dns-facts dt {
  color: var(--text-muted);
  font-size: var(--fs-2xs);
}

.dns-facts dd {
  margin: 0;
  font-size: var(--fs-sm);
  word-break: break-word;
}

/* KATLANMIŞ TEŞHİS (makine çıktısı). Tek aralıklı yazı BİLİNÇLİ: içerik `ad=değer; ad=değer`
   çiftlerinden oluşur ve orantılı yazıda okunmaz. Taban boy `--fs-2xs` (10.53px); ölçekte
   bunun altı YOK. */
.dns-details {
  margin: 0;
  color: var(--text-muted);
  font-size: var(--fs-2xs);
  font-family: var(--font-mono);
  word-break: break-word;
}

/* ÇEVRİLMİŞ CÜMLE — teşhis DEĞİL, dolayısıyla tek aralıklı da değil.
   Aynı `<p>`ye iki ayrı içerik basıldığı sürece ikisi de makine çıktısı gibi görünüyordu;
   oysa bu yol "ölçüm yapılamadı" cümlesini taşır ve okunmak içindir. */
.dns-details-text {
  margin: 0;
  color: var(--text-muted);
  font-size: var(--fs-sm);
  word-break: break-word;
}

/* Katlama başlığı: kartın CEVABI değil, cevabın altındaki kanıt. Bu yüzden `--fs-2xs` ve
   soluk — ama tıklanabilir olduğu imleçten ve odak halkasından anlaşılır. */
.dns-details-fold summary {
  color: var(--text-muted);
  font-size: var(--fs-2xs);
  cursor: pointer;
  /* Yerel `list-style` DOKUNULMADI: tarayıcının kendi üçgeni "burası açılır" işaretinin ta
     kendisi; onu silip yerine bir şey koymamak, katlamayı görünmez yapardı. */
}

.dns-details-fold[open] summary {
  margin-bottom: var(--sp-6);
}

/*
  Dar ekran: bu ekran olay akışının kalbi ("neyin bozuk olduğunu gör" + "yeniden başlat").
  Servis adı tek satıra, DURUM ROZETİ ile EYLEM DÜĞMESİ ise alt satırda yan yana durur —
  ikisi de asla gizlenmez, düğme parmak hedefine büyütülür.
*/
@media (max-width: 620px) {
  .health-row,
  .dns-head {
    flex-wrap: wrap;
    gap: var(--sp-10);
  }

  .health-id {
    flex: 1 1 100%;
  }

  /* DNS başlığında nokta + ad ilk satırı doldursun (nokta 9px + 10px boşluk). */
  .dns-head .health-name {
    flex: 1 1 calc(100% - 22px);
  }

  .health-row .status-pill,
  .dns-head .status-pill {
    order: 1;
  }

  /* `min-height: 42px` SILINDI: base.css dar ekranda her dugmeyi zaten 40px'e cikariyor
     (parmak hedefi kurali). Iki ayri sayi, ayni ekranda iki ayri dugme boyu demekti. */
  .health-row button,
  .health-row .na,
  .dns-head button {
    order: 2;
    margin-left: auto;
  }

  .health-row button,
  .dns-head button {
    min-width: 148px;
  }

  .na {
    width: auto;
    min-width: 0;
  }

  .dns-head {
    align-items: center;
  }

  /* DNS ayrıntıları: tek sütun, etiket üstte — dinlenen/beklenen adresler uzun. */
  .dns-facts {
    grid-template-columns: minmax(0, 1fr);
    gap: var(--sp-8);
  }

  .dns-facts dd {
    overflow-wrap: anywhere;
  }
}
</style>
