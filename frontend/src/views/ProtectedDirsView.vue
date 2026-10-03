<template>
  <section class="panel protected-view">
    <div class="pd-header">
      <div>
        <p class="section-kicker">{{ t("panel.sites.tools.protected") }}</p>
        <h2>{{ t("panel.protected.title") }}</h2>
        <p class="meta">{{ siteName || t("panel.php.selectSite") }}</p>
      </div>
      <button class="secondary compact" :disabled="loading || busy || !!removing" @click="refresh()">{{ t("panel.common.refresh") }}</button>
    </div>

    <p v-if="notice" :class="['notice', 'page-notice', { error: noticeError }]" :role="noticeError ? 'alert' : 'status'">{{ notice }}</p>

    <div v-if="siteName" class="form-surface pd-create">
      <h3>{{ t("panel.protected.createTitle") }}</h3>
      <p class="meta">{{ t("panel.protected.hint") }}</p>
      <div class="pd-grid">
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.protected.path") }}</span>
          <input v-model.trim="form.relativePath" :disabled="busy" :placeholder="t('panel.protected.pathPlaceholder')" />
        </label>
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.protected.user") }}</span>
          <input v-model.trim="form.userName" :disabled="busy" autocomplete="off" />
        </label>
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.protected.password") }}</span>
          <PasswordField v-model="form.password" :disabled="busy" autocomplete="new-password" />
        </label>
      </div>
      <button class="compact" v-bind="writeGate(busy || loading || !!removing || !form.relativePath || !form.userName || !form.password)" @click="create">
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

            <!--
              KAYIT VAR DEMEK KORUMA ÇALIŞIYOR DEMEK DEĞİL.

              Doğrulamayı AdaPanel'in IIS modülü yapıyor; modül kayıtlı değilse kayıt durur ama
              hiçbir isteği durdurmaz. Bunu göstermemek, korunmayan bir klasörü korunuyor diye
              sunmak olurdu — bu depoda en pahalı hata sınıfı budur.
            -->
            <span v-if="!item.isActive" class="status-pill warn" role="alert">
              {{ t("protected.engine_not_ready") }}
            </span>

            <!-- Eski sürümden kalan düz metin parola: dönüşüm otomatik ama modül kaydı gerekiyor. -->
            <span v-else-if="item.isLegacyPlaintext" class="status-pill warn">
              {{ t("panel.protected.legacyPlaintext") }}
            </span>
          </div>
          <button class="danger compact" v-bind="writeGate(!!removing || busy || loading)" @click="remove(item)">
            {{ t("panel.protected.remove") }}
          </button>
        </article>
      </div>
      <!-- Liste gelmeden "koruma yok" yazıyordu; korumalı dizinler korumasız sanılabiliyordu. -->
      <p v-else-if="loading" class="empty-state" role="status" aria-live="polite">
        {{ t("panel.common.loading") }}
      </p>
      <p v-else-if="listLoaded" class="empty-state">{{ t("panel.protected.empty") }}</p>
    </div>
  </section>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, reactive, ref, watch } from "vue";
import { useI18n } from "vue-i18n";
import PasswordField from "../components/PasswordField.vue";
import { useSitesStore } from "../stores/sites";
import { getProtectedDirs, protectDir, unprotectDir, type ProtectedDirectory } from "../api/sites";
import { useConfirm } from "../components/ConfirmDialog.vue";
import { useNotice } from "../composables/useNotice";
import { useSubscriptionGate } from "../composables/useSubscriptionGate";
import { getSessionEpoch } from "../api/client";

const { t } = useI18n();
// Askidaki abonelikte YAZMA dugmeleri bastan devre disi + sebebi title/aria ile soylenir
// (bkz. composables/useSubscriptionGate.ts). Okuma dugmeleri kapiya girmez.
const { writeGate } = useSubscriptionGate();
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

let siteGeneration = 0, readRequest = 0;
let disposed = false;
onBeforeUnmount(() => { disposed = true; form.password = ""; });
function context() {
  const site = siteName.value, generation = siteGeneration, epoch = getSessionEpoch();
  return { site, current: () => !disposed && siteName.value === site && generation === siteGeneration && getSessionEpoch() === epoch };
}
watch(siteName, () => {
  ++siteGeneration;
  Object.assign(form, { relativePath: "", userName: "", password: "" });
  notice.value = "";
  void refresh(true);
}, { immediate: true });

async function refresh(force = false) {
  if (disposed || (!force && (busy.value || removing.value || loading.value))) return;
  const request = ++readRequest, target = context();
  items.value = [];
  listLoaded.value = false;
  if (!siteName.value) {
    loading.value = false;
    return;
  }
  loading.value = true;
  try {
    const result = await getProtectedDirs(target.site);
    if (!target.current() || request !== readRequest) return;
    items.value = result;
    listLoaded.value = true;
  } catch (error) {
    if (target.current() && request === readRequest) showNotice(resolveApiMessage(error), true);
  } finally {
    if (target.current() && request === readRequest) loading.value = false;
  }
}

async function create() {
  if (disposed || !siteName.value || busy.value || removing.value || loading.value || !form.relativePath || !form.userName || !form.password) {
    return;
  }
  busy.value = true;
  const target = context();
  try {
    await protectDir(target.site, { ...form });
    if (!target.current()) return;
    form.relativePath = "";
    form.userName = "";
    form.password = "";
    showNotice(t("panel.protected.created"), false);
    await refresh(true);
  } catch (error) {
    if (target.current()) showNotice(resolveApiMessage(error), true);
  } finally {
    busy.value = false;
  }
}

/**
 * Korumayı kaldırmak dizini HERKESE AÇIK hâle getirir — sessiz bir güvenlik kaybı.
 * Onay metni hangi dizinin açılacağını açıkça söyler.
 */
async function remove(item: ProtectedDirectory) {
  if (disposed || !siteName.value || busy.value || removing.value || loading.value) return;
  const target = context(), path = item.relativePath;
  await confirm({
    title: t("panel.protected.confirmRemoveTitle"),
    message: t("panel.protected.confirmRemoveMessage", { path: `/${item.relativePath}` }),
    impact: [`/${item.relativePath}`, `${t("panel.protected.user")}: ${item.userName}`],
    confirmLabel: t("panel.protected.remove"),
    action: async () => {
      if (!target.current() || busy.value || removing.value || loading.value) return;
      removing.value = path;
      try {
        await unprotectDir(target.site, path);
        if (!target.current()) return;
        showNotice(t("panel.protected.removed"), false);
        await refresh(true);
      } catch (error) {
        if (target.current()) showNotice(resolveApiMessage(error), true);
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
  /* 0.74rem = 9.99px idi — olcek tabani `--fs-2xs` (10.53px); altina inen kural yok. */
  font-size: var(--fs-2xs);
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
