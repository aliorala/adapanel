<template>
  <div class="monitor-stack">
    <section class="panel">
      <div class="section-head">
        <div>
          <h2>{{ t("panel.monitor.title") }}</h2>
          <p class="meta">{{ t("panel.monitor.subtitle") }}</p>
        </div>
        <span class="meta">{{ loading ? t("panel.system.loading") : formatUtc(overview?.currentServerTimeUtc ?? null) }}</span>
      </div>

      <div v-if="overview" class="meter-grid">
        <article class="meter-card">
          <div class="meter-ring" :style="ringStyle(overview.cpuUsagePercent)">
            <div class="meter-core">
              <span>{{ t("panel.system.cpu") }}</span>
              <strong>{{ overview.cpuUsagePercent.toFixed(1) }}%</strong>
              <small>{{ overview.logicalProcessorCount }} {{ t("panel.system.cores") }}</small>
            </div>
          </div>
          <svg v-if="cpuSpark" class="meter-spark cpu" viewBox="0 0 100 28" preserveAspectRatio="none" aria-hidden="true">
            <polyline :points="cpuSpark" />
          </svg>
        </article>

        <article class="meter-card">
          <div class="meter-ring" :style="ringStyle(overview.memoryUsagePercent, '#6fd2ff')">
            <div class="meter-core">
              <span>{{ t("panel.system.memory") }}</span>
              <strong>{{ overview.memoryUsagePercent.toFixed(1) }}%</strong>
              <small>{{ formatBytes(overview.usedMemoryBytes) }} / {{ formatBytes(overview.totalMemoryBytes) }}</small>
            </div>
          </div>
          <svg v-if="memSpark" class="meter-spark mem" viewBox="0 0 100 28" preserveAspectRatio="none" aria-hidden="true">
            <polyline :points="memSpark" />
          </svg>
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
    </section>

    <div class="monitor-columns">
      <section class="panel">
        <h2>{{ t("panel.system.serverInfo") }}</h2>

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
                <dt>{{ t("panel.system.bootTime") }}</dt>
                <dd>{{ formatUtc(overview.bootTimeUtc) }}</dd>
              </div>
              <div>
                <dt>{{ t("panel.system.uptime") }}</dt>
                <dd>{{ formatTimeSpan(overview.uptime) }}</dd>
              </div>
            </dl>
          </article>
        </div>
      </section>

      <section class="panel">
        <h2>{{ t("panel.system.disks") }}</h2>

        <div v-if="overview" class="mini-table">
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
      </section>
    </div>

    <section class="panel">
      <h2>{{ t("panel.system.network") }}</h2>

      <div v-if="overview" class="mini-table">
        <div class="mini-row table-head network-row">
          <span>{{ t("panel.system.interface") }}</span>
          <span>{{ t("panel.system.download") }}</span>
          <span>{{ t("panel.system.upload") }}</span>
          <span>{{ t("panel.system.total") }}</span>
        </div>

        <div v-for="network in overview.networkInterfaces" :key="network.name" class="mini-row network-row">
          <span>{{ network.name }}</span>
          <span>{{ formatSpeed(network.bytesReceivedPerSecond) }}</span>
          <span>{{ formatSpeed(network.bytesSentPerSecond) }}</span>
          <span>{{ formatBytes(network.bytesReceivedPerSecond + network.bytesSentPerSecond) }}</span>
        </div>
      </div>

      <div v-if="overview && hasNetTrend" class="net-trend">
        <div class="net-trend-head">
          <span>{{ t("panel.monitor.networkTrend") }}</span>
          <span class="meta">{{ t("panel.monitor.trend10m") }}</span>
        </div>
        <svg class="net-spark" viewBox="0 0 100 28" preserveAspectRatio="none" aria-hidden="true">
          <polyline class="down" :points="netDownSpark" />
          <polyline class="up" :points="netUpSpark" />
        </svg>
        <div class="net-legend">
          <span><i class="legend-dot down"></i>{{ t("panel.system.download") }}</span>
          <span><i class="legend-dot up"></i>{{ t("panel.system.upload") }}</span>
        </div>
      </div>
    </section>

    <!-- Canlı süreç izleyici (htop benzeri). Fark: ham süreç adı yerine "hangi site / hangi
         bileşen" etiketi gösterilir — "sunucu yavaş" sorusunun asıl cevabı budur. -->
    <section class="panel">
      <div class="section-head">
        <div>
          <h2>{{ t("panel.processes.title") }}</h2>
          <p class="meta">{{ t("panel.processes.subtitle") }}</p>
        </div>
        <div class="row-actions">
          <select v-model="processSort" class="compact-select">
            <option value="cpu">{{ t("panel.processes.sortCpu") }}</option>
            <option value="memory">{{ t("panel.processes.sortMemory") }}</option>
          </select>
          <button class="secondary compact" @click="processPaused = !processPaused">
            {{ processPaused ? t("panel.processes.resume") : t("panel.processes.pause") }}
          </button>
        </div>
      </div>

      <p v-if="processError" class="notice error">{{ processError }}</p>
      <p v-else-if="!snapshot" class="meta">{{ t("panel.common.loading") }}</p>
      <p v-else-if="!snapshot.cpuBaselineReady" class="field-hint">{{ t("panel.processes.baselineHint") }}</p>

      <div v-if="snapshot" class="proc-table">
        <div class="proc-row head">
          <span>{{ t("panel.processes.owner") }}</span>
          <span>{{ t("panel.processes.pid") }}</span>
          <span>{{ t("panel.system.cpu") }}</span>
          <span>{{ t("panel.system.memory") }}</span>
          <span>{{ t("panel.processes.uptime") }}</span>
        </div>
        <!-- Sabit yükseklikli kaydırma alanı: satır sayısı değiştikçe bölümün boyu değişmesin,
             yoksa ALTINDAKİ "kim kullanıyor" bölümü sürekli aşağı yukarı zıplar.
             Fare/klavye tablonun üzerindeyken sıralama DONDURULUR (bkz. orderedProcesses). -->
        <div
          class="proc-scroll"
          @mouseenter="orderFrozen = true"
          @mouseleave="orderFrozen = false"
          @focusin="orderFrozen = true"
          @focusout="orderFrozen = false">
          <TransitionGroup name="proc" tag="div" class="proc-body">
            <div v-for="item in orderedProcesses" :key="item.processId" class="proc-row">
              <div class="proc-owner">
                <strong>{{ item.label || item.name }}</strong>
                <small>{{ t("panel.processes.category." + item.category) }} · {{ item.name }}<template v-if="item.detail"> · {{ item.detail }}</template></small>
              </div>
              <span class="proc-num">{{ item.processId }}</span>
              <span class="proc-num">
                <span :class="['proc-bar', cpuSeverity(item.cpuPercent)]" :style="{ '--fill': Math.min(100, item.cpuPercent) + '%' }"></span>
                {{ item.cpuPercent.toFixed(1) }}%
              </span>
              <span class="proc-num">{{ formatBytes(item.workingSetBytes) }}</span>
              <span class="proc-num">{{ formatUptime(item.uptimeSeconds) }}</span>
            </div>
          </TransitionGroup>
          <p v-if="orderedProcesses.length === 0" class="meta">{{ t("panel.processes.empty") }}</p>
        </div>
        <p class="field-hint">
          {{ t("panel.processes.footer", { shown: orderedProcesses.length, total: snapshot.totalProcessCount }) }}
          <span v-if="orderFrozen"> · {{ t("panel.processes.orderFrozen") }}</span>
        </p>
      </div>
    </section>

    <!-- Süreç listesinin bir adım derini. MySQL/PostgreSQL TEK süreçtir ve tüm siteler onu
         paylaşır; "MariaDB %42" satırı o yükün KİMDEN geldiğini söyleyemez. Cevap sürecin
         içindeki aktif sorgularda: her satır bir siteye bağlanır. -->
    <section class="panel">
      <div class="section-head">
        <div>
          <h2>{{ t("panel.consumers.title") }}</h2>
          <p class="meta">{{ t("panel.consumers.subtitle", { name: consumerSourceLabel }) }}</p>
        </div>
        <label class="stacked-field consumer-source">
          <span class="field-label">{{ t("panel.consumers.source") }}</span>
          <select v-model="consumerSource" class="compact-select">
            <option value="mariadb">MariaDB</option>
            <option value="mysql">MySQL</option>
            <option value="postgresql">PostgreSQL</option>
            <option value="iis">IIS (web)</option>
          </select>
        </label>
      </div>

      <ResourceConsumers :source="consumerSource" />
    </section>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from "vue";
import { useI18n } from "vue-i18n";
import { useSystemStore } from "../stores/system";
import { getProcessSnapshot, type ProcessSnapshot } from "../api/processes";
import ResourceConsumers from "../components/ResourceConsumers.vue";
import type { ConsumerSource } from "../api/consumers";

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

const props = defineProps<{
  overview: SystemOverview | null;
  loading: boolean;
}>();

const { t } = useI18n();

const primaryDisk = computed(() => props.overview?.disks[0] ?? null);

// Plesk-benzeri "derin izleme": sunucu dakikada bir CPU/RAM/ağ örnekler (kalıcı geçmiş halkası); izleme ekranı
// bu geçmişi çekip küçük SVG sparkline'larla trend gösterir → sayfa yenilense de DOLU gelir (~24 saatlik pencere).
const systemStore = useSystemStore();
let historyTimer: number | null = null;

const cpuSeries = computed(() => systemStore.history.map((sample) => sample.cpuPercent));
const memSeries = computed(() => systemStore.history.map((sample) => sample.memoryPercent));
const netDownSeries = computed(() => systemStore.history.map((sample) => sample.networkDownBytesPerSec));
const netUpSeries = computed(() => systemStore.history.map((sample) => sample.networkUpBytesPerSec));

const cpuSpark = computed(() => spark(cpuSeries.value, 100));
const memSpark = computed(() => spark(memSeries.value, 100));
const netScale = computed(() => Math.max(1, ...netDownSeries.value, ...netUpSeries.value));
const netDownSpark = computed(() => spark(netDownSeries.value, netScale.value));
const netUpSpark = computed(() => spark(netUpSeries.value, netScale.value));
const hasNetTrend = computed(() => systemStore.history.length > 1);

// --- "Şu an kim kullanıyor?" kaynak seçimi ---
const consumerSource = ref<ConsumerSource>("mariadb");
const consumerSourceLabel = computed(() => ({
  mariadb: "MariaDB",
  mysql: "MySQL",
  postgresql: "PostgreSQL",
  iis: "IIS"
}[consumerSource.value]));

// --- Canlı süreç izleyici ---
const snapshot = ref<ProcessSnapshot | null>(null);

// Sıralama dondurma: iki saniyede bir gelen veri satırları yeniden sıralarsa, okumaya çalıştığın
// satır elinin altından kaçar. Fare/klavye tablonun üzerindeyken ESKİ SIRA korunur; değerler
// yerinde güncellenmeye devam eder. İmleç ayrılınca sıra tekrar canlıya döner.
const orderFrozen = ref(false);
const frozenOrder = ref<number[]>([]);

const orderedProcesses = computed(() => {
  const rows = snapshot.value?.processes ?? [];
  if (!orderFrozen.value) {
    return rows;
  }

  const byId = new Map(rows.map((row) => [row.processId, row]));
  // Önce donmuş sıradaki (hâlâ var olan) satırlar, sonra bu arada beliren yeni süreçler sona.
  // Yeni süreç araya girip mevcut satırları kaydırmaz.
  const kept = frozenOrder.value.map((pid) => byId.get(pid)).filter((row) => row !== undefined);
  const keptIds = new Set(kept.map((row) => row!.processId));
  return [...kept, ...rows.filter((row) => !keptIds.has(row.processId))] as typeof rows;
});

// Donmadığı sürece güncel sırayı hatırla ki donduğu an referans hazır olsun.
watch(snapshot, (value) => {
  if (!orderFrozen.value && value) {
    frozenOrder.value = value.processes.map((row) => row.processId);
  }
});
const processSort = ref<"cpu" | "memory">("cpu");
const processPaused = ref(false);
const processError = ref("");
let processTimer: number | null = null;
let processInFlight = false;

async function pollProcesses() {
  // Sekme arka plandaysa veya kullanıcı duraklattıysa sunucuyu boşuna yorma.
  // Önceki istek hâlâ sürüyorsa yenisini kuyruğa alma (yavaş sunucuda istek yığılmasın).
  if (processPaused.value || document.hidden || processInFlight) {
    return;
  }

  processInFlight = true;
  try {
    snapshot.value = await getProcessSnapshot(processSort.value);
    processError.value = "";
  } catch {
    processError.value = t("panel.processes.failed");
  } finally {
    processInFlight = false;
  }
}

// Sıralama değişince beklemeden yenile.
watch(processSort, () => void pollProcesses());

onMounted(() => {
  void systemStore.fetchHistory();
  historyTimer = window.setInterval(() => void systemStore.fetchHistory(), 60000);

  void pollProcesses();
  processTimer = window.setInterval(() => void pollProcesses(), 2000);
});

onUnmounted(() => {
  if (historyTimer !== null) {
    window.clearInterval(historyTimer);
    historyTimer = null;
  }

  if (processTimer !== null) {
    window.clearInterval(processTimer);
    processTimer = null;
  }
});

function cpuSeverity(percent: number) {
  if (percent >= 50) return "high";
  return percent >= 20 ? "medium" : "low";
}

function formatUptime(seconds: number) {
  if (seconds <= 0) return "—";
  const days = Math.floor(seconds / 86400);
  const hours = Math.floor((seconds % 86400) / 3600);
  const minutes = Math.floor((seconds % 3600) / 60);
  if (days > 0) return `${days}g ${hours}sa`;
  if (hours > 0) return `${hours}sa ${minutes}dk`;
  return `${minutes}dk`;
}

// Değer dizisini 100×28 viewBox içinde bir polyline "points" dizgesine çevirir (üst=yüksek değer).
function spark(values: number[], maxValue: number): string {
  if (values.length < 2) {
    return "";
  }

  const max = maxValue > 0 ? maxValue : 1;
  const count = values.length;
  return values
    .map((value, index) => {
      const x = (index / (count - 1)) * 100;
      const ratio = Math.max(0, Math.min(1, value / max));
      const y = 27 - ratio * 26;
      return `${x.toFixed(1)},${y.toFixed(1)}`;
    })
    .join(" ");
}

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
.monitor-stack,
.meter-grid,
.monitor-columns,
.info-grid,
.mini-table {
  display: grid;
  gap: 18px;
}

.monitor-columns {
  grid-template-columns: repeat(2, minmax(0, 1fr));
}

.meter-grid {
  grid-template-columns: repeat(3, minmax(0, 1fr));
}

.section-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
}

.section-head h2 {
  margin: 0;
}

.meter-card,
.info-card,
.mini-row {
  border: 1px solid var(--panel-border);
  background: var(--surface-2);
}

.meter-card,
.info-card {
  padding: 18px;
  border-radius: var(--radius-lg);
}

.meter-card {
  display: grid;
  place-items: center;
}

.meter-spark {
  width: 100%;
  height: 30px;
  margin-top: 12px;
}

.meter-spark polyline {
  fill: none;
  stroke: var(--accent-1);
  stroke-width: 1.6;
  vector-effect: non-scaling-stroke;
  stroke-linejoin: round;
  stroke-linecap: round;
}

.meter-spark.mem polyline {
  stroke: #6fd2ff;
}

.net-trend {
  margin-top: 16px;
  display: grid;
  gap: 8px;
}

.net-trend-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.net-spark {
  width: 100%;
  height: 60px;
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-sm);
  background: var(--surface-2);
}

.net-spark polyline {
  fill: none;
  stroke-width: 1.6;
  vector-effect: non-scaling-stroke;
  stroke-linejoin: round;
  stroke-linecap: round;
}

.net-spark polyline.down {
  stroke: var(--accent-1);
}

.net-spark polyline.up {
  stroke: var(--warn-1);
}

.net-legend {
  display: flex;
  gap: 18px;
  color: var(--text-muted);
  font-size: 0.8rem;
}

.net-legend .legend-dot {
  display: inline-block;
  width: 10px;
  height: 10px;
  border-radius: 50%;
  margin-right: 6px;
  vertical-align: middle;
}

.legend-dot.down {
  background: var(--accent-1);
}

.legend-dot.up {
  background: var(--warn-1);
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

.meter-core span {
  color: var(--text-muted);
  font-size: 0.8rem;
  letter-spacing: 0.08em;
  text-transform: uppercase;
}

.meter-core small {
  max-width: 86%;
  color: var(--text-soft);
  font-size: 0.72rem;
  font-weight: 650;
  line-height: 1.2;
}

.meter-core strong {
  font-size: clamp(1.4rem, 2.8vw, 2rem);
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
  grid-template-columns: 1.2fr 0.9fr 0.9fr 0.9fr;
}

.table-head {
  font-weight: 700;
  background: var(--surface-2);
}

@media (max-width: 1180px) {
  .monitor-columns,
  .meter-grid {
    grid-template-columns: 1fr;
  }
}

@media (max-width: 820px) {
  .section-head {
    flex-direction: column;
    align-items: flex-start;
  }

  /*
    Disk/ağ satırları tek sütuna inmiyor: başlık satırı da tek sütuna inince değerlerden
    kopuyor ve "hangi sayı neydi" okunamıyordu. Sütunlar korunup yazı/boşluk daraltılıyor.
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

  /* "Toplam" sütunu gizlenir: indir+yükle toplamıdır, iki sütunu da ekranda tutmak
     türetilebilir bir sayıyı göstermekten önemli. */
  .network-row {
    grid-template-columns: minmax(0, 1.1fr) 0.9fr 0.9fr;
  }

  .network-row > :nth-child(4) {
    display: none;
  }
}

/* Ölçer halkaları: alt alta üç 220px halka yerine yan yana üç küçük halka →
   CPU/RAM/disk tek bakışta okunur (olay akışının "kaynak tüketimini oku" adımı). */
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

  /* Aynı bilgi hemen aşağıdaki Diskler tablosunda var. */
  .meter-core small {
    display: none;
  }

  .meter-spark {
    height: 22px;
    margin-top: 8px;
  }

  .info-card {
    padding: 12px;
  }

  .net-legend {
    gap: 12px;
    font-size: 0.74rem;
  }
}

.proc-table {
  display: grid;
  gap: 4px;
  margin-top: 10px;
}

/* Sabit görüntü alanı: satır sayısı 8'e de düşse 30'a da çıksa bölümün yüksekliği DEĞİŞMEZ,
   dolayısıyla altındaki "kim kullanıyor" bölümü yerinde kalır. Liste uzunsa içeride kaydırılır. */
.proc-scroll {
  height: 420px;
  overflow-y: auto;
  overscroll-behavior: contain;
  padding-right: 4px;
}

.proc-body {
  display: grid;
  gap: 4px;
}

/* Satırlar ışınlanmak yerine kayarak yer değiştirsin; göz takip edebilsin. */
.proc-move {
  transition: transform 320ms ease;
}

.proc-enter-active,
.proc-leave-active {
  transition: opacity 200ms ease;
}

.proc-enter-from,
.proc-leave-to {
  opacity: 0;
}

/* Ayrılan satır akıştan çıkarılır, yoksa kalanları aşağı iterek yeni bir zıplama yaratır. */
.proc-leave-active {
  position: absolute;
  width: 100%;
}

@media (prefers-reduced-motion: reduce) {
  .proc-move,
  .proc-enter-active,
  .proc-leave-active {
    transition: none;
  }
}

.proc-row {
  display: grid;
  grid-template-columns: minmax(0, 2.4fr) 0.6fr 1fr 0.8fr 0.8fr;
  gap: 10px;
  align-items: center;
  padding: 8px 10px;
  border: 1px solid var(--panel-border);
  border-radius: 9px;
  background: var(--surface-1);
}

.proc-row.head {
  border: 0;
  background: transparent;
  padding-bottom: 0;
  font-size: 0.74rem;
  color: var(--text-muted);
}

.proc-owner {
  display: grid;
  gap: 1px;
  min-width: 0;
}

.proc-owner strong {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.proc-owner small {
  color: var(--text-muted);
  font-size: 0.72rem;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.proc-num {
  position: relative;
  font-variant-numeric: tabular-nums;
  font-size: 0.86rem;
}

/* CPU çubuğu satırın arkasında ince bir dolgu: sıralama değişse de göz oranı yakalar. */
.proc-bar {
  position: absolute;
  left: 0;
  right: 0;
  bottom: -4px;
  height: 3px;
  border-radius: 999px;
  background: color-mix(in srgb, var(--text-muted) 20%, transparent);
}

.proc-bar::after {
  content: "";
  position: absolute;
  left: 0;
  top: 0;
  bottom: 0;
  width: var(--fill, 0%);
  border-radius: 999px;
  background: var(--ok-1);
}

.proc-bar.medium::after {
  background: #e0a500;
}

.proc-bar.high::after {
  background: var(--danger-1);
}

.compact-select {
  width: auto;
}

/*
  Canlı süreç tablosu telefonda: PID ve çalışma süresi gizlenir (PID telefondan zaten
  kullanılamaz, süre teşhis için ikincil), SAHİP + CPU + BELLEK kalır — "sunucuyu ne yiyor"
  sorusunun cevabı bu üçü. CPU çubuğu da korunur.
*/
@media (max-width: 720px) {
  .proc-row {
    grid-template-columns: minmax(0, 1.7fr) 0.8fr 0.9fr;
    gap: 8px;
    padding: 8px 10px 10px;
  }

  .proc-row > :nth-child(2),
  .proc-row > :nth-child(5) {
    display: none;
  }

  .proc-num {
    font-size: 0.8rem;
  }

  .proc-owner strong {
    font-size: 0.86rem;
  }

  .proc-owner small {
    font-size: 0.68rem;
  }

  .compact-select {
    min-width: 140px;
  }

  .row-actions {
    width: 100%;
  }
}
</style>
