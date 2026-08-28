<template>
  <section class="panel module-shell users-workspace">
    <!-- Servis planları (panel): limit şablonu tanımla, müşteriye ata. Yalnız admin. -->
    <div v-if="isAdmin" class="form-surface plans-card">
      <div class="plans-head">
        <div>
          <strong>{{ t("panel.plans.title") }}</strong>
          <p class="meta">{{ t("panel.plans.hint") }}</p>
        </div>
        <button type="button" class="compact" @click="planFormunuAcKapa">
          {{ showPlanForm ? t("panel.common.cancel") : t("panel.plans.add") }}
        </button>
      </div>

      <div v-if="servicePlans.length" class="plans-grid">
        <article v-for="plan in servicePlans" :key="plan.id" class="plan-card">
          <div class="plan-card-head">
            <strong class="plan-name">{{ plan.name }}</strong>
            <span class="plan-card-actions">
              <button type="button" class="secondary compact" @click="planiDuzenle(plan)">{{ t("panel.common.edit") }}</button>
              <button type="button" class="secondary compact danger" @click="removeServicePlan(plan)">{{ t("panel.common.delete") }}</button>
            </span>
          </div>
          <ul class="plan-limits">
            <li><span>{{ t("panel.plans.sitesLabel") }}</span><b>{{ plan.maxSites || "∞" }}</b></li>
            <!-- PLAN KARTI bir ÖLÇÜM değil, alttaki formun YANKISIDIR: yönetici "1024" yazar,
                 kart "1024" gösterir. Bu yüzden sayı ÖLÇEKLENMEZ (kota göstergelerinden farkı
                 bu); düzeltilen tek şey ETİKET. Sunucu bu alanları 1024 tabanında yorumluyor
                 (FileQuotaGuard: MaxDiskMb * 1024 * 1024; SubscriptionEnforcementService:
                 MaxTrafficGbPerMonth * 1024³), yani doğru birim MiB ve GiB'dir — koda gömülü
                 "MB"/"GB" kullanıcıya %7 yanlış bir taban söylüyordu. -->
            <li><span>{{ t("panel.plans.diskLabel") }}</span><b>{{ plan.maxDiskMb ? plan.maxDiskMb + " " + t("panel.units.mib") : "∞" }}</b></li>
            <li><span>{{ t("panel.plans.databasesLabel") }}</span><b>{{ plan.maxDatabases || "∞" }}</b></li>
            <li><span>{{ t("panel.plans.mailboxesLabel") }}</span><b>{{ plan.maxMailboxes || "∞" }}</b></li>
            <li><span>{{ t("panel.plans.scheduledTasksLabel") }}</span><b>{{ plan.maxScheduledTasks || "∞" }}</b></li>
            <li><span>{{ t("panel.plans.ftpLabel") }}</span><b>{{ plan.maxFtpUsers || "∞" }}</b></li>
            <li><span>{{ t("panel.plans.trafficLabel") }}</span><b>{{ plan.maxTrafficGbPerMonth ? plan.maxTrafficGbPerMonth + " " + t("panel.units.gib") : "∞" }}</b></li>
            <li><span>{{ t("panel.plans.validityLabel") }}</span><b>{{ plan.validityDays ? plan.validityDays + " " + t("panel.plans.days") : "∞" }}</b></li>
            <li><span>{{ t("panel.plans.backupsLabel") }}</span><b>{{ plan.maxBackupsPerDay || "∞" }}</b></li>
          </ul>
        </article>
      </div>
      <!-- Planlar gelmeden "plan yok" yazıyordu; tanımlı servis planları yokmuş gibi görünüyordu. -->
      <p v-else-if="!plansLoaded" class="empty-state plans-empty" role="status" aria-live="polite">
        {{ t("panel.common.loading") }}
      </p>
      <!-- ÇEKİLEMEDİ ile YOK ayrı gösterilir; ikisini birleştirmek yöneticiye var olan planları
           yok diye sunmak olurdu. -->
      <p v-else-if="plansError" class="notice error plans-empty" role="alert">{{ plansError }}</p>
      <p v-else class="empty-state plans-empty">{{ t("panel.plans.empty") }}</p>

      <form v-if="showPlanForm" class="plan-form-grid" @submit.prevent="planiKaydet">
        <!-- Plan ABONELIKLERE BAGLI: duzenlemek bagli butun musterilerin limitlerini degistirir.
             Bunu sessizce yapmak, bir kotayi farkinda olmadan dusurmeye yol acardi. -->
        <p v-if="duzenlenenPlanId" class="notice warn plan-form-hint">{{ t("panel.plans.editWarning") }}</p>
        <label class="stacked-field plan-name-cell">
          <span class="field-label">{{ t("panel.plans.name") }}</span>
          <input v-model.trim="planForm.name" :placeholder="t('panel.plans.namePlaceholder')" />
        </label>
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.users.maxSites") }}</span>
          <input v-model.number="planForm.maxSites" type="number" min="0" />
        </label>
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.users.maxDiskMb") }}</span>
          <input v-model.number="planForm.maxDiskMb" type="number" min="0" />
        </label>
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.users.maxDatabases") }}</span>
          <input v-model.number="planForm.maxDatabases" type="number" min="0" />
        </label>
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.users.maxMailboxes") }}</span>
          <input v-model.number="planForm.maxMailboxes" type="number" min="0" />
        </label>
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.users.maxScheduledTasks") }}</span>
          <input v-model.number="planForm.maxScheduledTasks" type="number" min="0" />
        </label>
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.plans.ftpLabel") }}</span>
          <input v-model.number="planForm.maxFtpUsers" type="number" min="0" />
        </label>
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.plans.trafficLabel") }}</span>
          <input v-model.number="planForm.maxTrafficGbPerMonth" type="number" min="0" />
          <small class="field-hint">{{ t("panel.plans.trafficHint") }}</small>
        </label>
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.plans.validityLabel") }}</span>
          <input v-model.number="planForm.validityDays" type="number" min="0" />
          <small class="field-hint">{{ t("panel.plans.validityHint") }}</small>
        </label>
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.plans.maxBackupsPerDay") }}</span>
          <input v-model.number="planForm.maxBackupsPerDay" type="number" min="0" max="24" />
          <small class="field-hint">{{ t("panel.plans.backupsHint") }}</small>
        </label>
        <p class="plan-form-hint">{{ t("panel.plans.formHint") }}</p>
        <div class="plan-form-actions">
          <button type="submit" class="compact" :disabled="!planForm.name || planKaydediliyor">
            {{ duzenlenenPlanId ? t("panel.common.save") : t("panel.plans.add") }}
          </button>
        </div>
      </form>
    </div>

    <div class="module-toolbar">
      <div>
        <strong>{{ t("panel.users.title") }}</strong>
        <p class="meta">{{ t("panel.settings.usersHint") }}</p>
      </div>
      <button class="compact" @click="showUserCreateForm = !showUserCreateForm">
        {{ showUserCreateForm ? t("panel.common.cancel") : t("panel.users.create") }}
      </button>
    </div>

    <form v-if="showUserCreateForm" class="form-surface form-grid user-create-grid" @submit.prevent="createUser">
      <label class="stacked-field">
        <span class="field-label">{{ t("panel.users.userName") }}</span>
        <input v-model="createUserForm.userName" minlength="3" maxlength="32" required />
      </label>
      <label class="stacked-field">
        <span class="field-label">{{ t("panel.form.email") }}</span>
        <input v-model="createUserForm.email" type="email" required />
      </label>
      <label class="stacked-field">
        <span class="field-label">{{ t("panel.form.password") }}</span>
        <PasswordField v-model="createUserForm.password" minlength="12" required />
      </label>
      <label v-if="isAdmin" class="stacked-field">
        <span class="field-label">{{ t("panel.users.role") }}</span>
        <select v-model="createUserForm.role">
          <option value="Administrator">{{ t("panel.role.administrator") }}</option>
          <option value="Operator">{{ t("panel.role.operator") }}</option>
          <option value="Reseller">{{ t("panel.role.reseller") }}</option>
          <option value="Customer">{{ t("panel.role.customer") }}</option>
        </select>
      </label>
      <label v-if="isAdmin && createUserForm.role === 'Customer' && parentCandidates.length > 0" class="stacked-field">
        <span class="field-label">{{ t("panel.users.parentSubscription") }}</span>
        <select v-model="createUserForm.parentUserId">
          <option value="">{{ t("panel.users.parentSubscriptionNone") }}</option>
          <option v-for="candidate in parentCandidates" :key="candidate.id" :value="candidate.id">{{ candidate.userName }}</option>
        </select>
        <small class="field-hint">{{ t("panel.users.parentSubscriptionHint") }}</small>
      </label>
      <label v-if="isAdmin && createUserForm.role === 'Customer' && !createUserForm.parentUserId && resellers.length > 0" class="stacked-field">
        <span class="field-label">{{ t("panel.users.owner") }}</span>
        <select v-model="createUserForm.ownerId">
          <option value="">{{ t("panel.users.ownerNone") }}</option>
          <option v-for="reseller in resellers" :key="reseller.id" :value="reseller.id">{{ reseller.userName }}</option>
        </select>
        <small class="field-hint">{{ t("panel.users.ownerHint") }}</small>
      </label>
      <div v-if="createUserForm.role === 'Customer' && !createUserForm.parentUserId" class="stacked-field customer-sites">
        <span class="field-label">{{ t("panel.users.assignedSites") }}</span>
        <label v-for="site in sitesStore.sites" :key="site.name" class="checkbox">
          <input v-model="createUserForm.assignedSites" type="checkbox" :value="site.name" />
          <span>{{ site.name }}</span>
        </label>
        <small v-if="sitesStore.sites.length === 0" class="field-hint">{{ t("panel.users.noSitesToAssign") }}</small>
      </div>
      <label v-if="createUserForm.role === 'Customer' && !createUserForm.parentUserId && servicePlans.length > 0" class="stacked-field">
        <span class="field-label">{{ t("panel.plans.select") }}</span>
        <select v-model="createPlanId" @change="applyPlanToCreate">
          <option value="">{{ t("panel.plans.custom") }}</option>
          <option v-for="plan in servicePlans" :key="plan.id" :value="plan.id">{{ plan.name }}</option>
        </select>
      </label>
      <label v-if="createUserForm.role === 'Customer' && !createUserForm.parentUserId" class="stacked-field">
        <span class="field-label">{{ t("panel.users.maxSites") }}</span>
        <input v-model.number="createUserForm.maxSites" type="number" min="0" />
      </label>
      <label v-if="createUserForm.role === 'Customer' && !createUserForm.parentUserId" class="stacked-field">
        <span class="field-label">{{ t("panel.users.maxDiskMb") }}</span>
        <input v-model.number="createUserForm.maxDiskMb" type="number" min="0" />
      </label>
      <label v-if="createUserForm.role === 'Customer' && !createUserForm.parentUserId" class="stacked-field">
        <span class="field-label">{{ t("panel.users.maxDatabases") }}</span>
        <input v-model.number="createUserForm.maxDatabases" type="number" min="0" />
      </label>
      <label v-if="createUserForm.role === 'Customer' && !createUserForm.parentUserId" class="stacked-field">
        <span class="field-label">{{ t("panel.users.maxMailboxes") }}</span>
        <input v-model.number="createUserForm.maxMailboxes" type="number" min="0" />
      </label>
      <label v-if="createUserForm.role === 'Customer' && !createUserForm.parentUserId" class="stacked-field">
        <span class="field-label">{{ t("panel.users.maxScheduledTasks") }}</span>
        <input v-model.number="createUserForm.maxScheduledTasks" type="number" min="0" />
      </label>
      <p v-if="createUserForm.role === 'Customer' && !createUserForm.parentUserId" class="quota-hint">{{ t("panel.plans.formHint") }}</p>
      <label class="stacked-field">
        <span class="field-label">{{ t("panel.form.language") }}</span>
        <select v-model="createUserForm.preferredLanguage">
          <option value="tr">{{ t("panel.language.tr") }}</option>
          <option value="en">{{ t("panel.language.en") }}</option>
        </select>
      </label>
      <label class="check-field welcome-cell">
        <input v-model="createUserForm.sendWelcomeEmail" type="checkbox" />
        <span>{{ t("panel.users.sendWelcomeEmail") }}</span>
      </label>
      <div class="form-submit-cell">
        <button type="submit" class="compact">{{ t("panel.users.create") }}</button>
      </div>
    </form>

    <div class="data-table">
      <div class="data-row table-head user-row">
        <span>{{ t("panel.users.userName") }}</span>
        <span>{{ t("panel.users.role") }}</span>
        <span>{{ t("panel.users.status") }}</span>
        <span>{{ t("panel.users.twoFactor") }}</span>
        <span>{{ t("panel.software.action") }}</span>
      </div>
      <div v-for="user in authStore.users" :key="user.id" class="data-row user-row">
        <div class="cell-main">
          <strong>{{ user.userName }}</strong>
          <small>{{ user.email }}</small>
          <small v-if="user.role === 'Customer'" class="customer-meta">
            {{ user.assignedSites.length }} {{ t("panel.users.sitesShort") }}
            <!-- Ölçüm KESİLDİYSE sayı bir ALT SINIRDIR; rozetsiz göstermek onu tam değer gibi
                 sunmak olurdu. Bayrak SATIR BAŞINA gelir: bütçe abonelik başına olduğu için bir
                 müşterinin ölçümü kesilirken komşusununki tam olabilir. -->
            · {{ formatQuotaMib(user.diskUsageMb) }} / {{ user.maxDiskMb > 0 ? formatQuotaMib(user.maxDiskMb) : "∞" }}<span
              v-if="!user.diskMeasured"
              class="status-pill warn"
              :title="t('users.disk_usage_partial_hint')">{{ t("users.disk_usage_partial") }}</span>
            · {{ user.databaseCount }} / {{ user.maxDatabases > 0 ? user.maxDatabases : "∞" }} {{ t("panel.users.databasesShort") }}
            <template v-if="user.ownerName"> · {{ t("panel.users.owner") }}: {{ user.ownerName }}</template>
            <template v-if="user.parentName"> · {{ t("panel.users.subUserOf") }}: {{ user.parentName }}</template>
          </small>
        </div>
        <span>{{ user.role }}</span>
        <!-- "Kilitli" rozeti DURUM hücresinin İÇİNDE. Önceden ayrı bir ızgara çocuğuydu ve
             başlık 5 sütunluk olduğu için, bir kullanıcı kilitlendiğinde satır 6 hücreye çıkıp
             "İşlem" düğmelerini alt satıra atıyordu — tam da acilen "Kilidi aç"a basılması
             gereken satır bozuluyordu. -->
        <span class="user-status-cell">
          <span :class="['status-pill', user.isActive ? 'ok' : 'warn']">
            {{ user.isActive ? t("panel.status.active") : t("panel.status.inactive") }}
          </span>
          <span v-if="user.lockedUntilUtc" class="status-pill danger" :title="t('panel.users.lockedHint')">
            {{ t("panel.users.locked") }}
          </span>
        </span>
        <span :class="['status-pill', user.isTwoFactorEnabled ? 'ok' : 'warn']">
          {{ user.isTwoFactorEnabled ? t("panel.status.enabled") : t("panel.status.disabled") }}
        </span>
        <span class="user-row-actions">
          <!-- Hatalı giriş kilidi: beklemeden aç (doğru parolayla da kalkar, bu kısayol). -->
          <button
            v-if="user.lockedUntilUtc"
            class="secondary compact row-button"
            :disabled="unlockingUserId === user.id"
            @click="unlockUser(user)">
            {{ unlockingUserId === user.id ? t("panel.users.unlocking") : t("panel.users.unlock") }}
          </button>
          <button
            v-if="user.role === 'Customer'"
            class="secondary compact row-button"
            @click="impersonateUser(user)">
            {{ t("panel.users.loginAs") }}
          </button>
          <button class="secondary compact row-button" @click="selectUser(user)">{{ t("panel.users.edit") }}</button>
          <button
            v-if="user.id !== authStore.session?.user?.id"
            class="danger compact row-button"
            @click="deleteUser(user)">
            {{ t("panel.users.delete") }}
          </button>
        </span>
      </div>
    </div>

    <!-- ABONELİK: plan bağı, kullanım ve askı denetimi seçili müşterinin yanında.
         Kota/askı işlemleri kullanıcı düzenlemenin parçasıdır; ayrı bir ekrana göndermek
         yöneticiyi iki yer arasında gezdirirdi. -->
    <div v-if="selectedUserId && selectedUserIsCustomer" class="form-surface">
      <SubscriptionCard :user-id="selectedUserId" manageable />
    </div>

    <form v-if="selectedUserId" class="form-surface form-grid user-edit-grid" @submit.prevent="saveUserChanges">
      <label class="stacked-field">
        <span class="field-label">{{ t("panel.form.email") }}</span>
        <input v-model="selectedUserForm.email" type="email" required />
      </label>
      <label v-if="isAdmin" class="stacked-field">
        <span class="field-label">{{ t("panel.users.role") }}</span>
        <select v-model="selectedUserForm.role">
          <option value="Administrator">{{ t("panel.role.administrator") }}</option>
          <option value="Operator">{{ t("panel.role.operator") }}</option>
          <option value="Reseller">{{ t("panel.role.reseller") }}</option>
          <option value="Customer">{{ t("panel.role.customer") }}</option>
        </select>
      </label>
      <p v-if="selectedUserForm.parentUserId" class="quota-hint settings-wide-field">{{ t("panel.users.subUserScopeNote") }}</p>
      <label v-if="isAdmin && selectedUserForm.role === 'Customer' && !selectedUserForm.parentUserId && resellers.length > 0" class="stacked-field">
        <span class="field-label">{{ t("panel.users.owner") }}</span>
        <select v-model="selectedUserForm.ownerId">
          <option value="">{{ t("panel.users.ownerNone") }}</option>
          <option v-for="reseller in resellers" :key="reseller.id" :value="reseller.id">{{ reseller.userName }}</option>
        </select>
      </label>
      <div v-if="selectedUserForm.role === 'Customer' && !selectedUserForm.parentUserId" class="stacked-field customer-sites">
        <span class="field-label">{{ t("panel.users.assignedSites") }}</span>
        <label v-for="site in sitesStore.sites" :key="site.name" class="checkbox">
          <input v-model="selectedUserForm.assignedSites" type="checkbox" :value="site.name" />
          <span>{{ site.name }}</span>
        </label>
        <small v-if="sitesStore.sites.length === 0" class="field-hint">{{ t("panel.users.noSitesToAssign") }}</small>
      </div>
      <label v-if="selectedUserForm.role === 'Customer' && !selectedUserForm.parentUserId" class="stacked-field">
        <span class="field-label">{{ t("panel.users.maxSites") }}</span>
        <input v-model.number="selectedUserForm.maxSites" type="number" min="0" />
      </label>
      <label v-if="selectedUserForm.role === 'Customer' && !selectedUserForm.parentUserId" class="stacked-field">
        <span class="field-label">{{ t("panel.users.maxDiskMb") }}</span>
        <input v-model.number="selectedUserForm.maxDiskMb" type="number" min="0" />
      </label>
      <label v-if="selectedUserForm.role === 'Customer' && !selectedUserForm.parentUserId" class="stacked-field">
        <span class="field-label">{{ t("panel.users.maxDatabases") }}</span>
        <input v-model.number="selectedUserForm.maxDatabases" type="number" min="0" />
      </label>
      <label v-if="selectedUserForm.role === 'Customer' && !selectedUserForm.parentUserId" class="stacked-field">
        <span class="field-label">{{ t("panel.users.maxMailboxes") }}</span>
        <input v-model.number="selectedUserForm.maxMailboxes" type="number" min="0" />
      </label>
      <label v-if="selectedUserForm.role === 'Customer' && !selectedUserForm.parentUserId" class="stacked-field">
        <span class="field-label">{{ t("panel.users.maxScheduledTasks") }}</span>
        <input v-model.number="selectedUserForm.maxScheduledTasks" type="number" min="0" />
      </label>
      <p v-if="selectedUserForm.role === 'Customer' && !selectedUserForm.parentUserId" class="quota-hint">{{ t("panel.plans.formHint") }}</p>
      <label class="stacked-field">
        <span class="field-label">{{ t("panel.form.language") }}</span>
        <select v-model="selectedUserForm.preferredLanguage">
          <option value="tr">{{ t("panel.language.tr") }}</option>
          <option value="en">{{ t("panel.language.en") }}</option>
        </select>
      </label>
      <label class="check-field">
        <input v-model="selectedUserForm.isActive" type="checkbox" />
        <span>{{ t("panel.users.isActive") }}</span>
      </label>
      <div class="row-actions">
        <button type="submit" class="compact">{{ t("panel.users.save") }}</button>
        <button type="button" class="secondary compact" @click="selectedUserId = ''">{{ t("panel.common.cancel") }}</button>
      </div>
    </form>
  </section>
</template>

<script setup lang="ts">
// Hesap yönetimi (Plesk "Müşteriler & Bayiler"): kullanıcı listesi, oluşturma/düzenleme/silme,
// kilit açma, müşteri olarak giriş ve alt-kullanıcı (abonelik altı hesap) bağlama.
// Daha önce SetupWizardView > Ayarlar > Kullanıcılar sekmesindeydi; davranış birebir korunmuştur.
import { computed, onMounted, reactive, ref } from "vue";
import { useI18n } from "vue-i18n";
import PasswordField from "../components/PasswordField.vue";
import { useConfirm } from "../components/ConfirmDialog.vue";
import { getServicePlans, createServicePlan, updateServicePlan, deleteServicePlan, type ServicePlan } from "../api/servicePlans";
import { extractApiDetails, extractApiMessage, useAuthStore, type ManagedUser } from "../stores/auth";
import { extractDeletionLeftovers, type UserDeletionLeftover } from "../api/users";
import SubscriptionCard from "../components/SubscriptionCard.vue";
import { useSitesStore } from "../stores/sites";
import { formatBytes as sharedFormatBytes } from "../utils/bytes";

// Bildirimler üst bandda (MainLayout > banner) gösterilsin diye kabuk bileşene iletilir.
const emit = defineEmits<{ (event: "notify", message: string, isError: boolean): void }>();

const i18n = useI18n();
// params: "'{name}' planı silinecek" gibi yer tutuculu metinler için. Verilmezse davranış aynı.
const t = (key: string, params?: Record<string, unknown>) => {
  try {
    const value = params ? i18n.t(key, params) : i18n.t(key);
    if (typeof value !== "string") {
      console.error("Unexpected i18n value", key, value);
      return key;
    }

    return value;
  } catch (error) {
    console.error("Failed i18n key", key, error);
    return key;
  }
};

const authStore = useAuthStore();
const sitesStore = useSitesStore();
const { confirm } = useConfirm();

const isAdmin = computed(() => authStore.session?.user?.role === "Administrator");
const isReseller = computed(() => authStore.session?.user?.role === "Reseller");

/**
 * `...Mb` ADI YANILTICI: sunucu bu sayıyı `bayt / (1024*1024)` ile üretiyor
 * (`CustomerScopeService.GetDiskUsageMbAsync`) ve kotayı `MaxDiskMb * 1024 * 1024` ile
 * uyguluyor (`FileQuotaGuard`) — yani alan MiB taşır, MB değil.
 */
const MIB = 1024 * 1024;

/**
 * MiB taşıyan sunucu alanı → bayt → panelin ORTAK biçimleyicisi ("gauge" hassasiyetiyle).
 *
 * NEDEN "gauge": buradaki sayı bir liste dolgusu değil, müşterinin SATIN ALDIĞI sınıra ne kadar
 * yaklaştığını gösteren kota okumasıdır; SubscriptionCard ve SiteHostingView'daki kota
 * göstergeleriyle aynı gerekçe ve aynı davranış (bkz. utils/bytes.ts). Bir kota ondalıklı,
 * diğeri ondalıksız görünemez.
 *
 * NEDEN ÇEVİRİYORUZ: satır önceden "512 / 5120 MB" basıyordu — sayılar 1024 tabanlı ama etiket
 * "MB" diyordu, yani kullanıcı %7 yanlış bir tabanla okuyordu.
 */
function formatQuotaMib(value: number) {
  return sharedFormatBytes(value * MIB, t, "gauge");
}

const selectedUserId = ref("");
const selectedUserIsCustomer = computed(() =>
  authStore.users.some((user) => user.id === selectedUserId.value && user.role === "Customer")
);
const unlockingUserId = ref("");
const showUserCreateForm = ref(false);
const showPlanForm = ref(false);

const createUserForm = reactive({
  userName: "",
  email: "",
  password: "",
  // Bayi YALNIZ müşteri oluşturabilir → rol seçimi gizli ve Customer'a sabit.
  role: isReseller.value ? "Customer" : "Operator",
  ownerId: "",
  assignedSites: [] as string[],
  maxSites: 0,
  maxDiskMb: 0,
  maxDatabases: 0,
  maxMailboxes: 0,
  maxScheduledTasks: 0,
  parentUserId: "",
  sendWelcomeEmail: false,
  preferredLanguage: "tr"
});

const selectedUserForm = reactive({
  email: "",
  role: "Operator",
  ownerId: "",
  assignedSites: [] as string[],
  maxSites: 0,
  maxDiskMb: 0,
  maxDatabases: 0,
  maxMailboxes: 0,
  maxScheduledTasks: 0,
  parentUserId: "",
  preferredLanguage: "tr",
  isActive: true
});

// Müşteriye sahip atamak için bayi (Reseller) kullanıcı listesi.
const resellers = computed(() => authStore.users.filter((user) => user.role === "Reseller"));

// Alt-kullanıcı için "ana abonelik" adayları: kendisi alt-kullanıcı OLMAYAN (bağımsız) müşteriler.
const parentCandidates = computed(() => authStore.users.filter((user) => user.role === "Customer" && !user.parentUserId));

// Servis planları (Servis Planlari): müşterilere atanabilen limit şablonları.
const servicePlans = ref<ServicePlan[]>([]);
// Yüklenmemiş plan listesi "plan yok" olarak görünüyordu.
const plansLoaded = ref(false);
/** Plan listesi ÇEKİLEMEDİ. Boş listeyle karıştırılmamalı: biri "yok", diğeri "bilmiyoruz". */
const plansError = ref("");
// Formun BOS hali tek kaynakta: hem baslangic hem de sifirlama bunu kullanir. Iki ayri elle
// yazilmis liste tutmak, birinin guncellenip digerinin unutulmasi demekti.
const BOS_PLAN_FORMU = {
  name: "",
  maxSites: 0,
  maxDiskMb: 0,
  maxDatabases: 0,
  maxMailboxes: 0,
  maxScheduledTasks: 0,
  maxFtpUsers: 0,
  maxTrafficGbPerMonth: 0,
  validityDays: 0,
  // Bu alan formda YOKSA `ServicePlanRequest.MaxBackupsPerDay` her kayıtta 0'a düşer ve
  // aboneliklerin günlük yedek kotası sessizce kalkar. Aynı sınıftan bir unutma trafik kotasını
  // ölü bırakmıştı; istek `planForm`un KENDİSİNDEN türediği için alan burada tanımlı olmalıdır.
  maxBackupsPerDay: 0
};
const planForm = reactive({ ...BOS_PLAN_FORMU });
/** Dolu ise form YENI plan degil, bu planin duzenlemesidir. */
const duzenlenenPlanId = ref<string | null>(null);
const planKaydediliyor = ref(false);
const createPlanId = ref("");

onMounted(async () => {
  // Kabuk (SetupWizardView) admin/bayi girişinde listeyi zaten doldurur; doğrudan bu ekrana
  // düşüldüğünde (veya liste boşsa) kendi verisini çeker.
  if (authStore.users.length === 0) {
    await authStore.fetchUsers().catch((error: unknown) => notify(resolveApiMessage(error), true));
  }

  if (sitesStore.sites.length === 0) {
    await sitesStore.fetchSites().catch(() => undefined);
  }

  if (isAdmin.value) {
    await loadServicePlans();
  }
});

async function loadServicePlans() {
  // HATA YUTULMUYORDU: `.catch(() => [])` istek başarısız olduğunda BOŞ liste döndürüyor,
  // ekran da "tanımlı servis planı yok" yazıyordu. Yani var olan planlar YOK gibi görünüyor,
  // yönetici yenisini oluşturmaya kalkıyor ve aynı adla çakışma alıyordu. "Ölçemedik" ile
  // "yok" ayrı şeyler; panelin geri kalanı bu ayrımı yapıyor, burası yapmıyordu.
  try {
    servicePlans.value = await getServicePlans();
    plansError.value = "";
  } catch (error) {
    servicePlans.value = [];
    plansError.value = resolveApiMessage(error);
  } finally {
    plansLoaded.value = true;
  }
}

// Hata yakalama YOKTU: sunucu planı reddettiğinde (aynı ad, geçersiz kota) form sessizce
// kapanıyor ve kullanıcı planın oluştuğunu sanıyordu. Form ancak istek BAŞARILI dönünce sıfırlanır.
function planFormunuSifirla() {
  Object.assign(planForm, BOS_PLAN_FORMU);
  duzenlenenPlanId.value = null;
}

function planFormunuAcKapa() {
  if (showPlanForm.value) {
    showPlanForm.value = false;
    // Kapatirken duzenleme kipinden de cikilir; aksi halde bir sonraki "Plan ekle" tiklamasi
    // eski planin degerleriyle dolu bir form acar ve kullanici yeni plan olusturdugunu sanirdi.
    planFormunuSifirla();
    return;
  }
  planFormunuSifirla();
  showPlanForm.value = true;
}

function planiDuzenle(plan: ServicePlan) {
  duzenlenenPlanId.value = plan.id;
  // Alanlar tek tek degil, planin KENDISINDEN kopyalaniyor: yeni bir limit eklendiginde
  // burada da unutulup bos gitmesin.
  Object.assign(planForm, BOS_PLAN_FORMU);
  for (const alan of Object.keys(BOS_PLAN_FORMU) as (keyof typeof BOS_PLAN_FORMU)[]) {
    const deger = (plan as unknown as Record<string, unknown>)[alan];
    if (deger !== undefined && deger !== null) {
      (planForm as Record<string, unknown>)[alan] = deger;
    }
  }
  showPlanForm.value = true;
}

async function planiKaydet() {
  if (!planForm.name.trim()) {
    return;
  }

  planKaydediliyor.value = true;
  try {
    // Alanlari ELLE saymak tam da su hataya yol acmisti: plana FTP hesabi, aylik trafik ve
    // gecerlilik eklendi, bu liste guncellenmedi. Kullanici "50 GB" yaziyordu, istege 0 gidiyordu,
    // API "basarili" donuyordu ve kota konuldugu sanilirken hicbir zaman uygulanmiyordu.
    // Istek artik formun KENDISINDEN turuyor: yeni alan eklenir eklenmez gonderilir.
    const istek = { ...planForm, name: planForm.name.trim() };
    if (duzenlenenPlanId.value) {
      await updateServicePlan(duzenlenenPlanId.value, istek);
    } else {
      await createServicePlan(istek);
    }
    planFormunuSifirla();
    showPlanForm.value = false;
    await loadServicePlans();
  } catch (error) {
    notify(resolveApiMessage(error), true);
  } finally {
    planKaydediliyor.value = false;
  }
}

/**
 * Onay + BAŞARI bildirimi eklendi. Öncesinde ikisi de yoktu: kullanıcı tek tıkla planı siliyor,
 * ekranda hiçbir şey yazmıyordu — "sildim mi, silemedim mi?" ayırt edilemiyordu.
 */
async function removeServicePlan(plan: ServicePlan) {
  await confirm({
    title: t("panel.plans.confirmDeleteTitle"),
    message: t("panel.plans.confirmDeleteMessage", { name: plan.name }),
    impact: [plan.name],
    action: async () => {
      try {
        await deleteServicePlan(plan.id);
        await loadServicePlans();
        notify(t("panel.plans.deleted"), false);
      } catch (error) {
        notify(resolveApiMessage(error), true);
      }
    }
  });
}

function applyPlanToCreate() {
  const plan = servicePlans.value.find((item) => item.id === createPlanId.value);
  if (!plan) {
    return;
  }
  // Alanlar ELLE sayılmıyor: plan ile formun ORTAK limit alanları taranıyor. Elle yazılan liste,
  // plana yeni bir limit eklendiğinde sessizce eksik kalır ve kullanıcı planı seçtiği hâlde
  // o limitin dolmadığını fark etmezdi.
  for (const alan of Object.keys(plan) as (keyof typeof plan)[]) {
    if (alan === "id" || alan === "name") {
      continue;
    }
    if (alan in createUserForm && typeof plan[alan] === "number") {
      (createUserForm as unknown as Record<string, number>)[alan] = plan[alan] as number;
    }
  }
}

async function createUser() {
  try {
    await authStore.createUser({ ...createUserForm, ownerId: createUserForm.ownerId || null, parentUserId: createUserForm.parentUserId || null });
    createUserForm.userName = "";
    createUserForm.email = "";
    createUserForm.password = "";
    // Bayi YALNIZ müşteri oluşturabilir → rolü Customer'da tut (yoksa kota/site/plan alanları gizlenir).
    createUserForm.role = isReseller.value ? "Customer" : "Operator";
    createUserForm.ownerId = "";
    createUserForm.assignedSites = [];
    createUserForm.maxSites = 0;
    createUserForm.maxDiskMb = 0;
    createUserForm.maxDatabases = 0;
    createUserForm.maxMailboxes = 0;
    createUserForm.maxScheduledTasks = 0;
    createUserForm.parentUserId = "";
    createUserForm.sendWelcomeEmail = false;
    createUserForm.preferredLanguage = "tr";
    showUserCreateForm.value = false;
    notify(t("panel.users.created"), false);
  } catch (error) {
    notify(resolveApiMessage(error), true);
  }
}

async function impersonateUser(user: ManagedUser) {
  try {
    await authStore.impersonate(user.id);
    // Müşteri arayüzüne temiz geçiş için paneli yeniden yükle.
    window.location.reload();
  } catch (error) {
    // Hata YUTULMAZ. Önceden sessizce geçiliyordu: istek 403/kilitli hesap/ağ hatasıyla dönünce
    // reload çalışmıyor, ekranda tek satır uyarı çıkmıyordu — kullanıcı düğmeye basıp hiçbir şey
    // olmadığını görüyor ve tekrar tekrar basıyordu. Bu dosyadaki diğer tüm işlemler zaten
    // notify(...) kullanıyor; burası tek istisnaydı.
    notify(resolveApiMessage(error), true);
  }
}

// Kullanıcı silme (Plesk paritesi): varsayılan davranış siteleri/verileri SUNUCUDA BIRAKIR (yönetime devir).
// Site+dosya+DB'yi de silmek isteyen admin, Web Siteleri ekranındaki cascade silmeyi kullanır.
async function deleteUser(user: ManagedUser) {
  // Yüksek riskli mod: kullanıcı silme alt-kullanıcıları, cron görevlerini ve DB sahipliklerini de
  // temizler. Ne kaybedileceğini SAYARAK göster ve adı yazdır — refleksle onaylanacak bir işlem değil.
  const impact = [
    `${user.userName} · ${user.email}`,
    `${t("panel.users.sitesShort")}: ${user.assignedSites?.length ?? 0}`,
    `${t("panel.users.databasesShort")}: ${user.databaseCount ?? 0}`
  ];

  await confirm({
    title: t("panel.confirm.deleteUserTitle"),
    // Yerel t() sarmalayıcısı parametre almaz; ad enterpolasyonu için i18n.t doğrudan kullanılır.
    message: i18n.t("panel.users.confirmDelete", { name: user.userName }) as string,
    impact,
    requireText: user.userName,
    action: async () => {
      try {
        const sonuc = await authStore.deleteUser(user.id);
        if (selectedUserId.value === user.id) {
          selectedUserId.value = "";
        }

        // KALINTILAR ARTIK GÖRÜNÜYOR. Silme başarılı olsa bile sunucuda kalanlar (site klasörleri,
        // veritabanları) ADIYLA yazılır: eskiden ekran "kullanıcı silindi" deyip susuyordu ve
        // yönetici, sahibi kalmamış onlarca kaynağın orada durduğunu hiç öğrenmiyordu.
        const kalintilar = sonuc?.leftovers ?? [];
        notify(`${t("panel.users.deleted")}${kalintiMetni(kalintilar)}`, false);
      } catch (error) {
        // 409 (kısmi silme) da buraya düşer; gövdedeki kalıntı listesi hata metnine eklenir.
        notify(`${resolveApiMessage(error)}${kalintiMetni(extractDeletionLeftovers(error))}`, true);
      }
    }
  });
}

/**
 * Kalıntı listesini okunabilir satırlara çevirir. Boş listede BOŞ dizge döner ki mesaj
 * gereksiz yere şişmesin.
 *
 * "Bir şeyler kaldı" demek yöneticiyi aramaya gönderir; burada TİP + AD yazılır (hangi site,
 * hangi veritabanı, hangi alan adı) çünkü temizliği yapacak kişinin ihtiyacı olan tek şey odur.
 */
function kalintiMetni(leftovers: UserDeletionLeftover[]): string {
  if (leftovers.length === 0) {
    return "";
  }

  const satirlar = leftovers.map((leftover) => {
    const site = leftover.siteName ? `${leftover.siteName} › ` : "";
    // Sunucu yeni bir kaynak tipi eklerse ekranda HAM ANAHTAR görünmesin: çeviri yoksa
    // genel "Kaynak" başlığına düşülür — ad ve sebep zaten yazılıyor, bilgi kaybolmaz.
    const tipAnahtari = `panel.users.leftoverKind.${leftover.kind}`;
    const cevrilen = t(tipAnahtari);
    const tip = cevrilen === tipAnahtari ? t("panel.users.leftoverKind.other") : cevrilen;
    const bilincli = leftover.deliberate ? ` (${t("panel.users.leftoverDeliberate")})` : "";
    return `• ${site}${tip}: ${leftover.name} — ${leftover.reason}${bilincli}`;
  });

  return `\n\n${t("panel.users.leftoversTitle")}\n${satirlar.join("\n")}`;
}

// Hatalı giriş kilidini beklemeden açar (yönetici kısayolu; bayi yalnız kendi müşterisini açabilir).
async function unlockUser(user: ManagedUser) {
  unlockingUserId.value = user.id;
  try {
    await authStore.unlockUser(user.id);
    notify(t("panel.users.unlocked"), false);
  } catch (error) {
    notify(resolveApiMessage(error), true);
  } finally {
    unlockingUserId.value = "";
  }
}

function selectUser(user: ManagedUser) {
  selectedUserId.value = user.id;
  selectedUserForm.email = user.email;
  selectedUserForm.role = user.role;
  selectedUserForm.ownerId = user.ownerId ?? "";
  selectedUserForm.assignedSites = [...(user.assignedSites ?? [])];
  selectedUserForm.maxSites = user.maxSites ?? 0;
  selectedUserForm.maxDiskMb = user.maxDiskMb ?? 0;
  selectedUserForm.maxDatabases = user.maxDatabases ?? 0;
  selectedUserForm.maxMailboxes = user.maxMailboxes ?? 0;
  selectedUserForm.maxScheduledTasks = user.maxScheduledTasks ?? 0;
  selectedUserForm.parentUserId = user.parentUserId ?? "";
  selectedUserForm.preferredLanguage = user.preferredLanguage;
  selectedUserForm.isActive = user.isActive;
}

async function saveUserChanges() {
  if (!selectedUserId.value) {
    return;
  }

  try {
    await authStore.updateUser(selectedUserId.value, { ...selectedUserForm, ownerId: selectedUserForm.ownerId || null });
    if (selectedUserId.value === authStore.session?.user?.id) {
      await authStore.fetchSession();
    }
    notify(t("panel.users.saved"), false);
  } catch (error) {
    notify(resolveApiMessage(error), true);
  }
}

function notify(message: string, isError: boolean) {
  emit("notify", message, isError);
}

function resolveApiMessage(error: unknown): string {
  const key = extractApiMessage(error);
  const translated = t(key);
  const message = translated === key ? key : translated;
  const details = extractApiDetails(error);
  return details ? `${message}\n\n${details}` : message;
}
</script>

<style scoped>
.users-workspace {
  align-content: start;
}

.customer-sites {
  grid-column: 1 / -1;
  display: grid;
  gap: 6px;
  max-height: 180px;
  overflow-y: auto;
  padding: 10px;
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-sm);
}

.customer-sites .checkbox {
  display: inline-flex;
  align-items: center;
  gap: 8px;
}

.customer-sites .checkbox input {
  width: auto;
}

.settings-wide-field {
  grid-column: 1 / -1;
}

.user-row {
  grid-template-columns: minmax(220px, 1.5fr) 0.75fr 0.7fr 0.7fr auto;
}

.user-status-cell {
  display: flex;
  flex-wrap: wrap;
  gap: 5px;
  min-width: 0;
}

.user-row-actions {
  display: inline-flex;
  gap: 6px;
  flex-wrap: wrap;
  justify-content: flex-end;
}

.plans-card {
  display: grid;
  gap: 14px;
}

.plans-head {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
}

.plans-grid {
  /* 190px, plan adı + "Sil" düğmesinin YAN YANA sığması için dardı: ad üç satıra bölünüyor ve
     kart bozuk görünüyordu. 240px, tipik plan adlarını tek satırda tutuyor. */
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(240px, 1fr));
  gap: 10px;
}

.plan-card {
  border: 1px solid var(--panel-border);
  border-radius: 12px;
  background: var(--surface-1);
  padding: 12px 14px;
  display: grid;
  gap: 10px;
  align-content: start;
}

.plan-card-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
}

.plan-card-head .plan-name {
  font-size: 0.98rem;
  min-width: 0;
  /* `anywhere` DEĞİL: "E2E Başlangıç" adını "Baslang" / "ic" diye kelime ortasından bölüyordu ve
     plan adı bozuk görünüyordu. `break-word` yalnızca kelimenin kendisi satıra sığmadığında böler. */
  overflow-wrap: break-word;
}

/* Düğmeler ne büyür ne küçülür: adın uzunluğu değiştikçe kartlar arasında yerleri kaymasın.
   Bu ancak düğmeler DOĞAL genişlikteyken güvenlidir; görünümdeki `button { width: 100% }`
   kuralıyla birlikteyken düğme kartı doldurup adı harf harf alt alta düşürüyordu. */
.plan-card-actions {
  display: flex;
  flex: none;
  gap: 6px;
}

.plan-card-head button {
  flex: none;
  white-space: nowrap;
}

.plan-limits {
  list-style: none;
  margin: 0;
  padding: 0;
  display: grid;
  gap: 6px;
}

.plan-limits li {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
  font-size: 0.84rem;
  padding: 4px 0;
  border-top: 1px solid color-mix(in srgb, var(--panel-border) 60%, transparent);
}

.plan-limits li:first-child {
  border-top: none;
}

.plan-limits li span {
  color: var(--text-muted);
}

.plan-limits li b {
  font-weight: 700;
}

.plans-empty {
  padding: 14px 0;
}

.plan-form-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(150px, 1fr));
  gap: 12px;
  /* USTTEN hizala. "end" iken, altinda ipuc yazisi olan hucreler (aylik trafik, gecerlilik)
     digerlerinden uzun oldugu icin girdileri yukari kaciyor ve etiketler kademeli duruyordu. */
  align-items: start;
  padding-top: 14px;
  border-top: 1px solid var(--panel-border);
}

.plan-form-grid .plan-name-cell {
  grid-column: 1 / -1;
}

.plan-form-hint {
  grid-column: 1 / -1;
  margin: 0;
  color: var(--text-muted);
  font-size: 0.8rem;
}

.plan-form-actions {
  grid-column: 1 / -1;
  display: flex;
  justify-content: flex-end;
}

.customer-meta {
  color: var(--accent-text);
}

.form-submit-cell {
  display: flex;
  align-items: end;
}

.form-submit-cell button {
  width: auto;
}

.user-edit-grid {
  align-items: end;
}

/* Onay kutusu satirlari DISARIDA: bunlar iki satirlik bir izgara degil, yan yana duran
   kutu+etikettir. Kapsamli `label` kurali element duzeyinde oldugu icin global
   `.check-field` kuralini eziyor, onay kutusu hucreyi kaplayip etiketi alt satira dusuruyordu.
   `:where()` KULLANILIYOR, duz `:not()` DEGIL: `:not(.x)` argumaninin ozgullugunu kurala EKLER
   ((0,1,1) -> (0,2,1)) ve boylece bilesenlerin kendi `.checkbox-field[data-v]` (0,2,0) kurallarini
   ezer -- SiteAliases'in onay kutulari tam olarak boyle bozulmustu. `:where()` her zaman SIFIR
   ozgullukte oldugu icin dislama yapar ama kurali guclendirmez. */
label:not(:where(.check-field, .checkbox-field, .checkbox-row)) {
  display: grid;
  gap: 8px;
}

.checkbox {
  grid-template-columns: auto 1fr;
  align-items: center;
}

.checkbox input {
  width: 18px;
  height: 18px;
  margin: 0;
}

.checkbox span {
  margin-left: 8px;
}

/* FORM ALANLARI gerilir, DÜĞMELER gerilmez.
   Bu kural düğmeleri de kapsıyordu ve görünümdeki HER düğmeyi tam genişlik yapıyordu: "Plan ekle"
   ve "Kullanıcı oluştur" sayfayı baştan sona kaplıyor, plan kartlarındaki "Sil" düğmesi ise kartı
   doldurup plan adını harf harf alt alta düşürüyordu. İstisna listesi (.row-button, .row-actions)
   yalnız satır içi düğmeleri kurtarıyor, geri kalanı kurtarmıyordu.
   Tam genişlik isteyen düğme bunu KENDİ sınıfıyla söyler (aşağıdaki .wide-button). */
input:not([type="checkbox"]):not([type="radio"]),
select,
textarea {
  width: 100%;
}

.wide-button {
  width: 100%;
}

.user-create-grid {
  grid-template-columns: repeat(3, minmax(0, 1fr));
}

.quota-hint {
  grid-column: 1 / -1;
  margin: 0;
  color: var(--text-muted);
  font-size: 0.8rem;
}

@media (max-width: 980px) {
  .user-create-grid,
  .user-edit-grid {
    grid-template-columns: 1fr;
  }
}

@media (max-width: 760px) {
  .settings-wide-field {
    grid-column: auto;
  }
}
</style>
