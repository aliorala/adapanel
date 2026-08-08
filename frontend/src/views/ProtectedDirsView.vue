<template>
  <section class="panel wide protected-view">
    <div class="pd-header">
      <div>
        <p class="section-kicker">{{ t("panel.sites.tools.protected") }}</p>
        <h2>{{ t("panel.protected.title") }}</h2>
        <p class="meta">{{ siteName || t("panel.php.selectSite") }}</p>
      </div>
      <button class="secondary compact" :disabled="loading" @click="refresh">{{ t("panel.backup.refresh") }}</button>
    </div>

    <p v-if="notice" :class="['notice', 'page-notice', { error: noticeError }]" :role="noticeError ? 'alert' : 'status'">{{ notice }}</p>

    <div v-if="siteName" class="form-surface pd-create">
      <h3>{{ t("panel.protected.createTitle") }}</h3>
      <p class="meta">{{ t("panel.protected.hint") }}</p>
      <div class="pd-grid">
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.protected.path") }}</span>
          <input v-model.trim="form.relativePath" :placeholder="t('panel.protected.pathPlaceholder')" />
        </label>
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.protected.user") }}</span>
          <input v-model.trim="form.userName" autocomplete="off" />
        </label>
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.protected.password") }}</span>
          <input v-model="form.password" type="password" autocomplete="new-password" />
        </label>
      </div>
      <button class="compact" :disabled="busy || !form.relativePath || !form.userName || !form.password" @click="create">
        {{ busy ? t("panel.protected.protecting") : t("panel.protected.protect") }}
      </button>
    </div>

    <div class="pd-list">
      <div class="section-head">
        <h3>{{ t("panel.protected.listTitle") }}</h3>
        <span class="meta">{{ listLoaded ? items.length : "…" }}</span>
      </div>
      <div v-if="items.length > 0" class="pd-rows">
        <article v-for="item in items" :key="item.relativePath" class="pd-row">
          <div class="pd-id">
            <strong>/{{ item.relativePath }}</strong>
            <span class="meta">{{ t("panel.protected.user") }}: {{ item.userName }}</span>
          </div>
          <button class="danger compact" :disabled="removing === item.relativePath" @click="remove(item)">
            {{ t("panel.protected.remove") }}
          </button>
        </article>
      </div>
      <!-- Liste gelmeden "koruma yok" yazıyordu; korumalı dizinler korumasız sanılabiliyordu. -->
      <p v-else-if="siteName && !listLoaded" class="empty-state" role="status" aria-live="polite">
        {{ t("panel.common.loading") }}
      </p>
      <p v-else class="empty-state">{{ t("panel.protected.empty") }}</p>
    </div>
  </section>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from "vue";
import { useI18n } from "vue-i18n";
import { useSitesStore } from "../stores/sites";
import { getProtectedDirs, protectDir, unprotectDir, type ProtectedDirectory } from "../api/sites";
import { useConfirm } from "../components/ConfirmDialog.vue";
import { useNotice } from "../composables/useNotice";

const { t } = useI18n();
const { confirm } = useConfirm();
// resolveApiMessage TEK KAYNAKTAN: bu ekran ham `extractApiMessage` basıyordu, yani
// kullanıcı `sites.path_outside_root` gibi ÇEVRİLMEMİŞ anahtar görüyordu.
const { notice, noticeError, showNotice, resolveApiMessage } = useNotice();
const sitesStore = useSitesStore();
const siteName = computed(() => sitesStore.selectedSiteName);

const items = ref<ProtectedDirectory[]>([]);
// Yüklenmemiş liste "kayıt yok" olarak görünüyordu.
const listLoaded = ref(false);
const loading = ref(false);
const busy = ref(false);
const removing = ref("");
const form = reactive({ relativePath: "", userName: "", password: "" });

onMounted(refresh);
watch(siteName, refresh);

async function refresh() {
  if (!siteName.value) {
    items.value = [];
    listLoaded.value = false;
    return;
  }
  loading.value = true;
  try {
    items.value = await getProtectedDirs(siteName.value);
    listLoaded.value = true;
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    loading.value = false;
  }
}

async function create() {
  if (!siteName.value) {
    return;
  }
  busy.value = true;
  try {
    await protectDir(siteName.value, { ...form });
    form.relativePath = "";
    form.userName = "";
    form.password = "";
    showNotice(t("panel.protected.created"), false);
    await refresh();
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    busy.value = false;
  }
}

/**
 * Korumayı kaldırmak dizini HERKESE AÇIK hâle getirir — sessiz bir güvenlik kaybı.
 * Onay metni hangi dizinin açılacağını açıkça söyler.
 */
async function remove(item: ProtectedDirectory) {
  await confirm({
    title: t("panel.protected.confirmRemoveTitle"),
    message: t("panel.protected.confirmRemoveMessage", { path: `/${item.relativePath}` }),
    impact: [`/${item.relativePath}`, `${t("panel.protected.user")}: ${item.userName}`],
    confirmLabel: t("panel.protected.remove"),
    action: async () => {
      removing.value = item.relativePath;
      try {
        await unprotectDir(siteName.value, item.relativePath);
        showNotice(t("panel.protected.removed"), false);
        await refresh();
      } catch (error) {
        showNotice(resolveApiMessage(error), true);
      } finally {
        removing.value = "";
      }
    }
  });
}

</script>

<style scoped>
.protected-view,
.pd-create,
.pd-list {
  display: grid;
  gap: 14px;
}

.pd-header,
.section-head,
.pd-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
}

.pd-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(180px, 1fr));
  gap: 12px;
}

.stacked-field {
  display: grid;
  gap: 6px;
}

.field-label {
  color: var(--text-muted);
  font-size: 0.74rem;
  font-weight: 700;
}

.pd-rows {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.pd-row {
  padding: 10px 14px;
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-lg);
  background: var(--surface-1);
}

.pd-id {
  display: grid;
  gap: 2px;
  min-width: 0;
}

.meta {
  color: var(--text-muted);
}
</style>
