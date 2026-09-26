<template>
  <div class="overview-stack">
    <section
      v-if="panelUpdate && panelUpdate.isUpdateAvailable"
      class="panel update-banner"
      role="alert">
      <span class="ub-badge" aria-hidden="true">↑</span>
      <div class="ub-text">
        <strong>{{ t("panel.dashboard.update.available") }}</strong>
        <span>{{ t("panel.dashboard.update.hint", { version: panelUpdate.latestVersion, current: panelUpdate.currentVersion }) }}</span>
      </div>
      <button class="ub-action" @click="go('updates')">{{ t("panel.dashboard.update.action") }}</button>
    </section>

    <!-- Güncelleme DENETİMİ yapılamadıysa bu SÖYLENİR. Sessizce hiçbir şey göstermek,
         "son sürümdesiniz" demekle aynı etkiyi yapıyor ve bekleyen güncelleme gizli kalıyordu. -->
    <SectionLoadError
      v-else-if="panelUpdateError"
      :message="panelUpdateError"
      @retry="emit('retryPanelUpdate')" />

    <!--
      Dikkat özeti: panele girer girmez "N site · N güncelleme bekliyor · N sertifika yakında
      bitiyor" görünür. Tıklanan parça ya ilgili ekrana ya da dikkat çekmecesine götürür.
    -->
    <section v-if="attention" :class="['panel', 'attention-card', attentionTone]">
      <div class="ac-head">
        <span class="tile-label">{{ t("panel.attention.summaryTitle") }}</span>
        <button class="secondary compact" @click="go('attention')">{{ t("panel.attention.viewAll") }}</button>
      </div>

      <div class="ac-chips">
        <button type="button" class="ac-chip" :disabled="!can('sites')" @click="can('sites') && go('sites')">
          <strong>{{ sitesCount }}</strong>
          <span>{{ t("panel.attention.summarySites") }}</span>
        </button>

        <button type="button" class="ac-chip" @click="go(pendingUpdateTarget)">
          <strong :aria-label="pendingUpdateCount === null ? t('panel.attention.unmeasuredCount') : undefined">{{ pendingUpdateCount ?? '—' }}</strong>
          <span>{{ t("panel.attention.summaryUpdates") }}</span>
        </button>

        <button type="button" class="ac-chip" :disabled="!can('sites')" @click="can('sites') && go('sites')">
          <strong :aria-label="expiringCertificateCount === null ? t('panel.attention.unmeasuredCount') : undefined">{{ expiringCertificateCount ?? '—' }}</strong>
          <span>{{ t("panel.attention.summaryCertificates") }}</span>
        </button>

        <button type="button" class="ac-chip alert" @click="go('attention')">
          <strong>{{ attention.criticalCount + attention.warningCount }}</strong>
          <span>{{ t("panel.attention.summaryAlerts") }}</span>
        </button>
      </div>

      <p v-if="attention.failedSources.length" class="notice warn" role="status">{{ t("panel.attention.incompleteSummary") }}</p>
      <p v-else-if="attention.totalCount === 0" class="ac-clear">{{ t("panel.attention.emptyTitle") }}</p>
    </section>

    <!--
      SAYFANIN SIRASI KULLANICININ SORDUĞU SORUYA GÖRE KURULDU.

      ÖLÇÜLDÜ (0.7.9, 1434x840 tarayıcı penceresi): bu panelin eski sırası
      kısayollar -> panel adresleri -> dış bağlantı ayarları -> CPU/bellek/disk halkaları idi.
      Yani sunucunun SAĞLIĞI, ilk üç bloğun altında, KIVRIMIN ALTINDA başlıyordu; panele her
      giren kişi "her şey yolunda mı" sorusunun cevabına ulaşmak için kaydırmak zorundaydı.
      Üstelik üstteki iki bloğun ikisi de tekrar: kısayollar sol menünün aynısı (aşağıda
      ölçüsü var), adresler ise ömürde bir kez okunup yer imine eklenen bir referans —
      ~280px dikey yeri her açılışta yeniden istiyordu.

      Yeni sıra üç basamak: ÖNCE DURUM (güncelleme bandı, dikkat özeti, kaynak göstergeleri +
      sayaçlar), SONRA İŞ (hızlı erişim), EN SONDA REFERANS (panel adresleri, dış bağlantı).
      Hiçbir bilgi SİLİNMEDİ: referans blok en alta indi ve katlanır hâle geldi (`<details>`),
      yani bir tık uzakta ve tarayıcının kendi metin aramasıyla yine bulunabilir.
    -->
    <section class="panel">
      <div v-if="overview" class="resource-grid">
        <ResourceMetric kind="cpu" :label="t('panel.system.cpu')" :value="overview.cpuUsagePercent" :detail="overview.logicalProcessorCount + ' ' + t('panel.system.cores')" />
        <ResourceMetric kind="memory" :label="t('panel.system.memory')" :value="overview.memoryUsagePercent" :detail="formatBytes(overview.usedMemoryBytes) + ' / ' + formatBytes(overview.totalMemoryBytes)" />
        <ResourceMetric kind="disk" :label="primaryDisk?.name || t('panel.system.disks')" :value="primaryDisk?.usagePercent ?? null" :detail="primaryDisk ? formatBytes(primaryDisk.usedBytes) + ' / ' + formatBytes(primaryDisk.totalBytes) : t('panel.common.notAvailable')" :hint="primaryDisk ? t('panel.system.free') + ': ' + formatBytes(primaryDisk.freeBytes) : ''" />
      </div>

      <div class="summary-grid">
        <button type="button" :class="['summary-card', { clickable: can('sites') }]" :disabled="!can('sites')" @click="can('sites') && go('sites')">
          <span class="tile-label">{{ t("panel.sites.title") }}</span>
          <strong>{{ sitesCount }}</strong>
          <small>{{ t("panel.sites.bindings") }}: {{ totalBindingCount }}</small>
        </button>

        <button type="button" :class="['summary-card', { clickable: can('sites') }]" :disabled="!can('sites')" @click="can('sites') && go('sites')">
          <span class="tile-label">{{ t("panel.ftp.title") }}</span>
          <strong>{{ ftpUserCount }}</strong>
          <small>{{ t("panel.ftp.cardHint") }}</small>
        </button>

        <button type="button" :class="['summary-card', { clickable: can('database') }]" :disabled="!can('database')" @click="can('database') && go('database')">
          <span class="tile-label">{{ t("panel.database.title") }}</span>
          <strong>{{ databaseCount }}</strong>
          <small>{{ t("panel.database.cardHint") }}</small>
        </button>

        <button type="button" :class="['summary-card', { clickable: can('security') }]" :disabled="!can('security')" @click="can('security') && go('security')">
          <span class="tile-label">{{ t("panel.dashboard.accessProtection") }}</span>
          <strong>{{ securityStatusLabel }}</strong>
          <small>{{ t("panel.dashboard.accessProtectionHint") }}</small>
        </button>
      </div>
    </section>

    <!--
      HIZLI ERİŞİM — KALDI ama RÜTBESİ DÜŞTÜ. Karar körlemesine değil, sayılarak verildi:

      1) TEKRAR ÖLÇÜLDÜ: buradaki beş hedefin (siteler, posta, veritabanı, dosyalar, sunucu
         araçları) BEŞİ DE `MainLayout` menüsünün üst düzey maddesi (`NAV_GROUPS`: sites/mail/
         files/database "hosting" grubunda, tools "server" grubunda). Yani geniş ekranda bu
         panelin sağladığı tek şey, menüde zaten bir tık uzaktaki beş yere ikinci bir tık.
      2) AMA SİLİNMEDİ: aynı `MainLayout` kenar menüyü 900px altında ÇEKMECEYE alıyor. Dar
         ekranda menü açılmadan görünmüyor, dolayısıyla bu şerit oradaki TEK tek-dokunuşluk
         yol. Silmek masaüstünde 106px kazandırır, telefonda bir adım GERİ götürürdü.
      3) İKİ HARFLİ ROZETLER (WS/ML/DB/FL/AR) KALDIRILDI: menü aynı beş hedefi gerçek SVG
         ikonlarla çiziyor (`NAV_ICONS`). Aynı yerin panelde iki farklı görsel kimliği vardı
         ve uydurma harf çiftleri hiçbir şey öğretmiyordu. 34px'lik ikon karesi gidince kart
         yüksekliği 58px'ten ~34px'e indi; şerit toplamda ~106px'ten ~66px'e düştü.
    -->
    <section class="panel quick-actions-panel">
      <span class="tile-label">{{ t("panel.dashboard.quickActions") }}</span>
      <div class="quick-actions">
        <button v-if="can('sites')" class="qa-btn" @click="go('sites')">{{ t("panel.navigation.sites") }}</button>
        <button v-if="can('mail')" class="qa-btn" @click="go('mail')">{{ t("panel.navigation.mail") }}</button>
        <button v-if="can('database')" class="qa-btn" @click="go('database')">{{ t("panel.navigation.database") }}</button>
        <button v-if="can('files')" class="qa-btn" @click="go('files')">{{ t("panel.navigation.files") }}</button>
        <button v-if="can('tools')" class="qa-btn" @click="go('tools')">{{ t("panel.navigation.serverTools") }}</button>
      </div>
    </section>

    <div class="overview-columns">
      <section class="panel server-info-panel">
        <div class="section-head">
          <h2>{{ t("panel.system.serverInfo") }}</h2>
          <span class="meta">{{ loading ? t("panel.system.loading") : formatUtc(overview?.currentServerTimeUtc ?? null) }}</span>
        </div>

        <div v-if="overview" class="info-grid">
          <article class="info-card">
            <dl class="info-list">
              <div>
                <dt>{{ t("panel.system.machineName") }}</dt>
                <dd>{{ overview.machineName }}</dd>
              </div>
              <div>
                <dt>{{ t("panel.system.operatingSystem") }}</dt>
                <dd>{{ overview.operatingSystem }}</dd>
              </div>
              <div>
                <dt>{{ t("panel.system.architecture") }}</dt>
                <dd>{{ overview.osArchitecture }}</dd>
              </div>
              <div>
                <dt>{{ t("panel.system.framework") }}</dt>
                <dd>{{ overview.frameworkDescription }}</dd>
              </div>
              <div>
                <dt>{{ t("panel.system.panelVersion") }}</dt>
                <dd>{{ overview.panelVersion || t("panel.common.notAvailable") }}</dd>
              </div>
              <div>
                <dt>{{ t("panel.system.bootTime") }}</dt>
                <dd>{{ formatUtc(overview.bootTimeUtc) }}</dd>
              </div>
              <div>
                <dt>{{ t("panel.system.uptime") }}</dt>
                <dd>{{ formatTimeSpan(overview.uptime) }}</dd>
              </div>
            </dl>
          </article>

          <article class="info-card">
            <h3>{{ t("panel.system.disks") }}</h3>
            <div class="mini-table">
              <div class="mini-row table-head disk-row">
                <span>{{ t("panel.system.diskName") }}</span>
                <span>{{ t("panel.system.usage") }}</span>
                <span>{{ t("panel.system.free") }}</span>
              </div>

              <!--
                DİSK TABLOSU ARTIK KONUŞUYOR.
                ÖLÇÜLDÜ (0.7.9 canlı panel): `C:\ 99.9% 533 MiB` satırı DÜZ METİNDİ — ne renk,
                ne rozet, ne sıralama. Yani dolmuş bir disk, panelin en sakin bileşeninde
                duruyordu. Eşik UYDURULMADI: halkalarla BİREBİR aynı iki sabit kullanılıyor
                (`METER_WARN_PERCENT` / `METER_DANGER_PERCENT`); ikinci bir eşik icat etmek
                aynı diski halkada "kritik", tabloda "iyi" gösterirdi.
                RENK TEK KANAL DEĞİL — üç kanal: (1) yüzde zaten yazılı, (2) yanında SÖZCÜKLE
                rozet ("Kritik" / "Uyarı"), (3) rozetin ve satırın tonu. Renk körü kullanıcı da,
                ekran okuyucu da rozetin METNİNİ okur.
              -->
              <div
                v-for="disk in sortedDisks"
                :key="disk.name"
                :class="['mini-row', 'disk-row', { warn: diskTone(disk) === 'warn', danger: diskTone(disk) === 'danger' }]">
                <span>{{ disk.name }}</span>
                <span class="disk-usage">
                  {{ disk.usagePercent.toFixed(1) }}%
                  <span v-if="diskTone(disk)" :class="['status-pill', diskTone(disk)]">{{ diskToneLabel(disk) }}</span>
                </span>
                <span>{{ formatBytes(disk.freeBytes) }}</span>
              </div>
            </div>
          </article>
        </div>
      </section>

      <section class="panel">
        <div class="section-head">
          <h2>{{ t("panel.system.network") }}</h2>
        </div>

        <div v-if="overview" class="mini-table">
          <div class="mini-row table-head network-row">
            <span>{{ t("panel.system.interface") }}</span>
            <span>{{ t("panel.system.download") }}</span>
            <span>{{ t("panel.system.upload") }}</span>
          </div>

          <div v-for="network in overview.networkInterfaces.slice(0, 4)" :key="network.name" class="mini-row network-row">
            <span>{{ network.name }}</span>
            <span>{{ formatSpeed(network.bytesReceivedPerSecond) }}</span>
            <span>{{ formatSpeed(network.bytesSentPerSecond) }}</span>
          </div>
        </div>

      </section>

      <!--
        DENETİM KAYITLARI KENDİ PANELİNE ALINDI.

        Eskiden ağ arayüzleri paneliyle AYNI kutunun içindeydi ve kendi başlığı, o panelin
        başlık çubuğuna sağ tarafa `meta` olarak sıkıştırılmıştı. Canlı panelde ölçüldü: başlık
        oraya sığmıyor ve "Audit kayıtlı…" diye KESİK görünüyordu — yani bölümün adı okunmuyordu.
        Ayrıca iki bölüm tek kutuda olunca ağ tablosunun altındaki kayıtlar, tablonun devamı gibi
        okunuyordu; oysa alakasız iki şey.
      -->
      <section class="panel">
        <div class="section-head">
          <h2>{{ t("panel.layout.recentActivity") }}</h2>
          <button v-if="can('audit')" class="secondary compact" @click="go('audit')">{{ t("panel.attention.viewAll") }}</button>
        </div>

        <p v-if="!auditItems.length" class="empty-state">{{ t("panel.audit.empty") }}</p>
        <div v-else class="audit-preview">
          <article v-for="item in auditItems.slice(0, 4)" :key="item.id" class="audit-item">
            <div class="audit-top">
              <strong>{{ t(`panel.activity.action.${item.action}`, item.action) }}</strong>
              <span>{{ formatUtc(item.createdAtUtc) }}</span>
            </div>
            <details v-if="item.details" class="activity-detail">
              <summary>{{ t('panel.audit.detailsShowAll') }}</summary>
              <p>{{ item.details }}</p>
            </details>
            <small>{{ item.userName }} / {{ item.ipAddress || t("panel.common.notAvailable") }}</small>
          </article>
        </div>
      </section>
    </div>

    <!--
      REFERANS BİLGİSİ — SİLİNMEDİ, KATLANDI VE EN ALTA ALINDI.
      Bu blok panele erişim adreslerini ve dış posta/veritabanı ayarlarını taşıyor. İkisi de
      "bir kez oku, kaydet" bilgisi: adresleri yer imine eklersiniz, IMAP/SMTP portlarını posta
      istemcisine bir kez girersiniz. ÖLÇÜLDÜ: eski hâlinde 5 adres satırı + 2 bağlantı satırı
      ~280px dikey yer tutuyordu ve sayfanın ÜÇÜNCÜ bloğuydu — yani her girişte sağlık
      bilgisinin önünde duruyordu.
      `<details>` SEÇİLDİ, kendi yazdığımız bir aç/kapa DEĞİL: yerel öge klavyeyle çalışır,
      ekran okuyucuya "genişletilebilir" der ve tarayıcının sayfa içi aramasıyla kapalıyken de
      bulunur. Yani bilgi GİZLENMİYOR, sırası değişiyor.
    -->
    <section class="panel access-panel">
      <details class="reference-fold">
        <summary class="reference-summary">
          <span class="tile-label">{{ t("panel.dashboard.reference.title") }}</span>
          <small class="reference-hint">{{ t("panel.dashboard.reference.hint") }}</small>
        </summary>

        <div class="reference-body">
          <div class="access-head">
            <span class="tile-label">{{ t("panel.dashboard.access.title") }}</span>
            <small>{{ t("panel.dashboard.access.hint") }}</small>
          </div>
          <ul class="access-list">
            <li v-for="link in accessLinks" :key="link.url" class="access-row">
              <span class="access-tag">{{ link.label }}</span>
              <a :href="link.url" target="_blank" rel="noopener" class="access-url">{{ link.url }}</a>
              <button class="secondary compact access-copy" @click="copyAccess(link.url)">
                {{ copiedUrl === link.url ? t("panel.dashboard.access.copied") : t("panel.dashboard.access.copy") }}
              </button>
              <!-- Kopyalanamadıysa SESSİZ kalınmaz: adres zaten yanda duruyor, kullanıcı seçip
                   elle kopyalayabilsin diye sebebi söylenir. -->
              <small v-if="copyFailed === link.url" class="access-copy-failed" role="alert">
                {{ t("panel.common.copyFailed") }}
              </small>
            </li>
          </ul>

          <div class="ext-conn">
            <span class="ext-title">{{ t("panel.dashboard.ext.title") }}</span>
            <div class="ext-row">
              <span class="ext-tag">{{ t("panel.dashboard.ext.mail") }}</span>
              <span class="ext-val">{{ t("panel.dashboard.ext.mailValue", { host: primaryHost }) }}</span>
            </div>
            <div class="ext-row">
              <span class="ext-tag">{{ t("panel.dashboard.ext.db") }}</span>
              <span class="ext-val">{{ t("panel.dashboard.ext.dbValue", { host: primaryHost }) }}</span>
            </div>
          </div>
        </div>
      </details>
    </section>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from "vue";
import ResourceMetric from "../components/ResourceMetric.vue";
import { useI18n } from "vue-i18n";
import { getServerIpAddresses } from "../api/system";
import type { UpdateStatus } from "../api/update";
import type { AttentionSummary } from "../api/attention";
import { copyToClipboard } from "../utils/clipboard";
import SectionLoadError from "../components/SectionLoadError.vue";
import { formatBytes as sharedFormatBytes } from "../utils/bytes";
import { formatDateTime, formatTimeSpan as sharedFormatTimeSpan } from "../utils/datetime";

type DiskUsage = {
  name: string;
  usagePercent: number;
  freeBytes: number;
  usedBytes: number;
  totalBytes: number;
};

type NetworkInterface = {
  name: string;
  bytesReceivedPerSecond: number;
  bytesSentPerSecond: number;
};

type SystemOverview = {
  machineName: string;
  operatingSystem: string;
  osArchitecture: string;
  frameworkDescription: string;
  panelVersion: string;
  currentServerTimeUtc: string | null;
  bootTimeUtc: string;
  uptime: string;
  cpuUsagePercent: number;
  logicalProcessorCount: number;
  totalMemoryBytes: number;
  usedMemoryBytes: number;
  memoryUsagePercent: number;
  disks: DiskUsage[];
  networkInterfaces: NetworkInterface[];
};

type AuditItem = {
  id: number;
  action: string;
  details: string;
  userName: string;
  ipAddress: string;
  createdAtUtc: string;
};

const props = defineProps<{
  overview: SystemOverview | null;
  loading: boolean;
  sitesCount: number;
  totalBindingCount: number;
  ftpUserCount: number;
  databaseCount: number;
  securityStatusLabel: string;
  securePath: string;
  auditItems: AuditItem[];
  allowedSections: string[];
  // Panel self-update durumu (yalnızca admin doldurur; null ise banner gizli).
  panelUpdate: UpdateStatus | null;
  // Güncelleme denetimi OKUNAMADIYSA hata metni; boş ise sorun yok demektir.
  panelUpdateError?: string;
  // Dikkat özeti (yalnızca admin doldurur; null ise özet kartı gizli).
  attention?: AttentionSummary | null;
}>();

const { t } = useI18n();

// Bekleyen güncellemeler: bileşen güncellemeleri ("software") + panel güncellemesi ("updates").
const pendingUpdateItems = computed(() => (props.attention?.items ?? [])
  .filter((item) => item.section === "software" || item.section === "updates"));
const updateCountUnknown = computed(() => props.attention?.failedSources.some(source => ["advisor", "components", "panelUpdate"].includes(source)) ?? true);
const pendingUpdateCount = computed(() => updateCountUnknown.value ? null : pendingUpdateItems.value.reduce((total, item) => total + (item.affectedCount ?? 1), 0));

// Tıklayınca DOĞRU ekrana git. Sayaç iki farklı güncelleme türünü topluyor; hepsini panel
// güncelleme sayfasına yollamak "1 güncelleme var" deyip "zaten son sürümdesiniz" gösterir.
// Tek madde varsa kendi bölümüne, birden çoksa hepsini listeleyen çekmeceye gideriz.
const pendingUpdateTarget = computed(() => {
  if (updateCountUnknown.value) return "attention";
  const items = pendingUpdateItems.value;
  if (items.length === 1) {
    return items[0].section || "updates";
  }
  return items.length > 1 ? "attention" : "attention";
});

// Süresi dolan/dolmak üzere olan sertifikalar (Danışman'ın SSL maddeleri).
const expiringCertificateCount = computed(() => props.attention?.failedSources.some(source => ["advisor", "ssl"].includes(source)) ? null : (props.attention?.items ?? [])
  .filter((item) => item.titleKey.startsWith("advisor.ssl")).length);

const attentionTone = computed(() => {
  if (!props.attention || props.attention.criticalCount > 0) {
    return "critical";
  }

  return props.attention.warningCount > 0 || props.attention.failedSources.length > 0 ? "warning" : "calm";
});

// profesyonel panel benzeri: hizli erisim kartlari + ozet kutulari ilgili bolume gider.
const emit = defineEmits<{
  (event: "navigate", sectionId: string): void;
  (event: "retryPanelUpdate"): void;
}>();

// Yalnızca kullanıcının rolünde mevcut olan bölümlere kısayol göster (boş ekrana götürmesin).
function can(sectionId: string): boolean {
  return props.allowedSections.includes(sectionId);
}

function go(sectionId: string) {
  emit("navigate", sectionId);
}

// Panele erişim adresleri: bu cihazdan (localhost) + ağdan (sunucu IPv4'leri). Şema/port mevcut
// pencereden, güvenli yol prop'tan; IP'ler API'den. Kurulumdan sonra "nasıl erişirim" sorusunu yanıtlar.
const serverIps = ref<string[]>([]);
const copiedUrl = ref("");
const copyFailed = ref("");

onMounted(async () => {
  try {
    const addresses = await getServerIpAddresses();
    serverIps.value = addresses
      .filter((item) => item.family === "InterNetwork" || item.family === "IPv4" || /^\d+\.\d+\.\d+\.\d+$/.test(item.address))
      .map((item) => item.address)
      .filter((address) => address && !address.startsWith("127.") && address !== "::1");
  } catch {
    serverIps.value = [];
  }
});

const accessLinks = computed(() => {
  const protocol = window.location.protocol;
  const port = window.location.port ? `:${window.location.port}` : "";
  const path = (props.securePath || "").replace(/^\/+|\/+$/g, "");
  const suffix = `${port}${path ? `/${path}` : ""}`;
  const links = [{ label: t("panel.dashboard.access.local"), url: `${protocol}//localhost${suffix}` }];
  for (const ip of serverIps.value) {
    links.push({ label: ip, url: `${protocol}//${ip}${suffix}` });
  }
  return links;
});

// Dış bağlantı (posta/DB) için sunucu adresi: erişim panelindeki ilk genel IP; yoksa pencere host'u.
const primaryHost = computed(() => serverIps.value[0] || window.location.hostname || "127.0.0.1");

async function copyAccess(url: string) {
  // window.prompt kaldırıldı: tarayıcı diyalogları engellendiğinde akış sessizce ölüyordu.
  // Ama SESSİZ de kalınmaz: eski hâli en azından adresi bir kutuda gösteriyordu, yani
  // kullanıcının elle kopyalama şansı vardı. Kopyalanamadıysa bunu SÖYLERİZ.
  if (!(await copyToClipboard(url))) {
    copyFailed.value = url;
    window.setTimeout(() => {
      if (copyFailed.value === url) copyFailed.value = "";
    }, 4000);
    return;
  }

  copyFailed.value = "";

  copiedUrl.value = url;
  window.setTimeout(() => {
    if (copiedUrl.value === url) copiedUrl.value = "";
  }, 1500);
}

const primaryDisk = computed(() => props.overview?.disks[0] ?? null);

/*
 * DİSK TABLOSU: EN DOLU DİSK EN ÜSTTE.
 * Sunucudan gelen sıra sürücü harfine göre (C, D, E...). Bir sorun varsa o satırın nerede
 * duracağı böylece TESADÜFE kalıyordu: dördüncü sürücü dolduğunda uyarı listenin dibindeydi.
 * Sıra yüzdeye göre AZALAN yapıldı — dikkat isteyen satır her zaman ilk satırdır.
 * KOPYA ÜZERİNDE sıralanıyor: `sort` yerinde çalışır ve prop dizisini doğrudan sıralamak
 * üst bileşenin verisini (ve kaynak göstergesinin okuduğu `disks[0]`ı) değiştirirdi.
 */
const sortedDisks = computed(() => [...(props.overview?.disks ?? [])]
  .sort((a, b) => b.usagePercent - a.usagePercent));

/* Disk satırları ortak kaynak göstergesiyle aynı %80 / %90 eşiklerini kullanır. */
const METER_WARN_PERCENT = 80;
const METER_DANGER_PERCENT = 90;


/**
 * Disk satırının tonu — kaynak göstergesiyle aynı İKİ EŞİKTEN okunur (bkz. `ringStyle`).
 * Boş dize "eşiğin altında" demektir; o zaman satır ne rozet alır ne ton, çünkü sağlıklı bir
 * disk hakkında söylenecek bir şey yoktur (her satırı yeşile boyamak gürültüdür).
 */
function diskTone(disk: DiskUsage): "" | "warn" | "danger" {
  if (disk.usagePercent >= METER_DANGER_PERCENT) {
    return "danger";
  }

  return disk.usagePercent >= METER_WARN_PERCENT ? "warn" : "";
}

/** Rozetin SÖZCÜĞÜ: rengin yanında ikinci (ve ekran okuyucunun okuduğu tek) kanal. */
function diskToneLabel(disk: DiskUsage): string {
  return diskTone(disk) === "danger"
    ? t("panel.common.severityCritical")
    : t("panel.common.severityWarning");
}

/** Tarih bicimi panelin geri kalaniyla AYNI (bkz. utils/datetime.ts). */
function formatUtc(value: string | null): string {
  return formatDateTime(value, t("panel.common.notAvailable"));
}

/** Bayt bicimi panelin geri kalaniyla AYNI (bkz. utils/bytes.ts); ekran kendi bicimini uydurmaz. */
function formatBytes(value: number) {
  return sharedFormatBytes(value, t);
}

function formatSpeed(value: number): string {
  return `${formatBytes(value)}/${t("panel.units.second")}`;
}

/** Sure bicimi panelin geri kalaniyla AYNI (bkz. utils/datetime.ts). */
function formatTimeSpan(value: string): string {
  return sharedFormatTimeSpan(value, t);
}
</script>

<style scoped>
.overview-stack {
  --metric-lg: clamp(1.4rem, 2.8vw, 2rem);
  --metric-md: clamp(1.15rem, 2vw, 1.4rem);
}

/* 18px ölçek DIŞIYDI (bkz. base.css `--sp-*`). 16'ya taşındı: `.panel`in kendi iç dolgusu da
   16, yani panelden panele boşluk ile panelin içindeki boşluk artık AYNI sayı — sayfanın
   dikey ritmi tek bir değerden okunur. */
.overview-stack,
.summary-grid,
.overview-columns,
.info-grid,
.mini-table,
.audit-preview {
  display: grid;
  gap: var(--sp-16);
}

.overview-columns {
  grid-template-columns: repeat(2, minmax(0, 1fr));

  /* PANELLER KENDİ YÜKSEKLİĞİNDE DURUR.
     Izgaranın varsayılanı `stretch`: iki panel satırın en uzununa göre GERİLİYORDU. Canlı
     panelde ölçüldü — "Sunucu bilgisi" başlığı ile ilk kartı arasında **~180px boş alan**
     oluşuyordu, çünkü panel komşusu kadar uzuyor ve içindeki ızgara fazla yeri dağıtıyordu.
     Kullanıcı bunu "boşluk kalmış, karışık" diye görüyor; oysa sebebi içerik değil hizalama. */
  align-items: start;
}

.summary-grid {
  grid-template-columns: repeat(4, minmax(0, 1fr));
}

/* `.mini-row` bu listeden ÇIKARILDI: satırın kendi kenarlığı ve kendi zemini olması, onu
   tablo satırı değil ayrı bir kart yapıyordu (gerekçesi aşağıda, `.mini-table`). */
.summary-card,
.info-card,
.audit-item {
  border: 1px solid var(--panel-border);
  background: var(--surface-2);
}

.summary-card,
.info-card {
  padding: var(--sp-16);
  border-radius: var(--radius-lg);
}

/* `.tile-label` BU LİSTEDEN ÇIKARILDI: ortak tanımı artık base.css'te ve kapsamlı stil
   ([data-v] niteliğiyle) onu EZİYORDU — yani panelin geri kalanı `--fs-2xs` çizerken bu ekran
   `--fs-xs` çiziyordu. Kopya silindi, ortak tanım devraldı. GÖRÜNEN FARK: kutucuk etiketleri
   0.82rem -> 0.78rem (bir ölçek adımı küçülür ve panelin geri kalanıyla eşitlenir). */

.access-panel {
  display: grid;
  gap: var(--sp-10);
}

/* KATLANIR REFERANS BLOĞU.
   Kapalıyken sayfada tuttuğu yer tek satırdır (~40px); eski hâli ~280px'ti ve sayfanın
   ÜÇÜNCÜ bloğuydu. Açıldığında içerik birebir eskisi gibi çizilir — hiçbir satır kaldırılmadı. */
.reference-fold {
  display: grid;
  gap: var(--sp-10);
}

/* `display` BİLEREK DEĞİŞTİRİLMEDİ. `summary` varsayılan olarak `list-item`tır ve açılır
   üçgeni o değerden gelir; `grid`/`flex` yazmak işaretçiyi SESSİZCE yok eder — "burada
   katlanmış bir şey var" diyen tek görsel ipucu odur. Bu yüzden iki parça (etiket + ipucu)
   satır içi akar; ikisi birlikte tek satır tutar. */
.reference-summary {
  cursor: pointer;
}

.reference-hint {
  margin-left: var(--sp-8);
  color: var(--text-muted);
  font-size: var(--fs-2xs);
}

.reference-body {
  display: grid;
  gap: var(--sp-10);
  margin-top: var(--sp-8);
}

.access-head {
  display: grid;
  gap: var(--sp-2);
}

.access-head small {
  color: var(--text-muted);
  font-size: var(--fs-2xs);
}

.access-list {
  list-style: none;
  margin: 0;
  padding: 0;
  display: grid;
  gap: var(--sp-8);
}

.access-row {
  display: flex;
  align-items: center;
  gap: var(--sp-10);
  flex-wrap: wrap;
  padding: var(--sp-8) var(--sp-12);
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-md);
  background: var(--surface-1);
}

/* Etiket sütunu HİZALI: adresler alt alta durduğu için etiketlerin farklı genişlikte olması
   (localhost / 10.0.0.4 / 192.168.1.20) satır başlarını tırtıklı gösteriyordu. */
.access-tag,
.ext-tag {
  font-weight: 700;
  font-size: var(--fs-2xs);
  color: var(--accent-text);
}

.access-tag {
  min-width: 56px;
}

.ext-tag {
  min-width: 72px;
}

/* `--font-mono` BELİRTECİ VAR ve tanımlı (base.css); burada yedek listesi ELDE yazılıydı ve
   base.css'in listesinden farklıydı ("Cascadia Mono" ve "Liberation Mono" eksikti). Aynı
   panelde iki farklı tek-aralıklı yazı tipi çizilebiliyordu. */
.access-url,
.ext-val {
  flex: 1;
  min-width: 200px;
  font-family: var(--font-mono);
}

.access-url {
  font-size: var(--fs-sm);
  word-break: break-all;
  color: var(--text-strong);
}

.access-copy {
  flex: none;
}

.access-copy-failed {
  flex-basis: 100%;
  color: var(--warn-text);
  font-size: var(--fs-2xs);
}

.ext-conn {
  display: grid;
  gap: var(--sp-4);
  margin-top: var(--sp-6);
  padding-top: var(--sp-8);
  border-top: 1px solid var(--panel-border);
}

.ext-title {
  color: var(--text-muted);
  font-size: var(--fs-2xs);
  letter-spacing: 0.035em;
  text-transform: uppercase;
}

.ext-row {
  display: flex;
  gap: var(--sp-8);
  align-items: baseline;
  flex-wrap: wrap;
}

.ext-val {
  font-size: var(--fs-2xs);
  color: var(--text-soft);
  word-break: break-word;
}

.summary-card strong {
  font-size: var(--metric-lg);
  font-variant-numeric: tabular-nums;
}

/* Dairenin kenarlarına değil, ortadaki güvenli alana yerleşen tek metin grubu. */

.summary-card {
  display: grid;
  gap: var(--sp-6);
  padding: var(--sp-12);
}

.summary-card small {
  color: var(--text-muted);
}

.quick-actions-panel {
  display: grid;
  gap: var(--sp-10);
}
.attention-card {
  display: grid;
  gap: var(--sp-10);
  border-left-width: 3px;
  border-left-style: solid;
}

.attention-card.critical {
  border-color: color-mix(in srgb, var(--danger-1) 45%, var(--panel-border));
  border-left-color: var(--danger-1);
  background: color-mix(in srgb, var(--danger-1) 5%, var(--surface-0));
}

.attention-card.warning {
  border-color: color-mix(in srgb, var(--warn-1) 40%, var(--panel-border));
  border-left-color: var(--warn-1);
  background: color-mix(in srgb, var(--warn-1) 5%, var(--surface-0));
}

.attention-card.calm {
  border-color: color-mix(in srgb, var(--ok-1) 35%, var(--panel-border));
  border-left-color: var(--ok-1);
}

.ac-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--sp-12);
}

.ac-chips {
  display: grid;
  grid-template-columns: repeat(4, minmax(0, 1fr));
  gap: var(--sp-10);
}
@media (max-width: 700px) { .ac-chips { grid-template-columns: repeat(2, minmax(0, 1fr)); } }

.ac-chip {
  display: grid;
  gap: var(--sp-2);
  padding: var(--sp-10) var(--sp-12);
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-md);
  background: var(--surface-1);
  color: var(--text-strong);
  text-align: left;
  cursor: pointer;
}

.ac-chip:disabled {
  cursor: default;
  opacity: 0.7;
}

.ac-chip strong {
  font-size: var(--metric-md);
  font-variant-numeric: tabular-nums;
  line-height: 1.1;
}

.ac-chip span {
  color: var(--text-muted);
  font-size: var(--fs-2xs);
}

/* Uyarı rozeti artık yalnız rakamı renklenmiyor: kutunun kendisi tonu alıyor. Rakam
   `--*-text` ile yazılıyor (`--*-1` değil) — o belirteçler tam olarak "bu tonun METİN
   üzerindeki okunur karşılığı" için ölçülmüş hâlleri (bkz. base.css). */
.attention-card.critical .ac-chip.alert {
  border-color: color-mix(in srgb, var(--danger-1) 45%, transparent);
  background: var(--danger-bg);
}

.attention-card.critical .ac-chip.alert strong {
  color: var(--danger-text);
}

.attention-card.warning .ac-chip.alert {
  border-color: color-mix(in srgb, var(--warn-1) 45%, transparent);
  background: var(--warn-bg);
}

.attention-card.warning .ac-chip.alert strong {
  color: var(--warn-text);
}
.ac-clear {
  margin: 0;
  color: var(--ok-text);
  font-size: var(--fs-xs);
  font-weight: 600;
}

/* "Yeni sürüm mevcut" uyarı bandı (yalnızca admin + guncelleme varsa). */
.update-banner {
  display: flex;
  align-items: center;
  gap: var(--sp-12);
  border-color: color-mix(in srgb, var(--accent-1) 45%, var(--panel-border));
  background: color-mix(in srgb, var(--accent-1) 10%, var(--surface-2));
}

.ub-badge {
  display: grid;
  place-items: center;
  width: 36px;
  height: 36px;
  border-radius: 50%;
  background: color-mix(in srgb, var(--accent-1) 20%, transparent);
  color: var(--accent-text);
  font-weight: 800;
  font-size: var(--fs-lg);
  flex-shrink: 0;
}

.ub-text {
  display: grid;
  gap: var(--sp-2);
  flex: 1;
  min-width: 0;
}

.ub-text span {
  color: var(--text-soft);
  font-size: var(--fs-sm);
}

.ub-action {
  flex: none;
  width: fit-content;
}

@media (max-width: 820px) {
  .update-banner {
    flex-wrap: wrap;
  }

  .ub-action {
    width: 100%;
  }
}

.quick-actions {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(150px, 1fr));
  gap: var(--sp-8);
}

/* Rütbe düşürüldü (gerekçesi şablondaki yorumda): kart değil, tek satırlık kısayol düğmesi.
   `<strong>` sarmalayıcısı da kalktı; etiket düğmenin kendi metni. Yükseklik `button`un
   `min-height: 34px` değerine iner (eskiden 12+34+12 = 58px). Ortalanmış metin BİLEREK:
   iki harfli rozet gidince sola dayalı tek sözcük şeridin sağında büyük bir boşluk bırakıyordu. */
.qa-btn {
  display: flex;
  align-items: center;
  justify-content: center;
  gap: var(--sp-8);
  padding: var(--sp-6) var(--sp-12);
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-md);
  background: var(--surface-1);
  color: var(--text-strong);
  box-shadow: none;
  font-weight: 600;
  cursor: pointer;
}

.summary-card.clickable {
  cursor: pointer;
  text-align: left;
  width: 100%;
  font: inherit;
  color: inherit;
}
.ac-chip:not(:disabled):hover,
.qa-btn:hover,
.summary-card.clickable:hover {
  border-color: color-mix(in srgb, var(--accent-1) 45%, transparent);
  box-shadow: 0 0 0 1px color-mix(in srgb, var(--accent-1) 20%, transparent);
}

.ac-chip,
.qa-btn,
.summary-card.clickable {
  transition: border-color 0.15s ease, box-shadow 0.15s ease;
}

/* `.section-head` KOPYASI SİLİNDİ (ortak tanım base.css'te).
   DİKKAT — GÖRÜNEN DEĞİŞİKLİK: buradaki kopya `align-items: center` yazıyordu ve kapsamlı stil
   daha yüksek özgüllükte olduğu için ortak `flex-start`ı EZİYORDU. Artık ortak davranış geçerli:
   "Sunucu bilgisi" / "Ağ" başlıklarının yanındaki tarih-saat metni, başlığın ortasına değil ÜST
   hizasına oturur. Ortak tanım ayrıca `flex-wrap: wrap` getirir (dar panelde başlık ile yanındaki
   metin birbirini ezmez). `.audit-top` bu ekrana özgü, kendi tanımıyla kalıyor. */
.audit-top {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--sp-12);
}

.info-grid {
  grid-template-columns: repeat(2, minmax(0, 1fr));
}

.info-list {
  display: grid;
  gap: var(--sp-12);
  margin: 0;
}

.info-list div {
  display: grid;
  gap: var(--sp-4);
}

.info-list dt {
  color: var(--text-muted);
  font-size: var(--fs-md);
}

.info-list dd {
  margin: 0;
  font-weight: 600;
}
.mini-table {
  gap: 0;
  overflow: hidden;
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-md);
}

/* `.panel h3` başlıkların margin'ini sıfırlıyor; tablo çerçeve kazandığı için başlık artık
   çerçeveye YAPIŞIYORDU. Başlıkla tablosu arasına ölçekten bir adım. */
.info-card h3 {
  margin-bottom: var(--sp-10);
}

.mini-row {
  display: grid;
  gap: var(--sp-12);
  align-items: center;
  padding: var(--sp-10) var(--sp-12);
  border-bottom: 1px solid var(--panel-border);
  background: var(--surface-0);
}

.mini-row:last-child {
  border-bottom: 0;
}

/* Kullanım sütunu artık iki parça taşıyor (yüzde + rozet), o yüzden biraz daha geniş:
   0.8fr -> 1.1fr. Kaynak ad sütunundan alınıyor (1.4 -> 1.1); disk adı "C:\" gibi 3-4
   karakterlik bir dize, bu ekranda en dar içeriğe sahip sütun odur. */
.disk-row {
  grid-template-columns: 1.1fr 1.1fr 1fr;
}

/* Yüzde ile rozet AYNI hücrede ve sağa dayalı; `.disk-row > span:nth-child(n + 2)` kuralının
   `text-align: right` değeri flex kapsayıcıda iş görmez, hizayı `justify-content` verir. */
.disk-usage {
  display: flex;
  align-items: center;
  justify-content: flex-end;
  gap: var(--sp-6);
  flex-wrap: wrap;
}

/* SATIR TONU — üçüncü kanal. Renk çok hafif tutuldu (%6/%8): satır zemini, yanındaki rozetin
   kendi dolgusunu bastırmamalı; işi yalnızca gözü doğru satıra çekmek.
   `--*-1` (renk) ile karıştırılıyor, `--*-text` ile DEĞİL: burada metin değil YÜZEY boyanıyor;
   satırın yazısı `--text-strong` olarak kalır ve iki temada da okunur. */
.mini-row.warn {
  background: color-mix(in srgb, var(--warn-1) 6%, var(--surface-0));
}

.mini-row.danger {
  background: color-mix(in srgb, var(--danger-1) 8%, var(--surface-0));
}

.network-row {
  grid-template-columns: 1.2fr 0.9fr 0.9fr;
}

/* SAYI SÜTUNLARI SAĞA HİZALI, RAKAMLAR TEK GENİŞLİKTE.
   Her iki tabloda da ilk sütun bir AD (disk harfi, arayüz adı), kalanların hepsi SAYI:
   "%12.4 / %7.0 / %100.0" ve "1,2 GB / 812 MB". Sola hizalı olduklarında ondalık ayraçlar
   birbirini tutmuyor ve iki satırın hangisinin büyük olduğu ancak okunarak anlaşılıyordu;
   sağa hizalı ve `tabular-nums` ile aynı basamak alt alta gelir, karşılaştırma bakışla olur.
   Başlıklar da hizaya dahil: başlık sütunundan kayarsa hangi sayının ne olduğu bulanıklaşır.
   `:nth-child` KULLANILIYOR, `:not(:first-child)` değil — sınıf argümanlı olmasa da bu depoda
   `:not()` özgüllük tuzağı için nöbetçi var; niyet zaten "ikinciden itibaren". */
.disk-row > span:nth-child(n + 2),
.network-row > span:nth-child(n + 2) {
  text-align: right;
  font-variant-numeric: tabular-nums;
}

/* Başlık satırı base.css'teki `.data-row.table-head` ile AYNI dili konuşur (aynı renk, aynı
   boyut, aynı kalınlık): panelde iki farklı "tablo başlığı" görünümü olmasın. */
.table-head {
  color: var(--text-soft);
  font-size: var(--fs-xs);
  font-weight: 600;
  background: var(--surface-2);
}

.audit-item {
  min-width: 0;
  overflow-wrap: anywhere;
  padding: var(--sp-16);
  border-radius: var(--radius-md);
}

.audit-item p,
.audit-item small {
  margin: 0;
}
@media (max-width: 1180px) {
  .overview-columns,
  .info-grid {
    grid-template-columns: 1fr;
  }

  .summary-grid {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }
}

@media (max-width: 820px) {
  /* `.section-head` buradan da çıkarıldı (kopyanın parçasıydı): ortak tanımdaki `flex-wrap`
     dar ekranda zaten alt satıra indiriyor, ayrıca zorlamaya gerek yok. */
  .audit-top {
    flex-direction: column;
    align-items: flex-start;
  }

  /*
    Disk/ağ satırları dar ekranda TEK sütuna inmiyor: başlık satırı da tek sütuna inip
    değerlerden koptuğu için "hangi sayı neydi" okunamıyordu. Bunun yerine sütunlar
    korunup yazı/boşluk daraltılıyor — üç kısa değer 360px'e sığar.
  */
  .mini-row {
    gap: var(--sp-8);
    padding: var(--sp-8) var(--sp-10);
    font-size: var(--fs-xs);
  }

  .mini-row span {
    min-width: 0;
    overflow-wrap: anywhere;
  }

  /* Kullanım sütunu dar ekranda da rozeti taşımalı (0.7fr -> 1fr). Alan disk adı sütunundan
     alınıyor: "C:\" üç karakter, buradaki en dar içerik odur. Rozet yine de sığmazsa
     `.disk-usage` sarar ve satır iki satıra çıkar — kırpma YOK. */
  .disk-row {
    grid-template-columns: minmax(0, 0.9fr) 1fr 0.9fr;
  }

  .network-row {
    grid-template-columns: minmax(0, 1.1fr) 0.9fr 0.9fr;
  }

  .audit-item {
    padding: var(--sp-12);
  }
}
@media (max-width: 700px) {

  /* Telefonda başlık dairenin dışına çıkar; yüzde için ayrılan alanı daraltmaz. */

  /* Özet kutuları telefonda da 2x2 (1180px'den beri öyle); burada yalnız iç ölçüler daralır. */
  .summary-grid {
    gap: var(--sp-10);
  }

  .summary-card {
    padding: var(--sp-12);
    gap: var(--sp-4);
  }

  /* Telefonda kart 2 sütuna sığdığı için metrik ölçeği bir basamak küçülür. Boyut TEK yerden
     iniyor: özet kartının rakamı da dikkat rozetinin rakamı da bu iki satırı izler. */
  .overview-stack {
    --metric-lg: clamp(1.1rem, 5.5vw, 1.5rem);
    --metric-md: clamp(1rem, 4.5vw, 1.25rem);
  }

  /* `.tile-label` buradan da çıkarıldı: ortak tanım zaten `--fs-2xs` (0.78rem) ve o boyut
     telefonda da okunur. 0.7rem ölçek dışıydı ve yalnızca bu ekranda geçerliydi. */
  .summary-card small {
    font-size: var(--fs-xs);
  }

  .info-card {
    padding: var(--sp-12);
  }

  .access-row {
    gap: var(--sp-6) var(--sp-10);
    padding: var(--sp-10);
  }

  .access-url,
  .ext-val {
    min-width: 100%;
  }
}
.server-info-panel { grid-row: span 2; }
.audit-preview { gap: 0; }
.audit-item { background: transparent; border: 0; border-radius: 0; padding: 12px 0; display: grid; gap: 6px; }
.audit-item + .audit-item { border-top: 1px solid var(--panel-border); }
.audit-item small { color: var(--text-muted); }
.activity-detail summary { cursor: pointer; color: var(--accent-text); font-size: var(--fs-xs); }
.activity-detail p { max-height: 180px; overflow: auto; padding: 10px; margin-top: 8px; background: var(--surface-1); white-space: pre-wrap; font-family: var(--font-mono); font-size: var(--fs-xs); }
@media (max-width: 1180px) { .server-info-panel { grid-row: auto; } }
@media (min-width: 901px) and (max-width: 1180px) { .summary-grid { grid-template-columns: repeat(4, minmax(0, 1fr)); } }
</style>
