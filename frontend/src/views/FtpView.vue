<template>
  <section class="panel module-shell">
    <div class="module-head">
      <div>
        <h2>{{ t("panel.ftp.title") }}</h2>
        <p class="meta">{{ t("panel.ftp.subtitle") }}</p>
      </div>
    </div>

    <!-- Sonuç ekrana SABİTLENİR (`page-notice`, bkz. styles/base.css): kullanıcı listesi uzadıkça
         sayfanın altındaki "Parola değiştir"/"Sil" düğmelerine basan yönetici, en tepedeki sonuç
         şeridini görmüyordu. -->
    <div v-if="notice" :class="['notice', 'page-notice', { error: noticeError }]" :role="noticeError ? 'alert' : 'status'">
      <p>{{ notice }}</p>
      <details v-if="noticeDetails" class="technical-details">
        <summary>{{ t("panel.common.technicalDetails") }}</summary>
        <pre>{{ noticeDetails }}</pre>
      </details>
    </div>

    <section class="module-toolbar ftp-toolbar">
      <div class="toolbar-group">
        <!-- Form ACIKKEN bu dugme "Vazgec" olur; birincil mavi kalirsa ekrandaki EN BASKIN dugme
             "Vazgec" olurdu (asagidaki "Kullaniciyi olustur" ile ayni agirlikta). Tek birincil
             eylem kurali: form acikken birincil olan sey formu KAYDETMEKTIR. -->
        <button :class="['compact', { secondary: showCreateForm }]" v-bind="writeGate()" @click="toggleCreateForm">
          {{ showCreateForm ? t("panel.ftp.cancelCreate") : t("panel.ftp.addUser") }}
        </button>
        <span class="ftp-address">
          <small>{{ t("panel.ftp.address") }}</small>
          <strong>{{ ftpAddress }}</strong>
        </span>
        <span class="status-pill">{{ filteredUsers.length }} / {{ users.length }}</span>
        <span v-if="isAdmin" :class="['status-pill', serverStatus?.isConfigured ? 'ok' : 'warn']">
          {{ serverStateLabel }}
        </span>
      </div>

      <div class="toolbar-group ftp-filters">
        <input v-model="search" :aria-label="t('panel.ftp.search')" :placeholder="t('panel.ftp.searchPlaceholder')" />
        <button class="secondary compact" @click="refreshAll">
          {{ t("panel.ftp.refresh") }}
        </button>
      </div>
    </section>

    <!-- SUNUCU DURUMU — bu ekranin en pahali sessizligi.
         19.08.2026 bu makinede OLCULDU: 21 portunu FileZilla Server tutuyordu, panelin actigi
         kullanicilar dogru parolayla bile 530 aliyordu; ekran ise SADECE bir rozetle "hazir degil"
         diyordu. Yonetici "kullanici var ama giremiyorum" ile bas basa kaliyordu.
         Artik SEBEP yazili: sunucunun dondugu i18n anahtari (ftp.control_port_conflict, …) ve
         cakismada portu tutan surecin ADI. Kosul `isConfigured` DEGIL, `statusMessage`:
         olcum yapilamadiginda (`ftp.server_status_unknown`) `isConfigured` eski altyapi bayragina
         duser ve true kalabilir -- "olculemedi"yi sessizce "sorun yok" saymak bu ekranin ta
         kendisiydi. -->
    <div
      v-if="serverStatus && serverStatus.statusMessage !== 'ftp.server_ready'"
      class="notice warn-banner ftp-status"
      role="alert">
      <strong>{{ t(serverStatus.statusMessage) }}</strong>
      <span v-if="serverStatus.controlPortOwner">
        {{ t("panel.ftp.controlPortOwner", { owner: serverStatus.controlPortOwner }) }}
      </span>
      <!-- Olculebilen ham durumlar: "site Started ama servis Stopped" gibi ayrimi yalniz bunlar
           gosterir. Olculemeyen alan bos gelir; bos degeri "-" ile yazmak olcumu yapilmis
           gostermek olurdu, o yuzden satir yalniz en az biri doluysa cikar. -->
      <span v-if="serverStatus.serviceState || serverStatus.siteState" class="hint">
        {{ t("panel.ftp.serverStateLine", {
          service: serverStatus.serviceState || t("panel.ftp.stateUnmeasured"),
          site: serverStatus.siteState || t("panel.ftp.stateUnmeasured")
        }) }}
      </span>
    </div>

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

    <!-- Ayar OKUNAMADIYSA anahtar sessizce yok olmaz. Bu bir GÜVENLİK anahtarı: kaybolduğunda
         yönetici "panelde FTPS zorunluluğu yok" sanıp şifresiz FTP'yi açık bırakıyordu.
         "Kapalı" ile "okunamadı" burada aynı şey değildir. -->
    <SectionLoadError
      v-if="isAdmin && !siteName && !tlsLoaded && tlsLoad.message.value"
      :message="`${t('panel.ftp.tlsLoadFailed')} ${tlsLoad.message.value}`"
      :busy="tlsSaving"
      @retry="loadTls" />

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
          <input v-model="form.userName" :disabled="creating" />
        </label>
        <label>
          <span>{{ t("panel.form.password") }}</span>
          <PasswordField v-model="form.password" :disabled="creating" />
        </label>
        <label v-if="!siteName">
          <span>{{ t("panel.php.targetSite") }}</span>
          <select v-model="form.siteName" :disabled="creating">
            <option value="">{{ t("panel.php.selectSite") }}</option>
            <option v-for="site in sitesStore.sites" :key="site.name" :value="site.name">
              {{ site.name }}
            </option>
          </select>
        </label>
        <label>
          <span>{{ t("panel.ftp.homePath") }}</span>
          <input v-model="form.homePath" :disabled="creating" />
        </label>
      </div>

      <div class="row-actions">
        <button class="compact" v-bind="writeGate(creating)" @click="createUser">
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
          <span class="cell-muted">
            {{ user.homePath }}
            <!--
              EV KLASÖRÜ BOZUKSA BURADA SÖYLENİR.

              Kayıt duruyor diye koruma çalışıyor sanmak bu depodaki en pahalı hata sınıfıdır ve
              FTP'de canlı olarak yaşandı: hesap listede sapasağlam görünürken sunucu her girişte
              "530 home directory inaccessible" dönüyordu. Ölçüm arka uçtan geliyor
              (FtpService.ProbeHome); burada yalnızca GÖRÜNÜR hâle getiriliyor.

              `title` bozuk yolu yazar: kullanıcı sorunu panelden okuyup sunucuda düzeltebilsin.
            -->
            <span v-if="user.homeLinkExists === false" class="status-pill danger" role="alert" :title="user.homeLinkPath">
              {{ t("panel.ftp.homeMissing") }}
            </span>
            <span v-else-if="user.homeTargetExists === false" class="status-pill danger" role="alert" :title="user.homeLinkPath">
              {{ t("panel.ftp.homeTargetMissing") }}
            </span>
            <span v-else-if="user.homeLinkExists === null" class="status-pill warn" :title="user.homeLinkPath">
              {{ t("panel.ftp.homeUnmeasured") }}
            </span>
          </span>
          <span :class="['status-pill', user.isEnabled && user.existsOnSystem ? 'ok' : 'warn']">
            {{ user.isEnabled && user.existsOnSystem ? t("panel.status.active") : t("panel.status.inactive") }}
          </span>
          <span class="row-actions">
            <button class="secondary compact" v-bind="writeGate(accountBusy)" @click="changeFtpPassword(user.userName)">
              {{ changingPassword === user.userName ? t("panel.database.saving") : t("panel.database.changePassword") }}
            </button>
            <button class="danger compact" v-bind="writeGate(accountBusy)" @click="removeUser(user.userName)">
              {{ deletingUser === user.userName ? t("panel.database.removing") : t("panel.sites.delete") }}
            </button>
          </span>
        </div>
      </template>

      <!-- Liste sunucudan gelmeden "kullanıcı yok" yazıyordu; var olan FTP hesapları
           silinmiş/yok sanılabiliyordu. -->
      <p v-else-if="!usersLoaded" class="empty-state" role="status" aria-live="polite">
        {{ t(usersLoading ? "panel.common.loading" : "panel.common.loadFailed") }}
      </p>

      <p v-else class="empty-state">
        {{ t("panel.ftp.noUsers") }}
      </p>

      <TablePager :state="pager" />
    </section>
  </section>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from "vue";
import { useI18n } from "vue-i18n";
import PasswordField from "../components/PasswordField.vue";
import { createFtpUser, deleteFtpUser, ensureFtpServer, getFtpStatus, getFtpTls, getFtpUsers, setFtpPassword, setFtpTls, type FtpServerStatus, type FtpUser } from "../api/ftp";
import { extractApiDetails, extractApiMessage, useAuthStore } from "../stores/auth";
import { useSitesStore } from "../stores/sites";
import { useConfirm } from "../components/ConfirmDialog.vue";
import { usePrompt } from "../components/PromptDialog.vue";
import SectionLoadError from "../components/SectionLoadError.vue";
import { useSectionError } from "../composables/useNotice";
import TablePager from "../components/TablePager.vue";
import { usePagination } from "../composables/usePagination";
import { useSubscriptionGate } from "../composables/useSubscriptionGate";
import { getSessionEpoch } from "../api/client";

// siteName verilince (web sitesi çalışma alanı sekmesi) FTP yalnızca o siteye
// göre filtrelenir ve yeni kullanıcı o siteye sabitlenir (profesyonel paneller gibi site-bağlamı).
const props = defineProps<{ siteName?: string }>();

const { t } = useI18n();
// Askidaki abonelikte YAZMA dugmeleri bastan devre disi + sebebi title/aria ile soylenir
// (bkz. composables/useSubscriptionGate.ts). Okuma dugmeleri kapiya girmez.
const { writeGate } = useSubscriptionGate();
const { prompt } = usePrompt();
const { confirm } = useConfirm();
const sitesStore = useSitesStore();
const authStore = useAuthStore();
const isAdmin = computed(() => authStore.session?.user?.role === "Administrator");

const users = ref<FtpUser[]>([]);
// Yüklenmemiş durumu "kayıt yok" / "kapalı" göstermek yanıltıcıydı.
const usersLoaded = ref(false);
const usersLoading = ref(false);
const tlsLoaded = ref(false);
// FTPS ayarı OKUNAMADI durumu — anahtarın kapalı olmasından AYRI tutulur.
const tlsLoad = useSectionError();
const serverStatus = ref<FtpServerStatus | null>(null);
const serverCheckCompleted = ref(false);
const creating = ref(false);
const deletingUser = ref("");
const changingPassword = ref("");
const accountBusy = computed(() => Boolean(deletingUser.value || changingPassword.value));
const notice = ref("");
const noticeDetails = ref("");
const noticeError = ref(false);
const search = ref("");
const showCreateForm = ref(false);
// M5: FTPS zorunlu mu (yalnız admin + standalone FTP sayfasında gösterilir).
const requireSsl = ref(false);
const tlsSaving = ref(false);
let disposed = false;
let mountedReady = false;
let scopeVersion = 0;
let usersRequest = 0;
let statusRequest = 0;
let tlsRequest = 0;
let createVersion = 0;
onBeforeUnmount(() => { disposed = true; scopeVersion++; });

function ftpContext() {
  const site = props.siteName, version = scopeVersion, epoch = getSessionEpoch();
  return { current: () => !disposed && props.siteName === site && scopeVersion === version && getSessionEpoch() === epoch };
}

function canManageUser(userName: string) {
  return !disposed && usersLoaded.value && !accountBusy.value && users.value.some(user =>
    user.userName === userName && (!props.siteName || (user.siteName ?? "").toLowerCase() === props.siteName.toLowerCase()));
}

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
  [() => props.siteName, () => authStore.session?.user?.id, () => authStore.session?.user?.role],
  ([value]) => {
    scopeVersion++;
    creating.value = false;
    changingPassword.value = "";
    deletingUser.value = "";
    users.value = [];
    usersLoaded.value = false;
    usersLoading.value = false;
    showCreateForm.value = false;
    Object.assign(form, { siteName: value || "", userName: "", password: "", homePath: "" });
    showNotice("", false);
    tlsLoaded.value = false;
    tlsSaving.value = false;
    requireSsl.value = false;
    tlsLoad.clear();
    serverStatus.value = null;
    serverCheckCompleted.value = false;
    pager.reset();
    if (mountedReady) {
      void Promise.allSettled([refresh(), loadStatus(), loadTls()]);
    }
  },
  { immediate: true, flush: "sync" },
);

watch([() => form.siteName, showCreateForm], ([target, open], [previousTarget]) => {
  createVersion++;
  creating.value = false;
  if (target !== previousTarget) form.homePath = "";
  if (!open) form.password = "";
}, { flush: "sync" });

onMounted(async () => {
  const epoch = getSessionEpoch();
  if (sitesStore.sites.length === 0 && !sitesStore.loading) {
    await sitesStore.fetchSites().catch(() => undefined);
  }
  if (disposed || epoch !== getSessionEpoch()) return;
  mountedReady = true;
  await Promise.allSettled([prepareServer(), refresh(), loadTls()]);
});

async function loadTls() {
  if (!isAdmin.value || props.siteName || disposed || tlsSaving.value) {
    return;
  }
  const context = ftpContext(), request = ++tlsRequest;
  try {
    const result = await getFtpTls();
    if (!context.current() || request !== tlsRequest) return;
    requireSsl.value = result.requireSsl;
    tlsLoaded.value = true;
    tlsLoad.clear();
  } catch (error) {
    if (!context.current() || request !== tlsRequest) return;
    // Eskiden hata sessizce yutuluyordu ve anahtar HİÇ görünmüyordu. Artık sebebi yazılır.
    tlsLoaded.value = false;
    tlsLoad.capture(error);
  }
}

async function toggleTls(value: boolean) {
  if (disposed || !isAdmin.value || props.siteName || !tlsLoaded.value || tlsSaving.value) return;
  const context = ftpContext();
  tlsRequest++;
  tlsSaving.value = true;
  try {
    const sonuc = await setFtpTls(value);
    if (!context.current()) return;
    requireSsl.value = sonuc.requireSsl;

    // "FTPS artık zorunlu; düz FTP bağlantıları reddedilecek" bir GÜVENLİK İDDİASIDIR ve eskiden
    // ISTENEN bayraktan yazılıyordu — IIS'e hiç bakmadan. Artık üç durum ayrı gösterilir; uyarı
    // dalları kırmızı (isError=true) çünkü yönetici sunucuyu güvenli sanmamalı.
    if (sonuc.verified === false) {
      showNotice(t("panel.ftp.tlsNotApplied"), true);
    } else if (sonuc.verified === true) {
      showNotice(t(value ? "panel.ftp.tlsRequired" : "panel.ftp.tlsOptional"), false);
    } else {
      showNotice(t("panel.ftp.tlsUnverified"), true);
    }
  } catch (error) {
    if (context.current()) showApiError(error);
  } finally {
    if (context.current()) tlsSaving.value = false;
  }
}

async function prepareServer() {
  if (disposed || !isAdmin.value) return;
  const context = ftpContext(), request = ++statusRequest;
  try {
    const result = await ensureFtpServer();
    if (context.current() && request === statusRequest) serverStatus.value = result;
  } catch (error) {
    if (context.current() && request === statusRequest) showApiError(error);
  } finally {
    if (context.current() && request === statusRequest) serverCheckCompleted.value = true;
  }
}

async function refresh() {
  if (disposed) return false;
  const context = ftpContext(), request = ++usersRequest;
  usersLoading.value = true;
  try {
    const result = await getFtpUsers();
    if (!context.current() || request !== usersRequest) return false;
    users.value = result;
    usersLoaded.value = true;
    return true;
  } catch (error) {
    if (context.current() && request === usersRequest) showApiError(error);
    return false;
  } finally {
    if (context.current() && request === usersRequest) usersLoading.value = false;
  }
}

/** Yenileme dugmesi: kullanici listesi + sunucu durumu birlikte tazelenir (ikisi de salt okunur). */
async function refreshAll() {
  await Promise.allSettled([refresh(), loadStatus()]);
}

/**
 * Durumu YENIDEN OLCER (GET /ftp/status) — kurmaz, FTPSVC'yi yeniden baslatmaz.
 *
 * Onceden durumu ogrenmenin tek yolu `POST /ftp/ensure` idi, yani bir MUTASYON: "neden kimse
 * baglanamiyor" sorusunun bedeli altyapiyi yeniden yapilandirmakti. Yenileme dugmesi artik
 * salt-okunur ucu kullanir; ilk acilista `prepareServer()` kurulumu garanti etmeye devam eder.
 *
 * Uc YONETICI-OZEL (yanit yabanci sureclerin adlarini/PID'lerini tasir). Site calisma alanindaki
 * FTP sekmesinde bayi bunu goremez; orada `serverStatus` bos kalir ve serit hic cizilmez -- bir
 * bilgi GIZLENMIYOR, hic olculmemis oluyor.
 */
async function loadStatus() {
  if (!isAdmin.value || disposed) {
    return;
  }
  const context = ftpContext(), request = ++statusRequest;
  try {
    const result = await getFtpStatus();
    if (context.current() && request === statusRequest) serverStatus.value = result;
  } catch (error) {
    if (context.current() && request === statusRequest) showApiError(error);
  } finally {
    if (context.current() && request === statusRequest) serverCheckCompleted.value = true;
  }
}

async function createUser() {
  if (disposed || creating.value || !showCreateForm.value) return;
  const context = ftpContext(), version = createVersion;
  const current = () => context.current() && version === createVersion;
  const payload = { ...form, siteName: props.siteName || form.siteName };
  creating.value = true;

  try {
    const result = await createFtpUser({ ...payload });
    if (!current()) return;
    const expiryWarning = result.message === "ftp.user_created_password_expires" || result.message === "ftp.user_created_expiry_unverified"
      ? result.message
      : result.passwordNeverExpires === false ? "ftp.user_created_password_expires"
      : result.passwordNeverExpires === null ? "ftp.user_created_expiry_unverified" : "";
    form.userName = "";
    form.password = "";
    form.homePath = "";
    showCreateForm.value = false;
    const completedVersion = createVersion;
    const refreshed = await refresh();
    if (!context.current() || createVersion !== completedVersion) return;
    if (expiryWarning) {
      // Hesap açıldı; süre dolumu uyarısı sonraki liste okumasında kaybolmamalı.
      const refreshError = refreshed ? "" : notice.value;
      const refreshDetails = refreshed ? "" : noticeDetails.value;
      showNotice([t(expiryWarning), refreshError].filter(Boolean).join("\n\n"), true);
      noticeDetails.value = refreshDetails;
    } else if (refreshed) {
      showNotice(t("panel.ftp.created"), false);
    }
  } catch (error) {
    if (current()) showApiError(error);
  } finally {
    payload.password = "";
    if (current()) creating.value = false;
  }
}

async function changeFtpPassword(userName: string) {
  if (!canManageUser(userName)) return;
  const context = ftpContext();
  changingPassword.value = userName;
  let password: string | null = null;
  try {
    // window.prompt DEĞİL: parola açıkta görünüyordu ve diyaloglar engellenirse akış ölüyordu.
    password = await prompt({
      title: t("panel.ftp.newPasswordPrompt", { user: userName }),
      label: t("panel.form.password"),
      password: true,
      minLength: 8,
      confirmLabel: t("panel.ftp.changePassword")
    });

    if (!context.current() || password === null) return;
    if (password.length < 8) {
      showNotice(t("panel.ftp.passwordTooShort"), true);
      return;
    }
    await setFtpPassword(userName, password);
    if (context.current()) showNotice(t("panel.ftp.passwordChanged"), false);
  } catch (error) {
    if (context.current()) showApiError(error);
  } finally {
    password = null;
    if (context.current()) changingPassword.value = "";
  }
}

/**
 * Silme ONAYSIZ yapılmaz: FTP hesabı Windows kullanıcısıyla birlikte gider ve geri alınamaz.
 * (Parola değiştirmede zaten PromptDialog kullanılıyordu; silmede onay atlanmıştı.)
 */
async function removeUser(userName: string) {
  if (!canManageUser(userName)) return;
  const context = ftpContext();
  deletingUser.value = userName;
  try {
    await confirm({
      title: t("panel.ftp.confirmDeleteTitle"),
      message: t("panel.ftp.confirmDeleteMessage", { name: userName }),
      impact: [userName],
      requireText: userName,
      action: async () => {
        if (!context.current()) return;
        try {
          await deleteFtpUser(userName);
          if (!context.current()) return;
          const refreshed = await refresh();
          if (context.current() && refreshed) showNotice(t("panel.ftp.deleted"), false);
        } catch (error) {
          if (context.current()) showApiError(error);
        }
      }
    });
  } finally {
    if (context.current()) deletingUser.value = "";
  }
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
.compact-head {
  align-items: center;
}

/* Uyari seridi: `.notice.warn-banner` (base.css) zaten kehribar bir izgara. Buradaki tek ekleme
   ilk satirin (sebep cumlesi) govdeden bir adim buyuk olmasi -- serit uc satira kadar cikabiliyor
   ve hangisinin HUKUM, hangisinin ayrinti oldugu okunmadan anlasilmali. */
.ftp-status strong {
  font-size: var(--fs-h4);
}

.ftp-address {
  display: grid;
  gap: var(--sp-2);
  padding: 0 var(--sp-4);
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
  gap: var(--sp-12);
  align-items: flex-start;
  cursor: pointer;
}

.ftp-tls-bar input {
  width: auto;
  /* Kutu, yanindaki KALIN metnin govde cizgisine otursun (metin satir yuksekligi 19.6px, kutu
     ~13px): 2px'lik tek adim yeterli, --sp-4 kutuyu metnin altina dusururdu. */
  margin-top: var(--sp-2);
}

.ftp-tls-text {
  display: grid;
  gap: var(--sp-2);
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
  margin-top: var(--sp-8);
}

.technical-details summary {
  width: fit-content;
  cursor: pointer;
  font-weight: 700;
}

.technical-details pre {
  max-height: 260px;
  margin: var(--sp-8) 0 0;
  padding: var(--sp-10);
  overflow: auto;
  border-radius: var(--radius-sm);
  background: var(--input-bg);
  color: inherit;
  /* Yazi tipi listesi ELDE SAYILMIYOR: `--font-mono` (base.css) panelin butun konsol
     yuzeylerinin tek kaynagi; burada ayri bir liste yazmak, ayni panelde iki farkli tek
     aralikli yazi tipi demekti. */
  font: var(--fs-2xs)/1.45 var(--font-mono);
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
