<template>
  <section class="panel module-shell site-db">
    <div class="module-head">
      <div>
        <h3>{{ t("panel.siteDb.title") }}</h3>
        <p class="meta">{{ t("panel.siteDb.subtitle") }}</p>
      </div>
      <button class="secondary compact" :disabled="loading || !siteName" @click="load">{{ t("panel.siteDb.refresh") }}</button>
    </div>

    <p v-if="notice" :class="['notice', 'page-notice', { error: noticeError }]" :role="noticeError ? 'alert' : 'status'">{{ notice }}</p>
    <p v-if="!siteName" class="meta">{{ t("panel.php.selectSite") }}</p>

    <template v-else>
      <div v-if="isAdmin" class="db-create">
        <button class="secondary compact" type="button" v-bind="writeGate()" @click="showForm = !showForm">
          {{ showForm ? t("panel.common.cancel") : t("panel.siteDb.create") }}
        </button>
        <form v-if="showForm" class="db-create-form" @submit.prevent="createDb">
          <input v-model.trim="form.name" :placeholder="t('panel.siteDb.dbName')" :disabled="creating" />
          <input v-model.trim="form.userName" :placeholder="t('panel.siteDb.userName')" :disabled="creating" />
          <PasswordField v-model="form.password" autocomplete="off" :placeholder="t('panel.siteDb.password')" :disabled="creating" />
          <button type="submit" v-bind="writeGate(creating || !form.name.trim())">
            {{ creating ? t("panel.siteDb.creating") : t("panel.siteDb.create") }}
          </button>
        </form>
      </div>

      <div v-if="isAdmin && candidates.length" class="db-link">
        <span class="db-link-label">{{ t("panel.siteDb.linkTitle") }}</span>
        <div class="db-link-row">
          <select v-model="selectedCandidate" :disabled="linking">
            <option value="">{{ t("panel.siteDb.linkSelect") }}</option>
            <option v-for="db in candidates" :key="db.engine + ':' + db.name" :value="db.engine + ':' + db.name">
              {{ db.name }} ({{ db.engine }})
            </option>
          </select>
          <button class="secondary compact" type="button" v-bind="writeGate(linking || !selectedCandidate)" @click="linkDb">
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
            <button class="secondary compact" :disabled="opening" @click="openPma(db)">phpMyAdmin ↗</button>
            <!-- BAĞLANTIYI KALDIR: veritabanını SİLMEZ. Bu düğme yoktu ve yanlış veritabanını
                 siteye bağlayan kullanıcının tek seçeneği onu SİLMEKTİ — üstelik site silinince
                 cascade o veritabanını zaten DROP ediyordu, yani geri dönüşü olmayan veri kaybı.
                 Uç ve çevirileri hazırdı, yalnız arayüzü eksikti. -->
            <button
              class="secondary compact"
              v-bind="writeGate(unlinkingKey === db.engine + ':' + db.name)"
              @click="unlinkDb(db)">
              {{ unlinkingKey === db.engine + ':' + db.name ? t("panel.siteDb.unlinking") : t("panel.siteDb.unlink") }}
            </button>
            <button
              v-if="isAdmin"
              class="danger compact"
              v-bind="writeGate(deletingKey === db.engine + ':' + db.name)"
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
import { computed, onMounted, reactive, ref, watch } from "vue";
import { useI18n } from "vue-i18n";
import PasswordField from "../components/PasswordField.vue";
import { getSiteDatabases, linkSiteDatabase, unlinkSiteDatabase, type SiteDatabaseRef } from "../api/sites";
import { createDatabase, deleteDatabase, openPhpMyAdmin } from "../api/databases";
import { useConfirm } from "../components/ConfirmDialog.vue";
import { extractApiMessage, useAuthStore } from "../stores/auth";
import { useSitesStore } from "../stores/sites";
import { useSubscriptionGate } from "../composables/useSubscriptionGate";

type LinkedDb = SiteDatabaseRef;

const { t } = useI18n();
// Askidaki abonelikte YAZMA dugmeleri bastan devre disi + sebebi title/aria ile soylenir
// (bkz. composables/useSubscriptionGate.ts). Okuma dugmeleri kapiya girmez.
const { writeGate } = useSubscriptionGate();
const { confirm } = useConfirm();
const sitesStore = useSitesStore();
const isAdmin = computed(() => useAuthStore().session?.user?.role === "Administrator");
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
    const data = await getSiteDatabases(siteName.value);
    databases.value = data.linked;
    candidates.value = data.candidates;
    // Seçili aday artık listede yoksa seçimi temizle.
    if (!candidates.value.some((db) => db.engine + ":" + db.name === selectedCandidate.value)) {
      selectedCandidate.value = "";
    }
    loaded.value = true;
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    loading.value = false;
  }
}

// Seçilen mevcut veritabanını bu siteye bağla (Plesk gibi: isim eşleşmesi gerekmez).
async function linkDb() {
  const choice = candidates.value.find((db) => db.engine + ":" + db.name === selectedCandidate.value);
  if (!choice || !siteName.value) {
    return;
  }
  linking.value = true;
  notice.value = "";
  try {
    await linkSiteDatabase(siteName.value, choice.engine, choice.name);
    showNotice(t("panel.siteDb.linked"), false);
    selectedCandidate.value = "";
    await load();
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    linking.value = false;
  }
}

async function openPma(db: LinkedDb) {
  // Sekmeyi TIK anında aç (kullanıcı hareketi içinde); URL'yi await sonrası ata (popup engeli olmasın).
  const tab = window.open("", "_blank");
  opening.value = true;
  try {
    const result = await openPhpMyAdmin(db.engine, db.name);
    if (tab) {
      tab.location.href = result.url;
    }
  } catch (error) {
    if (tab) {
      tab.close();
    }
    showNotice(resolveApiMessage(error), true);
  } finally {
    opening.value = false;
  }
}

const unlinkingKey = ref("");

async function unlinkDb(db: LinkedDb) {
  unlinkingKey.value = db.engine + ":" + db.name;
  notice.value = "";
  try {
    await unlinkSiteDatabase(siteName.value, db.engine, db.name);
    showNotice(t("panel.siteDb.unlinked"), false);
    await load();
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    unlinkingKey.value = "";
  }
}

async function deleteDb(db: LinkedDb) {
  // Yüksek riskli onay: veritabanı adı yazılmadan silinemez.
  await confirm({
    title: t("panel.confirm.deleteDatabaseTitle", { name: db.name }),
    message: t("panel.siteDb.confirmDelete", { name: db.name }),
    impact: [`${t("panel.siteDb.dbName")}: ${db.name} (${db.engine})`, t("panel.confirm.impactDbUser")],
    requireText: db.name,
    action: async () => {
      deletingKey.value = db.engine + ":" + db.name;
      notice.value = "";
      try {
        // DeleteDatabaseAsync DB'yi + (başka yerde kullanılmıyorsa) kullanıcısını da düşürür + site bağını temizler.
        await deleteDatabase(db.engine, db.name);
        showNotice(t("panel.siteDb.deleted"), false);
        await load();
      } catch (error) {
        showNotice(resolveApiMessage(error), true);
      } finally {
        deletingKey.value = "";
      }
    },
  });
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
      password: form.password || undefined,
      // Bu site iş alanından oluşturulan DB doğrudan siteye BAĞLANIR → site silinince cascade gider.
      siteName: siteName.value || undefined
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
