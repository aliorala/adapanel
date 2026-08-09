<template>
  <section class="panel module-shell">
    <div class="module-head">
      <div>
        <h2>{{ t("panel.ftp.title") }}</h2>
        <p class="meta">{{ t("panel.ftp.subtitle") }}</p>
      </div>
    </div>

    <div v-if="notice" :class="['notice', { error: noticeError }]" :role="noticeError ? 'alert' : 'status'">
      <p>{{ notice }}</p>
      <details v-if="noticeDetails" class="technical-details">
        <summary>{{ t("panel.common.technicalDetails") }}</summary>
        <pre>{{ noticeDetails }}</pre>
      </details>
    </div>

    <section class="module-toolbar ftp-toolbar">
      <div class="toolbar-group">
        <button class="compact" @click="toggleCreateForm">
          {{ showCreateForm ? t("panel.ftp.cancelCreate") : t("panel.ftp.addUser") }}
        </button>
        <span class="ftp-address">
          <small>{{ t("panel.ftp.address") }}</small>
          <strong>{{ ftpAddress }}</strong>
        </span>
        <span class="status-pill">{{ filteredUsers.length }} / {{ users.length }}</span>
        <span :class="['status-pill', serverStatus?.isConfigured ? 'ok' : 'warn']">
          {{ serverStateLabel }}
        </span>
      </div>

      <div class="toolbar-group ftp-filters">
        <input v-model="search" :aria-label="t('panel.ftp.search')" :placeholder="t('panel.ftp.searchPlaceholder')" />
        <button class="secondary compact" @click="refresh">
          {{ t("panel.ftp.refresh") }}
        </button>
      </div>
    </section>

    <div v-if="serverStatus" class="summary-strip ftp-summary">
      <div class="summary-item">
        <span>{{ t("panel.ftp.controlPort") }}</span>
        <strong>{{ serverStatus.controlPort }}</strong>
      </div>
      <div class="summary-item">
        <span>{{ t("panel.ftp.passivePorts") }}</span>
        <strong>{{ serverStatus.passivePortStart }}-{{ serverStatus.passivePortEnd }}</strong>
      </div>
      <div class="summary-item">
        <span>{{ t("panel.ftp.serverAddress") }}</span>
        <strong>{{ serverStatus.serverAddress }}</strong>
      </div>
    </div>

    <!-- FTPS anahtarı sunucudan okunmadan kapalı görünüyordu; TLS zorunluyken kapalı sanılıyordu. -->
    <label v-if="isAdmin && !siteName && tlsLoaded" class="form-surface ftp-tls-bar">
      <input type="checkbox" :checked="requireSsl" :disabled="tlsSaving" @change="toggleTls(($event.target as HTMLInputElement).checked)" />
      <span class="ftp-tls-text">
        <strong>{{ t("panel.ftp.requireTls") }}</strong>
        <small>{{ t("panel.ftp.requireTlsHint") }}</small>
      </span>
    </label>

    <section v-if="showCreateForm" class="form-surface ftp-create">
      <div class="section-head compact-head">
        <div>
          <h3>{{ t("panel.ftp.createTitle") }}</h3>
          <p class="meta">{{ t("panel.ftp.createSubtitle") }}</p>
        </div>
      </div>

      <div class="form-grid ftp-form-grid">
        <label>
          <span>{{ t("panel.database.userName") }}</span>
          <input v-model="form.userName" />
        </label>
        <label>
          <span>{{ t("panel.form.password") }}</span>
          <input v-model="form.password" type="password" />
        </label>
        <label v-if="!siteName">
          <span>{{ t("panel.php.targetSite") }}</span>
          <select v-model="form.siteName">
            <option value="">{{ t("panel.php.selectSite") }}</option>
            <option v-for="site in sitesStore.sites" :key="site.name" :value="site.name">
              {{ site.name }}
            </option>
          </select>
        </label>
        <label>
          <span>{{ t("panel.ftp.homePath") }}</span>
          <input v-model="form.homePath" />
        </label>
      </div>

      <div class="row-actions">
        <button class="compact" :disabled="creating" @click="createUser">
          {{ creating ? t("panel.database.saving") : t("panel.ftp.createAction") }}
        </button>
        <button class="secondary compact" @click="showCreateForm = false">
          {{ t("panel.ftp.cancelCreate") }}
        </button>
      </div>
    </section>

    <section class="data-table">
      <div class="data-row table-head ftp-row">
        <span>{{ t("panel.database.userName") }}</span>
        <span>{{ t("panel.sites.name") }}</span>
        <span>{{ t("panel.ftp.homePath") }}</span>
        <span>{{ t("panel.users.status") }}</span>
        <span>{{ t("panel.files.actions") }}</span>
      </div>

      <template v-if="filteredUsers.length">
        <div v-for="user in pager.items.value" :key="user.userName" class="data-row ftp-row">
          <span class="cell-main">
            <strong>{{ user.userName }}</strong>
            <small>{{ formatCreatedAt(user.createdAtUtc) }}</small>
          </span>
          <span>{{ user.siteName || "-" }}</span>
          <span class="cell-muted">{{ user.homePath }}</span>
          <span :class="['status-pill', user.isEnabled && user.existsOnSystem ? 'ok' : 'warn']">
            {{ user.isEnabled && user.existsOnSystem ? t("panel.status.active") : t("panel.status.inactive") }}
          </span>
          <span class="row-actions">
            <button class="secondary compact" :disabled="changingPassword === user.userName" @click="changeFtpPassword(user.userName)">
              {{ changingPassword === user.userName ? t("panel.database.saving") : t("panel.database.changePassword") }}
            </button>
            <button class="danger compact" :disabled="deletingUser === user.userName" @click="removeUser(user.userName)">
              {{ deletingUser === user.userName ? t("panel.database.removing") : t("panel.sites.delete") }}
            </button>
          </span>
        </div>
      </template>

      <!-- Liste sunucudan gelmeden "kullanıcı yok" yazıyordu; var olan FTP hesapları
           silinmiş/yok sanılabiliyordu. -->
      <p v-else-if="!usersLoaded" class="empty-state" role="status" aria-live="polite">
        {{ t("panel.common.loading") }}
      </p>

      <p v-else class="empty-state">
        {{ t("panel.ftp.noUsers") }}
      </p>

      <TablePager :state="pager" />
    </section>
  </section>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from "vue";
import { useI18n } from "vue-i18n";
import { createFtpUser, deleteFtpUser, ensureFtpServer, getFtpTls, getFtpUsers, setFtpPassword, setFtpTls, type FtpServerStatus, type FtpUser } from "../api/ftp";
import { extractApiDetails, extractApiMessage, useAuthStore } from "../stores/auth";
import { useSitesStore } from "../stores/sites";
import { useConfirm } from "../components/ConfirmDialog.vue";
import { usePrompt } from "../components/PromptDialog.vue";
import TablePager from "../components/TablePager.vue";
import { usePagination } from "../composables/usePagination";

// siteName verilince (web sitesi çalışma alanı sekmesi) FTP yalnızca o siteye
// göre filtrelenir ve yeni kullanıcı o siteye sabitlenir (profesyonel paneller gibi site-bağlamı).
const props = defineProps<{ siteName?: string }>();

const { t } = useI18n();
const { prompt } = usePrompt();
const { confirm } = useConfirm();
const sitesStore = useSitesStore();
const authStore = useAuthStore();
const isAdmin = computed(() => authStore.session?.user?.role === "Administrator");

const users = ref<FtpUser[]>([]);
// Yüklenmemiş durumu "kayıt yok" / "kapalı" göstermek yanıltıcıydı.
const usersLoaded = ref(false);
const tlsLoaded = ref(false);
const serverStatus = ref<FtpServerStatus | null>(null);
const serverCheckCompleted = ref(false);
const creating = ref(false);
const deletingUser = ref("");
const changingPassword = ref("");
const notice = ref("");
const noticeDetails = ref("");
const noticeError = ref(false);
const search = ref("");
const showCreateForm = ref(false);
// M5: FTPS zorunlu mu (yalnız admin + standalone FTP sayfasında gösterilir).
const requireSsl = ref(false);
const tlsSaving = ref(false);

const form = reactive({
  userName: "",
  password: "",
  siteName: "",
  homePath: ""
});

// Site bağlamında "Kullanıcı ekle"ye basınca, profesyonel paneller gibi geçerli bir kullanıcı adı önerir
// (<site>_ftp, Windows hesap sınırı 20 karaktere sığdırılmış). Kullanıcı düzenleyebilir.
function suggestedFtpUserName(site: string): string {
  let base = site.toLowerCase().replace(/[^a-z0-9]/g, "_").replace(/^_+|_+$/g, "");
  if (!base) {
    base = "site";
  }
  if (base.length > 16) {
    base = base.slice(0, 16);
  }
  return `${base}_ftp`;
}

function toggleCreateForm() {
  showCreateForm.value = !showCreateForm.value;
  if (showCreateForm.value && props.siteName && !form.userName) {
    form.userName = suggestedFtpUserName(props.siteName);
  }
}

const ftpAddress = computed(() => {
  const host = serverStatus.value?.serverAddress || window.location.hostname || "localhost";
  return `ftp://${host}:${serverStatus.value?.controlPort ?? 21}`;
});
const serverStateLabel = computed(() => {
  if (serverStatus.value?.isConfigured) {
    return t("panel.ftp.serverReady");
  }

  return serverCheckCompleted.value ? t("panel.ftp.serverUnavailable") : t("panel.ftp.serverChecking");
});
const filteredUsers = computed(() => {
  const scoped = props.siteName
    ? users.value.filter((user) => (user.siteName ?? "").toLowerCase() === props.siteName!.toLowerCase())
    : users.value;

  const query = search.value.trim().toLowerCase();
  if (!query) {
    return scoped;
  }

  return scoped.filter((user) =>
    [user.userName, user.siteName, user.homePath]
      .some((value) => (value ?? "").toLowerCase().includes(query)));
});

// Sayfalama ARAMADAN SONRA gelir (bkz. composables/usePagination.ts).
const pager = usePagination(filteredUsers);
// Arama daralınca 3. sayfada kalıp boş tabloya düşülmesin.
watch(search, () => pager.reset());

watch(
  () => props.siteName,
  (value) => {
    if (value) {
      form.siteName = value;
    }
  },
  { immediate: true },
);

onMounted(async () => {
  if (sitesStore.sites.length === 0 && !sitesStore.loading) {
    await sitesStore.fetchSites().catch(() => undefined);
  }

  await Promise.allSettled([prepareServer(), refresh(), loadTls()]);
});

async function loadTls() {
  if (!isAdmin.value || props.siteName) {
    return;
  }
  try {
    requireSsl.value = (await getFtpTls()).requireSsl;
    tlsLoaded.value = true;
  } catch {
    // erişim yoksa toggle gizli kalır
  }
}

async function toggleTls(value: boolean) {
  tlsSaving.value = true;
  try {
    requireSsl.value = (await setFtpTls(value)).requireSsl;
    showNotice(t(value ? "panel.ftp.tlsRequired" : "panel.ftp.tlsOptional"), false);
  } catch (error) {
    showApiError(error);
  } finally {
    tlsSaving.value = false;
  }
}

async function prepareServer() {
  try {
    serverStatus.value = await ensureFtpServer();
  } catch (error) {
    showApiError(error);
  } finally {
    serverCheckCompleted.value = true;
  }
}

async function refresh() {
  try {
    users.value = await getFtpUsers();
    usersLoaded.value = true;
  } catch (error) {
    showApiError(error);
  }
}

async function createUser() {
  creating.value = true;

  try {
    await createFtpUser({
      userName: form.userName,
      password: form.password,
      siteName: form.siteName,
      homePath: form.homePath
    });
    form.userName = "";
    form.password = "";
    form.homePath = "";
    showCreateForm.value = false;
    await refresh();
    showNotice(t("panel.ftp.created"), false);
  } catch (error) {
    showApiError(error);
  } finally {
    creating.value = false;
  }
}

async function changeFtpPassword(userName: string) {
  // window.prompt DEĞİL: parola açıkta görünüyordu ve diyaloglar engellenirse akış ölüyordu.
  const password = await prompt({
    title: t("panel.ftp.newPasswordPrompt", { user: userName }),
    label: t("panel.form.password"),
    password: true,
    minLength: 8,
    confirmLabel: t("panel.ftp.changePassword")
  });

  if (password === null) {
    return;
  }
  if (password.length < 8) {
    showNotice(t("panel.ftp.passwordTooShort"), true);
    return;
  }
  changingPassword.value = userName;
  try {
    await setFtpPassword(userName, password);
    showNotice(t("panel.ftp.passwordChanged"), false);
  } catch (error) {
    showApiError(error);
  } finally {
    changingPassword.value = "";
  }
}

/**
 * Silme ONAYSIZ yapılmaz: FTP hesabı Windows kullanıcısıyla birlikte gider ve geri alınamaz.
 * (Parola değiştirmede zaten PromptDialog kullanılıyordu; silmede onay atlanmıştı.)
 */
async function removeUser(userName: string) {
  await confirm({
    title: t("panel.ftp.confirmDeleteTitle"),
    message: t("panel.ftp.confirmDeleteMessage", { name: userName }),
    impact: [userName],
    requireText: userName,
    action: async () => {
      deletingUser.value = userName;
      try {
        await deleteFtpUser(userName);
        await refresh();
        showNotice(t("panel.ftp.deleted"), false);
      } catch (error) {
        showApiError(error);
      } finally {
        deletingUser.value = "";
      }
    }
  });
}

function showNotice(message: string, isError: boolean) {
  notice.value = message;
  noticeDetails.value = "";
  noticeError.value = isError;
}

function showApiError(error: unknown) {
  const key = extractApiMessage(error);
  const translated = t(key);
  notice.value = translated === key ? key : translated;
  noticeDetails.value = extractApiDetails(error);
  noticeError.value = true;
}

function formatCreatedAt(value: string) {
  if (!value) {
    return "-";
  }

  return new Intl.DateTimeFormat(undefined, {
    dateStyle: "short",
    timeStyle: "short"
  }).format(new Date(value));
}
</script>

<style scoped>
.section-head {
  display: flex;
  justify-content: space-between;
  gap: 12px;
  align-items: flex-start;
}

.compact-head {
  align-items: center;
}

.ftp-address {
  display: grid;
  gap: 1px;
  padding: 0 4px;
}

.ftp-address small {
  color: var(--text-muted);
}

.ftp-filters input {
  width: min(320px, 52vw);
}

.ftp-form-grid {
  grid-template-columns: repeat(4, minmax(0, 1fr));
}

.ftp-row {
  grid-template-columns: minmax(150px, 0.85fr) minmax(130px, 0.75fr) minmax(240px, 1.45fr) 0.65fr auto;
}

.ftp-summary {
  grid-template-columns: repeat(3, minmax(0, 1fr));
}

.ftp-tls-bar {
  display: flex;
  gap: 12px;
  align-items: flex-start;
  margin-top: 12px;
  cursor: pointer;
}

.ftp-tls-bar input {
  width: auto;
  margin-top: 3px;
}

.ftp-tls-text {
  display: grid;
  gap: 2px;
}

.ftp-tls-text small {
  color: var(--text-muted);
}

.meta {
  margin: 0;
  color: var(--text-muted);
}

.notice p {
  margin: 0;
}

.technical-details {
  margin-top: 8px;
}

.technical-details summary {
  width: fit-content;
  cursor: pointer;
  font-weight: 700;
}

.technical-details pre {
  max-height: 260px;
  margin: 8px 0 0;
  padding: 10px;
  overflow: auto;
  border-radius: var(--radius-sm);
  background: var(--input-bg);
  color: inherit;
  font: 0.78rem/1.45 "Cascadia Code", "Consolas", monospace;
  white-space: pre-wrap;
}

@media (max-width: 900px) {
  .ftp-form-grid {
    grid-template-columns: 1fr;
  }

  .ftp-filters {
    width: 100%;
  }

  .ftp-filters input {
    flex: 1;
    width: auto;
    min-width: 180px;
  }
}
</style>
