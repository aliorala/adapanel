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
        <button class="compact" type="submit" v-bind="writeGate(busy || !form.name || !form.userName || !form.password)">
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
          <button class="danger compact" v-bind="writeGate(busy)" @click="remove(db)">{{ t("panel.myDb.delete") }}</button>
        </span>
      </div>
      <p v-if="!listLoaded" class="empty-state" role="status" aria-live="polite">{{ t("panel.common.loading") }}</p>
      <p v-else-if="databases.length === 0" class="empty-state">{{ t("panel.myDb.empty") }}</p>
    </div>

    <TablePager :state="pager" :disabled="busy" />
  </section>
</template>

<script setup lang="ts">
import { onMounted, reactive, ref } from "vue";
import { useI18n } from "vue-i18n";
import PasswordField from "../components/PasswordField.vue";
import { getMyDatabases, createMyDatabase, deleteMyDatabase, type OwnedDatabase } from "../api/myDatabases";
import { useConfirm } from "../components/ConfirmDialog.vue";
import TablePager from "../components/TablePager.vue";
import { useNotice } from "../composables/useNotice";
import { usePagination } from "../composables/usePagination";
import { useSubscriptionGate } from "../composables/useSubscriptionGate";

const { t } = useI18n();
// Askidaki abonelikte YAZMA dugmeleri bastan devre disi + sebebi title/aria ile soylenir
// (bkz. composables/useSubscriptionGate.ts). Okuma dugmeleri kapiya girmez.
const { writeGate } = useSubscriptionGate();
const { confirm } = useConfirm();
const { notice, noticeError, showNotice, resolveApiMessage } = useNotice();
const databases = ref<OwnedDatabase[]>([]);
const loading = ref(false);
// Liste BİR KEZ başarıyla okundu mu. `loading` hata durumunda da false olur; boş dizi + false
// "hiç veritabanınız yok" gibi KESİN bir cümleye dönüşüyordu (bkz. SiteBackupView `listLoaded`).
const listLoaded = ref(false);
const busy = ref(false);
const form = reactive({ engine: "mariadb", name: "", userName: "", password: "" });
// Yüzlerce veritabanı tek seferde DOM'a basılmaz (bkz. composables/usePagination.ts).
const pager = usePagination(databases);

onMounted(load);

async function load() {
  loading.value = true;
  try {
    databases.value = await getMyDatabases();
    listLoaded.value = true;
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
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
    showNotice(resolveApiMessage(error), true);
  } finally {
    busy.value = false;
  }
}

/**
 * Bu ekran MÜŞTERİYE açık ve veritabanı silmenin geri dönüşü yok: içindeki tüm tablolar gider.
 * Bu yüzden `requireText` ile veritabanı adının birebir yazılması istenir.
 */
async function remove(db: OwnedDatabase) {
  await confirm({
    title: t("panel.confirm.deleteDatabaseTitle"),
    message: t("panel.confirm.deleteDatabaseMessage", { name: db.name }),
    impact: [`${db.engine} — ${db.name}`],
    requireText: db.name,
    action: async () => {
      busy.value = true;
      try {
        databases.value = await deleteMyDatabase(db.engine, db.name);
        showNotice(t("panel.myDb.deleted"), false);
      } catch (error) {
        showNotice(resolveApiMessage(error), true);
      } finally {
        busy.value = false;
      }
    }
  });
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
