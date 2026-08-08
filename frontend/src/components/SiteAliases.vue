<template>
  <section class="site-aliases">
    <div class="section-head">
      <div>
        <h4>{{ t("panel.alias.title") }}</h4>
        <p class="meta">{{ t("panel.alias.hint") }}</p>
      </div>
    </div>

    <p v-if="error" class="notice error" role="alert">{{ error }}</p>
    <p v-else-if="loading && !workspace" class="empty-state">{{ t("panel.common.loading") }}</p>

    <template v-else-if="workspace">
      <!-- ANA ALAN ADI OLMADAN yönlendirme yapılamaz; sebebini önden söylüyoruz ki kullanıcı
           takma ad ekleyip "neden yönlenmiyor" diye aramasın. -->
      <p v-if="!workspace.primaryHostName" class="notice warn" role="alert">
        {{ t("panel.alias.noPrimary") }}
      </p>
      <p v-else class="field-hint">
        {{ t("panel.alias.primaryIs", { host: workspace.primaryHostName }) }}
      </p>

      <div class="form-grid alias-form">
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.alias.hostName") }}</span>
          <input
            v-model.trim="form.hostName"
            :placeholder="t('panel.alias.hostPlaceholder')"
            spellcheck="false"
            @keyup.enter="add" />
        </label>
        <label class="checkbox-field">
          <input v-model="form.redirectToPrimary" type="checkbox" />
          <span>{{ t("panel.alias.redirect") }}</span>
        </label>
        <label class="checkbox-field">
          <input v-model="form.includeWww" type="checkbox" />
          <span>{{ t("panel.alias.includeWww") }}</span>
        </label>
        <button class="compact" :disabled="busy || !form.hostName" @click="add">
          {{ busy ? t("panel.common.saving") : t("panel.alias.add") }}
        </button>
      </div>
      <small class="field-hint">{{ t("panel.alias.redirectHint") }}</small>

      <p v-if="workspace.aliases.length === 0" class="empty-state">{{ t("panel.alias.empty") }}</p>

      <div v-else class="data-table">
        <div class="data-row table-head alias-row">
          <span>{{ t("panel.alias.hostName") }}</span>
          <span>{{ t("panel.alias.mode") }}</span>
          <span>{{ t("panel.alias.https") }}</span>
          <span>{{ t("panel.files.actions") }}</span>
        </div>
        <div v-for="alias in workspace.aliases" :key="alias.hostName" class="data-row alias-row">
          <span class="cell-main">
            <strong>{{ alias.hostName }}</strong>
            <small v-if="alias.includesWww">www.{{ alias.hostName }}</small>
          </span>
          <span :class="['status-pill', alias.redirectToPrimary ? 'ok' : '']">
            {{ alias.redirectToPrimary ? t("panel.alias.modeRedirect") : t("panel.alias.modeServe") }}
          </span>
          <!-- HTTPS yoksa bu bir EKSİKLİK, "kapalı" bir ayar değil: takma addan gelen ziyaretçi
               sertifika uyarısı alır. O yüzden uyarı rengiyle ve ne yapılacağıyla birlikte. -->
          <span
            :class="['status-pill', alias.hasHttps ? 'ok' : 'warn']"
            :title="alias.hasHttps ? undefined : t('panel.alias.noHttpsHint')">
            {{ alias.hasHttps ? t("panel.status.enabled") : t("panel.alias.noHttps") }}
          </span>
          <span class="row-actions">
            <button class="danger compact" :disabled="busy" @click="remove(alias.hostName)">
              {{ t("panel.alias.remove") }}
            </button>
          </span>
        </div>
      </div>
    </template>
  </section>
</template>

<script setup lang="ts">
import { onMounted, reactive, ref, watch } from "vue";
import { useI18n } from "vue-i18n";
import { addSiteAlias, getSiteAliases, removeSiteAlias, type SiteAliasWorkspace } from "../api/aliases";
import { extractApiDetails, extractApiMessage } from "../stores/auth";

const props = defineProps<{ siteName: string }>();
const { t } = useI18n();

const workspace = ref<SiteAliasWorkspace | null>(null);
const loading = ref(false);
const busy = ref(false);
const error = ref("");

const form = reactive({
  hostName: "",
  // Yönlendirme VARSAYILAN AÇIK: iki alan adından aynı içeriği sunmak arama motorlarında
  // yinelenen içerik sayılır ve ikisi de zayıflar.
  redirectToPrimary: true,
  includeWww: true
});

function resolveApiMessage(err: unknown) {
  const anahtar = extractApiMessage(err);
  const cevrilmis = t(anahtar);
  const ayrinti = extractApiDetails(err);
  return (cevrilmis === anahtar ? anahtar : cevrilmis) + (ayrinti ? `\n\n${ayrinti}` : "");
}

async function load() {
  if (!props.siteName) {
    return;
  }
  loading.value = true;
  error.value = "";
  try {
    workspace.value = await getSiteAliases(props.siteName);
  } catch (err) {
    error.value = resolveApiMessage(err);
  } finally {
    loading.value = false;
  }
}

async function add() {
  if (!form.hostName) {
    return;
  }
  busy.value = true;
  error.value = "";
  try {
    workspace.value = await addSiteAlias(props.siteName, { ...form });
    form.hostName = "";
  } catch (err) {
    error.value = resolveApiMessage(err);
  } finally {
    busy.value = false;
  }
}

async function remove(hostName: string) {
  busy.value = true;
  error.value = "";
  try {
    workspace.value = await removeSiteAlias(props.siteName, hostName);
  } catch (err) {
    error.value = resolveApiMessage(err);
  } finally {
    busy.value = false;
  }
}

watch(() => props.siteName, () => void load());
onMounted(() => void load());
</script>

<style scoped>
.site-aliases {
  display: grid;
  gap: 10px;
}

.alias-form {
  align-items: end;
}

/* .checkbox-field base.css'te (paylasilan sinif, paylasilan tanim). */

.alias-row {
  grid-template-columns: minmax(0, 1.6fr) 0.8fr 0.8fr auto;
  align-items: start;
}
</style>
