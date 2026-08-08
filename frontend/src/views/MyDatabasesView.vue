<template>
  <section class="panel module-shell">
    <div class="module-head">
      <div>
        <h3>{{ t("panel.myDb.title") }}</h3>
        <p class="meta">{{ t("panel.myDb.subtitle") }}</p>
      </div>
      <button class="secondary compact" :disabled="loading" @click="load">{{ t("panel.database.refresh") }}</button>
    </div>

    <p v-if="notice" :class="['notice', 'page-notice', { error: noticeError }]" :role="noticeError ? 'alert' : 'status'">{{ notice }}</p>

    <form class="form-surface my-db-form" @submit.prevent="create">
      <div class="form-grid">
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.myDb.engine") }}</span>
          <select v-model="form.engine">
            <option value="mariadb">MariaDB</option>
            <option value="mysql">MySQL</option>
          </select>
        </label>
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.myDb.name") }}</span>
          <input v-model.trim="form.name" placeholder="db_adi" />
        </label>
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.myDb.user") }}</span>
          <input v-model.trim="form.userName" placeholder="db_kullanici" />
        </label>
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.myDb.password") }}</span>
          <input v-model="form.password" type="text" />
        </label>
      </div>
      <div class="row-actions">
        <button class="compact" type="submit" :disabled="busy || !form.name || !form.userName || !form.password">
          {{ busy ? t("panel.database.saving") : t("panel.myDb.create") }}
        </button>
      </div>
    </form>

    <div class="data-table">
      <div class="data-row table-head my-db-row">
        <span>{{ t("panel.myDb.name") }}</span>
        <span>{{ t("panel.myDb.engine") }}</span>
        <span>{{ t("panel.files.actions") }}</span>
      </div>
      <div v-for="db in databases" :key="db.engine + ':' + db.name" class="data-row my-db-row">
        <span><strong>{{ db.name }}</strong></span>
        <span>{{ db.engine }}</span>
        <span class="row-actions">
          <button class="danger compact" :disabled="busy" @click="remove(db)">{{ t("panel.myDb.delete") }}</button>
        </span>
      </div>
      <p v-if="loading && databases.length === 0" class="empty-state">{{ t("panel.common.loading") }}</p>
      <p v-else-if="databases.length === 0" class="empty-state">{{ t("panel.myDb.empty") }}</p>
    </div>
  </section>
</template>

<script setup lang="ts">
import { onMounted, reactive, ref } from "vue";
import { useI18n } from "vue-i18n";
import { getMyDatabases, createMyDatabase, deleteMyDatabase, type OwnedDatabase } from "../api/myDatabases";
import { extractApiMessage } from "../stores/auth";

const { t } = useI18n();
const databases = ref<OwnedDatabase[]>([]);
const loading = ref(false);
const busy = ref(false);
const notice = ref("");
const noticeError = ref(false);
const form = reactive({ engine: "mariadb", name: "", userName: "", password: "" });

onMounted(load);

async function load() {
  loading.value = true;
  try {
    databases.value = await getMyDatabases();
  } catch (error) {
    showNotice(resolveMessage(error), true);
  } finally {
    loading.value = false;
  }
}

async function create() {
  busy.value = true;
  try {
    databases.value = await createMyDatabase({ ...form });
    showNotice(t("panel.myDb.created"), false);
    form.name = "";
    form.userName = "";
    form.password = "";
  } catch (error) {
    showNotice(resolveMessage(error), true);
  } finally {
    busy.value = false;
  }
}

async function remove(db: OwnedDatabase) {
  busy.value = true;
  try {
    databases.value = await deleteMyDatabase(db.engine, db.name);
    showNotice(t("panel.myDb.deleted"), false);
  } catch (error) {
    showNotice(resolveMessage(error), true);
  } finally {
    busy.value = false;
  }
}

function resolveMessage(error: unknown) {
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
.my-db-form { margin-bottom: 14px; }
.form-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(180px, 1fr)); gap: 12px; }
.stacked-field { display: grid; gap: 6px; }
.my-db-row { grid-template-columns: 1.5fr 0.8fr 0.6fr; }
.row-actions { margin-top: 12px; }
.row-actions button { width: auto; }
</style>
