<template>
  <section class="website-workspace">
    <!-- Liste görünümü: doğrudan site listesi. -->
    <SitesView v-if="!inWorkspace" @open-tool="onOpenTool" />

    <!-- Domain paneli (profesyonel paneller gibi): geri çubuğu + 4 ana sekme + dikey araç listesi. -->
    <template v-else>
      <div class="tool-context">
        <button class="secondary compact back-btn" @click="backToSites">
          ← {{ t("panel.sites.backToSites") }}
        </button>
        <!-- `<strong>` -> `<h2>`: BASLIK MERDIVENININ EKSIK BASAMAGI BURASIYDI.
             OLCULDU (Pano sekmesi, calisma alani acik): sayfadaki baslik dizisi ust cubugun
             `h1`inden dogrudan `SiteDetailView`in `h3`une atliyordu -- arada `h2` YOKTU.
             Ustelik atlanan basamak, sayfanin KIMLIGI olan alan adiydi: ekran okuyucu
             kullanicisi baslik listesinde hangi sitede oldugunu HIC goremiyordu, cunku
             alan adi bir baslik degil kalin bir metindi.
             BOY DEGISMEDI, bilerek: 15.9px (`--fs-xl`) bir onceki turda olculerek secilmisti
             -- arac govdesinin kendi `h2`si 19.2px ve bu serit onunla YARISMAMALI. Merdiven
             gorsel degil YAPISAL olarak eksikti; duzelen o. -->
        <h2 class="context-domain">{{ selectedSiteName || t("panel.php.selectSite") }}</h2>
        <!-- `secondary compact` KALDIRILDI: bu bir `<a>`, base.css'teki kural ise element
             duzeyinde `button.secondary`. Yani siniflar hicbir sey yapmiyordu ve "Siteyi ac"
             masaustunde cercevesiz duz bir metin olarak duruyordu (ikincil dugme gorunumu
             yalniz 620px altindaki blokta yaziliydi). Gorunum artik `.open-link`te, her
             genislikte. -->
        <a
          v-if="primaryUrl"
          class="open-link"
          :href="primaryUrl"
          target="_blank"
          rel="noopener">
          {{ t("panel.sites.open") }} ↗
        </a>
      </div>

      <!-- Başlığın hemen altında: sitenin DIŞARIDAN görünen sağlığı (DNS/HTTP/SSL/posta).
           Panelin iç durumu yeşilken dünyanın siteyi göremediği durumu burada yakalarız.
           Tek ölçüm site başına: her araç sekmesinde görünür kalır ama yeniden ölçmez. -->
      <SiteHealthStrip v-if="selectedSiteName" :site-name="selectedSiteName" />

      <!-- Onarilacak bir sey varsa BURADA soylenir. Kullanici sorunu, sorunun oldugu sayfada
           gormeli; Sunucu Sagligi ekranindaki toplu sayi hangi sitenin bozuk oldugunu soylemiyordu. -->
      <SiteRepairNotice v-if="selectedSiteName" :site-name="selectedSiteName" @notify="showNotice" />

      <p v-if="notice" :class="['notice', 'page-notice', { error: noticeError }]" :role="noticeError ? 'alert' : 'status'">{{ notice }}</p>

      <!-- panel ana sekmeleri: Pano · Barındırma ve DNS · Posta · Başlarken -->
      <!-- Panelin ORTAK sekme seridi (`.segmented-tabs`, base.css) devralindi. Bu ekran kendi
           seridini kuruyordu: ayri yaricap, ayri zemin gradyani, ayri golge ve ayri aktif
           rengi — yani panelde iki farkli sekme dili vardi. Yerel kurallar artik yalnizca
           FARKI yaziyor (esit genislik + ikon + dokunma yuksekligi). -->
      <nav class="segmented-tabs primary-nav" :aria-label="t('panel.sites.toolsBar')">
        <button
          v-for="tab in primaryTabs"
          :key="tab.id"
          :class="['primary-tab', { active: primary === tab.id && !openToolId }]"
          @click="selectPrimary(tab.id)">
          <svg class="tab-icon" viewBox="0 0 24 24" aria-hidden="true" v-html="siteToolIcon(tab.icon)"></svg>
          <strong>{{ tab.label }}</strong>
        </button>
      </nav>

      <!-- Bir araç açıldığında: geri linki + araç görünümü (Barındırma ve DNS / Başlarken drill-in). -->
      <template v-if="openToolId">
        <button class="secondary compact back-btn back-to-list" @click="backToList">
          ← {{ activePrimaryLabel }}
        </button>

        <PhpView v-if="openToolId === 'php'" />
        <AspView v-else-if="openToolId === 'asp'" />
        <DnsView v-else-if="openToolId === 'dns'" />
        <SslView v-else-if="openToolId === 'ssl'" />
        <ProxyView v-else-if="openToolId === 'proxy'" />
        <FilesView v-else-if="openToolId === 'files'" embedded />
        <FtpView v-else-if="openToolId === 'ftp'" :site-name="selectedSiteName" />
        <SiteDatabasesView v-else-if="openToolId === 'webdb' || openToolId === 'domaindb'" />
        <ProtectedDirsView v-else-if="openToolId === 'protected'" />
        <SiteStatisticsView v-else-if="openToolId === 'stats'" />
        <SiteBackupView v-else-if="openToolId === 'backup'" />
        <CronView v-else-if="openToolId === 'cron'" />
        <SiteLogsView v-else-if="openToolId === 'logs'" />
        <CacheView v-else-if="openToolId === 'cache'" />
        <AppStoreView v-else-if="openToolId === 'store'" />
        <WordPressToolkitView v-else-if="openToolId === 'wp'" />
        <AppsView v-else-if="openToolId === 'apps'" />
        <AppToolkitView v-else-if="openToolId === 'toolkit'" />
        <SiteMigrationView v-else-if="openToolId === 'migrate'" />
        <SiteHostingView v-else-if="openToolId === 'hosting'" />
      </template>

      <!-- Sekme açılış içeriği (araç seçilmediğinde). -->
      <template v-else>
        <!-- PANO: profesyonel panel benzeri domain panosu (özet + önizleme + kısayollar + bağlamalar + subdomain). -->
        <SiteDetailView
          v-if="primary === 'pano'"
          :site="selectedSite"
          :busy-site-name="sitesStore.busySiteName"
          :busy-binding-key="sitesStore.busyBindingKey"
          :defaults-report="defaultsReport"
          @start="handleStart"
          @stop="handleStop"
          @delete="handleDelete"
          @prepare-defaults="handlePrepareDefaults"
          @open-tool="focusTool"
          @add-subdomain="handleAddSubdomain"
          @add-binding="handleAddBinding"
          @remove-binding="handleRemoveBinding" />

        <!-- POSTA: tek araç → doğrudan. -->
        <MailView v-else-if="primary === 'mail'" :domain="selectedSiteName" />

        <!-- BARINDIRMA ve DNS / BAŞLARKEN: dikey araç listesi (alt alta). -->
        <div v-else class="tool-stack">
          <button
            v-for="tool in toolsFor(primary)"
            :key="tool.id"
            type="button"
            class="tool-row"
            @click="openTool(tool.id)">
            <svg class="tab-icon" viewBox="0 0 24 24" aria-hidden="true" v-html="siteToolIcon(tool.id)"></svg>
            <span class="tool-row-text">
              <strong>{{ tool.label }}</strong>
              <small>{{ tool.description }}</small>
            </span>
            <span class="chev">›</span>
          </button>

          <!-- Rolün yetkisi olmayan bir sekmede liste TAMAMEN boş çiziliyordu: kullanıcı sayfanın
               yüklenmediğini sanıyordu. Boş durum artık sebebiyle söylenir. -->
          <p v-if="toolsFor(primary).length === 0" class="empty-state">
            {{ t("panel.sites.noToolsForRole") }}
          </p>
        </div>
      </template>
    </template>
  </section>
</template>

<script setup lang="ts">
import { primarySiteUrl } from "../utils/siteUrl";
import { computed, onMounted, ref, watch } from "vue";
import router from "../router";
import { allowNavigation } from "../composables/navigationGuard";
import { useI18n } from "vue-i18n";
import { getPanelPreferences } from "../api/panelSettings";
import type { SiteBindingPayload, SiteDefaultsResult } from "../api/sites";
import { useConfirm } from "../components/ConfirmDialog.vue";
import SiteHealthStrip from "../components/SiteHealthStrip.vue";
import SiteRepairNotice from "../components/SiteRepairNotice.vue";
import { extractApiMessage, useAuthStore } from "../stores/auth";
import { useSitesStore } from "../stores/sites";
import { lazyView } from "../utils/lazyView";
import { siteToolIcon } from "../utils/siteToolIcons";
import { toReachableUrl } from "../utils/url";
import SitesView from "./SitesView.vue";
import SiteDetailView from "./SiteDetailView.vue";

/*
 * ALAN ADI ARAÇLARI TALEP ÜZERİNE İNDİRİLİR (bkz. utils/lazyView.ts).
 *
 * Bu ekran 21 araç görünümünü statik import ediyordu; hiçbirine tıklanmasa bile hepsinin kodu
 * indiriliyordu. SitesView (liste) ve SiteDetailView (Pano) BİLEREK statik: çalışma alanına
 * girildiğinde ilk çizilen ikisi bunlar.
 *
 * NOT: per-site "Web DB" (araç kurulumu) kaldırıldı; DB yönetimi tek "Veritabanları" sekmesinde
 * (SiteDatabasesView). phpMyAdmin/Adminer KURULUMU global Veritabanları bölümünde (WebDbView) kalır.
 */
const AppStoreView = lazyView(() => import("./AppStoreView.vue"));
const AppsView = lazyView(() => import("./AppsView.vue"));
const AppToolkitView = lazyView(() => import("./AppToolkitView.vue"));
const AspView = lazyView(() => import("./AspView.vue"));
const CacheView = lazyView(() => import("./CacheView.vue"));
const CronView = lazyView(() => import("./CronView.vue"));
const SiteDatabasesView = lazyView(() => import("./SiteDatabasesView.vue"));
const SiteLogsView = lazyView(() => import("./SiteLogsView.vue"));
const SiteMigrationView = lazyView(() => import("./SiteMigrationView.vue"));
const FilesView = lazyView(() => import("./FilesView.vue"));
const MailView = lazyView(() => import("./MailView.vue"));
const SiteBackupView = lazyView(() => import("./SiteBackupView.vue"));
const SiteStatisticsView = lazyView(() => import("./SiteStatisticsView.vue"));
const ProtectedDirsView = lazyView(() => import("./ProtectedDirsView.vue"));
const WordPressToolkitView = lazyView(() => import("./WordPressToolkitView.vue"));
const DnsView = lazyView(() => import("./DnsView.vue"));
const FtpView = lazyView(() => import("./FtpView.vue"));
const PhpView = lazyView(() => import("./PhpView.vue"));
const ProxyView = lazyView(() => import("./ProxyView.vue"));
const SiteHostingView = lazyView(() => import("./SiteHostingView.vue"));
const SslView = lazyView(() => import("./SslView.vue"));

type PrimaryTab = "pano" | "hostingdns" | "mail" | "start";
/*
 * `glyph` ALANI KALDIRILDI. Taşıdığı değer ölçüldü ve sıfırdı: her satırda damganın hemen
 * yanında zaten tam ad (`label`) ve bir cümlelik açıklama (`description`) yazılı, yani "PX" ya
 * da "GÖÇ" kısaltmasının anlatacağı hiçbir şey kalmıyordu. Alan dursaydı yeni bir araç eklerken
 * kimse ikonu değil o kısaltmayı doldururdu; tipten çıkarmak tek yolu kapatır.
 */
type ToolDef = { id: string; group: PrimaryTab; label: string; description: string };

/**
 * Üst bardaki aramadan gelen "bu siteyi aç" isteği. `seq` sayacıyla gelir: aynı site arka arkaya
 * seçildiğinde de tetiklenmesi gerekir (değer aynı kalırsa izleyici çalışmazdı).
 */
const props = defineProps<{ openRequest?: { siteName: string; seq: number } | null }>();

const { t } = useI18n();
const { confirm } = useConfirm();
const sitesStore = useSitesStore();
const authStore = useAuthStore();
const isAdmin = computed(() => authStore.session?.user?.role === "Administrator");
const isReseller = computed(() => authStore.session?.user?.role === "Reseller");

// GÖRÜNÜRLÜK, BACKEND'İN ROL LİSTESİYLE AYNI OLMALI.
//
// Burada yalnız dört araç kısıtlanıyordu; geri kalan her şey müşteriye görünüyordu ama
// controller'ların çoğu "Administrator,Reseller" istiyor. Sonuç: müşteri PHP, SSL, FTP, DNS,
// istatistik gibi 13 araca tıklıyor ve her birinde 403 alıyordu — panel ona olmayan bir yetki
// vaat ediyordu.
//
// Server-level araçlar (yazılım mağazası, web DB kurulumu, önbellek, cron) yalnız admin'e açık.
const adminOnlyTools = new Set(["store", "webdb", "cache", "cron", "logs", "migrate"]);

// Bayi ve admin'e açık, MÜŞTERİYE KAPALI olanlar — karşılıkları:
//   hosting/toolkit → SiteHostingSettingsController, php → PhpController, asp → AspController,
//   dns → DnsController, ssl → SslController, proxy → ProxyController, ftp → FtpController,
//   stats → StatisticsController, wp → WordPressController, apps → AppRuntimeController
const resellerAndAdminTools = new Set([
  "hosting", "toolkit", "php", "asp", "dns", "ssl", "proxy", "ftp", "stats", "wp", "apps"
]);

const inWorkspace = ref(false);
const primary = ref<PrimaryTab>("pano");
const openToolId = ref<string>("");
const notice = ref("");
const noticeError = ref(false);
const defaultsReport = ref<SiteDefaultsResult | null>(null);
const defaultSiteRoot = ref("C:\\inetpub\\wwwroot");

const selectedSiteName = computed(() => sitesStore.selectedSiteName);
const selectedSite = computed(() => sitesStore.selectedSite);

const primaryUrl = computed(() => primarySiteUrl(selectedSite.value?.bindings ?? []));

watch(
  () => props.openRequest,
  (request) => {
    if (!request?.siteName) {
      return;
    }
    sitesStore.selectSite(request.siteName);
    notice.value = "";
    primary.value = "pano";
    openToolId.value = "";
    inWorkspace.value = true;
    publishContext();
  },
  { immediate: true }
);

onMounted(async () => {
  await getPanelPreferences()
    .then((settings) => {
      defaultSiteRoot.value = settings.defaultSiteRoot.replace(/[\\/]+$/, "");
    })
    .catch(() => undefined);
});

// Tüm domain araçları + ait olduğu ana sekme (panel gruplaması).
const allTools = computed<ToolDef[]>(() => [
  { id: "hosting", group: "hostingdns", label: t("panel.sites.tools.hosting"), description: t("panel.sites.tools.hostingHint") },
  { id: "php", group: "hostingdns", label: t("panel.sites.tools.php"), description: t("panel.sites.tools.phpHint") },
  { id: "asp", group: "hostingdns", label: t("panel.sites.tools.asp"), description: t("panel.sites.tools.aspHint") },
  { id: "dns", group: "hostingdns", label: t("panel.sites.tools.dns"), description: t("panel.sites.tools.dnsHint") },
  { id: "ssl", group: "hostingdns", label: t("panel.sites.tools.ssl"), description: t("panel.sites.tools.sslHint") },
  { id: "proxy", group: "hostingdns", label: t("panel.sites.tools.proxy"), description: t("panel.sites.tools.proxyHint") },
  { id: "files", group: "hostingdns", label: t("panel.sites.tools.files"), description: t("panel.sites.tools.filesHint") },
  { id: "ftp", group: "hostingdns", label: t("panel.sites.tools.ftp"), description: t("panel.sites.tools.ftpHint") },
  { id: "webdb", group: "hostingdns", label: t("panel.sites.tools.domaindb"), description: t("panel.sites.tools.domaindbHint") },
  { id: "protected", group: "hostingdns", label: t("panel.sites.tools.protected"), description: t("panel.sites.tools.protectedHint") },
  { id: "stats", group: "hostingdns", label: t("panel.sites.tools.stats"), description: t("panel.sites.tools.statsHint") },
  { id: "backup", group: "hostingdns", label: t("panel.sites.tools.backup"), description: t("panel.sites.tools.backupHint") },
  { id: "cron", group: "hostingdns", label: t("panel.sites.tools.cron"), description: t("panel.sites.tools.cronHint") },
  { id: "logs", group: "hostingdns", label: t("panel.sites.tools.logs"), description: t("panel.sites.tools.logsHint") },
  { id: "cache", group: "hostingdns", label: t("panel.sites.tools.cache"), description: t("panel.sites.tools.cacheHint") },
  { id: "mail", group: "mail", label: t("panel.sites.tools.mail"), description: t("panel.sites.tools.mailHint") },
  { id: "toolkit", group: "start", label: t("panel.sites.tools.toolkit"), description: t("panel.sites.tools.toolkitHint") },
  { id: "migrate", group: "start", label: t("panel.sites.tools.migrate"), description: t("panel.sites.tools.migrateHint") },
  { id: "store", group: "start", label: t("panel.sites.tools.store"), description: t("panel.sites.tools.storeHint") },
  { id: "wp", group: "start", label: "WordPress", description: t("panel.wp.hint") },
  { id: "apps", group: "start", label: t("panel.sites.tools.apps"), description: t("panel.sites.tools.appsHint") }
]);

const visibleTools = computed(() =>
  allTools.value.filter((tool) => {
    if (isAdmin.value) {
      return true;
    }
    if (adminOnlyTools.has(tool.id)) {
      return false;
    }
    return isReseller.value || !resellerAndAdminTools.has(tool.id);
  })
);
function toolsFor(group: PrimaryTab): ToolDef[] {
  return visibleTools.value.filter((tool) => tool.group === group);
}

// `icon` ayrı bir alan (id'ye eşit değil): sekme kimlikleri bu ekranın grup adları
// ("pano", "start"), ikon sözlüğü ise ortak. İkisini karıştırmak sessizce boş daire çizdirirdi.
const primaryTabs = computed(() => [
  { id: "pano" as const, icon: "dashboard", label: t("panel.sites.primary.dashboard") },
  { id: "hostingdns" as const, icon: "hosting", label: t("panel.sites.primary.hostingDns") },
  { id: "mail" as const, icon: "mail", label: t("panel.sites.primary.mail") },
  { id: "start" as const, icon: "getStarted", label: t("panel.sites.primary.getStarted") }
]);

const activePrimaryLabel = computed(() =>
  primaryTabs.value.find((tab) => tab.id === primary.value)?.label ?? "");

// Domain satırından / pano kısayolundan gelen hızlı eylem: siteyi seç + doğru sekme/araca git.
async function onOpenTool(payload: { siteName: string; tool: string }) {
  if (!await allowNavigation()) return;
  sitesStore.selectSite(payload.siteName);
  notice.value = "";
  focusTool(payload.tool);
  inWorkspace.value = true;
  publishContext();
}

// Bir aracı doğru ana sekme altında aç. "pano" / bilinmeyen → Pano sekmesi; Posta tek araç → doğrudan.
function focusTool(tool: string) {
  if (tool === "pano") {
    primary.value = "pano";
    openToolId.value = "";
    return;
  }
  const def = visibleTools.value.find((item) => item.id === tool);
  const group = def?.group ?? "hostingdns";
  primary.value = group;
  openToolId.value = group === "mail" || !def ? "" : tool;
}

async function selectPrimary(tab: PrimaryTab) {
  if (!await allowNavigation()) return;
  primary.value = tab;
  openToolId.value = "";
  publishContext();
}

async function openTool(id: string) {
  if (!await allowNavigation()) return;
  openToolId.value = id;
  publishContext();
}

async function backToList() {
  if (!await allowNavigation()) return;
  openToolId.value = "";
  publishContext();
}

async function backToSites() {
  if (!await allowNavigation()) return;
  inWorkspace.value = false;
  openToolId.value = "";
  primary.value = "pano";
  publishContext();
}

function publishContext() {
  void router.push({ query: { section: "sites", ...(inWorkspace.value ? { site: selectedSiteName.value, tool: openToolId.value || undefined, tab: primary.value } : {}) } });
}
watch([() => router.currentRoute.value, () => sitesStore.sites], () => {
  const query = router.currentRoute.value.query;
  if (query.section !== "sites") return;
  const site = typeof query.site === "string" ? query.site : "";
  if (!site || !sitesStore.sites.some((item) => item.name === site)) {
    inWorkspace.value = false;
    openToolId.value = "";
    return;
  }
  sitesStore.selectSite(site);
  inWorkspace.value = true;
  const tool = typeof query.tool === "string" ? query.tool : "";
  if (visibleTools.value.some((item) => item.id === tool)) focusTool(tool);
  else {
    openToolId.value = "";
    primary.value = (primaryTabs.value.some((tab) => tab.id === query.tab) ? query.tab : "pano") as PrimaryTab;
  }
}, { immediate: true });

// --- Pano (SiteDetailView) eylemleri: sitesStore üzerinden. ---
function showNotice(message: string, isError: boolean) {
  notice.value = message;
  noticeError.value = isError;
}

function resolveApiMessage(error: unknown): string {
  const key = extractApiMessage(error);
  const translated = t(key);
  return translated === key ? key : translated;
}

async function handleStart(siteName: string) {
  try {
    await sitesStore.startSite(siteName);
    showNotice(t("panel.sites.started"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
}

async function handleStop(siteName: string) {
  try {
    await sitesStore.stopSite(siteName);
    showNotice(t("panel.sites.stopped"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
}

async function handleDelete(siteName: string) {
  // Yüksek riskli onay: cascade silme (dosya + veritabanı) için site adı yazılmadan devam edilemez.
  await confirm({
    title: t("panel.confirm.deleteSiteTitle", { name: siteName }),
    message: t("panel.sites.confirmDelete"),
    impact: [
      t("panel.sites.deletePlan.files"),
      t("panel.sites.deletePlan.databases"),
      t("panel.sites.deletePlan.dns"),
    ],
    requireText: siteName,
    action: async () => {
      try {
        // Cascade: site dosyaları + SAHİPLİĞİ DOĞRULANMIŞ veritabanları silinir. Adı siteye
        // benzeyen ama sahipliği doğrulanamayan veritabanları bilerek silinmez.
        const result = await sitesStore.deleteSite(siteName, { deleteDatabases: true, deleteFiles: true });
        // Uyarılar YUTULMAZ: silinemeyen/atlanan kaynak varsa kullanıcı bunu görmeli, yoksa
        // "silindi" yazısına güvenip arkada kalan veritabanını/görevi hiç fark etmez.
        //
        // Dosya silmenin sonucu ÖLÇÜLEMEDİYSE başlık da kesin konuşamaz: "temizlenemedi" demek,
        // sunucunun tam olarak kuramadığını söylediği iddiayı arayüzde geri kurmak olurdu.
        if (result && result.filesDeletionMeasured === false) {
          showNotice(`${t("panel.sites.deletedFilesUnmeasured")}: ${result.warnings.join(" • ")}`, true);
        } else if (result && result.warnings.length) {
          showNotice(`${t("panel.sites.deletedWithWarnings")}: ${result.warnings.join(" • ")}`, true);
        } else {
          showNotice(t("panel.sites.deleted"), false);
        }
        backToSites();
      } catch (error) {
        showNotice(resolveApiMessage(error), true);
      }
    },
  });
}

async function handlePrepareDefaults(siteName: string) {
  try {
    defaultsReport.value = await sitesStore.prepareSiteDefaults(siteName);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
}

async function handleAddBinding(payload: { siteName: string; binding: SiteBindingPayload }) {
  try {
    await sitesStore.addBinding(payload.siteName, payload.binding);
    showNotice(t("panel.sites.bindingAdded"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
}

/**
 * Bağlantı kaldırma ONAYSIZ YAPILMAZ — gerekçesi `SitesView`'deki ikiziyle aynı: işlem tek tıkla
 * siteyi bir adresten yayından kaldırıyor. İki ekran aynı mağazadan (`sitesStore`) aynı işi
 * yapıyor; birine onay koyup öbürüne koymamak, kullanıcının hangi ekranda olduğuna göre farklı
 * korunması olurdu.
 */
async function handleRemoveBinding(payload: { siteName: string; binding: SiteBindingPayload }) {
  await confirm({
    title: t("panel.sites.removeBinding"),
    message: t("panel.sites.confirmRemoveBinding"),
    action: async () => {
      try {
        await sitesStore.removeBinding(payload.siteName, payload.binding);
        showNotice(t("panel.sites.bindingRemoved"), false);
      } catch (error) {
        showNotice(resolveApiMessage(error), true);
      }
    }
  });
}

async function handleAddSubdomain(label: string) {
  const parent = selectedSiteName.value;
  if (!parent || !label) {
    return;
  }
  const host = `${label}.${parent}`.toLowerCase();
  try {
    const sonuc = await sitesStore.createSite({
      name: host,
      hostName: host,
      physicalPath: `${defaultSiteRoot.value}\\${host}`,
      applicationPoolName: "",
      ipAddress: "*",
      port: 80,
      protocol: "http",
      autoStart: true,
      runtimeProfile: "auto",
      runtimeComponentId: "",
      installRuntimeIfMissing: true,
      startupFile: "",
      provisionDefaultServices: false
    });
    // UYARILAR YUTULMUYORDU: site kuruluyor, DNS kaydı düşmüyor ve kullanıcı "başarılı" mesajını
    // görüp alan adının çözmesini bekliyordu. Sunucunun söylediği neyse o yazılır.
    if (sonuc.warnings.length > 0) {
      showNotice(`${t("panel.sites.subdomainCreated", { host })} — ${sonuc.warnings.join(" · ")}`, true);
      return;
    }

    const dnsKaydi = sonuc.created.find((satir) => satir.startsWith("dns:"));
    showNotice(
      dnsKaydi
        ? `${t("panel.sites.subdomainCreated", { host })} — ${dnsKaydi}`
        : t("panel.sites.subdomainCreated", { host }),
      false
    );
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
}
</script>

<style scoped>
.website-workspace {
  display: grid;
  gap: var(--sp-10);
  min-width: 0;
}

/* Bağlam çubuğu (panel breadcrumb): geri + hedef domain + siteyi aç. */
.tool-context {
  display: flex;
  align-items: center;
  gap: var(--sp-12);
  flex-wrap: wrap;
  padding: var(--sp-8) var(--sp-12);
  border: 1px solid var(--panel-border);
  /* 11px olcek disiydi ve komsu yuzeylerin hicbiriyle eslesmiyordu (panel 13, form 9). */
  border-radius: var(--radius-md);
  background: var(--surface-1);
}

.tool-context .back-btn {
  font-weight: 700;
}

/* ALAN ADI bu ekranin KIMLIGI. 0.96rem = 12.96px, yani sayfanin en onemli bilgisi govde
   metninden (13.5px) KUCUK yaziliyordu. Baslik merdiveninin bir adimina alindi (--fs-xl,
   15.9px): asagidaki arac basliklariyla (h2, 19.2px) yarismaz ama ust satirdaki dugmelerden
   ayrisir. */
.tool-context .context-domain {
  font-size: var(--fs-xl);
  /* KALINLIK 700'DE TUTULUYOR. Etiket `<strong>`ten `<h2>`ye gecince taban kural onu 660'a
     dusururdu; hemen yanindaki geri dugmesi ise ACIKCA 700 (bir ust kural). Yani baslik,
     komsusundan DAHA SONUK olurdu. Etiket degisikligi YAPISALDI (baslik merdiveni), gorsel
     degil: onceki tur bu seridin gorunumunu olcerek kurmustu, o karar korunuyor. */
  font-weight: 700;
  color: var(--text-strong);
}

/* `<a>` oldugu icin `button.secondary` kurallari BURAYA ULASMAZ (bkz. sablondaki not);
   ikincil dugme gorunumu burada, HER genislikte veriliyor. */
.tool-context .open-link {
  margin-left: auto;
  display: inline-flex;
  align-items: center;
  min-height: 30px;
  padding: var(--sp-4) var(--sp-8);
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-sm);
  background: var(--surface-3);
  color: var(--text-strong);
  font-weight: 600;
  text-decoration: none;
}

/* 4 ana sekme — kapsayici gorunumu ortak `.segmented-tabs`ten gelir.
   YEREL FARK: sekmeler seridi ESIT bolusur (ortak seritte icerige gore daralirlar) ve
   ikon tasidiklari icin daha yuksekler. Aktif/hover/odak gorunumu BILEREK yazilmadi:
   eski aktif dolgusu `rgba(22, 29, 41, 0.34)` iceriyordu — koda GOMULU koyu bir renk,
   yani acik temada aktif sekmenin uzerine koyu bir perde cekiyordu. */
.primary-tab {
  display: inline-flex;
  flex: 1 1 auto;
  justify-content: center;
  align-items: center;
  gap: var(--sp-8);
  min-height: 42px;
}

.primary-tab strong {
  font-size: var(--fs-sm);
}

/*
 * IKON KARESI — artik gercek ikon, harf damgasi degil.
 *
 * ONCEKI HAL: bu kare uc harfe kadar cikan uydurulmus kisaltmalar tasiyordu (PX, KOR, IST, CA,
 * CR, LOG, GOC, </>, .N, @, ▦, ★) ve ilk olcumde 8.91px cizilyordu — panelin taban adiminin
 * (`--fs-2xs`, 10.53px) altinda. Once yalnizca boy tabana cekildi; asil sorun bu degildi.
 * Kisaltma HIC BILGI TASIMIYORDU: her satirda hemen yaninda tam ad + bir cumlelik aciklama
 * zaten yaziliydi, yani "PX" buyutulunce de "Ters vekil" demiyordu. Ayni seritte bes ayri
 * kisaltma dili + dort yazi tipi simgesi vardi.
 *
 * SIMDI: `utils/siteToolIcons.ts` — SitesView ve SiteDetailView ile TEK kaynak. Kutu olcusu
 * (26px), yaricap ve zemin AYNEN korundu; degisen yalnizca kutunun ICI. Kimligi olmayan bir
 * arac icin `siteToolIcon` bos daireye duser: arac kaybolmaz, yalnizca kimliksiz gorunur.
 */
.tab-icon {
  box-sizing: border-box;
  width: 26px;
  height: 26px;
  padding: 5px;
  border-radius: var(--radius-sm);
  background: color-mix(in srgb, var(--accent-1) 12%, transparent);
  fill: none;
  stroke: var(--accent-1);
  stroke-width: 1.7;
  stroke-linecap: round;
  stroke-linejoin: round;
  flex-shrink: 0;
}

.back-to-list {
  justify-self: start;
}

/* Dikey araç listesi (alt alta — panel Barındırma ve DNS düzeni). */
.tool-stack {
  display: grid;
  gap: var(--sp-8);
}

.tool-row {
  display: flex;
  align-items: center;
  gap: var(--sp-12);
  width: 100%;
  padding: var(--sp-12) var(--sp-16);
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-md);
  background: var(--surface-1);
  color: var(--text-strong);
  text-align: left;
  cursor: pointer;
  transition: border-color 0.15s ease, transform 0.05s ease;
}

.tool-row:hover {
  border-color: color-mix(in srgb, var(--accent-1) 42%, transparent);
}

.tool-row:active {
  transform: translateY(1px);
}

.tool-row-text {
  display: grid;
  gap: var(--sp-2);
  min-width: 0;
  flex: 1 1 auto;
}

.tool-row-text strong {
  font-size: var(--fs-md);
}

.tool-row-text small {
  color: var(--text-muted);
  font-size: var(--fs-2xs);
}

.tool-row .chev {
  color: var(--text-muted);
  font-size: var(--fs-2xl);
  line-height: 1;
  flex-shrink: 0;
}

/*
  KIRILIM NOKTALARI 760/620 -> 900/720.
  Bu ekran kendi esiklerini uyduruyordu: 760'ta sekmeler kaydirma seridine, 620'de 2x2
  izgaraya geciyordu. Oysa MainLayout kenar menuyu 900'de cekmeceye aliyor ve base.css
  parmak hedeflerini 720'de buyutuyor. Sonuc dort ayri esikti; ekran, panelin geri kalani
  hic degismemisken tek basina yerlesim degistiriyordu.
*/
@media (max-width: 900px) {
  .primary-tab {
    flex: 0 0 auto;
  }
}

/*
  Telefon: 4 ana sekme yatay kaydırmalı şeritte kalınca "Posta"/"Başlarken" ekran dışında
  kalıyor ve kullanıcı orada bir şey olduğunu göremiyordu. 2x2 ızgaraya alınır: dördü de
  görünür, her biri tam genişlikte bir parmak hedefi olur.
*/
@media (max-width: 720px) {
  .primary-nav {
    display: grid;
    grid-template-columns: repeat(2, minmax(0, 1fr));
    gap: var(--sp-4);
    overflow: visible;
  }

  .primary-tab {
    flex: none;
    justify-content: flex-start;
    gap: var(--sp-8);
    min-height: 46px;
    padding: var(--sp-8) var(--sp-10);
    white-space: normal;
    text-align: left;
  }

  .primary-tab strong {
    font-size: var(--fs-xs);
    line-height: 1.15;
  }

  .tool-context {
    gap: var(--sp-8) var(--sp-10);
    padding: var(--sp-10);
  }

  .tool-context .back-btn {
    min-height: 40px;
  }

  /* Uzun alan adı düğmeleri sıkıştırmasın: kendi satırını alsın.
     `font-size: var(--fs-lg)` KALDIRILDI. OLCULDU: 1rem = 13.5px, yani GOVDE METNIYLE
     BIREBIR AYNI -- telefonda sayfanin basligi altindaki cumlelerden ayirt edilemiyordu.
     Kucultmenin gerekcesi de yoktu: satir zaten `flex: 1 1 100%` ile kendi satirini
     aliyor, sikisacak bir komsusu yok. Boy artik iki genislikte de `--fs-xl` (15.9px). */
  .tool-context .context-domain {
    flex: 1 1 100%;
    order: -1;
    overflow-wrap: anywhere;
  }

  /* Yalniz PARMAK HEDEFI buyur: gorunumun kendisi artik yukaridaki kuralda. */
  .tool-context .open-link {
    min-height: 40px;
    padding: var(--sp-8) var(--sp-12);
  }

  .tool-row {
    gap: var(--sp-10);
    min-height: 56px;
    padding: var(--sp-10) var(--sp-12);
  }

  .tool-row-text small {
    font-size: var(--fs-2xs);
  }
}
</style>
