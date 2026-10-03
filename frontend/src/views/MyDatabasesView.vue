<template>
  <section class="panel module-shell">
    <div class="module-head">
      <div>
        <h3>{{ t("panel.myDb.title") }}</h3>
        <p class="meta">{{ t("panel.myDb.subtitle") }}</p>
      </div>
      <button class="secondary compact" :disabled="loading || busy || confirming" @click="load">{{ t("panel.database.refresh") }}</button>
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
          <input v-model.trim="form.name" :placeholder="t('panel.myDb.namePlaceholder')" />
        </label>
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.myDb.user") }}</span>
          <input v-model.trim="form.userName" :placeholder="t('panel.myDb.userPlaceholder')" />
        </label>
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.myDb.password") }}</span>
          <!-- `type="text"` idi: kiracinin veritabani parolasi ekranda SUREKLI aciktı.
               `autocomplete="new-password"` EKLENDI (once hic yoktu): alan artik gercek bir
               parola alani oldugu icin, deger verilmezse tarayici burayi PANEL parolasiyla
               doldurmaya calisir. "new-password" bunu keser ve dogru davranistir: burada
               YENI bir parola belirleniyor. -->
          <PasswordField v-model="form.password" autocomplete="new-password" />
        </label>
      </div>
      <div class="row-actions">
        <button class="compact" type="submit" v-bind="writeGate(busy || confirming || loading || !listLoaded || !form.name || !form.userName || !form.password)">
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
      <div v-for="db in pager.items.value" :key="db.engine + ':' + db.name" class="data-row my-db-row">
        <span><strong>{{ db.name }}</strong></span>
        <span>{{ db.engine }}</span>
        <span class="row-actions">
          <button class="danger compact" v-bind="writeGate(busy || confirming || loading || !listLoaded)" @click="remove(db)">{{ t("panel.myDb.delete") }}</button>
        </span>
      </div>
      <p v-if="!listLoaded" class="empty-state" role="status" aria-live="polite">{{ t(loading ? "panel.common.loading" : "panel.common.loadFailed") }}</p>
      <p v-else-if="databases.length === 0" class="empty-state">{{ t("panel.myDb.empty") }}</p>
    </div>

    <TablePager :state="pager" :disabled="busy" />
  </section>
</template>

<script setup lang="ts">
import { onMounted, onUnmounted, reactive, ref, watch } from "vue";
import { useI18n } from "vue-i18n";
import PasswordField from "../components/PasswordField.vue";
import { getMyDatabases, createMyDatabase, deleteMyDatabase, type OwnedDatabase } from "../api/myDatabases";
import { useConfirm } from "../components/ConfirmDialog.vue";
import TablePager from "../components/TablePager.vue";
import { useNotice } from "../composables/useNotice";
import { usePagination } from "../composables/usePagination";
import { useSubscriptionGate } from "../composables/useSubscriptionGate";
import { getSessionEpoch } from "../api/client";
import { databaseIdentity } from "../utils/databaseIdentity";

const { t } = useI18n();
// Askidaki abonelikte YAZMA dugmeleri bastan devre disi + sebebi title/aria ile soylenir
// (bkz. composables/useSubscriptionGate.ts). Okuma dugmeleri kapiya girmez.
const { writeGate, locked } = useSubscriptionGate();
const { confirm } = useConfirm();
const { notice, noticeError, showNotice, resolveApiMessage } = useNotice();
const databases = ref<OwnedDatabase[]>([]);
const loading = ref(false);
// Yalnız son başarılı ölçüm yeni yazma işlemlerine izin verir.
const listLoaded = ref(false);
const busy = ref(false);
const confirming = ref(false);
const form = reactive({ engine: "mariadb", name: "", userName: "", password: "" });
const sessionEpoch = getSessionEpoch();
let disposed = false, readSequence = 0, draftVersion = 0;
const isActive = () => !disposed && sessionEpoch === getSessionEpoch();
const canWrite = () => isActive() && listLoaded.value && !loading.value && !busy.value && !confirming.value && !locked.value;
watch(form, () => { draftVersion++; }, { flush: "sync" });
watch(() => form.engine, () => { form.password = ""; }, { flush: "sync" });
// Yüzlerce veritabanı tek seferde DOM'a basılmaz (bkz. composables/usePagination.ts).
const pager = usePagination(databases);

onMounted(load);
onUnmounted(() => { disposed = true; readSequence++; form.password = ""; });

async function load() {
  if (!isActive() || busy.value || confirming.value) return;
  const sequence = ++readSequence;
  const current = () => isActive() && sequence === readSequence;
  loading.value = true;
  listLoaded.value = false;
  try {
    const result = await getMyDatabases();
    if (!current()) return;
    databases.value = result;
    listLoaded.value = true;
    showNotice("", false);
  } catch (error) {
    if (current()) showNotice(resolveApiMessage(error), true);
  } finally {
    if (current()) loading.value = false;
  }
}

async function create() {
  if (!canWrite() || !["mariadb", "mysql"].includes(form.engine) || !form.name.trim() || !form.userName.trim() || !form.password) return;
  const submitted = { ...form, name: form.name.trim(), userName: form.userName.trim() };
  const version = draftVersion;
  busy.value = true;
  try {
    const result = await createMyDatabase(submitted);
    if (!isActive()) return;
    databases.value = result;
    if (!result.some(db => databaseIdentity(db.engine, db.name) === databaseIdentity(submitted.engine, submitted.name))) {
      showNotice(t("panel.myDb.resultUnconfirmed"), true);
      return;
    }
    showNotice(t("panel.myDb.created"), false);
    if (version === draftVersion) {
      form.name = "";
      form.userName = "";
      form.password = "";
    }
  } catch (error) {
    if (isActive()) showNotice(resolveApiMessage(error), true);
  } finally {
    if (isActive()) busy.value = false;
  }
}

/**
 * Bu ekran MÜŞTERİYE açık ve veritabanı silmenin geri dönüşü yok: içindeki tüm tablolar gider.
 * Bu yüzden `requireText` ile veritabanı adının birebir yazılması istenir.
 */
async function remove(db: OwnedDatabase) {
  if (!canWrite() || !databases.value.some(item => item.engine === db.engine && item.name === db.name)) return;
  const { engine, name } = db;
  let executed = false;
  confirming.value = true;
  try {
    await confirm({
      title: t("panel.confirm.deleteDatabaseTitle"),
      message: t("panel.confirm.deleteDatabaseMessage", { name }),
      impact: [`${engine} — ${name}`],
      requireText: name,
      action: async () => {
        if (executed || !isActive() || locked.value || busy.value || loading.value || !listLoaded.value) return;
        executed = true;
        busy.value = true;
        try {
          const result = await deleteMyDatabase(engine, name);
          if (!isActive()) return;
          databases.value = result;
          const stillPresent = result.some(db => databaseIdentity(db.engine, db.name) === databaseIdentity(engine, name));
          showNotice(t(stillPresent ? "panel.myDb.resultUnconfirmed" : "panel.myDb.deleted"), stillPresent);
        } catch (error) {
          if (isActive()) showNotice(resolveApiMessage(error), true);
        } finally {
          if (isActive()) busy.value = false;
        }
      }
    });
  } finally {
    if (isActive()) confirming.value = false;
  }
}
</script>

<style scoped>
.my-db-form { margin-bottom: 14px; }
.form-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(180px, 1fr)); gap: 12px; }
.stacked-field { display: grid; gap: 6px; }
.my-db-row { grid-template-columns: 1.5fr 0.8fr 0.6fr; }
.row-actions { margin-top: 12px; }
.row-actions button { width: auto; }
@media (max-width: 640px) {
  .my-db-row { min-width: 0; grid-template-columns: minmax(0, 1fr) auto; }
  .my-db-row.table-head { display: none; }
  .my-db-row > span:first-child { grid-column: 1 / -1; min-width: 0; overflow-wrap: anywhere; }
  .my-db-row .row-actions { margin-top: 0; }
}
</style>
