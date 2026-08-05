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
          <strong>{{ pendingUpdateCount }}</strong>
          <span>{{ t("panel.attention.summaryUpdates") }}</span>
        </button>

        <button type="button" class="ac-chip" :disabled="!can('sites')" @click="can('sites') && go('sites')">
          <strong>{{ expiringCertificateCount }}</strong>
          <span>{{ t("panel.attention.summaryCertificates") }}</span>
        </button>

        <button type="button" class="ac-chip alert" @click="go('attention')">
          <strong>{{ attention.criticalCount + attention.warningCount }}</strong>
          <span>{{ t("panel.attention.summaryAlerts") }}</span>
        </button>
      </div>

      <p v-if="attention.totalCount === 0" class="ac-clear">{{ t("panel.attention.emptyTitle") }}</p>
    </section>

    <section class="panel quick-actions-panel">
      <span class="tile-label">{{ t("panel.dashboard.quickActions") }}</span>
      <div class="quick-actions">
        <button v-if="can('sites')" class="qa-btn" @click="go('sites')">
          <span class="qa-glyph">WS</span>
          <strong>{{ t("panel.navigation.sites") }}</strong>
        </button>
        <button v-if="can('mail')" class="qa-btn" @click="go('mail')">
          <span class="qa-glyph">ML</span>
          <strong>{{ t("panel.navigation.mail") }}</strong>
        </button>
        <button v-if="can('database')" class="qa-btn" @click="go('database')">
          <span class="qa-glyph">DB</span>
          <strong>{{ t("panel.navigation.database") }}</strong>
        </button>
        <button v-if="can('files')" class="qa-btn" @click="go('files')">
          <span class="qa-glyph">FL</span>
          <strong>{{ t("panel.navigation.files") }}</strong>
        </button>
        <button v-if="can('tools')" class="qa-btn" @click="go('tools')">
          <span class="qa-glyph">AR</span>
          <strong>{{ t("panel.navigation.serverTools") }}</strong>
        </button>
      </div>
    </section>

    <section class="panel access-panel">
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
    </section>

    <section class="panel overview-panel">
      <div v-if="overview" class="meter-grid">
        <article class="meter-card">
          <div class="meter-ring" :style="ringStyle(overview.cpuUsagePercent)">
            <div class="meter-core">
              <span>{{ t("panel.system.cpu") }}</span>
              <strong>{{ overview.cpuUsagePercent.toFixed(1) }}%</strong>
              <small>{{ overview.logicalProcessorCount }} {{ t("panel.system.cores") }}</small>
            </div>
          </div>
        </article>

        <article class="meter-card">
          <div class="meter-ring" :style="ringStyle(overview.memoryUsagePercent)">
            <div class="meter-core">
              <span>{{ t("panel.system.memory") }}</span>
              <strong>{{ overview.memoryUsagePercent.toFixed(1) }}%</strong>
              <small>{{ formatBytes(overview.usedMemoryBytes) }} / {{ formatBytes(overview.totalMemoryBytes) }}</small>
            </div>
          </div>
        </article>

        <article class="meter-card">
          <div class="meter-ring" :style="ringStyle(primaryDisk?.usagePercent ?? 0, 'var(--warn-1)')">
            <div class="meter-core">
              <span>{{ primaryDisk?.name || t("panel.system.disks") }}</span>
              <strong>{{ primaryDisk ? `${primaryDisk.usagePercent.toFixed(1)}%` : t("panel.common.notAvailable") }}</strong>
              <small v-if="primaryDisk">{{ formatBytes(primaryDisk.usedBytes) }} / {{ formatBytes(primaryDisk.totalBytes) }}</small>
              <small v-if="primaryDisk">{{ t("panel.system.free") }}: {{ formatBytes(primaryDisk.freeBytes) }}</small>
            </div>
          </div>
        </article>
      </div>

      <div class="summary-grid">
        <button type="button" :class="['summary-card', { clickable: can('sites') }]" @click="can('sites') && go('sites')">
          <span class="tile-label">{{ t("panel.sites.title") }}</span>
          <strong>{{ sitesCount }}</strong>
          <small>{{ t("panel.sites.bindings") }}: {{ totalBindingCount }}</small>
        </button>

        <button type="button" :class="['summary-card', { clickable: can('sites') }]" @click="can('sites') && go('sites')">
          <span class="tile-label">{{ t("panel.ftp.title") }}</span>
          <strong>{{ ftpUserCount }}</strong>
          <small>{{ t("panel.ftp.cardHint") }}</small>
        </button>

        <button type="button" :class="['summary-card', { clickable: can('database') }]" @click="can('database') && go('database')">
          <span class="tile-label">{{ t("panel.database.title") }}</span>
          <strong>{{ databaseCount }}</strong>
          <small>{{ t("panel.database.cardHint") }}</small>
        </button>

        <button type="button" :class="['summary-card', { clickable: can('security') }]" @click="can('security') && go('security')">
          <span class="tile-label">{{ t("panel.security.title") }}</span>
          <strong>{{ securityStatusLabel }}</strong>
          <small>{{ securePath ? `/${securePath}` : t("panel.common.notAvailable") }}</small>
        </button>
      </div>
    </section>

    <div class="overview-columns">
      <section class="panel">
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

              <div v-for="disk in overview.disks" :key="disk.name" class="mini-row disk-row">
                <span>{{ disk.name }}</span>
                <span>{{ disk.usagePercent.toFixed(1) }}%</span>
                <span>{{ formatBytes(disk.freeBytes) }}</span>
              </div>
            </div>
          </article>
        </div>
      </section>

      <section class="panel">
        <div class="section-head">
          <h2>{{ t("panel.system.network") }}</h2>
          <span class="meta">{{ t("panel.audit.title") }}</span>
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

        <div class="audit-preview">
          <article v-for="item in auditItems.slice(0, 4)" :key="item.id" class="audit-item">
            <div class="audit-top">
              <strong>{{ item.action }}</strong>
              <span>{{ formatUtc(item.createdAtUtc) }}</span>
            </div>
            <p>{{ item.details }}</p>
            <small>{{ item.userName }} / {{ item.ipAddress || t("panel.common.notAvailable") }}</small>
          </article>
        </div>
      </section>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from "vue";
import { useI18n } from "vue-i18n";
import { getServerIpAddresses } from "../api/system";
import type { UpdateStatus } from "../api/update";
import type { AttentionSummary } from "../api/attention";
import { copyToClipboard } from "../utils/clipboard";

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
  // Dikkat özeti (yalnızca admin doldurur; null ise özet kartı gizli).
  attention?: AttentionSummary | null;
}>();

const { t } = useI18n();

// Bekleyen güncellemeler: bileşen güncellemeleri ("software") + panel güncellemesi ("updates").
const pendingUpdateItems = computed(() => (props.attention?.items ?? [])
  .filter((item) => item.section === "software" || item.section === "updates"));
const pendingUpdateCount = computed(() => pendingUpdateItems.value.length);

// Tıklayınca DOĞRU ekrana git. Sayaç iki farklı güncelleme türünü topluyor; hepsini panel
// güncelleme sayfasına yollamak "1 güncelleme var" deyip "zaten son sürümdesiniz" gösterir.
// Tek madde varsa kendi bölümüne, birden çoksa hepsini listeleyen çekmeceye gideriz.
const pendingUpdateTarget = computed(() => {
  const items = pendingUpdateItems.value;
  if (items.length === 1) {
    return items[0].section || "updates";
  }
  return items.length > 1 ? "attention" : "attention";
});

// Süresi dolan/dolmak üzere olan sertifikalar (Danışman'ın SSL maddeleri).
const expiringCertificateCount = computed(() => (props.attention?.items ?? [])
  .filter((item) => item.titleKey.startsWith("advisor.ssl")).length);

const attentionTone = computed(() => {
  if (!props.attention || props.attention.criticalCount > 0) {
    return "critical";
  }

  return props.attention.warningCount > 0 ? "warning" : "calm";
});

// profesyonel panel benzeri: hizli erisim kartlari + ozet kutulari ilgili bolume gider.
const emit = defineEmits<{ (event: "navigate", sectionId: string): void }>();

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
  // Ortak yardımcı http bağlamında da kopyalar; olmadıysa "kopyalandı" işareti verilmez.
  if (!(await copyToClipboard(url))) {
    return;
  }

  copiedUrl.value = url;
  window.setTimeout(() => {
    if (copiedUrl.value === url) copiedUrl.value = "";
  }, 1500);
}

const primaryDisk = computed(() => props.overview?.disks[0] ?? null);

function ringStyle(value: number, color = "var(--accent-1)") {
  return {
    "--progress": `${Math.max(0, Math.min(100, value))}%`,
    "--ring-color": color
  };
}

function formatUtc(value: string | null): string {
  if (!value) {
    return t("panel.common.notAvailable");
  }

  return new Date(value).toLocaleString();
}

function formatBytes(value: number): string {
  if (!Number.isFinite(value) || value <= 0) {
    return `0 ${t("panel.units.bytes")}`;
  }

  const units = [
    t("panel.units.bytes"),
    t("panel.units.kib"),
    t("panel.units.mib"),
    t("panel.units.gib"),
    t("panel.units.tib")
  ];

  let size = value;
  let unitIndex = 0;

  while (size >= 1024 && unitIndex < units.length - 1) {
    size /= 1024;
    unitIndex += 1;
  }

  return `${size.toFixed(size >= 10 || unitIndex === 0 ? 0 : 1)} ${units[unitIndex]}`;
}

function formatSpeed(value: number): string {
  return `${formatBytes(value)}/${t("panel.units.second")}`;
}

function formatTimeSpan(value: string): string {
  const match = value.match(/^(?:(\d+)\.)?(\d{2}):(\d{2}):(\d{2})(?:\.\d+)?$/);
  if (!match) {
    return value;
  }

  const days = Number(match[1] ?? "0");
  const hours = Number(match[2]);
  const minutes = Number(match[3]);
  const seconds = Number(match[4]);
  const parts: string[] = [];

  if (days > 0) {
    parts.push(`${days}${t("panel.units.dayShort")}`);
  }

  parts.push(`${hours}${t("panel.units.hourShort")}`);
  parts.push(`${minutes}${t("panel.units.minuteShort")}`);
  parts.push(`${seconds}${t("panel.units.secondShort")}`);
  return parts.join(" ");
}
</script>

<style scoped>
.overview-stack,
.meter-grid,
.summary-grid,
.overview-columns,
.info-grid,
.mini-table,
.audit-preview {
  display: grid;
  gap: 18px;
}

.overview-columns {
  grid-template-columns: repeat(2, minmax(0, 1fr));
}

.meter-grid {
  grid-template-columns: repeat(3, minmax(0, 1fr));
}

.summary-grid {
  grid-template-columns: repeat(4, minmax(0, 1fr));
}

.meter-card,
.summary-card,
.info-card,
.mini-row,
.audit-item {
  border: 1px solid var(--panel-border);
  background: var(--surface-2);
}

.meter-card,
.summary-card,
.info-card {
  padding: 18px;
  border-radius: var(--radius-lg);
}

.meter-card {
  display: grid;
  place-items: center;
}

.meter-ring {
  --progress: 0%;
  --ring-color: var(--accent-1);
  width: min(220px, 100%);
  aspect-ratio: 1;
  padding: 14px;
  border-radius: 50%;
  background: conic-gradient(var(--ring-color) var(--progress), var(--ring-track) 0);
}

.meter-core {
  display: grid;
  place-items: center;
  gap: 8px;
  width: 100%;
  height: 100%;
  border-radius: 50%;
  background: linear-gradient(180deg, var(--surface-2), var(--surface-0));
  text-align: center;
}

.meter-core span,
.tile-label {
  color: var(--text-muted);
  font-size: 0.8rem;
  letter-spacing: 0.035em;
  text-transform: uppercase;
}

.access-panel {
  display: grid;
  gap: 10px;
}

.access-head {
  display: grid;
  gap: 2px;
}

.access-head small {
  color: var(--text-muted);
  font-size: 0.78rem;
}

.access-list {
  list-style: none;
  margin: 0;
  padding: 0;
  display: grid;
  gap: 8px;
}

.access-row {
  display: flex;
  align-items: center;
  gap: 10px;
  flex-wrap: wrap;
  padding: 8px 12px;
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-md);
  background: var(--surface-1);
}

.access-tag {
  min-width: 56px;
  font-weight: 700;
  font-size: 0.78rem;
  color: var(--accent-text);
}

.access-url {
  flex: 1;
  min-width: 200px;
  font-family: ui-monospace, "Cascadia Code", Consolas, monospace;
  font-size: 0.85rem;
  word-break: break-all;
  color: var(--text-strong);
}

.access-copy {
  flex: none;
}

.ext-conn {
  display: grid;
  gap: 4px;
  margin-top: 6px;
  padding-top: 8px;
  border-top: 1px solid var(--panel-border);
}

.ext-title {
  color: var(--text-muted);
  font-size: 0.72rem;
  letter-spacing: 0.035em;
  text-transform: uppercase;
}

.ext-row {
  display: flex;
  gap: 8px;
  align-items: baseline;
  flex-wrap: wrap;
}

.ext-tag {
  min-width: 72px;
  font-weight: 700;
  font-size: 0.74rem;
  color: var(--accent-text);
}

.ext-val {
  flex: 1;
  min-width: 200px;
  font-family: ui-monospace, "Cascadia Code", Consolas, monospace;
  font-size: 0.76rem;
  color: var(--text-soft);
  word-break: break-word;
}

.meter-core small {
  max-width: 86%;
  color: var(--text-soft);
  font-size: 0.72rem;
  font-weight: 650;
  line-height: 1.2;
}

.meter-core strong,
.summary-card strong {
  font-size: clamp(1.4rem, 2.8vw, 2rem);
}

.summary-card {
  display: grid;
  gap: 8px;
}

.summary-card small {
  color: var(--text-muted);
}

.quick-actions-panel {
  display: grid;
  gap: 10px;
}

/* Dikkat özeti kartı: panele girer girmez görünen tek satırlık durum. */
.attention-card {
  display: grid;
  gap: 10px;
}

.attention-card.critical {
  border-color: color-mix(in srgb, var(--danger-1) 45%, var(--panel-border));
}

.attention-card.warning {
  border-color: color-mix(in srgb, var(--warn-1) 40%, var(--panel-border));
}

.attention-card.calm {
  border-color: color-mix(in srgb, var(--ok-1) 35%, var(--panel-border));
}

.ac-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
}

.ac-chips {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(150px, 1fr));
  gap: 10px;
}

.ac-chip {
  display: grid;
  gap: 2px;
  padding: 10px 14px;
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

.ac-chip:not(:disabled):hover {
  border-color: color-mix(in srgb, var(--accent-1) 45%, transparent);
}

.ac-chip strong {
  font-size: 1.4rem;
  line-height: 1.1;
}

.ac-chip span {
  color: var(--text-muted);
  font-size: 0.78rem;
}

.attention-card.critical .ac-chip.alert strong {
  color: var(--danger-1);
}

.attention-card.warning .ac-chip.alert strong {
  color: var(--warn-1);
}

.ac-clear {
  margin: 0;
  color: var(--ok-1);
  font-size: 0.82rem;
  font-weight: 600;
}

/* "Yeni sürüm mevcut" uyarı bandı (yalnızca admin + guncelleme varsa). */
.update-banner {
  display: flex;
  align-items: center;
  gap: 14px;
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
  color: var(--accent-1);
  font-weight: 800;
  font-size: 1.05rem;
  flex-shrink: 0;
}

.ub-text {
  display: grid;
  gap: 2px;
  flex: 1;
  min-width: 0;
}

.ub-text span {
  color: var(--text-soft);
  font-size: 0.85rem;
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
  gap: 10px;
}

.qa-btn {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 12px 14px;
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-md);
  background: var(--surface-1);
  color: var(--text-strong);
  cursor: pointer;
  text-align: left;
  transition: border-color 0.15s ease, color 0.15s ease;
}

.qa-btn:hover {
  border-color: color-mix(in srgb, var(--accent-1) 45%, transparent);
  color: var(--accent-1);
}

.qa-glyph {
  display: grid;
  place-items: center;
  width: 34px;
  height: 34px;
  border-radius: 9px;
  background: color-mix(in srgb, var(--accent-1) 14%, transparent);
  color: var(--accent-1);
  font-size: 0.66rem;
  font-weight: 800;
  flex-shrink: 0;
}

.summary-card.clickable {
  cursor: pointer;
  text-align: left;
  width: 100%;
  font: inherit;
  color: inherit;
  transition: border-color 0.15s ease, box-shadow 0.15s ease;
}

.summary-card.clickable:hover {
  border-color: color-mix(in srgb, var(--accent-1) 45%, transparent);
  box-shadow: 0 0 0 1px color-mix(in srgb, var(--accent-1) 20%, transparent);
}

.section-head,
.audit-top {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
}

.info-grid {
  grid-template-columns: repeat(2, minmax(0, 1fr));
}

.info-list {
  display: grid;
  gap: 12px;
  margin: 0;
}

.info-list div {
  display: grid;
  gap: 4px;
}

.info-list dt {
  color: var(--text-muted);
  font-size: 0.9rem;
}

.info-list dd {
  margin: 0;
  font-weight: 600;
}

.mini-row {
  display: grid;
  gap: 12px;
  align-items: center;
  padding: 12px 14px;
  border-radius: var(--radius-sm);
}

.disk-row {
  grid-template-columns: 1.4fr 0.8fr 1fr;
}

.network-row {
  grid-template-columns: 1.2fr 0.9fr 0.9fr;
}

.table-head {
  font-weight: 700;
  background: var(--surface-2);
}

.audit-item {
  padding: 16px;
  border-radius: var(--radius-md);
}

.audit-item p,
.audit-item small {
  margin: 0;
}

@media (max-width: 1180px) {
  .overview-columns,
  .summary-grid,
  .meter-grid,
  .info-grid {
    grid-template-columns: 1fr;
  }
}

@media (max-width: 820px) {
  .section-head,
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
    gap: 8px;
    padding: 9px 10px;
    font-size: 0.82rem;
  }

  .mini-row span {
    min-width: 0;
    overflow-wrap: anywhere;
  }

  .disk-row {
    grid-template-columns: minmax(0, 1.2fr) 0.7fr 0.9fr;
  }

  .network-row {
    grid-template-columns: minmax(0, 1.1fr) 0.9fr 0.9fr;
  }

  .audit-item {
    padding: 12px;
  }
}

/*
  Telefon: olay akışının 3. adımı "kaynak tüketimini oku". Üç adet 220px halka alt alta
  gelince kullanıcı üçünü görmek için kaydırmak zorunda kalıyordu; küçültülüp yan yana
  dizilir, tek bakışta CPU/RAM/disk okunur.
*/
@media (max-width: 700px) {
  .meter-grid {
    grid-template-columns: repeat(3, minmax(0, 1fr));
    gap: 8px;
  }

  .meter-card {
    padding: 8px;
    border-radius: var(--radius-md);
  }

  .meter-ring {
    width: min(112px, 100%);
    padding: 8px;
  }

  .meter-core {
    gap: 2px;
  }

  .meter-core span {
    font-size: 0.56rem;
    letter-spacing: 0.02em;
  }

  .meter-core strong {
    font-size: clamp(0.9rem, 4.4vw, 1.15rem);
  }

  /* Halkanın içindeki "kullanılan / toplam" satırı bu boyutta okunmuyor; aynı bilgi
     hemen aşağıdaki Diskler tablosunda ve özet kartlarında zaten var. */
  .meter-core small {
    display: none;
  }

  /* Özet kutuları: 4 tam genişlik kart yerine 2x2 → kaydırma yarıya iner. */
  .summary-grid {
    grid-template-columns: repeat(2, minmax(0, 1fr));
    gap: 10px;
  }

  .summary-card {
    padding: 12px;
    gap: 4px;
  }

  .summary-card strong {
    font-size: clamp(1.1rem, 5.5vw, 1.5rem);
  }

  .summary-card small,
  .tile-label {
    font-size: 0.7rem;
  }

  .info-card {
    padding: 12px;
  }

  .access-row {
    gap: 6px 10px;
    padding: 10px;
  }

  .access-url,
  .ext-val {
    min-width: 100%;
  }
}
</style>
