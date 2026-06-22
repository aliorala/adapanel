<template>
  <section class="panel module-shell site-migration">
    <div class="module-head">
      <div>
        <h3>{{ t("panel.migration.title") }}</h3>
        <p class="meta">{{ t("panel.migration.subtitle") }}</p>
      </div>
    </div>

    <p v-if="notice" :class="['notice', { error: noticeError }]">{{ notice }}</p>
    <p v-if="!siteName" class="meta">{{ t("panel.php.selectSite") }}</p>

    <div v-else class="form-surface">
      <label class="stacked-field">
        <span class="field-label">{{ t("panel.migration.url") }}</span>
        <input v-model.trim="archiveUrl" placeholder="https://eski-sunucu/site.zip" :disabled="busy" />
        <small class="field-hint">{{ t("panel.migration.urlHint") }}</small>
      </label>

      <label class="check-field">
        <input v-model="cleanFirst" type="checkbox" :disabled="busy" />
        <span>{{ t("panel.migration.cleanFirst") }}</span>
      </label>

      <div class="row-actions">
        <button :disabled="busy || !archiveUrl.trim()" @click="run">
          {{ busy ? t("panel.migration.running") : t("panel.migration.run") }}
        </button>
      </div>

      <p v-if="busy" class="hint">{{ t("panel.migration.runningHint") }}</p>
      <p class="hint">{{ t("panel.migration.help") }}</p>
    </div>
  </section>
</template>

<script setup lang="ts">
import { computed, ref } from "vue";
import { useI18n } from "vue-i18n";
import { importSiteArchive } from "../api/siteHosting";
import { extractApiDetails, extractApiMessage } from "../stores/auth";
import { useSitesStore } from "../stores/sites";

const { t } = useI18n();
const sitesStore = useSitesStore();
const siteName = computed(() => sitesStore.selectedSiteName);
const archiveUrl = ref("");
const cleanFirst = ref(false);
const busy = ref(false);
const notice = ref("");
const noticeError = ref(false);

async function run() {
  if (!siteName.value || !archiveUrl.value.trim()) {
    return;
  }
  busy.value = true;
  notice.value = "";
  try {
    const result = await importSiteArchive(siteName.value, archiveUrl.value.trim(), cleanFirst.value);
    showNotice(t("panel.migration.done", { count: result.filesCopied }), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    busy.value = false;
  }
}

function resolveApiMessage(error: unknown) {
  const key = extractApiMessage(error);
  const translated = t(key);
  const message = translated === key ? key : translated;
  const details = extractApiDetails(error);
  return details ? `${message}\n\n${details}` : message;
}

function showNotice(message: string, isError: boolean) {
  notice.value = message;
  noticeError.value = isError;
}
</script>

<style scoped>
.site-migration {
  display: grid;
  gap: 12px;
}

.field-hint {
  color: var(--text-muted);
  font-size: 0.74rem;
}

.row-actions button {
  width: auto;
}

.hint {
  color: var(--text-muted);
  font-size: 0.8rem;
  white-space: pre-line;
}
</style>
