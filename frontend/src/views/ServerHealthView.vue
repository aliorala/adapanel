<template>
  <section class="panel module-shell server-health">
    <div class="module-head">
      <div>
        <h3>{{ t("panel.health.title") }}</h3>
        <p class="meta">{{ t("panel.health.subtitle") }}</p>
      </div>
      <button class="secondary compact" :disabled="loading" @click="load">{{ t("panel.health.refresh") }}</button>
    </div>

    <p v-if="notice" :class="['notice', { error: noticeError }]">{{ notice }}</p>

    <!-- DNS erişilebilirliği: servisin "Running" olması YETMEZ, 53 portunun genel IP'de
         dinlemesi gerekir. Bu kart o farkı gösterir ve tek tıkla onarır. -->
    <article v-if="dns && dns.installed" :class="['dns-card', dns.needsRepair ? 'bad' : 'good']">
      <div class="dns-head">
        <span :class="['state-dot', dns.needsRepair ? 'dot-danger' : 'dot-ok']"></span>
        <div class="health-name">
          <strong>{{ t("panel.health.dnsTitle") }}</strong>
          <span class="svc-id">{{ dns.needsRepair ? t("panel.health.dnsBad") : t("panel.health.dnsGood") }}</span>
        </div>
        <span :class="['status-pill', dns.needsRepair ? 'danger' : 'ok']">
          {{ dns.publiclyBound ? t("panel.health.dnsBound") : t("panel.health.dnsNotBound") }}
        </span>
        <button class="secondary compact" :disabled="dnsBusy" @click="repairDns">
          {{ dnsBusy ? t("panel.health.dnsRepairing") : t("panel.health.dnsRepair") }}
        </button>
      </div>
      <dl class="dns-facts">
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
          <dd class="warn">{{ dns.missingAddresses.join(", ") }}</dd>
        </div>
        <div>
          <dt>{{ t("panel.health.dnsZones") }}</dt>
          <dd>{{ dns.zoneCount }}</dd>
        </div>
        <div>
          <dt>{{ t("panel.health.dnsStartMode") }}</dt>
          <dd>
            {{ dns.startMode }}
            <span v-if="dns.delayedAutoStart">({{ t("panel.health.dnsDelayedOn") }})</span>
            <span v-else class="warn">({{ t("panel.health.dnsDelayedOff") }})</span>
          </dd>
        </div>
        <div v-if="dns.lastAutoRepairUtc">
          <dt>{{ t("panel.health.dnsLastAutoRepair") }}</dt>
          <dd>{{ formatDate(dns.lastAutoRepairUtc) }} — {{ dns.lastAutoRepairStatus }}</dd>
        </div>
      </dl>
      <p class="dns-details">{{ dns.details }}</p>
    </article>

    <p v-if="loading && !services.length" class="meta">{{ t("panel.common.loading") }}</p>

    <div v-else-if="services.length" class="health-list">
      <article v-for="svc in services" :key="svc.name" class="health-row">
        <div class="health-id">
          <span :class="['state-dot', dotClass(svc.status)]"></span>
          <div class="health-name">
            <strong>{{ svc.label }}</strong>
            <span class="svc-id">{{ svc.name }}</span>
          </div>
        </div>
        <span :class="['status-pill', pillClass(svc.status)]">{{ statusText(svc.status) }}</span>
        <button
          v-if="svc.status !== 'NotInstalled'"
          class="secondary compact"
          :disabled="busyName === svc.name"
          @click="restart(svc.name)">
          {{ busyName === svc.name ? t("panel.health.restarting") : t("panel.health.restart") }}
        </button>
        <span v-else class="na">—</span>
      </article>
    </div>
    <p v-else-if="loaded" class="meta">{{ t("panel.health.empty") }}</p>
  </section>
</template>

<script setup lang="ts">
import { onMounted, ref } from "vue";
import { useI18n } from "vue-i18n";
import { getServerHealth, restartService, type ServiceHealth } from "../api/health";
import { getDnsServerHealth, repairDnsServer, type DnsServerHealth } from "../api/dns";
import { extractApiMessage } from "../stores/auth";

const { t } = useI18n();
const services = ref<ServiceHealth[]>([]);
const dns = ref<DnsServerHealth | null>(null);
const dnsBusy = ref(false);
const loading = ref(false);
const loaded = ref(false);
const busyName = ref("");
const notice = ref("");
const noticeError = ref(false);

onMounted(load);

async function load() {
  loading.value = true;
  notice.value = "";
  try {
    services.value = await getServerHealth();
    loaded.value = true;
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    loading.value = false;
  }

  // DNS kartı bağımsız yüklenir: ölçüm başarısız olsa bile servis listesi görünmeye devam eder.
  try {
    dns.value = await getDnsServerHealth();
  } catch {
    dns.value = null;
  }
}

async function repairDns() {
  dnsBusy.value = true;
  notice.value = "";
  try {
    dns.value = await repairDnsServer(true);
    showNotice(
      dns.value.publiclyBound ? t("panel.health.dnsRepaired") : t("panel.health.dnsStillBad"),
      !dns.value.publiclyBound
    );
    services.value = await getServerHealth();
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    dnsBusy.value = false;
  }
}

function formatDate(value: string) {
  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime()) ? value : parsed.toLocaleString();
}

async function restart(name: string) {
  busyName.value = name;
  notice.value = "";
  try {
    const updated = await restartService(name);
    const index = services.value.findIndex((svc) => svc.name === updated.name);
    if (index >= 0) {
      services.value[index] = updated;
    }
    showNotice(t("panel.health.restartedNotice", { name: updated.label }), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    busyName.value = "";
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

function resolveApiMessage(error: unknown) {
  const key = extractApiMessage(error);
  const translated = t(key);
  return translated === key ? key : translated;
}

function showNotice(message: string, isError: boolean) {
  notice.value = message;
  noticeError.value = isError;
}
</script>

<style scoped>
.server-health {
  display: grid;
  gap: 12px;
}

.health-list {
  display: grid;
  gap: 8px;
}

.health-row {
  display: flex;
  align-items: center;
  gap: 14px;
  padding: 11px 14px;
  border: 1px solid var(--panel-border);
  border-radius: 11px;
  background: var(--surface-1);
}

.health-id {
  display: flex;
  align-items: center;
  gap: 12px;
  flex: 1 1 auto;
  min-width: 0;
}

.health-name {
  display: grid;
  gap: 2px;
  min-width: 0;
}

.svc-id {
  color: var(--text-muted);
  font-size: 0.74rem;
  font-family: var(--font-mono, ui-monospace, monospace);
}

.health-row .status-pill {
  flex-shrink: 0;
}

.health-row button {
  width: auto;
  flex-shrink: 0;
}

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

.state-dot.dot-danger {
  background: var(--danger-1);
  box-shadow: 0 0 0 3px color-mix(in srgb, var(--danger-1) 22%, transparent);
}

.state-dot.dot-muted {
  background: var(--text-muted);
}

.dns-card {
  display: grid;
  gap: 10px;
  padding: 13px 14px;
  border: 1px solid var(--panel-border);
  border-radius: 11px;
  background: var(--surface-1);
}

.dns-card.bad {
  border-color: color-mix(in srgb, var(--danger-1) 55%, var(--panel-border));
  background: color-mix(in srgb, var(--danger-1) 7%, var(--surface-1));
}

.dns-head {
  display: flex;
  align-items: center;
  gap: 12px;
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
  gap: 8px 16px;
  margin: 0;
}

.dns-facts dt {
  color: var(--text-muted);
  font-size: 0.74rem;
}

.dns-facts dd {
  margin: 0;
  font-size: 0.86rem;
  word-break: break-word;
}

.dns-facts .warn {
  color: var(--danger-1);
}

.dns-details {
  margin: 0;
  color: var(--text-muted);
  font-size: 0.72rem;
  font-family: var(--font-mono, ui-monospace, monospace);
  word-break: break-word;
}
</style>
