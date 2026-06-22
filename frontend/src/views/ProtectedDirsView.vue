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

    <p v-if="notice" :class="['notice', { error: noticeError }]">{{ notice }}</p>

    <div v-if="siteName" class="form-surface pd-create">
      <h3>{{ t("panel.protected.createTitle") }}</h3>
      <p class="meta">{{ t("panel.protected.hint") }}</p>
      <div class="pd-grid">
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.protected.path") }}</span>
          <input v-model.trim="form.relativePath" placeholder="yonetim" />
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
        <span class="meta">{{ items.length }}</span>
      </div>
      <div v-if="items.length > 0" class="pd-rows">
        <article v-for="item in items" :key="item.relativePath" class="pd-row">
          <div class="pd-id">
            <strong>/{{ item.relativePath }}</strong>
            <span class="meta">{{ t("panel.protected.user") }}: {{ item.userName }}</span>
          </div>
          <button class="danger compact" :disabled="removing === item.relativePath" @click="remove(item.relativePath)">
            {{ t("panel.protected.remove") }}
          </button>
        </article>
      </div>
      <p v-else class="empty-state">{{ t("panel.protected.empty") }}</p>
    </div>
  </section>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from "vue";
import { useI18n } from "vue-i18n";
import { useSitesStore } from "../stores/sites";
import { getProtectedDirs, protectDir, unprotectDir, type ProtectedDirectory } from "../api/sites";
import { extractApiMessage } from "../stores/auth";

const { t } = useI18n();
const sitesStore = useSitesStore();
const siteName = computed(() => sitesStore.selectedSiteName);

const items = ref<ProtectedDirectory[]>([]);
const loading = ref(false);
const busy = ref(false);
const removing = ref("");
const notice = ref("");
const noticeError = ref(false);
const form = reactive({ relativePath: "", userName: "", password: "" });

onMounted(refresh);
watch(siteName, refresh);

async function refresh() {
  if (!siteName.value) {
    items.value = [];
    return;
  }
  loading.value = true;
  try {
    items.value = await getProtectedDirs(siteName.value);
  } catch (error) {
    showNotice(extractApiMessage(error), true);
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
    showNotice(extractApiMessage(error), true);
  } finally {
    busy.value = false;
  }
}

async function remove(relativePath: string) {
  removing.value = relativePath;
  try {
    await unprotectDir(siteName.value, relativePath);
    showNotice(t("panel.protected.removed"), false);
    await refresh();
  } catch (error) {
    showNotice(extractApiMessage(error), true);
  } finally {
    removing.value = "";
  }
}

function showNotice(message: string, isError: boolean) {
  notice.value = message;
  noticeError.value = isError;
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
