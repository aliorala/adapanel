<template>
  <section class="panel module-shell ai-workspace">
    <div class="module-head">
      <div>
        <h2>{{ t("panel.aiConnection.title") }}</h2>
        <p class="meta">{{ t("panel.aiConnection.subtitle") }}</p>
      </div>
      <button class="secondary compact" :disabled="loading" @click="refresh">
        {{ t("panel.aiConnection.refresh") }}
      </button>
    </div>

    <p v-if="notice" :class="['notice', { error: noticeError }]">{{ notice }}</p>

    <!-- (a) Bağlantı bilgisi: uç nokta adresi TARAYICIDAN türetilir; panel her sunucuda
         farklı alan adı/port ile çalıştığı için hiçbir adres koda gömülmez. -->
    <section class="form-surface">
      <div class="section-head">
        <div>
          <h3>{{ t("panel.aiConnection.endpointTitle") }}</h3>
          <p class="meta">{{ t("panel.aiConnection.endpointHint") }}</p>
        </div>
      </div>
      <div class="stacked-field">
        <span class="field-label">{{ t("panel.aiConnection.endpointLabel") }}</span>
        <CodeSample
          :code="mcpEndpoint"
          :copy-label="t('panel.aiConnection.copy')"
          :copied-label="t('panel.aiConnection.copied')" />
      </div>
    </section>

    <!-- (d) Anahtar değeri YALNIZCA oluşturma yanıtında gelir; bellekte tutulur, sayfa
         yenilenince kaybolur. Hiçbir yerde saklanmaz. -->
    <section v-if="createdValue" class="form-surface created-token">
      <div class="section-head">
        <div>
          <h3>{{ t("panel.aiConnection.newTokenTitle") }}</h3>
          <p class="meta">{{ createdName }}</p>
        </div>
        <span class="status-pill warn">{{ t("panel.aiConnection.newTokenOnce") }}</span>
      </div>
      <CodeSample
        :code="createdValue"
        :copy-label="t('panel.aiConnection.copy')"
        :copied-label="t('panel.aiConnection.copied')" />
      <p class="notice error">{{ t("panel.aiConnection.newTokenWarning") }}</p>
      <div class="row-actions">
        <button class="secondary compact" @click="dismissCreatedValue">
          {{ t("panel.aiConnection.newTokenHide") }}
        </button>
      </div>
    </section>

    <!-- (c) Yeni anahtar formu -->
    <section class="form-surface">
      <div class="section-head">
        <div>
          <h3>{{ t("panel.aiConnection.formTitle") }}</h3>
          <p class="meta">{{ t("panel.aiConnection.formHint") }}</p>
        </div>
        <button class="secondary compact" @click="showForm = !showForm">
          {{ showForm ? t("panel.common.cancel") : t("panel.aiConnection.newToken") }}
        </button>
      </div>

      <template v-if="showForm">
        <div class="form-grid">
          <label class="stacked-field">
            <span class="field-label">{{ t("panel.aiConnection.formName") }}</span>
            <input
              v-model="form.name"
              maxlength="128"
              :placeholder="t('panel.aiConnection.formNamePlaceholder')" />
          </label>
          <label class="stacked-field">
            <span class="field-label">{{ t("panel.aiConnection.formExpires") }}</span>
            <input v-model.number="form.expiresInDays" type="number" min="0" max="3650" />
            <span class="field-hint">{{ t("panel.aiConnection.formExpiresHint") }}</span>
          </label>
        </div>

        <label class="check-field">
          <input v-model="form.allowDestructive" type="checkbox" />
          <span>{{ t("panel.aiConnection.formDestructive") }}</span>
        </label>
        <p class="field-hint">{{ t("panel.aiConnection.formDestructiveHint") }}</p>

        <!-- Terminal izni = paneli değil SUNUCUYU vermek. Sunucu tarafı da müşteri/bayi
             isteğinde bu bayrağı sessizce kapatır; arayüzde de yalnız yöneticiye gösterilir. -->
        <template v-if="isAdmin">
          <label class="check-field">
            <input v-model="form.allowTerminal" type="checkbox" />
            <span>{{ t("panel.aiConnection.formTerminal") }}</span>
          </label>
          <p class="field-hint">{{ t("panel.aiConnection.formTerminalHint") }}</p>
        </template>

        <div class="row-actions">
          <button class="compact" :disabled="creating" @click="create">
            {{ creating ? t("panel.aiConnection.creating") : t("panel.aiConnection.create") }}
          </button>
        </div>
      </template>
    </section>

    <!-- (b) Anahtar listesi -->
    <section class="module-shell">
      <div class="section-head">
        <div>
          <h3>{{ t("panel.aiConnection.tokensTitle") }}</h3>
          <p class="meta">{{ t("panel.aiConnection.tokensHint") }}</p>
        </div>
      </div>

      <div class="data-table">
        <div class="data-row table-head token-row">
          <span>{{ t("panel.aiConnection.colName") }}</span>
          <span>{{ t("panel.aiConnection.colCreated") }}</span>
          <span>{{ t("panel.aiConnection.colLastUsed") }}</span>
          <span>{{ t("panel.aiConnection.colPermissions") }}</span>
          <span>{{ t("panel.aiConnection.colStatus") }}</span>
          <span>{{ t("panel.aiConnection.colAction") }}</span>
        </div>

        <div v-for="token in tokens" :key="token.id" class="data-row token-row">
          <div class="cell-main">
            <strong>{{ token.name }}</strong>
            <small>{{ t("panel.aiConnection.suffixLabel", { suffix: token.suffix }) }}</small>
          </div>
          <span class="cell-muted">{{ formatDate(token.createdAtUtc) }}</span>
          <span class="cell-muted">
            {{ token.lastUsedAtUtc ? formatDate(token.lastUsedAtUtc) : t("panel.aiConnection.neverUsed") }}
          </span>
          <span class="permission-tags">
            <span v-if="token.allowTerminal" class="status-pill danger">{{ t("panel.aiConnection.permTerminal") }}</span>
            <span v-if="token.allowDestructive" class="status-pill warn">{{ t("panel.aiConnection.permDestructive") }}</span>
            <span v-if="!token.allowTerminal && !token.allowDestructive" class="status-pill ok">
              {{ t("panel.aiConnection.permReadOnly") }}
            </span>
          </span>
          <div class="cell-main">
            <span :class="['status-pill', statusTone(token)]">{{ t(statusLabelKey(token)) }}</span>
            <small>{{ token.expiresAtUtc ? formatDate(token.expiresAtUtc) : t("panel.aiConnection.neverExpires") }}</small>
          </div>
          <button
            class="danger compact row-button"
            :disabled="token.isRevoked || busyTokenId === token.id"
            @click="revoke(token)">
            {{ t("panel.aiConnection.revoke") }}
          </button>
        </div>

        <!-- Veri gelmeden "kayıt yok" gösterilmez: kullanıcı anahtarı yok sanıp ikinci bir
             anahtar üretmesin. -->
        <p v-if="!loaded" class="empty-state" role="status" aria-live="polite">
          {{ t("panel.common.loading") }}
        </p>
        <p v-else-if="tokens.length === 0" class="empty-state">{{ t("panel.aiConnection.empty") }}</p>
      </div>
    </section>

    <!-- (e) Kurulum yönergesi -->
    <section class="form-surface">
      <div class="section-head">
        <div>
          <h3>{{ t("panel.aiConnection.setupTitle") }}</h3>
          <p class="meta">{{ t("panel.aiConnection.setupHint") }}</p>
        </div>
      </div>

      <ol class="setup-steps">
        <li>{{ t("panel.aiConnection.setupStep1") }}</li>
        <li>{{ t("panel.aiConnection.setupStep2") }}</li>
        <li>
          {{ t("panel.aiConnection.setupStep3") }}
          <CodeSample
            :code="mcpEndpoint"
            :copy-label="t('panel.aiConnection.copy')"
            :copied-label="t('panel.aiConnection.copied')" />
        </li>
        <li>
          {{ t("panel.aiConnection.setupStep4") }}
          <CodeSample
            :code="authorizationHeaderSample"
            :copy-label="t('panel.aiConnection.copy')"
            :copied-label="t('panel.aiConnection.copied')" />
        </li>
        <li>{{ t("panel.aiConnection.setupStep5") }}</li>
      </ol>

      <div class="stacked-field">
        <span class="field-label">{{ t("panel.aiConnection.setupCliLabel") }}</span>
        <CodeSample
          :code="cliCommandSample"
          :copy-label="t('panel.aiConnection.copy')"
          :copied-label="t('panel.aiConnection.copied')" />
      </div>

      <p class="field-hint">{{ t("panel.aiConnection.setupNote") }}</p>
    </section>
  </section>
</template>

<script setup lang="ts">
// "Yapay zeka bağlantısı": kullanıcı kendi Claude/ChatGPT hesabını panele bağlar (Plesk'in
// MCP Server eklentisinin muadili). Anahtarın yetkisi sahibinin rolünden türer; bu ekran
// yalnızca oturum açan kullanıcının KENDİ anahtarlarını yönetir.
import { computed, onMounted, reactive, ref } from "vue";
import { useI18n } from "vue-i18n";
import CodeSample from "../components/CodeSample.vue";
import { useConfirm } from "../components/ConfirmDialog.vue";
import {
  createApiToken,
  getApiTokens,
  revokeApiToken,
  type ApiToken
} from "../api/apiTokens";
import { extractApiDetails, extractApiMessage, useAuthStore } from "../stores/auth";

const { t } = useI18n();
const { confirm } = useConfirm();
const authStore = useAuthStore();

const tokens = ref<ApiToken[]>([]);
// Liste okunmadan "kayıt yok" gösterilmesin diye ayrı bayrak.
const loaded = ref(false);
const loading = ref(false);
const creating = ref(false);
const busyTokenId = ref("");
const showForm = ref(false);
const notice = ref("");
const noticeError = ref(false);

// Yalnızca oluşturma yanıtında dönen düz metin değer. Kalıcı hiçbir yere yazılmaz;
// sayfa yenilenince kaybolur (istenen davranış).
const createdValue = ref("");
const createdName = ref("");

const form = reactive({
  name: "",
  allowDestructive: false,
  allowTerminal: false,
  expiresInDays: 0
});

const isAdmin = computed(() => authStore.session?.user?.role === "Administrator");

// Adres tarayıcıdaki mevcut kökten üretilir — panel farklı sunuculara/portlara kurulduğu
// için sabit bir alan adı YAZILMAZ.
const mcpEndpoint = computed(() => `${window.location.origin}/mcp`);
const authorizationHeaderSample = computed(() => `Authorization: Bearer ${t("panel.aiConnection.tokenPlaceholder")}`);
const cliCommandSample = computed(
  () => `claude mcp add --transport http adapanel ${mcpEndpoint.value} --header "Authorization: Bearer ${t("panel.aiConnection.tokenPlaceholder")}"`);

onMounted(refresh);

async function refresh() {
  loading.value = true;
  try {
    tokens.value = await getApiTokens();
    loaded.value = true;
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    loading.value = false;
  }
}

async function create() {
  const name = form.name.trim();
  if (name.length < 2) {
    showNotice(t("panel.aiConnection.nameRequired"), true);
    return;
  }

  creating.value = true;
  try {
    const created = await createApiToken({
      name,
      allowDestructive: form.allowDestructive,
      // Yönetici olmayan rolde kutu görünmez; yine de istekte açık gitmesin.
      allowTerminal: isAdmin.value && form.allowTerminal,
      expiresInDays: form.expiresInDays || 0
    });

    createdValue.value = created.value;
    createdName.value = created.token.name;
    form.name = "";
    form.allowDestructive = false;
    form.allowTerminal = false;
    form.expiresInDays = 0;
    showForm.value = false;
    await refresh();
    showNotice(t("panel.aiConnection.created"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    creating.value = false;
  }
}

async function revoke(token: ApiToken) {
  const confirmed = await confirm({
    title: t("panel.aiConnection.revokeTitle"),
    message: t("panel.aiConnection.revokeMessage", { name: token.name }),
    impact: [
      t("panel.aiConnection.revokeImpactClient"),
      t("panel.aiConnection.revokeImpactPermanent")
    ],
    confirmLabel: t("panel.aiConnection.revoke"),
    busyLabel: t("panel.aiConnection.revoking")
  });

  if (!confirmed) {
    return;
  }

  busyTokenId.value = token.id;
  try {
    await revokeApiToken(token.id);
    await refresh();
    showNotice(t("panel.aiConnection.revoked"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    busyTokenId.value = "";
  }
}

function dismissCreatedValue() {
  createdValue.value = "";
  createdName.value = "";
}

function statusTone(token: ApiToken) {
  if (token.isRevoked) return "danger";
  return token.isUsable ? "ok" : "warn";
}

function statusLabelKey(token: ApiToken) {
  if (token.isRevoked) return "panel.aiConnection.statusRevoked";
  return token.isUsable ? "panel.aiConnection.statusActive" : "panel.aiConnection.statusExpired";
}

function formatDate(value: string) {
  return new Date(value).toLocaleString();
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
.ai-workspace {
  align-content: start;
}

.section-head {
  display: flex;
  justify-content: space-between;
  gap: 10px;
  align-items: flex-start;
  flex-wrap: wrap;
}

.token-row {
  grid-template-columns: minmax(200px, 1.4fr) 1fr 1fr minmax(150px, 1fr) minmax(130px, 0.9fr) auto;
}

.permission-tags {
  display: flex;
  flex-wrap: wrap;
  gap: 5px;
}

.created-token {
  border-color: color-mix(in srgb, var(--warn-1) 45%, transparent);
}

.row-button {
  width: auto;
}

.row-actions button {
  width: auto;
}

.setup-steps {
  display: grid;
  gap: 9px;
  margin: 0;
  padding-left: 20px;
  color: var(--text-soft);
  line-height: 1.55;
}

.setup-steps li {
  display: grid;
  gap: 7px;
}

@media (max-width: 760px) {
  .form-grid {
    grid-template-columns: 1fr;
  }
}
</style>
