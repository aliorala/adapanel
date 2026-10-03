<template>
  <section class="panel module-shell site-db">
    <div class="module-head">
      <div>
        <h3>{{ t("panel.siteDb.title") }}</h3>
        <p class="meta">{{ t("panel.siteDb.subtitle") }}</p>
      </div>
      <button class="secondary compact" :disabled="loading || mutationBusy || !siteName" @click="load">{{ t("panel.siteDb.refresh") }}</button>
    </div>

    <p v-if="notice" :class="['notice', 'page-notice', { error: noticeError }]" :role="noticeError ? 'alert' : 'status'">{{ notice }}</p>
    <p v-if="!siteName" class="meta">{{ t("panel.php.selectSite") }}</p>

    <template v-else>
      <div v-if="isAdmin" class="db-create">
        <button class="secondary compact" type="button" v-bind="writeGate(!showForm && !canWrite)" @click="showForm = !showForm">
          {{ showForm ? t("panel.common.cancel") : t("panel.siteDb.create") }}
        </button>
        <form v-if="showForm" class="db-create-form" @submit.prevent="createDb">
          <input v-model.trim="form.name" :placeholder="t('panel.siteDb.dbName')" :disabled="creating" />
          <input v-model.trim="form.userName" :placeholder="t('panel.siteDb.userName')" :disabled="creating" />
          <PasswordField v-model="form.password" autocomplete="off" :placeholder="t('panel.siteDb.password')" :disabled="creating" />
          <button type="submit" v-bind="writeGate(!canWrite || !form.name.trim())">
            {{ creating ? t("panel.siteDb.creating") : t("panel.siteDb.create") }}
          </button>
        </form>
      </div>

      <div v-if="isAdmin && candidates.length" class="db-link">
        <span class="db-link-label">{{ t("panel.siteDb.linkTitle") }}</span>
        <div class="db-link-row">
          <select v-model="selectedCandidate" :disabled="!canWrite">
            <option value="">{{ t("panel.siteDb.linkSelect") }}</option>
            <option v-for="db in candidates" :key="db.engine + ':' + db.name" :value="db.engine + ':' + db.name">
              {{ db.name }} ({{ db.engine }})
            </option>
          </select>
          <button class="secondary compact" type="button" v-bind="writeGate(!canWrite || !selectedCandidate)" @click="linkDb">
            {{ linking ? t("panel.siteDb.linking") : t("panel.siteDb.link") }}
          </button>
        </div>
      </div>

      <p v-if="loading && !databases.length" class="meta loading-row">{{ t("panel.siteDb.loading") }}</p>

      <div v-if="databases.length" class="db-list">
        <article v-for="db in databases" :key="db.engine + ':' + db.name" class="db-row">
          <div class="db-id">
            <span class="db-glyph">DB</span>
            <div>
              <strong>{{ db.name }}</strong>
              <span class="db-engine">{{ db.engine }}</span>
            </div>
          </div>
          <span class="db-actions">
            <button v-if="isAdmin" class="secondary compact" :disabled="opening || !canWrite" @click="openDatabaseTool(db)">{{ isMysqlEngine(db) ? "phpMyAdmin" : "Adminer" }} ↗</button>
            <!-- BAĞLANTIYI KALDIR: veritabanını SİLMEZ. Bu düğme yoktu ve yanlış veritabanını
                 siteye bağlayan kullanıcının tek seçeneği onu SİLMEKTİ — üstelik site silinince
                 cascade o veritabanını zaten DROP ediyordu, yani geri dönüşü olmayan veri kaybı.
                 Uç ve çevirileri hazırdı, yalnız arayüzü eksikti. -->
            <button
              class="secondary compact"
              v-bind="writeGate(!canWrite)"
              @click="unlinkDb(db)">
              {{ unlinkingKey === db.engine + ':' + db.name ? t("panel.siteDb.unlinking") : t("panel.siteDb.unlink") }}
            </button>
            <button
              v-if="isAdmin"
              class="danger compact"
              v-bind="writeGate(!canWrite)"
              @click="deleteDb(db)">
              {{ deletingKey === db.engine + ':' + db.name ? t("panel.siteDb.deleting") : t("panel.siteDb.delete") }}
            </button>
          </span>
        </article>
      </div>

      <p v-else-if="loaded" class="meta empty">{{ t("panel.siteDb.empty") }}</p>

      <p class="hint">{{ t("panel.siteDb.hint") }}</p>
    </template>
  </section>
</template>

<script setup lang="ts">
import { computed, onUnmounted, reactive, ref, watch } from "vue";
import { CanceledError } from "axios";
import { useI18n } from "vue-i18n";
import PasswordField from "../components/PasswordField.vue";
import { getSiteDatabases, linkSiteDatabase, unlinkSiteDatabase, type SiteDatabaseRef } from "../api/sites";
import { createDatabase, deleteDatabase, openPhpMyAdmin, openAdminer } from "../api/databases";
import { useConfirm } from "../components/ConfirmDialog.vue";
import { extractApiMessage, useAuthStore } from "../stores/auth";
import { useSitesStore } from "../stores/sites";
import { useSubscriptionGate } from "../composables/useSubscriptionGate";
import { openAsyncReachable } from "../utils/externalTab";
import { getSessionEpoch } from "../api/client";

type LinkedDb = SiteDatabaseRef;

const { t } = useI18n();
// Askidaki abonelikte YAZMA dugmeleri bastan devre disi + sebebi title/aria ile soylenir
// (bkz. composables/useSubscriptionGate.ts). Okuma dugmeleri kapiya girmez.
const { writeGate } = useSubscriptionGate();
const { confirm } = useConfirm();
const sitesStore = useSitesStore();
const authStore = useAuthStore();
const isAdmin = computed(() => authStore.session?.user?.role === "Administrator");
const siteName = computed(() => sitesStore.selectedSiteName);
const databases = ref<LinkedDb[]>([]);
const candidates = ref<SiteDatabaseRef[]>([]);
const selectedCandidate = ref("");
const linking = ref(false);
const loading = ref(false);
const loaded = ref(false);
const opening = ref(false);
const creating = ref(false);
const deletingKey = ref("");
const unlinkingKey = ref("");
const mutationBusy = computed(() => creating.value || linking.value || Boolean(deletingKey.value || unlinkingKey.value));
const canWrite = computed(() => Boolean(siteName.value) && loaded.value && !loading.value && !mutationBusy.value);
const showForm = ref(false);
const notice = ref("");
const noticeError = ref(false);
const form = reactive({ name: "", userName: "", password: "" });

// Backend ResourceNaming.DatabaseNameFromSite ile AYNI kural (küçük harf, harf/rakam dışı '_') →
// oluşturulan DB cascade silmede ve bağlı listede eşleşsin.
function dbNameFromSite(value: string): string {
  return (value || "").trim().toLowerCase().split("").map((c) => /[a-z0-9]/.test(c) ? c : "_").join("");
}

let loadVersion = 0;
let scopeVersion = 0;
let formVersion = 0;
let active = true;
onUnmounted(() => { active = false; scopeVersion++; loadVersion++; });
function databaseContext() {
  const site = siteName.value, version = scopeVersion, epoch = getSessionEpoch();
  return { site, current: () => active && version === scopeVersion && siteName.value === site && epoch === getSessionEpoch() };
}
function containsDatabase(db: LinkedDb) {
  return databases.value.some(item => item.engine === db.engine && item.name === db.name);
}
watch(showForm, open => {
  formVersion++;
  creating.value = false;
  if (!open) form.password = "";
}, { flush: "sync" });
watch([siteName, () => authStore.session?.user?.id, () => authStore.session?.user?.role], ([value]) => {
  scopeVersion++;
  creating.value = false;
  linking.value = false;
  opening.value = false;
  deletingKey.value = "";
  unlinkingKey.value = "";
  form.name = dbNameFromSite(value);
  form.userName = "";
  form.password = "";
  databases.value = [];
  candidates.value = [];
  selectedCandidate.value = "";
  showForm.value = false;
  loaded.value = false;
  showNotice("", false);
  void readWorkspace(databaseContext());
}, { immediate: true, flush: "sync" });

async function load() {
  if (!active || mutationBusy.value) return;
  showNotice("", false);
  await readWorkspace(databaseContext());
}

async function readWorkspace(context: ReturnType<typeof databaseContext>) {
  if (!context.current()) return false;
  const version = ++loadVersion;
  loaded.value = false;
  databases.value = [];
  candidates.value = [];
  if (!context.site) {
    loading.value = false;
    return false;
  }
  loading.value = true;
  try {
    const data = await getSiteDatabases(context.site);
    if (version !== loadVersion || !context.current()) return false;
    databases.value = data.linked;
    candidates.value = data.candidates;
    // Seçili aday artık listede yoksa seçimi temizle.
    if (!candidates.value.some((db) => db.engine + ":" + db.name === selectedCandidate.value)) {
      selectedCandidate.value = "";
    }
    loaded.value = true;
    return true;
  } catch (error) {
    if (version === loadVersion && context.current()) showNotice(resolveApiMessage(error), true);
    return false;
  } finally {
    if (version === loadVersion && context.current()) loading.value = false;
  }
}

// Seçilen mevcut veritabanını bu siteye bağla (Plesk gibi: isim eşleşmesi gerekmez).
async function linkDb() {
  if (!active || !canWrite.value || !isAdmin.value) return;
  const context = databaseContext();
  const choice = candidates.value.find((db) => db.engine + ":" + db.name === selectedCandidate.value);
  if (!choice) return;
  const target = { ...choice };
  linking.value = true;
  showNotice("", false);
  try {
    await linkSiteDatabase(context.site, target.engine, target.name);
    if (!context.current()) return;
    selectedCandidate.value = "";
    const refreshed = await readWorkspace(context);
    if (context.current() && refreshed) showNotice(t("panel.siteDb.linked"), false);
  } catch (error) {
    if (context.current()) showNotice(resolveApiMessage(error), true);
  } finally {
    if (context.current()) linking.value = false;
  }
}

function isMysqlEngine(db: LinkedDb) {
  return db.engine.toLowerCase() === "mariadb" || db.engine.toLowerCase() === "mysql";
}

async function openDatabaseTool(db: LinkedDb) {
  if (!active || !isAdmin.value || opening.value || !canWrite.value || !containsDatabase(db)) return;
  const context = databaseContext(), target = { ...db };
  opening.value = true;
  try {
    await openAsyncReachable(async () => {
      const result = await (isMysqlEngine(target) ? openPhpMyAdmin : openAdminer)(target.engine, target.name);
      if (!context.current()) throw new CanceledError("Database scope changed");
      return result.url;
    });
  } catch (error) {
    if (context.current()) showNotice(resolveApiMessage(error), true);
  } finally {
    if (context.current()) opening.value = false;
  }
}

async function unlinkDb(db: LinkedDb) {
  if (!active || !canWrite.value || !containsDatabase(db)) return;
  const context = databaseContext(), target = { ...db };
  unlinkingKey.value = target.engine + ":" + target.name;
  showNotice("", false);
  try {
    await unlinkSiteDatabase(context.site, target.engine, target.name);
    if (!context.current()) return;
    const refreshed = await readWorkspace(context);
    if (context.current() && refreshed) showNotice(t("panel.siteDb.unlinked"), false);
  } catch (error) {
    if (context.current()) showNotice(resolveApiMessage(error), true);
  } finally {
    if (context.current()) unlinkingKey.value = "";
  }
}

async function deleteDb(db: LinkedDb) {
  if (!active || !canWrite.value || !isAdmin.value || !containsDatabase(db)) return;
  const context = databaseContext(), target = { ...db };
  deletingKey.value = target.engine + ":" + target.name;
  // Yüksek riskli onay: veritabanı adı yazılmadan silinemez.
  try {
    await confirm({
      title: t("panel.confirm.deleteDatabaseTitle", { name: target.name }),
      message: t("panel.siteDb.confirmDelete", { name: target.name }),
      impact: [`${t("panel.siteDb.dbName")}: ${target.name} (${target.engine})`, t("panel.confirm.impactDbUser")],
      requireText: target.name,
      action: async () => {
        if (!context.current()) return;
        showNotice("", false);
        try {
          // DeleteDatabaseAsync DB'yi + (başka yerde kullanılmıyorsa) kullanıcısını da düşürür + site bağını temizler.
          await deleteDatabase(target.engine, target.name);
          if (!context.current()) return;
          const refreshed = await readWorkspace(context);
          if (context.current() && refreshed) showNotice(t("panel.siteDb.deleted"), false);
        } catch (error) {
          if (context.current()) showNotice(resolveApiMessage(error), true);
        }
      },
    });
  } finally {
    if (context.current()) deletingKey.value = "";
  }
}

async function createDb() {
  if (!active || !canWrite.value || !isAdmin.value || !showForm.value || !form.name.trim()) {
    return;
  }
  creating.value = true;
  const context = databaseContext(), version = formVersion;
  const current = () => context.current() && version === formVersion;
  showNotice("", false);
  try {
    await createDatabase({
      engine: "mariadb",
      name: form.name.trim(),
      userName: form.userName.trim() || undefined,
      password: form.password || undefined,
      // Bu site iş alanından oluşturulan DB doğrudan siteye BAĞLANIR → site silinince cascade gider.
      siteName: context.site
    });
    if (!current()) return;
    form.userName = "";
    form.password = "";
    showForm.value = false;
    const completedVersion = formVersion;
    const refreshed = await readWorkspace(context);
    if (context.current() && formVersion === completedVersion && refreshed) showNotice(t("panel.siteDb.created"), false);
  } catch (error) {
    if (current()) showNotice(resolveApiMessage(error), true);
  } finally {
    if (current()) creating.value = false;
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
  /* 0.66rem = 8.91px idi — olcek tabani `--fs-2xs` (10.53px); altina inen kural yok. */
  font-size: var(--fs-2xs);
  font-weight: 800;
  flex-shrink: 0;
}

.db-engine {
  color: var(--text-muted);
  /* 0.74rem = 9.99px idi — olcek tabani `--fs-2xs` (10.53px); altina inen kural yok. */
  font-size: var(--fs-2xs);
  text-transform: uppercase;
}

.db-actions {
  display: flex;
  gap: 8px;
  flex-shrink: 0;
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

/* Parola alani artik bir BILESEN (PasswordField): satirdaki yeri kendi kok elemani tutar.
   Kapsamli CSS bilesenin ICINE gecmez, ama kok elemani ust bilesenin kapsam damgasini ALIR --
   bu yuzden esnek payi ic `input`e degil `.password-field`e verilir, yoksa alan satirda
   buzusuyor ve "Goster" dugmesi alt satira dusuyordu. Payi biraz genis: alanin yaninda
   dugme de duruyor. */
.db-create-form input,
.db-create-form .password-field {
  flex: 1 1 160px;
  width: auto;
}

.db-create-form .password-field {
  flex-basis: 220px;
}

.db-create-form button {
  width: auto;
}

.db-link {
  display: grid;
  gap: 6px;
  padding: 12px 14px;
  border: 1px dashed var(--panel-border);
  border-radius: 11px;
  background: var(--surface-1);
}

.db-link-label {
  font-size: 0.82rem;
  color: var(--text-muted);
}

.db-link-row {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
}

.db-link-row select {
  flex: 1 1 200px;
}

.db-link-row button {
  width: auto;
}

.empty {
  padding: 18px 0;
}

.loading-row {
  padding: 18px 0;
  color: var(--text-muted);
}

</style>
