<template>
  <section class="panel module-shell users-workspace">
    <!-- Servis planları (panel): limit şablonu tanımla, müşteriye ata. Yalnız admin. -->
    <div v-if="isAdmin" class="form-surface plans-card">
      <div class="plans-head">
        <div>
          <strong>{{ t("panel.plans.title") }}</strong>
          <p class="meta">{{ t("panel.plans.hint") }}</p>
        </div>
        <button type="button" class="compact" @click="showPlanForm = !showPlanForm">
          {{ showPlanForm ? t("panel.common.cancel") : t("panel.plans.add") }}
        </button>
      </div>

      <div v-if="servicePlans.length" class="plans-grid">
        <article v-for="plan in servicePlans" :key="plan.id" class="plan-card">
          <div class="plan-card-head">
            <strong class="plan-name">{{ plan.name }}</strong>
            <button class="secondary compact danger" @click="removeServicePlan(plan.id)">{{ t("panel.common.delete") }}</button>
          </div>
          <ul class="plan-limits">
            <li><span>{{ t("panel.plans.sitesLabel") }}</span><b>{{ plan.maxSites || "∞" }}</b></li>
            <li><span>{{ t("panel.plans.diskLabel") }}</span><b>{{ plan.maxDiskMb ? plan.maxDiskMb + " MB" : "∞" }}</b></li>
            <li><span>{{ t("panel.plans.databasesLabel") }}</span><b>{{ plan.maxDatabases || "∞" }}</b></li>
            <li><span>{{ t("panel.plans.mailboxesLabel") }}</span><b>{{ plan.maxMailboxes || "∞" }}</b></li>
            <li><span>{{ t("panel.plans.scheduledTasksLabel") }}</span><b>{{ plan.maxScheduledTasks || "∞" }}</b></li>
          </ul>
        </article>
      </div>
      <!-- Planlar gelmeden "plan yok" yazıyordu; tanımlı servis planları yokmuş gibi görünüyordu. -->
      <p v-else-if="!plansLoaded" class="empty-state plans-empty" role="status" aria-live="polite">
        {{ t("panel.common.loading") }}
      </p>
      <p v-else class="empty-state plans-empty">{{ t("panel.plans.empty") }}</p>

      <form v-if="showPlanForm" class="plan-form-grid" @submit.prevent="addServicePlan">
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
        <p class="plan-form-hint">{{ t("panel.plans.formHint") }}</p>
        <div class="plan-form-actions">
          <button type="submit" class="compact" :disabled="!planForm.name">{{ t("panel.plans.add") }}</button>
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
        <input v-model="createUserForm.password" type="password" minlength="12" required />
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
            · {{ user.diskUsageMb }} / {{ user.maxDiskMb > 0 ? user.maxDiskMb : "∞" }} MB
            · {{ user.databaseCount }} / {{ user.maxDatabases > 0 ? user.maxDatabases : "∞" }} {{ t("panel.users.databasesShort") }}
            <template v-if="user.ownerName"> · {{ t("panel.users.owner") }}: {{ user.ownerName }}</template>
            <template v-if="user.parentName"> · {{ t("panel.users.subUserOf") }}: {{ user.parentName }}</template>
          </small>
        </div>
        <span>{{ user.role }}</span>
        <span :class="['status-pill', user.isActive ? 'ok' : 'warn']">
          {{ user.isActive ? t("panel.status.active") : t("panel.status.inactive") }}
        </span>
        <span :class="['status-pill', user.isTwoFactorEnabled ? 'ok' : 'warn']">
          {{ user.isTwoFactorEnabled ? t("panel.status.enabled") : t("panel.status.disabled") }}
        </span>
        <span v-if="user.lockedUntilUtc" class="status-pill danger" :title="t('panel.users.lockedHint')">
          {{ t("panel.users.locked") }}
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
import { useConfirm } from "../components/ConfirmDialog.vue";
import { getServicePlans, createServicePlan, deleteServicePlan, type ServicePlan } from "../api/servicePlans";
import { extractApiDetails, extractApiMessage, useAuthStore, type ManagedUser } from "../stores/auth";
import { useSitesStore } from "../stores/sites";

// Bildirimler üst bandda (MainLayout > banner) gösterilsin diye kabuk bileşene iletilir.
const emit = defineEmits<{ (event: "notify", message: string, isError: boolean): void }>();

const i18n = useI18n();
const t = (key: string) => {
  try {
    const value = i18n.t(key);
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

const selectedUserId = ref("");
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
const planForm = reactive({ name: "", maxSites: 0, maxDiskMb: 0, maxDatabases: 0, maxMailboxes: 0, maxScheduledTasks: 0 });
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
  servicePlans.value = await getServicePlans().catch(() => []);
  plansLoaded.value = true;
}

async function addServicePlan() {
  if (!planForm.name.trim()) {
    return;
  }
  await createServicePlan({ name: planForm.name.trim(), maxSites: planForm.maxSites, maxDiskMb: planForm.maxDiskMb, maxDatabases: planForm.maxDatabases, maxMailboxes: planForm.maxMailboxes, maxScheduledTasks: planForm.maxScheduledTasks });
  planForm.name = "";
  planForm.maxSites = 0;
  planForm.maxDiskMb = 0;
  planForm.maxDatabases = 0;
  planForm.maxMailboxes = 0;
  planForm.maxScheduledTasks = 0;
  showPlanForm.value = false;
  await loadServicePlans();
}

async function removeServicePlan(id: string) {
  await deleteServicePlan(id);
  await loadServicePlans();
}

function applyPlanToCreate() {
  const plan = servicePlans.value.find((item) => item.id === createPlanId.value);
  if (plan) {
    createUserForm.maxSites = plan.maxSites;
    createUserForm.maxDiskMb = plan.maxDiskMb;
    createUserForm.maxDatabases = plan.maxDatabases;
    createUserForm.maxMailboxes = plan.maxMailboxes;
    createUserForm.maxScheduledTasks = plan.maxScheduledTasks;
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
    void error;
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
        await authStore.deleteUser(user.id);
        if (selectedUserId.value === user.id) {
          selectedUserId.value = "";
        }
        notify(t("panel.users.deleted"), false);
      } catch (error) {
        notify(resolveApiMessage(error), true);
      }
    }
  });
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
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(190px, 1fr));
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
  overflow-wrap: anywhere;
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
  align-items: end;
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

label {
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

input,
select,
textarea,
button {
  width: 100%;
}

.row-button,
.row-actions button {
  width: auto;
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
