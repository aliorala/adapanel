<template>
  <section class="panel wide wp-view">
    <div class="wp-header">
      <div>
        <p class="section-kicker">WordPress Toolkit</p>
        <h2>WordPress</h2>
        <p class="meta">{{ siteName || t("panel.php.selectSite") }}</p>
      </div>
      <button class="secondary compact" :disabled="loading || !siteName" @click="refresh">{{ t("panel.backup.refresh") }}</button>
    </div>

    <p v-if="notice" :class="['notice', 'page-notice', { error: noticeError }]" :role="noticeError ? 'alert' : 'status'">{{ notice }}</p>
    <p v-if="!siteName" class="empty-state">{{ t("panel.php.selectSite") }}</p>
    <p v-else-if="loading && !wp" class="empty-state">{{ t("panel.common.loading") }}</p>

    <template v-else-if="wp && wp.isWordPress">
      <div class="wp-summary">
        <div class="wp-tile">
          <span class="tile-label">{{ t("panel.wp.version") }}</span>
          <strong>{{ wp.version || "?" }}</strong>
          <span v-if="wp.updateAvailable" class="status-pill warn wp-update-pill">
            {{ t("panel.wp.updateAvailable", { version: wp.latestVersion }) }}
          </span>
          <span v-else-if="wp.latestVersion" class="status-pill ok wp-update-pill">{{ t("panel.wp.upToDate") }}</span>
        </div>
        <div class="wp-tile">
          <span class="tile-label">{{ t("panel.wp.maintenance") }}</span>
          <strong :class="wp.maintenanceMode ? 'on' : ''">{{ wp.maintenanceMode ? t("panel.status.enabled") : t("panel.status.disabled") }}</strong>
        </div>
        <div class="wp-tile path">
          <span class="tile-label">{{ t("panel.sites.physicalPath") }}</span>
          <strong>{{ wp.path }}</strong>
        </div>
      </div>

      <div class="wp-actions">
        <a v-if="wp.adminUrl" class="secondary compact link-btn" :href="wp.adminUrl" target="_blank" rel="noopener">{{ t("panel.wp.openAdmin") }}</a>
        <a v-if="wp.updateAvailable && wp.adminUrl" class="compact link-btn" :href="`${wp.adminUrl}update-core.php`" target="_blank" rel="noopener">{{ t("panel.wp.runUpdate") }}</a>
        <button class="secondary compact" v-bind="writeGate(busy)" @click="toggleMaintenance">
          {{ wp.maintenanceMode ? t("panel.wp.maintenanceOff") : t("panel.wp.maintenanceOn") }}
        </button>
      </div>
      <p class="meta">{{ t("panel.wp.maintenanceHint") }}</p>

      <!-- GÜNCELLEMELER en üstte: eklenti listesini okumadan önce görülmesi gereken şey,
           hangilerinin eski kaldığıdır. Panel bugüne kadar WordPress'i yalnız OKUYORDU. -->
      <div class="wp-updates-block">
        <WordPressUpdates :site-name="wp.siteName" />
      </div>

      <div class="wp-section-head">
        <h3>{{ t("panel.wp.plugins") }}</h3>
        <span class="status-pill">{{ wp.plugins.length }}</span>
      </div>
      <div class="data-table">
        <div class="data-row table-head wp-comp-row">
          <span>{{ t("panel.wp.componentName") }}</span>
          <span>{{ t("panel.wp.componentVersion") }}</span>
        </div>
        <div v-for="plugin in wp.plugins" :key="plugin.slug" class="data-row wp-comp-row">
          <strong>{{ plugin.name }}</strong>
          <span>{{ plugin.version || "—" }}</span>
        </div>
        <p v-if="wp.plugins.length === 0" class="empty-state">{{ t("panel.wp.noPlugins") }}</p>
      </div>

      <div class="wp-section-head">
        <h3>{{ t("panel.wp.themes") }}</h3>
        <span class="status-pill">{{ wp.themes.length }}</span>
      </div>
      <div class="data-table">
        <div class="data-row table-head wp-comp-row">
          <span>{{ t("panel.wp.componentName") }}</span>
          <span>{{ t("panel.wp.componentVersion") }}</span>
        </div>
        <div v-for="theme in wp.themes" :key="theme.slug" class="data-row wp-comp-row">
          <strong>{{ theme.name }}</strong>
          <span>{{ theme.version || "—" }}</span>
        </div>
        <p v-if="wp.themes.length === 0" class="empty-state">{{ t("panel.wp.noThemes") }}</p>
      </div>
    </template>

    <div v-else-if="wp" class="empty-state wp-empty">
      <p>{{ t("panel.wp.notDetected") }}</p>
      <p class="meta">{{ t("panel.wp.installHint") }}</p>
    </div>
  </section>
</template>

<script setup lang="ts">
import { computed, onMounted, ref, watch } from "vue";
import { useI18n } from "vue-i18n";
import WordPressUpdates from "../components/WordPressUpdates.vue";
import { useSitesStore } from "../stores/sites";
import { getWordPress, setWordPressMaintenance, type WordPressSite } from "../api/wordpress";
import { useNotice } from "../composables/useNotice";
import { useSubscriptionGate } from "../composables/useSubscriptionGate";

const { t } = useI18n();
// Askidaki abonelikte YAZMA dugmeleri bastan devre disi + sebebi title/aria ile soylenir
// (bkz. composables/useSubscriptionGate.ts). Okuma dugmeleri kapiya girmez.
const { writeGate } = useSubscriptionGate();
const sitesStore = useSitesStore();
const siteName = computed(() => sitesStore.selectedSiteName);

const wp = ref<WordPressSite | null>(null);
const loading = ref(false);
const busy = ref(false);
// Bildirim + hata ÇEVİRİSİ tek kaynaktan (composables/useNotice.ts). Buradaki elle yazılmış
// kopya `t()` çağrısını atlıyordu: kullanıcı kırmızı şeritte `wpcli.not_wordpress` gibi HAM
// ANAHTAR görüyordu. Kopya kaldıkça bu hata sınıfı geri gelir.
const { notice, noticeError, showNotice, showError, clearNotice } = useNotice();

onMounted(refresh);
watch(siteName, refresh);

async function refresh() {
  if (!siteName.value) {
    wp.value = null;
    return;
  }
  loading.value = true;
  clearNotice();
  try {
    wp.value = await getWordPress(siteName.value);
  } catch (error) {
    showError(error);
  } finally {
    loading.value = false;
  }
}

async function toggleMaintenance() {
  if (!wp.value) {
    return;
  }
  busy.value = true;
  try {
    wp.value = await setWordPressMaintenance(siteName.value, !wp.value.maintenanceMode);
    showNotice(t("panel.wp.maintenanceUpdated"));
  } catch (error) {
    showError(error);
  } finally {
    busy.value = false;
  }
}
</script>

<style scoped>
.wp-updates-block {
  margin: 14px 0;
  padding: 12px;
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-md);
  background: var(--surface-0);
}

.wp-view {
  display: grid;
  gap: 16px;
}

.wp-header {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
}

.wp-summary {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(160px, 1fr));
  gap: 10px;
}

.wp-tile {
  display: grid;
  gap: 4px;
  padding: 12px 14px;
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-lg);
  background: var(--surface-1);
}

.wp-tile.path strong {
  font-size: 0.78rem;
  word-break: break-all;
}

.tile-label {
  color: var(--text-muted);
  font-size: 0.72rem;
}

.wp-tile strong.on {
  color: var(--warn-text);
}

.wp-actions {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
}

.link-btn {
  display: inline-flex;
  align-items: center;
  text-decoration: none;
}

.wp-section-head {
  display: flex;
  align-items: center;
  gap: 10px;
  margin-top: 6px;
}

.wp-section-head h3 {
  margin: 0;
}

.wp-comp-row {
  grid-template-columns: minmax(200px, 2fr) minmax(80px, 0.6fr);
  align-items: center;
}

.wp-update-pill {
  margin-top: 6px;
  font-size: 0.7rem;
}

.wp-empty {
  display: grid;
  gap: 4px;
}

.meta {
  color: var(--text-muted);
}
</style>
