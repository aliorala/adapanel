<template>
  <section class="panel module-shell ftp-shell">
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
      <button class="secondary compact ftp-dismiss" :aria-label="t('panel.common.dismissNotice')" @click="showNotice('', false)">×</button>
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
        <span v-if="usersLoaded" class="status-pill">{{ t('panel.ftp.userCount', { visible: filteredUsers.length, total: scopedUsers.length }) }}</span>
        <span v-if="isAdmin" :class="['status-pill', serverReady ? 'ok' : 'warn']">
          {{ serverStateLabel }}
        </span>
      </div>

      <div class="toolbar-group ftp-filters">
        <input v-model="search" :aria-label="t('panel.ftp.search')" :placeholder="t('panel.ftp.searchPlaceholder')" />
        <button class="secondary compact" :disabled="usersLoading || statusLoading" @click="refreshAll">
          {{ t(usersLoading || statusLoading ? "panel.common.loading" : "panel.ftp.refresh") }}
        </button>
      </div>
    </section>

    <SectionLoadError v-if="isAdmin && statusLoad.message.value" :message="readErrorMessage('panel.ftp.statusLoadFailed', statusLoad.message.value)" :busy="statusLoading" @retry="loadStatus" />

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
      v-if="serverStatus && !statusLoad.message.value && serverStatus.statusMessage !== 'ftp.server_ready'"
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
    <section v-if="isAdmin && !siteName && tlsLoaded" class="form-surface ftp-tls-section" aria-labelledby="ftp-tls-heading" :aria-busy="certificateBusy">
      <div class="section-head">
        <h3 id="ftp-tls-heading">{{ t('panel.ftp.tlsTitle') }}</h3>
        <span :class="['status-pill', tlsVerification === true && !tlsSaving ? 'ok' : 'warn']" role="status">{{ tlsVerificationLabel }}</span>
      </div>
      <label class="ftp-tls-bar">
        <input type="checkbox" :checked="requireSsl" :disabled="certificateBusy || locked" @change="requestTlsChange" />
        <span class="ftp-tls-text">
          <strong>{{ t("panel.ftp.requireTls") }}</strong>
          <small>{{ t("panel.ftp.requireTlsHint") }}</small>
        </span>
      </label>
      <div class="ftp-tls-summary">
        <div><span>{{ t('panel.ftp.tlsSavedPolicy') }}</span><strong>{{ t(requireSsl ? 'panel.ftp.tlsPolicyRequired' : 'panel.ftp.tlsPolicyOptional') }}</strong></div>
        <p :class="{ 'ftp-tls-warning': tlsAttempted && tlsVerification !== true }" :role="tlsAttempted && tlsVerification !== true ? 'alert' : undefined">{{ tlsVerificationHint }}</p>
      </div>
      <div class="ftp-certificate">
        <div class="section-head"><h4>{{ t('panel.ftp.certTitle') }}</h4><span :class="['status-pill', certificate?.certificateVerified === true && !certificateSaving ? 'ok' : 'warn']" role="status">{{ certificateLabel }}</span></div>
        <p class="meta">{{ t('panel.ftp.certHint') }}</p>
        <label><span>{{ t('panel.ftp.certHost') }}</span><input v-model="certificateHost" :disabled="certificateBusy || locked" autocomplete="off" autocapitalize="none" spellcheck="false" /></label>
        <div class="row-actions"><button class="compact" v-bind="writeGate(certificateBusy || !certificateHost.trim())" @click="saveCertificate">{{ t(certificateSaving ? 'panel.ftp.certApplying' : 'panel.ftp.certApply') }}</button></div>
        <dl v-if="certificate?.certificateThumbprint" class="ftp-account-identity">
          <div><dt>{{ t('panel.ftp.certHost') }}</dt><dd>{{ certificate.hostName }}</dd></div>
          <div><dt>{{ t('panel.ftp.certSubject') }}</dt><dd>{{ certificate.certificateSubject || '—' }}</dd></div>
          <div><dt>{{ t('panel.ftp.certThumbprint') }}</dt><dd>{{ certificate.certificateThumbprint }}</dd></div>
          <div><dt>{{ t('panel.ftp.certExpiry') }}</dt><dd>{{ formatCreatedAt(certificate.certificateNotAfterUtc || '') }}</dd></div>
        </dl>
        <p v-if="certificate?.certificateDetail" class="hint">{{ certificate.certificateDetail }}</p>
        <p class="hint">{{ t('panel.ftp.certProbeScope') }}</p>
      </div>
      <details v-if="tlsDetails" class="technical-details">
        <summary>{{ t('panel.common.technicalDetails') }}</summary><pre>{{ tlsDetails }}</pre>
      </details>
    </section>

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
        <button class="compact" v-bind="writeGate(accountBusy)" @click="createUser">
          {{ creating ? t("panel.database.saving") : t("panel.ftp.createAction") }}
        </button>
        <button class="secondary compact" @click="showCreateForm = false">
          {{ t("panel.ftp.cancelCreate") }}
        </button>
      </div>
    </section>

    <section v-if="accountOperation" :class="['ftp-account-result', `is-${accountOperation.status}`]" aria-labelledby="ftp-account-result-heading" :aria-busy="accountOperation.status === 'running' || accountOperation.status === 'refreshing'">
      <div class="ftp-account-result-head">
        <h3 id="ftp-account-result-heading" role="status">{{ t(accountOperation.status === 'running'
          ? accountOperation.kind === 'create' ? 'panel.ftp.accountCreating' : accountOperation.kind === 'password' ? 'panel.ftp.accountChangingPassword' : 'panel.ftp.accountDeleting'
          : accountOperation.status === 'refreshing' ? 'panel.ftp.accountRefreshing'
          : accountOperation.status === 'failed' ? 'panel.ftp.accountOperationFailed'
          : accountOperation.kind === 'create' ? 'panel.ftp.created' : accountOperation.kind === 'password' ? 'panel.ftp.passwordChanged' : 'panel.ftp.deleted') }}</h3>
        <button v-if="accountOperation.status === 'succeeded' || accountOperation.status === 'failed'" class="secondary compact" :aria-label="t('panel.ftp.accountDismissResult')" @click="dismissAccountOperation">×</button>
      </div>
      <dl class="ftp-account-identity">
        <div><dt>{{ t('panel.database.userName') }}</dt><dd>{{ accountOperation.userName }}</dd></div>
        <div v-if="accountOperation.siteName"><dt>{{ t('panel.sites.name') }}</dt><dd>{{ accountOperation.siteName }}</dd></div>
        <div v-if="accountOperation.homePath" class="ftp-account-home"><dt>{{ t('panel.ftp.homePath') }}</dt><dd>{{ accountOperation.homePath }}</dd></div>
      </dl>
      <p v-if="accountOperation.status === 'running' || accountOperation.status === 'refreshing'" class="ftp-account-hint">{{ t('panel.ftp.accountOperationPendingHint') }}</p>
      <p v-if="accountOperation.error" class="ftp-account-error" role="alert">{{ accountOperation.error }}</p>
      <p v-if="accountOperation.warningKey" class="ftp-account-warning" role="status">{{ t(accountOperation.warningKey) }}</p>
      <div v-if="accountOperation.refreshError" class="ftp-account-refresh">
        <p role="alert">{{ accountOperation.refreshError }}</p>
        <button class="secondary compact" :disabled="accountBusy || usersLoading" @click="refreshAccountListing">{{ t('panel.ftp.refresh') }}</button>
      </div>
    </section>

    <SectionLoadError v-if="usersLoad.message.value && !accountOperation?.refreshError" :message="readErrorMessage(usersLoaded ? 'panel.ftp.listStale' : 'panel.ftp.listLoadFailed', usersLoad.message.value)" :busy="usersLoading" @retry="refresh" />
    <section class="data-table ftp-table" role="table" :aria-label="t('panel.ftp.title')" :aria-busy="usersLoading">
      <div class="data-row table-head ftp-row" role="row">
        <span role="columnheader">{{ t("panel.database.userName") }}</span>
        <span role="columnheader">{{ t("panel.sites.name") }}</span>
        <span role="columnheader">{{ t("panel.ftp.homePath") }}</span>
        <span role="columnheader">{{ t("panel.users.status") }}</span>
        <span role="columnheader">{{ t("panel.files.actions") }}</span>
      </div>

      <template v-if="filteredUsers.length">
        <div v-for="user in pager.items.value" :key="user.userName" class="data-row ftp-row" role="row">
          <span class="cell-main" role="cell" :data-label="t('panel.database.userName')">
            <strong>{{ user.userName }}</strong>
            <small>{{ formatCreatedAt(user.createdAtUtc) }}</small>
          </span>
          <span role="cell" :data-label="t('panel.sites.name')">{{ user.siteName || "-" }}</span>
          <span class="cell-muted" role="cell" :data-label="t('panel.ftp.homePath')">
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
          <span role="cell" :data-label="t('panel.users.status')"><span :class="['status-pill', user.isEnabled && user.existsOnSystem ? 'ok' : 'warn']">
            {{ user.isEnabled && user.existsOnSystem ? t("panel.status.active") : t("panel.status.inactive") }}
          </span></span>
          <span class="row-actions" role="cell" :data-label="t('panel.files.actions')">
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

      <div v-else class="empty-state ftp-empty" role="status">
        <p>{{ t(search.trim() && scopedUsers.length ? 'panel.ftp.noMatches' : 'panel.ftp.noUsers') }}</p>
        <button v-if="search.trim() && scopedUsers.length" class="secondary compact" @click="search = ''">{{ t('panel.ftp.clearSearch') }}</button>
      </div>

      <TablePager :state="pager" />
    </section>
  </section>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from "vue";
import { useI18n } from "vue-i18n";
import PasswordField from "../components/PasswordField.vue";
import { applyFtpCertificate, createFtpUser, deleteFtpUser, ensureFtpServer, getFtpStatus, getFtpTls, getFtpUsers, setFtpPassword, setFtpTls, type FtpCertificate, type FtpServerStatus, type FtpUser } from "../api/ftp";
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
import { formatDateTime } from "../utils/datetime";

// siteName verilince (web sitesi çalışma alanı sekmesi) FTP yalnızca o siteye
// göre filtrelenir ve yeni kullanıcı o siteye sabitlenir (profesyonel paneller gibi site-bağlamı).
const props = defineProps<{ siteName?: string }>();

const { t } = useI18n();
// Askidaki abonelikte YAZMA dugmeleri bastan devre disi + sebebi title/aria ile soylenir
// (bkz. composables/useSubscriptionGate.ts). Okuma dugmeleri kapiya girmez.
const { writeGate, locked } = useSubscriptionGate();
const { prompt } = usePrompt();
const { confirm } = useConfirm();
const sitesStore = useSitesStore();
const authStore = useAuthStore();
const isAdmin = computed(() => authStore.session?.user?.role === "Administrator");

const users = ref<FtpUser[]>([]);
// Yüklenmemiş durumu "kayıt yok" / "kapalı" göstermek yanıltıcıydı.
const usersLoaded = ref(false);
const usersLoading = ref(false);
const usersLoad = useSectionError();
const statusLoad = useSectionError();
const statusLoading = ref(false);
const tlsLoaded = ref(false);
// FTPS ayarı OKUNAMADI durumu — anahtarın kapalı olmasından AYRI tutulur.
const tlsLoad = useSectionError();
const serverStatus = ref<FtpServerStatus | null>(null);
const serverCheckCompleted = ref(false);
const creating = ref(false);
const deletingUser = ref("");
const changingPassword = ref("");
const accountBusy = computed(() => Boolean(creating.value || deletingUser.value || changingPassword.value));
type AccountOperation = {
  kind: 'create' | 'password' | 'delete';
  status: 'running' | 'refreshing' | 'succeeded' | 'failed';
  userName: string;
  siteName: string;
  homePath: string;
  warningKey: string;
  error: string;
  refreshError: string;
};
const accountState = ref<AccountOperation | null>(null);
let accountContext: ReturnType<typeof ftpContext> | null = null;
const accountOperation = computed(() => accountState.value && accountContext?.current() ? accountState.value : null);
const notice = ref("");
const noticeDetails = ref("");
const noticeError = ref(false);
const search = ref("");
const showCreateForm = ref(false);
// M5: FTPS zorunlu mu (yalnız admin + standalone FTP sayfasında gösterilir).
const requireSsl = ref(false);
const tlsSaving = ref(false);
const tlsVerification = ref<boolean | null>(null);
const tlsAttempted = ref(false);
const tlsDetails = ref('');
const certificate = ref<FtpCertificate | null>(null);
const certificateHost = ref('');
const certificateSaving = ref(false);
const certificateBusy = computed(() => certificateSaving.value || tlsSaving.value);
const certificateLabel = computed(() => t(certificateSaving.value ? 'panel.ftp.certApplying'
  : certificate.value?.certificateVerified === true ? 'panel.ftp.certVerified' : 'panel.ftp.certUnverified'));
const tlsVerificationLabel = computed(() => t(tlsSaving.value ? 'panel.ftp.tlsApplying'
  : tlsVerification.value === true ? 'panel.ftp.tlsVerifiedLabel'
  : tlsVerification.value === false ? 'panel.ftp.tlsFailedLabel' : 'panel.ftp.tlsUnverifiedLabel'));
const tlsVerificationHint = computed(() => t(tlsSaving.value ? 'panel.ftp.tlsApplyingHint'
  : !tlsAttempted.value ? 'panel.ftp.tlsSavedOnlyHint'
  : tlsVerification.value === true ? 'panel.ftp.tlsVerifiedHint'
  : tlsVerification.value === false ? 'panel.ftp.tlsFailedHint' : 'panel.ftp.tlsUnknownHint'));
let disposed = false;
let mountedReady = false;
let scopeVersion = 0;
let usersRequest = 0;
let statusRequest = 0;
let tlsRequest = 0;
let createVersion = 0;
onBeforeUnmount(() => { disposed = true; scopeVersion++; accountState.value = null; form.password = ''; });

function ftpContext() {
  const site = props.siteName, version = scopeVersion, epoch = getSessionEpoch();
  const session = authStore.session, actorId = session?.user?.id, role = session?.user?.role;
  return { current: () => !disposed && props.siteName === site && scopeVersion === version && getSessionEpoch() === epoch
    && authStore.session === session && authStore.session?.isAuthenticated === true
    && authStore.session?.user?.id === actorId && authStore.session?.user?.role === role };
}

function canManageUser(userName: string) {
  return !disposed && !locked.value && usersLoaded.value && !accountBusy.value && users.value.some(user =>
    user.userName === userName && (!props.siteName || (user.siteName ?? "").toLowerCase() === props.siteName.toLowerCase()));
}

function sameAccountTarget(target: Pick<FtpUser, 'userName' | 'siteName' | 'homePath'>) {
  const normalized = (value: string) => value.trim().replace(/\\/g, '/').toLowerCase();
  return users.value.some(user => normalized(user.userName) === normalized(target.userName)
    && normalized(user.siteName) === normalized(target.siteName) && normalized(user.homePath) === normalized(target.homePath));
}

async function runAccountWrite(kind: AccountOperation['kind'], target: Pick<FtpUser, 'userName' | 'siteName' | 'homePath'>,
  context: ReturnType<typeof ftpContext>, action: () => Promise<unknown>, success: () => void, notify: () => boolean = () => true) {
  // Invalidate reads that started before this write. No password enters this retained result.
  usersRequest++;
  usersLoading.value = false;
  accountContext = context;
  accountState.value = { kind, status: 'running', userName: target.userName, siteName: target.siteName,
    homePath: target.homePath, warningKey: '', error: '', refreshError: '' };
  const operation = accountState.value;
  showNotice('', false);
  let succeeded = false;
  try {
    await action();
    if (!context.current()) return;
    succeeded = true;
  } catch (error) {
    if (!context.current()) return;
    operation.error = t(extractApiMessage(error));
    if (notify()) showApiError(error);
  }
  if (!context.current()) return;
  operation.status = 'refreshing';
  const refreshed = await refresh();
  if (!context.current() || accountState.value !== operation) return;
  operation.refreshError = refreshed ? '' : t('panel.ftp.accountRefreshFailed');
  operation.status = succeeded ? 'succeeded' : 'failed';
  if (succeeded && notify()) success();
}

async function refreshAccountListing() {
  if (!accountOperation.value || accountBusy.value || usersLoading.value) return;
  await refresh();
}

function dismissAccountOperation() {
  if (accountOperation.value?.status === 'succeeded' || accountOperation.value?.status === 'failed') {
    accountState.value = null;
    accountContext = null;
  }
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
const serverReady = computed(() => !statusLoading.value && !statusLoad.message.value && serverStatus.value?.isConfigured === true && serverStatus.value.statusMessage === 'ftp.server_ready');
const serverStateLabel = computed(() => {
  if (statusLoading.value) return t('panel.ftp.serverChecking');
  if (statusLoad.message.value || serverStatus.value?.statusMessage === 'ftp.server_status_unknown') return t('panel.ftp.serverUnmeasured');
  if (serverReady.value) {
    return t("panel.ftp.serverReady");
  }

  return serverCheckCompleted.value ? t("panel.ftp.serverUnavailable") : t("panel.ftp.serverChecking");
});
const scopedUsers = computed(() => props.siteName
    ? users.value.filter((user) => (user.siteName ?? "").toLowerCase() === props.siteName!.toLowerCase())
    : users.value);
const filteredUsers = computed(() => {
  const scoped = scopedUsers.value;

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
  [() => props.siteName, () => authStore.session, () => authStore.session?.user?.id, () => authStore.session?.user?.role],
  ([value]) => {
    scopeVersion++;
    creating.value = false;
    changingPassword.value = "";
    deletingUser.value = "";
    accountState.value = null;
    accountContext = null;
    users.value = [];
    usersLoaded.value = false;
    usersLoading.value = false;
    usersLoad.clear();
    statusLoad.clear();
    statusLoading.value = false;
    showCreateForm.value = false;
    Object.assign(form, { siteName: value || "", userName: "", password: "", homePath: "" });
    showNotice("", false);
    tlsLoaded.value = false;
    tlsSaving.value = false;
    tlsVerification.value = null;
    tlsAttempted.value = false;
    tlsDetails.value = '';
    certificate.value = null;
    certificateHost.value = '';
    certificateSaving.value = false;
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
  if (target !== previousTarget) form.homePath = "";
  if (!open) form.password = "";
}, { flush: "sync" });

onMounted(async () => {
  const context = ftpContext();
  if (sitesStore.sites.length === 0 && !sitesStore.loading) {
    await sitesStore.fetchSites().catch(() => undefined);
  }
  if (!context.current()) return;
  mountedReady = true;
  await Promise.allSettled([prepareServer(), refresh(), loadTls()]);
});

async function loadTls() {
  if (!isAdmin.value || props.siteName || disposed || certificateBusy.value) {
    return;
  }
  const context = ftpContext(), request = ++tlsRequest;
  try {
    const result = await getFtpTls();
    if (!context.current() || request !== tlsRequest) return;
    requireSsl.value = result.requireSsl;
    tlsVerification.value = result.verified ?? null;
    tlsAttempted.value = result.verified != null;
    tlsDetails.value = result.detail ?? '';
    certificate.value = result;
    certificateHost.value = result.hostName ?? '';
    tlsLoaded.value = true;
    tlsLoad.clear();
  } catch (error) {
    if (!context.current() || request !== tlsRequest) return;
    // Eskiden hata sessizce yutuluyordu ve anahtar HİÇ görünmüyordu. Artık sebebi yazılır.
    tlsLoaded.value = false;
    tlsLoad.capture(error);
  }
}

async function requestTlsChange(event: Event) {
  const input = event.target as HTMLInputElement;
  const requested = input.checked;
  // Keep the native checkbox controlled even when a failed response leaves the saved value unchanged.
  input.checked = requireSsl.value;
  await toggleTls(requested);
}

async function toggleTls(value: boolean) {
  if (disposed || !isAdmin.value || props.siteName || !tlsLoaded.value || certificateBusy.value || locked.value) return;
  const context = ftpContext();
  tlsRequest++;
  tlsSaving.value = true;
  tlsVerification.value = null;
  tlsAttempted.value = true;
  tlsDetails.value = '';
  try {
    const sonuc = await setFtpTls(value);
    if (!context.current()) return;
    requireSsl.value = sonuc.requireSsl;
    tlsVerification.value = sonuc.verified ?? null;
    tlsDetails.value = sonuc.detail ?? '';
    certificate.value = sonuc;

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
    if (context.current()) { tlsDetails.value = extractApiDetails(error); showApiError(error); }
  } finally {
    if (context.current()) tlsSaving.value = false;
  }
}

async function saveCertificate() {
  if (disposed || !isAdmin.value || props.siteName || locked.value || !tlsLoaded.value || certificateBusy.value || !certificateHost.value.trim()) return;
  const context = ftpContext(), hostName = certificateHost.value.trim();
  tlsRequest++;
  certificateSaving.value = true;
  showNotice('', false);
  try {
    const result = await applyFtpCertificate(hostName);
    if (!context.current()) return;
    certificate.value = result;
    showNotice(t(result.certificateVerified === true ? 'panel.ftp.certApplied' : 'panel.ftp.certNotVerified'), result.certificateVerified !== true);
    if (result.certificateVerified !== true) noticeDetails.value = result.certificateDetail ?? '';
  } catch (error) {
    if (context.current()) { certificate.value = null; showApiError(error); }
  } finally {
    if (context.current()) {
      certificateSaving.value = false;
      // Re-read IIS policy without changing it. Keep any failed operation notice.
      await loadTls();
    }
  }
}

async function prepareServer() {
  if (disposed || !isAdmin.value) return;
  const context = ftpContext(), request = ++statusRequest;
  statusLoading.value = true;
  try {
    const result = await ensureFtpServer();
    if (context.current() && request === statusRequest) { serverStatus.value = result; statusLoad.clear(); }
  } catch (error) {
    if (context.current() && request === statusRequest) captureReadError(statusLoad, 'panel.ftp.statusLoadFailed', error);
  } finally {
    if (context.current() && request === statusRequest) { serverCheckCompleted.value = true; statusLoading.value = false; }
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
    usersLoad.clear();
    if (accountOperation.value) accountOperation.value.refreshError = '';
    return true;
  } catch (error) {
    if (context.current() && request === usersRequest) {
      captureReadError(usersLoad, 'panel.ftp.listLoadFailed', error);
      if (accountOperation.value) accountOperation.value.refreshError = t('panel.ftp.accountRefreshFailed');
    }
    return false;
  } finally {
    if (context.current() && request === usersRequest) usersLoading.value = false;
  }
}

/** Yenileme dugmesi: kullanici listesi + sunucu durumu birlikte tazelenir (ikisi de salt okunur). */
async function refreshAll() {
  await Promise.allSettled([refresh(), loadStatus(), loadTls()]);
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
  statusLoading.value = true;
  try {
    const result = await getFtpStatus();
    if (context.current() && request === statusRequest) { serverStatus.value = result; statusLoad.clear(); }
  } catch (error) {
    if (context.current() && request === statusRequest) captureReadError(statusLoad, 'panel.ftp.statusLoadFailed', error);
  } finally {
    if (context.current() && request === statusRequest) { serverCheckCompleted.value = true; statusLoading.value = false; }
  }
}

async function createUser() {
  if (disposed || locked.value || accountBusy.value || !showCreateForm.value) return;
  const context = ftpContext(), version = createVersion;
  const payload = { ...form, siteName: props.siteName || form.siteName };
  let notificationVersion = version;
  creating.value = true;

  try {
    await runAccountWrite('create', payload, context, async () => {
      const result = await createFtpUser({ ...payload });
      if (!context.current()) return;
      const operation = accountState.value!;
      Object.assign(operation, { userName: result.userName, siteName: result.siteName, homePath: result.homePath });
      operation.warningKey = result.message === "ftp.user_created_password_expires" || result.message === "ftp.user_created_expiry_unverified"
        ? result.message : result.passwordNeverExpires === false ? "ftp.user_created_password_expires"
        : result.passwordNeverExpires === null ? "ftp.user_created_expiry_unverified" : "";
      users.value = [...users.value.filter(user => user.userName.toLowerCase() !== result.userName.toLowerCase()), result];
      if (version === createVersion) {
        form.userName = "";
        form.password = "";
        form.homePath = "";
        showCreateForm.value = false;
        notificationVersion = createVersion;
      }
    }, () => {
      const warning = accountOperation.value?.warningKey;
      showNotice(warning ? [t(warning), usersLoad.message.value].filter(Boolean).join('\n\n') : t('panel.ftp.created'), !!warning);
    }, () => createVersion === notificationVersion);
  } finally {
    payload.password = "";
    if (context.current()) creating.value = false;
  }
}

async function changeFtpPassword(userName: string) {
  if (!canManageUser(userName)) return;
  const context = ftpContext();
  const target = { ...users.value.find(user => user.userName === userName)! };
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

    if (!context.current() || locked.value || !sameAccountTarget(target) || password === null) return;
    if (password.length < 8) {
      showNotice(t("panel.ftp.passwordTooShort"), true);
      return;
    }
    const submittedPassword = password;
    await runAccountWrite('password', target, context, () => setFtpPassword(target.userName, submittedPassword),
      () => showNotice(t("panel.ftp.passwordChanged"), false));
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
  const target = { ...users.value.find(user => user.userName === userName)! };
  deletingUser.value = userName;
  try {
    await confirm({
      title: t("panel.ftp.confirmDeleteTitle"),
      message: t("panel.ftp.confirmDeleteMessage", { name: userName }),
      impact: [userName],
      requireText: userName,
      action: async () => {
        if (!context.current() || locked.value || !sameAccountTarget(target)) return;
        await runAccountWrite('delete', target, context, async () => {
          await deleteFtpUser(target.userName);
          if (!context.current()) return;
          // The deletion was confirmed even if the next list read fails.
          users.value = users.value.filter(user => user.userName.toLowerCase() !== userName.toLowerCase());
        }, () => showNotice(t("panel.ftp.deleted"), false));
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
  return formatDateTime(value, "-");
}

function captureReadError(section: ReturnType<typeof useSectionError>, fallback: string, error: unknown) {
  section.capture(error);
  // A failed read must not say a separately confirmed account mutation failed.
  if (extractApiMessage(error) === 'common.unexpected_error') section.message.value = t(fallback);
}

function readErrorMessage(prefix: string, error: string) {
  const label = t(prefix);
  return label === error ? label : `${label} ${error}`;
}
</script>

<style scoped>
.ftp-shell { container: ftp-workspace / inline-size; min-width: 0; }
.ftp-address, .ftp-filters, .ftp-summary > *, .ftp-create, .ftp-form-grid > * { min-width: 0; }
.ftp-address strong, .ftp-summary strong { overflow-wrap: anywhere; }
.ftp-dismiss { position: absolute; top: var(--sp-8); right: var(--sp-8); }
.page-notice { padding-right: 52px; }
.ftp-empty { display: grid; justify-items: start; gap: var(--sp-8); }
.ftp-empty p { margin: 0; }
.ftp-account-result {
  --account-tone: var(--panel-border);
  display: grid;
  gap: var(--sp-12);
  min-width: 0;
  padding: var(--sp-16);
  border: 1px solid color-mix(in srgb, var(--account-tone) 45%, var(--panel-border));
  border-left: 3px solid var(--account-tone);
  border-radius: var(--radius-sm);
  background: var(--surface-1);
}
.ftp-account-result.is-succeeded { --account-tone: var(--ok-1); }
.ftp-account-result.is-failed { --account-tone: var(--danger-1); }
.ftp-account-result.is-running, .ftp-account-result.is-refreshing { --account-tone: var(--warn-1); }
.ftp-account-result-head { display: flex; justify-content: space-between; align-items: flex-start; gap: var(--sp-12); }
.ftp-account-result-head h3 { margin: 0; min-width: 0; overflow-wrap: anywhere; }
.ftp-account-result-head button { flex: 0 0 auto; }
.ftp-account-identity { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: var(--sp-12); margin: 0; }
.ftp-account-identity > div { min-width: 0; }
.ftp-account-identity dt { color: var(--text-muted); font-size: var(--fs-xs); margin-bottom: var(--sp-4); }
.ftp-account-identity dd { margin: 0; overflow-wrap: anywhere; font-weight: 600; }
.ftp-account-home { grid-column: 1 / -1; }
.ftp-account-result p { margin: 0; overflow-wrap: anywhere; }
.ftp-account-hint { color: var(--text-muted); }
.ftp-account-error { color: var(--danger-text); }
.ftp-account-warning, .ftp-account-refresh { color: var(--warn-text); }
.ftp-account-refresh { display: flex; flex-wrap: wrap; align-items: center; gap: var(--sp-12); border-top: 1px solid var(--panel-border); padding-top: var(--sp-12); }
.ftp-account-refresh p { flex: 1 1 240px; }
.ftp-row > [role="cell"]::before { display: none; }
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
  grid-template-columns: minmax(150px, 0.85fr) minmax(130px, 0.75fr) minmax(240px, 1.45fr) minmax(64px, 0.65fr) 180px;
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
  flex-shrink: 0;
  /* Kutu, yanindaki KALIN metnin govde cizgisine otursun (metin satir yuksekligi 19.6px, kutu
     ~13px): 2px'lik tek adim yeterli, --sp-4 kutuyu metnin altina dusururdu. */
  margin-top: var(--sp-2);
}

.ftp-tls-text {
  display: grid;
  min-width: 0;
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
    min-width: 0;
  }
}

.ftp-tls-section { display: grid; gap: var(--sp-12); min-width: 0; }
.ftp-tls-section h3 { margin: 0; }
.ftp-certificate { display: grid; gap: var(--sp-12); min-width: 0; border-top: 1px solid var(--panel-border); padding-top: var(--sp-12); }
.ftp-certificate h4, .ftp-certificate p { margin: 0; }
.ftp-certificate label { min-width: 0; }
.ftp-certificate input { width: 100%; box-sizing: border-box; }
.ftp-tls-summary { display: grid; grid-template-columns: minmax(150px, .7fr) minmax(0, 1.3fr); gap: var(--sp-12); padding-top: var(--sp-12); border-top: 1px solid var(--panel-border); }
.ftp-tls-summary > div { display: grid; gap: var(--sp-4); align-content: start; }
.ftp-tls-summary > div > span { color: var(--text-muted); font-size: var(--fs-xs); }
.ftp-tls-summary p { margin: 0; color: var(--text-muted); overflow-wrap: anywhere; }
.ftp-tls-summary p.ftp-tls-warning { color: var(--warn-1); }

@container ftp-workspace (max-width: 880px) {
  .ftp-account-identity { grid-template-columns: 1fr; }
  .ftp-tls-summary { grid-template-columns: 1fr; }
  .ftp-row { grid-template-columns: 1fr; min-width: 0; gap: var(--sp-8); }
  .ftp-row.table-head { position: absolute; width: 1px; height: 1px; padding: 0; clip-path: inset(50%); overflow: hidden; white-space: nowrap; }
  .ftp-row > [role="cell"] { display: grid; justify-items: start; gap: var(--sp-2); }
  .ftp-row > [role="cell"]::before { display: block; content: attr(data-label); color: var(--text-muted); font-size: var(--fs-xs); font-weight: 600; }
  .ftp-row > .row-actions { display: flex; flex-wrap: wrap; gap: var(--sp-8); padding-top: var(--sp-4); }
  .ftp-row > .row-actions::before { flex-basis: 100%; }
  .ftp-summary { grid-template-columns: 1fr; }
  .ftp-form-grid { grid-template-columns: 1fr; }
  .ftp-filters { width: 100%; }
  .ftp-filters input { flex: 1; min-width: 0; width: auto; }
}
</style>
