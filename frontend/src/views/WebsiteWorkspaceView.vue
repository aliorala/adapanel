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
        <strong class="context-domain">{{ selectedSiteName || t("panel.php.selectSite") }}</strong>
        <a
          v-if="primaryUrl"
          class="secondary compact open-link"
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
      <nav class="primary-nav" :aria-label="t('panel.sites.toolsBar')">
        <button
          v-for="tab in primaryTabs"
          :key="tab.id"
          :class="['primary-tab', { active: primary === tab.id && !openToolId }]"
          @click="selectPrimary(tab.id)">
          <span class="tab-icon">{{ tab.glyph }}</span>
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
        <FilesView v-else-if="openToolId === 'files'" />
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
            <span class="tab-icon">{{ tool.glyph }}</span>
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
import { computed, onMounted, ref, watch } from "vue";
import { useI18n } from "vue-i18n";
import { getPanelPreferences } from "../api/panelSettings";
import type { SiteBindingPayload, SiteDefaultsResult } from "../api/sites";
import { useConfirm } from "../components/ConfirmDialog.vue";
import SiteHealthStrip from "../components/SiteHealthStrip.vue";
import SiteRepairNotice from "../components/SiteRepairNotice.vue";
import { extractApiMessage, useAuthStore } from "../stores/auth";
import { useSitesStore } from "../stores/sites";
import { lazyView } from "../utils/lazyView";
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
type ToolDef = { id: string; group: PrimaryTab; glyph: string; label: string; description: string };

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

const primaryUrl = computed(() => {
  const binding = selectedSite.value?.bindings?.[0];
  if (!binding) {
    return "";
  }
  const host = binding.hostName || (binding.ipAddress === "*" ? window.location.hostname : binding.ipAddress);
  const standard = (binding.protocol === "http" && binding.port === 80) ||
    (binding.protocol === "https" && binding.port === 443);
  return `${binding.protocol}://${host}${standard ? "" : `:${binding.port}`}`;
});

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
  { id: "hosting", group: "hostingdns", glyph: "⚙", label: t("panel.sites.tools.hosting"), description: t("panel.sites.tools.hostingHint") },
  { id: "php", group: "hostingdns", glyph: "P", label: t("panel.sites.tools.php"), description: t("panel.sites.tools.phpHint") },
  { id: "asp", group: "hostingdns", glyph: ".N", label: t("panel.sites.tools.asp"), description: t("panel.sites.tools.aspHint") },
  { id: "dns", group: "hostingdns", glyph: "DNS", label: t("panel.sites.tools.dns"), description: t("panel.sites.tools.dnsHint") },
  { id: "ssl", group: "hostingdns", glyph: "SSL", label: t("panel.sites.tools.ssl"), description: t("panel.sites.tools.sslHint") },
  { id: "proxy", group: "hostingdns", glyph: "PX", label: t("panel.sites.tools.proxy"), description: t("panel.sites.tools.proxyHint") },
  { id: "files", group: "hostingdns", glyph: "FL", label: t("panel.sites.tools.files"), description: t("panel.sites.tools.filesHint") },
  { id: "ftp", group: "hostingdns", glyph: "FT", label: t("panel.sites.tools.ftp"), description: t("panel.sites.tools.ftpHint") },
  { id: "webdb", group: "hostingdns", glyph: "SQL", label: t("panel.sites.tools.domaindb"), description: t("panel.sites.tools.domaindbHint") },
  { id: "protected", group: "hostingdns", glyph: "KOR", label: t("panel.sites.tools.protected"), description: t("panel.sites.tools.protectedHint") },
  { id: "stats", group: "hostingdns", glyph: "IST", label: t("panel.sites.tools.stats"), description: t("panel.sites.tools.statsHint") },
  { id: "backup", group: "hostingdns", glyph: "BK", label: t("panel.sites.tools.backup"), description: t("panel.sites.tools.backupHint") },
  { id: "cron", group: "hostingdns", glyph: "CR", label: t("panel.sites.tools.cron"), description: t("panel.sites.tools.cronHint") },
  { id: "logs", group: "hostingdns", glyph: "LOG", label: t("panel.sites.tools.logs"), description: t("panel.sites.tools.logsHint") },
  { id: "cache", group: "hostingdns", glyph: "CA", label: t("panel.sites.tools.cache"), description: t("panel.sites.tools.cacheHint") },
  { id: "mail", group: "mail", glyph: "@", label: t("panel.sites.tools.mail"), description: t("panel.sites.tools.mailHint") },
  { id: "toolkit", group: "start", glyph: "</>", label: t("panel.sites.tools.toolkit"), description: t("panel.sites.tools.toolkitHint") },
  { id: "migrate", group: "start", glyph: "GÖÇ", label: t("panel.sites.tools.migrate"), description: t("panel.sites.tools.migrateHint") },
  { id: "store", group: "start", glyph: "AP", label: t("panel.sites.tools.store"), description: t("panel.sites.tools.storeHint") },
  { id: "wp", group: "start", glyph: "WP", label: "WordPress", description: t("panel.wp.hint") },
  { id: "apps", group: "start", glyph: "JS", label: t("panel.sites.tools.apps"), description: t("panel.sites.tools.appsHint") }
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

const primaryTabs = computed(() => [
  { id: "pano" as const, glyph: "▦", label: t("panel.sites.primary.dashboard") },
  { id: "hostingdns" as const, glyph: "⚙", label: t("panel.sites.primary.hostingDns") },
  { id: "mail" as const, glyph: "@", label: t("panel.sites.primary.mail") },
  { id: "start" as const, glyph: "★", label: t("panel.sites.primary.getStarted") }
]);

const activePrimaryLabel = computed(() =>
  primaryTabs.value.find((tab) => tab.id === primary.value)?.label ?? "");

// Domain satırından / pano kısayolundan gelen hızlı eylem: siteyi seç + doğru sekme/araca git.
function onOpenTool(payload: { siteName: string; tool: string }) {
  sitesStore.selectSite(payload.siteName);
  notice.value = "";
  focusTool(payload.tool);
  inWorkspace.value = true;
}

// Bir aracı doğru ana sekme altında aç. "pano" / bilinmeyen → Pano sekmesi; Posta tek araç → doğrudan.
function focusTool(tool: string) {
  if (tool === "pano") {
    primary.value = "pano";
    openToolId.value = "";
    return;
  }
  const def = allTools.value.find((item) => item.id === tool);
  const group = def?.group ?? "hostingdns";
  primary.value = group;
  openToolId.value = group === "mail" || !def ? "" : tool;
}

function selectPrimary(tab: PrimaryTab) {
  primary.value = tab;
  openToolId.value = "";
}

function openTool(id: string) {
  openToolId.value = id;
}

function backToList() {
  openToolId.value = "";
}

function backToSites() {
  inWorkspace.value = false;
  openToolId.value = "";
  primary.value = "pano";
}

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
        if (result && result.warnings.length) {
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

async function handleRemoveBinding(payload: { siteName: string; binding: SiteBindingPayload }) {
  try {
    await sitesStore.removeBinding(payload.siteName, payload.binding);
    showNotice(t("panel.sites.bindingRemoved"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
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
  gap: 10px;
  min-width: 0;
}

/* Bağlam çubuğu (panel breadcrumb): geri + hedef domain + siteyi aç. */
.tool-context {
  display: flex;
  align-items: center;
  gap: 14px;
  flex-wrap: wrap;
  padding: 8px 12px;
  border: 1px solid var(--panel-border);
  border-radius: 11px;
  background: var(--surface-1);
}

.tool-context .back-btn {
  font-weight: 700;
}

.tool-context .context-domain {
  font-size: 0.96rem;
  color: var(--text-strong);
}

.tool-context .open-link {
  margin-left: auto;
  display: inline-flex;
  align-items: center;
  text-decoration: none;
}

/* 4 ana sekme (panel üst sekmeleri). */
.primary-nav {
  display: flex;
  gap: 6px;
  overflow-x: auto;
  padding: 5px;
  border: 1px solid var(--panel-border);
  border-radius: 12px;
  background: linear-gradient(180deg, var(--surface-2), var(--surface-0));
  box-shadow: var(--panel-shadow);
  scrollbar-width: thin;
}

.primary-tab {
  display: inline-flex;
  flex: 1 1 auto;
  justify-content: center;
  gap: 8px;
  align-items: center;
  min-height: 42px;
  padding: 8px 14px;
  border: 1px solid transparent;
  border-bottom: 2px solid transparent;
  border-radius: 9px;
  background: transparent;
  color: var(--text-soft);
  white-space: nowrap;
  cursor: pointer;
}

.primary-tab:hover {
  border-color: color-mix(in srgb, var(--accent-1) 20%, transparent);
  background: var(--surface-1);
}

.primary-tab.active {
  border-color: color-mix(in srgb, var(--accent-1) 16%, transparent);
  border-bottom-color: var(--accent-1);
  background: linear-gradient(145deg, color-mix(in srgb, var(--accent-2) 18%, transparent), rgba(22, 29, 41, 0.34));
}

.primary-tab strong {
  font-size: 0.84rem;
}

.tab-icon {
  display: grid;
  place-items: center;
  width: 26px;
  height: 26px;
  border-radius: 7px;
  background: color-mix(in srgb, var(--accent-1) 12%, transparent);
  color: var(--accent-1);
  font-size: 0.58rem;
  font-weight: 850;
  flex-shrink: 0;
}

.back-to-list {
  justify-self: start;
}

/* Dikey araç listesi (alt alta — panel Barındırma ve DNS düzeni). */
.tool-stack {
  display: grid;
  gap: 8px;
}

.tool-row {
  display: flex;
  align-items: center;
  gap: 14px;
  width: 100%;
  padding: 13px 16px;
  border: 1px solid var(--panel-border);
  border-radius: 12px;
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
  gap: 2px;
  min-width: 0;
  flex: 1 1 auto;
}

.tool-row-text strong {
  font-size: 0.9rem;
}

.tool-row-text small {
  color: var(--text-muted);
  font-size: 0.74rem;
}

.tool-row .chev {
  color: var(--text-muted);
  font-size: 1.3rem;
  line-height: 1;
  flex-shrink: 0;
}

@media (max-width: 760px) {
  .primary-tab {
    flex: 0 0 auto;
  }
}

/*
  Telefon: 4 ana sekme yatay kaydırmalı şeritte kalınca "Posta"/"Başlarken" ekran dışında
  kalıyor ve kullanıcı orada bir şey olduğunu göremiyordu. 2x2 ızgaraya alınır: dördü de
  görünür, her biri tam genişlikte bir parmak hedefi olur.
*/
@media (max-width: 620px) {
  .primary-nav {
    display: grid;
    grid-template-columns: repeat(2, minmax(0, 1fr));
    gap: 5px;
    overflow: visible;
  }

  .primary-tab {
    flex: none;
    justify-content: flex-start;
    gap: 8px;
    min-height: 46px;
    padding: 8px 10px;
    white-space: normal;
    text-align: left;
  }

  .primary-tab strong {
    font-size: 0.8rem;
    line-height: 1.15;
  }

  .tool-context {
    gap: 8px 10px;
    padding: 10px;
  }

  .tool-context .back-btn {
    min-height: 40px;
  }

  /* Uzun alan adı düğmeleri sıkıştırmasın: kendi satırını alsın. */
  .tool-context .context-domain {
    flex: 1 1 100%;
    order: -1;
    font-size: 0.9rem;
    overflow-wrap: anywhere;
  }

  .tool-context .open-link {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    min-height: 40px;
    padding: 8px 12px;
    border: 1px solid var(--panel-border);
    border-radius: var(--radius-sm);
    background: var(--surface-3);
    color: var(--text-strong);
    font-weight: 600;
  }

  .tool-row {
    gap: 10px;
    min-height: 56px;
    padding: 11px 12px;
  }

  .tool-row-text small {
    font-size: 0.7rem;
  }
}
</style>
