<template>
  <section class="panel module-shell">
    <div class="module-head">
      <div>
        <h3>{{ t("panel.webdb.title") }}</h3>
        <p>{{ t("panel.webdb.subtitle") }}</p>
      </div>
      <button class="secondary compact" :disabled="loading" @click="refreshWorkspace">
        {{ t("panel.webdb.refresh") }}
      </button>
    </div>

    <p v-if="notice" :class="['notice', 'page-notice', { error: noticeError }]" :role="noticeError ? 'alert' : 'status'">{{ notice }}</p>

    <div class="summary-strip">
      <div class="summary-item">
        <span>{{ t("panel.webdb.providers") }}</span>
        <strong>{{ deployments.length }}</strong>
      </div>
      <div class="summary-item">
        <span>{{ t("panel.webdb.readyCount") }}</span>
        <strong>{{ deployedCount }}</strong>
      </div>
      <div class="summary-item">
        <span>{{ t("panel.webdb.installedCount") }}</span>
        <strong>{{ installedCount }}</strong>
      </div>
      <div class="summary-item">
        <span>{{ t("panel.webdb.access") }}</span>
        <strong>{{ t("panel.webdb.accessSummary") }}</strong>
      </div>
    </div>

    <div class="webdb-guidance">
      <strong>{{ t("panel.webdb.postgresTitle") }}</strong>
      <span>{{ t("panel.webdb.postgresHint") }}</span>
    </div>

    <div class="data-table">
      <div class="data-row table-head webdb-row">
        <span>{{ t("panel.webdb.provider") }}</span>
        <span>{{ t("panel.sites.state") }}</span>
        <span>{{ t("panel.webdb.engines") }}</span>
        <span>{{ t("panel.webdb.accessType") }}</span>
        <span>{{ t("panel.webdb.url") }}</span>
        <span>{{ t("panel.files.actions") }}</span>
      </div>
      <div v-for="item in deployments" :key="item.provider" class="data-row webdb-row">
        <span class="cell-main">
          <strong>{{ item.displayName }}</strong>
          <small>{{ item.componentId }}</small>
          <small>{{ item.entryFilePath || item.installDir || "-" }}</small>
        </span>
        <span class="health-cell">
          <span :class="['status-pill', item.isHealthy ? 'ok' : item.isInstalled ? 'warn' : '']">
            {{ item.isHealthy ? t("panel.webdb.deployed") : item.isInstalled ? t("panel.webdb.needsRepair") : t("panel.database.notInstalled") }}
          </span>
          <small v-if="item.healthMessage">{{ translateHealth(item.healthMessage) }}</small>
          <details v-if="item.healthDetails" class="health-details">
            <summary>{{ t("panel.webdb.diagnostics") }}</summary>
            <pre>{{ item.healthDetails }}</pre>
          </details>
        </span>
        <span>{{ item.supportedEngines || "-" }}</span>
        <span>{{ accessModeLabel(item.accessMode) }}</span>
        <a v-if="item.url && item.isDeployed" :href="accessUrl(item)" target="_blank" rel="noreferrer">{{ accessUrl(item) }}</a>
        <span v-else class="muted-cell">{{ item.provider === "pgadmin" ? t("panel.webdb.desktopLaunchHint") : "-" }}</span>
        <span class="row-actions">
          <button
            class="compact"
            v-bind="writeGate(busyProvider === item.provider || (item.provider === 'pgadmin' && item.isInstalled))"
            @click="runAction(item.provider, item.componentId)">
            {{ actionLabel(item) }}
          </button>
          <button
            v-if="item.provider === 'phpmyadmin' && item.isDeployed"
            class="secondary compact"
            :disabled="openingPma"
            @click="openPhpMyAdminRoot">
            {{ openingPma ? t("panel.database.opening") : t("panel.webdb.open") }}
          </button>
          <a v-else-if="item.url && item.isDeployed" class="secondary-link" :href="accessUrl(item)" target="_blank" rel="noreferrer">
            {{ t("panel.webdb.open") }}
          </a>
        </span>
      </div>
      <!-- `deployments` çalışma alanından TÜRETİLİR; alan gelmeden de okunmadan da boş görünür.
           `loading` hata durumunda false olduğu için eski koşul "dağıtım yok" diyordu. Çalışma
           alanının KENDİSİ geldi mi, ölçüt bu. -->
      <p v-if="!workspace" class="empty-state" role="status" aria-live="polite">{{ t("panel.common.loading") }}</p>
      <p v-else-if="deployments.length === 0" class="empty-state">{{ t("panel.webdb.empty") }}</p>
    </div>
  </section>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from "vue";
import { useI18n } from "vue-i18n";
import {
  deployAdminer,
  deployPhpMyAdmin,
  getWebDbWorkspace,
  type WebDbDeployment,
  type WebDbWorkspace
} from "../api/webdb";
import { openPhpMyAdmin } from "../api/databases";
import { openReachable, toReachableUrl } from "../utils/url";
import { extractApiDetails, extractApiMessage } from "../stores/auth";
import { useSoftwareStore } from "../stores/software";
import { useSubscriptionGate } from "../composables/useSubscriptionGate";

const { t } = useI18n();
// Askidaki abonelikte YAZMA dugmeleri bastan devre disi + sebebi title/aria ile soylenir
// (bkz. composables/useSubscriptionGate.ts). Okuma dugmeleri kapiya girmez.
const { writeGate } = useSubscriptionGate();
const softwareStore = useSoftwareStore();
const workspace = ref<WebDbWorkspace | null>(null);
const busyProvider = ref("");
const openingPma = ref(false);

async function openPhpMyAdminRoot() {
  openingPma.value = true;
  try {
    // Genel açılış: root (tüm veritabanları). Tek DB için Veritabanları ekranındaki düğme kullanılır.
    const result = await openPhpMyAdmin("mariadb");
    openReachable(result.url);
  } catch (error) {
    notice.value = resolveApiMessage(error);
    noticeError.value = true;
  } finally {
    openingPma.value = false;
  }
}
const loading = ref(false);
const notice = ref("");
const noticeError = ref(false);
const deployments = computed(() => workspace.value?.deployments ?? []);
const deployedCount = computed(() => deployments.value.filter((item) => item.isHealthy).length);
const installedCount = computed(() => deployments.value.filter((item) => item.isInstalled).length);

onMounted(refreshWorkspace);

async function refreshWorkspace() {
  loading.value = true;
  try {
    workspace.value = await getWebDbWorkspace();
    notice.value = "";
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    loading.value = false;
  }
}

async function runAction(provider: string, componentId: string) {
  busyProvider.value = provider;
  try {
    if (provider === "pgadmin") {
      await softwareStore.installComponent(componentId);
    } else if (provider === "adminer") {
      await deployAdminer();
    } else {
      await deployPhpMyAdmin();
    }

    await refreshWorkspace();
    showNotice(
      provider === "pgadmin" ? t("panel.webdb.installedMessage") : t("panel.webdb.deployedMessage"),
      false
    );
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    busyProvider.value = "";
  }
}

function actionLabel(item: WebDbDeployment) {
  if (busyProvider.value === item.provider) {
    return item.provider === "pgadmin" ? t("panel.webdb.installing") : t("panel.webdb.deploying");
  }

  if (item.provider === "pgadmin") {
    return item.isInstalled ? t("panel.php.installedState") : t("panel.webdb.install");
  }

  return item.isHealthy
    ? t("panel.webdb.redeploy")
    : item.isDeployed
      ? t("panel.webdb.repair")
      : t("panel.webdb.deploy");
}

/**
 * Aracin PANEL UZERINDEN erisim adresi.
 *
 * Ham IIS portuna (http://sunucu:8897/) GIDILMEZ. Iki sebep:
 *
 * 1) O siteler yalnizca loopback'e baglidir; disaridan gelen bir istegin Host basligi bagla
 *    uymadigi icin IIS "400 - Invalid Hostname" dondurur. Kullanicinin gordugu hata buydu.
 * 2) Panelin kendi tasarimi (B3) zaten bunu yasakliyor: tarayici dis duz-HTTP portuna degil,
 *    panelin kimlik-dogrulamali HTTPS ters vekiline gitmeli. Ham porta yonlendirmek, araci
 *    kimlik dogrulamasiz bir yuzeye tasimak olurdu.
 */
function accessUrl(item: WebDbDeployment) {
  if (!item.url || !item.isDeployed) {
    return "";
  }

  try {
    return `/api/v1/dbtools/${encodeURIComponent(item.provider)}/`;
  } catch {
    return item.url;
  }
}

function translateHealth(messageKey: string) {
  const translated = t(messageKey);
  return translated === messageKey ? messageKey : translated;
}

function accessModeLabel(accessMode: string) {
  return accessMode === "desktop"
    ? t("panel.webdb.accessDesktop")
    : t("panel.webdb.accessIis");
}

function showNotice(message: string, isError: boolean) {
  notice.value = message;
  noticeError.value = isError;
}

function resolveApiMessage(error: unknown) {
  const key = extractApiMessage(error);
  const translated = t(key);
  const message = translated === key ? key : translated;
  const details = extractApiDetails(error);
  if (!details) {
    return message;
  }

  const translatedDetails = t(details);
  return `${message} ${translatedDetails === details ? details : translatedDetails}`;
}
</script>

<style scoped>
.webdb-row {
  grid-template-columns: 1.35fr 0.7fr 1.1fr 0.8fr 1.25fr 0.9fr;
}

.webdb-guidance {
  display: flex;
  gap: 10px;
  align-items: flex-start;
  padding: 12px 14px;
  border: 1px solid color-mix(in srgb, var(--accent-1) 22%, transparent);
  border-radius: var(--radius-sm);
  background: color-mix(in srgb, var(--accent-1) 7%, transparent);
}

.webdb-guidance span,
.muted-cell {
  color: var(--text-muted);
}

.secondary-link {
  padding: 7px 9px;
  border-radius: var(--radius-sm);
  background: var(--surface-2);
  color: var(--text-strong);
  font-weight: 700;
  text-decoration: none;
}

.health-cell {
  display: grid;
  gap: 5px;
  justify-items: start;
}

.health-cell small {
  color: var(--text-muted);
  line-height: 1.35;
}

.health-details {
  max-width: 100%;
  color: var(--text-muted);
  font-size: 0.76rem;
}

.health-details summary {
  color: var(--accent-text);
  cursor: pointer;
}

.health-details pre {
  max-width: 420px;
  max-height: 180px;
  margin: 7px 0 0;
  padding: 8px;
  overflow: auto;
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-sm);
  background: var(--surface-1);
  color: var(--text-soft);
  font: 0.75rem/1.45 "Cascadia Code", "Consolas", monospace;
  white-space: pre-wrap;
  word-break: break-word;
}

@media (max-width: 1080px) {
  .webdb-row {
    grid-template-columns: 1fr;
  }

  .webdb-row.table-head {
    display: none;
  }

  .webdb-guidance {
    flex-direction: column;
  }
}
</style>
