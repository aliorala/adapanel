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
        </article>

        <article class="meter-card">
          <div class="meter-ring" :style="ringStyle(overview.memoryUsagePercent, '#6fd2ff')">
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
    </section>
  </div>
</template>

<script setup lang="ts">
import { computed } from "vue";
import { useI18n } from "vue-i18n";

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
  .disk-row,
  .network-row {
    grid-template-columns: 1fr;
  }

  .section-head {
    flex-direction: column;
    align-items: flex-start;
  }
}
</style>
