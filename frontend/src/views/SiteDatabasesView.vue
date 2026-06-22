<template>
  <section class="panel module-shell site-db">
    <div class="module-head">
      <div>
        <h3>{{ t("panel.siteDb.title") }}</h3>
        <p class="meta">{{ t("panel.siteDb.subtitle") }}</p>
      </div>
      <button class="secondary compact" :disabled="loading || !siteName" @click="load">{{ t("panel.siteDb.refresh") }}</button>
    </div>

    <p v-if="notice" :class="['notice', { error: noticeError }]">{{ notice }}</p>
    <p v-if="!siteName" class="meta">{{ t("panel.php.selectSite") }}</p>

    <template v-else>
      <div v-if="isAdmin" class="db-create">
        <button class="secondary compact" type="button" @click="showForm = !showForm">
          {{ showForm ? t("panel.common.cancel") : t("panel.siteDb.create") }}
        </button>
        <form v-if="showForm" class="db-create-form" @submit.prevent="createDb">
          <input v-model.trim="form.name" :placeholder="t('panel.siteDb.dbName')" :disabled="creating" />
          <input v-model.trim="form.userName" :placeholder="t('panel.siteDb.userName')" :disabled="creating" />
          <input v-model="form.password" type="password" autocomplete="off" :placeholder="t('panel.siteDb.password')" :disabled="creating" />
          <button type="submit" :disabled="creating || !form.name.trim()">
            {{ creating ? t("panel.siteDb.creating") : t("panel.siteDb.create") }}
          </button>
        </form>
      </div>

      <div v-if="databases.length" class="db-list">
        <article v-for="db in databases" :key="db.engine + ':' + db.name" class="db-row">
          <div class="db-id">
            <span class="db-glyph">DB</span>
            <div>
              <strong>{{ db.name }}</strong>
              <span class="db-engine">{{ db.engine }}</span>
            </div>
          </div>
          <button class="secondary compact" :disabled="opening" @click="openPma(db)">phpMyAdmin ↗</button>
        </article>
      </div>

      <p v-else-if="loaded" class="meta empty">{{ t("panel.siteDb.empty") }}</p>

      <p class="hint">{{ t("panel.siteDb.hint") }}</p>
    </template>
  </section>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from "vue";
import { useI18n } from "vue-i18n";
import { getSiteDeletionPlan } from "../api/sites";
import { createDatabase, openPhpMyAdmin } from "../api/databases";
import { extractApiMessage, useAuthStore } from "../stores/auth";
import { useSitesStore } from "../stores/sites";

type LinkedDb = { engine: string; name: string };

const { t } = useI18n();
const sitesStore = useSitesStore();
const isAdmin = computed(() => useAuthStore().session?.user?.role === "Administrator");
const siteName = computed(() => sitesStore.selectedSiteName);
const databases = ref<LinkedDb[]>([]);
const loading = ref(false);
const loaded = ref(false);
const opening = ref(false);
const creating = ref(false);
const showForm = ref(false);
const notice = ref("");
const noticeError = ref(false);
const form = reactive({ name: "", userName: "", password: "" });

// Backend ResourceNaming.DatabaseNameFromSite ile AYNI kural (küçük harf, harf/rakam dışı '_') →
// oluşturulan DB cascade silmede ve bağlı listede eşleşsin.
function dbNameFromSite(value: string): string {
  return (value || "").trim().toLowerCase().split("").map((c) => /[a-z0-9]/.test(c) ? c : "_").join("");
}

watch(siteName, (value) => { form.name = dbNameFromSite(value); }, { immediate: true });

onMounted(load);

async function load() {
  if (!siteName.value) {
    return;
  }
  loading.value = true;
  notice.value = "";
  try {
    const plan = await getSiteDeletionPlan(siteName.value);
    databases.value = (plan.databases ?? []).map((entry) => {
      const separator = entry.indexOf(":");
      return separator > 0
        ? { engine: entry.slice(0, separator), name: entry.slice(separator + 1) }
        : { engine: "mysql", name: entry };
    });
    loaded.value = true;
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    loading.value = false;
  }
}

async function openPma(db: LinkedDb) {
  opening.value = true;
  try {
    const result = await openPhpMyAdmin(db.engine, db.name);
    window.open(result.url, "_blank", "noopener");
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    opening.value = false;
  }
}

async function createDb() {
  if (!form.name.trim()) {
    return;
  }
  creating.value = true;
  notice.value = "";
  try {
    await createDatabase({
      engine: "mariadb",
      name: form.name.trim(),
      userName: form.userName.trim() || undefined,
      password: form.password || undefined
    });
    showNotice(t("panel.siteDb.created"), false);
    form.userName = "";
    form.password = "";
    showForm.value = false;
    await load();
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    creating.value = false;
  }
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
.site-db {
  display: grid;
  gap: 12px;
}

.db-list {
  display: grid;
  gap: 8px;
}

.db-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  padding: 12px 14px;
  border: 1px solid var(--panel-border);
  border-radius: 11px;
  background: var(--surface-1);
}

.db-id {
  display: flex;
  align-items: center;
  gap: 12px;
  min-width: 0;
}

.db-id > div {
  display: grid;
  gap: 2px;
  min-width: 0;
}

.db-glyph {
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

.db-engine {
  color: var(--text-muted);
  font-size: 0.74rem;
  text-transform: uppercase;
}

.db-row button {
  width: auto;
  flex-shrink: 0;
}

.db-create {
  display: grid;
  gap: 8px;
  justify-items: start;
}

.db-create-form {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  width: 100%;
}

.db-create-form input {
  flex: 1 1 160px;
  width: auto;
}

.db-create-form button {
  width: auto;
}

.empty {
  padding: 18px 0;
}

.hint {
  color: var(--text-muted);
  font-size: 0.8rem;
}
</style>
