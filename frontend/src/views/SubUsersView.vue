<template>
  <section class="panel module-shell">
    <div class="module-head">
      <div>
        <h3>{{ t("panel.subUsers.title") }}</h3>
        <p class="meta">{{ t("panel.subUsers.subtitle") }}</p>
      </div>
      <button class="secondary compact" :disabled="loading" @click="load">{{ t("panel.database.refresh") }}</button>
    </div>

    <p v-if="notice" :class="['notice', { error: noticeError }]">{{ notice }}</p>

    <form class="form-surface sub-user-form" @submit.prevent="save">
      <h4>{{ editingId ? t("panel.subUsers.editTitle") : t("panel.subUsers.createTitle") }}</h4>
      <div class="form-grid">
        <label v-if="!editingId" class="stacked-field">
          <span class="field-label">{{ t("panel.subUsers.userName") }}</span>
          <input v-model.trim="form.userName" autocomplete="off" />
        </label>
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.subUsers.email") }}</span>
          <input v-model.trim="form.email" type="email" autocomplete="off" />
        </label>
        <label class="stacked-field">
          <span class="field-label">{{ editingId ? t("panel.subUsers.passwordOptional") : t("panel.subUsers.password") }}</span>
          <input v-model="form.password" type="password" autocomplete="new-password" />
        </label>
        <label v-if="editingId" class="stacked-field">
          <span class="field-label">{{ t("panel.subUsers.active") }}</span>
          <select v-model="form.isActive">
            <option :value="true">{{ t("panel.status.enabled") }}</option>
            <option :value="false">{{ t("panel.status.disabled") }}</option>
          </select>
        </label>
      </div>

      <fieldset class="permission-set">
        <legend class="field-label">{{ t("panel.subUsers.permissions") }}</legend>
        <p class="meta">{{ t("panel.subUsers.permissionsHint") }}</p>
        <div class="permission-grid">
          <label v-for="section in SUB_USER_SECTIONS" :key="section" class="permission-item">
            <input v-model="form.sections" type="checkbox" :value="section" />
            <span>{{ sectionLabel(section) }}</span>
          </label>
        </div>
      </fieldset>

      <div class="row-actions">
        <button class="compact" type="submit" :disabled="busy || !canSubmit">
          {{ busy ? t("panel.database.saving") : (editingId ? t("panel.subUsers.saveChanges") : t("panel.subUsers.create")) }}
        </button>
        <button v-if="editingId" class="secondary compact" type="button" :disabled="busy" @click="resetForm">
          {{ t("panel.subUsers.cancelEdit") }}
        </button>
      </div>
    </form>

    <div class="data-table">
      <div class="data-row table-head sub-user-row">
        <span>{{ t("panel.subUsers.userName") }}</span>
        <span>{{ t("panel.subUsers.email") }}</span>
        <span>{{ t("panel.subUsers.permissions") }}</span>
        <span>{{ t("panel.subUsers.status") }}</span>
        <span>{{ t("panel.files.actions") }}</span>
      </div>
      <div v-for="user in subUsers" :key="user.id" class="data-row sub-user-row">
        <span><strong>{{ user.userName }}</strong></span>
        <span>{{ user.email }}</span>
        <span>{{ permissionSummary(user) }}</span>
        <span :class="user.isActive ? 'status-ok' : 'status-off'">
          {{ user.isActive ? t("panel.status.enabled") : t("panel.status.disabled") }}
        </span>
        <span class="row-actions">
          <button class="secondary compact" :disabled="busy" @click="startEdit(user)">{{ t("panel.subUsers.edit") }}</button>
          <button class="danger compact" :disabled="busy" @click="remove(user)">{{ t("panel.subUsers.delete") }}</button>
        </span>
      </div>
      <p v-if="loading && subUsers.length === 0" class="empty-state">{{ t("panel.common.loading") }}</p>
      <p v-else-if="subUsers.length === 0" class="empty-state">{{ t("panel.subUsers.empty") }}</p>
    </div>
  </section>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref } from "vue";
import { useI18n } from "vue-i18n";
import {
  SUB_USER_SECTIONS,
  createSubUser,
  deleteSubUser,
  fetchSubUsers,
  updateSubUser,
  type SubUser
} from "../api/subUsers";
import { useConfirm } from "../components/ConfirmDialog.vue";
import { extractApiDetails, extractApiMessage } from "../stores/auth";

const { t } = useI18n();
const { confirm } = useConfirm();
const subUsers = ref<SubUser[]>([]);
const loading = ref(false);
const busy = ref(false);
const notice = ref("");
const noticeError = ref(false);
const editingId = ref<string | null>(null);
const form = reactive({
  userName: "",
  email: "",
  password: "",
  isActive: true,
  sections: [...SUB_USER_SECTIONS] as string[]
});

const canSubmit = computed(() => {
  if (editingId.value) {
    return form.email.length > 0;
  }
  return form.userName.length >= 3 && form.email.length > 0 && form.password.length >= 12;
});

onMounted(load);

async function load() {
  loading.value = true;
  try {
    subUsers.value = await fetchSubUsers();
  } catch (error) {
    showNotice(resolveMessage(error), true);
  } finally {
    loading.value = false;
  }
}

async function save() {
  busy.value = true;
  try {
    // Tüm bölümler seçiliyse null gönderilir (kısıtsız); backend de aynı normalizasyonu yapar.
    const sections = form.sections.length >= SUB_USER_SECTIONS.length ? null : [...form.sections];
    if (editingId.value) {
      await updateSubUser(editingId.value, {
        email: form.email,
        password: form.password || null,
        isActive: form.isActive,
        allowedSections: sections
      });
      showNotice(t("panel.subUsers.updated"), false);
    } else {
      await createSubUser({
        userName: form.userName,
        email: form.email,
        password: form.password,
        allowedSections: sections
      });
      showNotice(t("panel.subUsers.created"), false);
    }
    resetForm();
    await load();
  } catch (error) {
    showNotice(resolveMessage(error), true);
  } finally {
    busy.value = false;
  }
}

function startEdit(user: SubUser) {
  editingId.value = user.id;
  form.userName = user.userName;
  form.email = user.email;
  form.password = "";
  form.isActive = user.isActive;
  form.sections = user.allowedSections ? [...user.allowedSections] : [...SUB_USER_SECTIONS];
}

async function remove(user: SubUser) {
  const impact = [`${t("panel.subUsers.userName")}: ${user.userName}`];
  if (user.email) {
    impact.push(`${t("panel.subUsers.email")}: ${user.email}`);
  }

  // Normal onay: yalnız giriş hesabı kaldırılır; site/veri silinmez → ad yazdırmaya gerek yok.
  await confirm({
    title: t("panel.confirm.deleteSubUserTitle", { name: user.userName }),
    message: t("panel.subUsers.confirmDelete", { name: user.userName }),
    impact,
    action: async () => {
      busy.value = true;
      try {
        await deleteSubUser(user.id);
        if (editingId.value === user.id) {
          resetForm();
        }
        showNotice(t("panel.subUsers.deleted"), false);
        await load();
      } catch (error) {
        showNotice(resolveMessage(error), true);
      } finally {
        busy.value = false;
      }
    },
  });
}

function resetForm() {
  editingId.value = null;
  form.userName = "";
  form.email = "";
  form.password = "";
  form.isActive = true;
  form.sections = [...SUB_USER_SECTIONS];
}

function sectionLabel(section: string) {
  const keyBySection: Record<string, string> = {
    sites: "panel.navigation.sites",
    mail: "panel.navigation.mail",
    database: "panel.navigation.database",
    files: "panel.navigation.files",
    backup: "panel.navigation.backup",
    tasks: "panel.navigation.tasks"
  };
  return t(keyBySection[section] ?? section);
}

function permissionSummary(user: SubUser) {
  if (!user.allowedSections) {
    return t("panel.subUsers.fullAccess");
  }
  if (user.allowedSections.length === 0) {
    return t("panel.subUsers.noAccess");
  }
  return user.allowedSections.map(sectionLabel).join(", ");
}

function resolveMessage(error: unknown) {
  const key = extractApiMessage(error);
  const translated = t(key);
  const message = translated === key ? key : translated;
  const details = extractApiDetails(error);
  return details ? `${message} — ${details}` : message;
}

function showNotice(message: string, isError: boolean) {
  notice.value = message;
  noticeError.value = isError;
}
</script>

<style scoped>
.sub-user-form { margin-bottom: 14px; }
.form-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(180px, 1fr)); gap: 12px; }
.stacked-field { display: grid; gap: 6px; }
.permission-set { border: 1px solid var(--border, #d7dce3); border-radius: 8px; margin-top: 12px; padding: 10px 12px; }
.permission-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(140px, 1fr)); gap: 8px; margin-top: 8px; }
.permission-item { align-items: center; display: flex; gap: 8px; }
.sub-user-row { grid-template-columns: 1fr 1.4fr 1.6fr 0.7fr 1fr; }
.row-actions { display: flex; gap: 8px; margin-top: 12px; }
.row-actions button { width: auto; }
.data-row .row-actions { margin-top: 0; }
.status-ok { color: var(--success, #1a7f37); }
.status-off { color: var(--danger, #b42318); }
</style>
