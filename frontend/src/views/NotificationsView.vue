<template>
  <section class="panel module-shell settings-editor">
    <div class="module-head">
      <div>
        <h2>{{ t("panel.notifications.title") }}</h2>
        <p>{{ t("panel.notifications.subtitle") }}</p>
      </div>
      <button class="secondary compact" :disabled="Boolean(busy)" @click="loadSettings">
        {{ t("panel.notifications.refresh") }}
      </button>
    </div>

    <p v-if="notice" :class="['notice', 'page-notice', { error: noticeError }]" :role="noticeError ? 'alert' : 'status'">{{ notice }}</p>

    <!-- Ayarlar gelmeden form boş / "yapılandırılmamış" görünüyordu; kayıtlı SMTP'si olan
         yönetici bunu "ayar yok" sanıp boş formu üstüne kaydedebiliyordu. -->
    <div v-if="loadError" class="settings-read-error" role="alert">
      <strong>{{ t("panel.notifications.loadFailed") }}</strong>
      <p>{{ loadError }}</p>
      <p>{{ t("panel.notifications.loadFailedHint") }}</p>
      <button class="secondary compact" :disabled="Boolean(busy)" @click="loadSettings">{{ t("panel.common.retry") }}</button>
    </div>
    <p v-else-if="!settingsLoaded" class="empty-state" role="status" aria-live="polite">
      {{ t("panel.common.loading") }}
    </p>

    <template v-if="settingsLoaded">
    <p class="settings-caption">{{ t(settingsConfirmed ? "panel.notifications.savedSettings" : "panel.notifications.lastKnownSettings") }}</p>
    <div class="summary-strip">
      <div class="summary-item">
        <span>{{ t("panel.notifications.smtpStatus") }}</span>
        <strong>{{ smtpReady ? t("panel.notifications.configured") : t("panel.notifications.notConfigured") }}</strong>
      </div>
      <div class="summary-item">
        <span>{{ t("panel.notifications.webhookStatus") }}</span>
        <strong>{{ savedSettings?.webhookUrl ? t("panel.notifications.configured") : t("panel.notifications.notConfigured") }}</strong>
      </div>
      <div class="summary-item">
        <span>{{ t("panel.notifications.encryption") }}</span>
        <strong>{{ savedSettings?.useSsl ? "TLS / SSL" : t("panel.notifications.disabled") }}</strong>
      </div>
      <div class="summary-item">
        <span>{{ t("panel.notifications.recipient") }}</span>
        <strong>{{ savedSettings?.toEmail || "-" }}</strong>
      </div>
    </div>

    <p v-if="hasUnsavedChanges" class="settings-draft-hint" role="status">{{ t("panel.notifications.saveBeforeTest") }}</p>
    <div class="segmented-tabs" role="tablist">
      <button role="tab" :aria-selected="activeTab === 'email'" :class="{ active: activeTab === 'email' }" @click="activeTab = 'email'">
        {{ t("panel.notifications.emailTab") }}
      </button>
      <button role="tab" :aria-selected="activeTab === 'webhook'" :class="{ active: activeTab === 'webhook' }" @click="activeTab = 'webhook'">
        {{ t("panel.notifications.webhookTab") }}
      </button>
    </div>

    <section v-if="activeTab === 'email'" class="form-surface">
      <div class="form-grid">
        <label>
          <span class="field-label">{{ t("panel.notifications.smtpHost") }}</span>
          <input v-model.trim="form.smtpHost" autocomplete="off" placeholder="smtp.example.com" />
        </label>
        <label>
          <span class="field-label">{{ t("panel.notifications.smtpPort") }}</span>
          <input v-model.number="form.smtpPort" type="number" min="1" max="65535" />
        </label>
        <label>
          <span class="field-label">{{ t("panel.notifications.smtpUser") }}</span>
          <input v-model.trim="form.smtpUserName" autocomplete="username" />
        </label>
        <label>
          <span class="field-label">{{ t("panel.notifications.smtpPassword") }}</span>
          <PasswordField
            v-model="form.smtpPassword"
            autocomplete="new-password"
            :placeholder="form.hasSmtpPassword ? t('panel.notifications.passwordSaved') : ''" />
          <small class="field-hint">{{ t("panel.notifications.passwordHint") }}</small>
        </label>
        <label>
          <span class="field-label">{{ t("panel.notifications.sender") }}</span>
          <input v-model.trim="form.fromEmail" type="email" placeholder="panel@example.com" />
        </label>
        <label>
          <span class="field-label">{{ t("panel.notifications.recipient") }}</span>
          <input v-model.trim="form.toEmail" type="email" placeholder="admin@example.com" />
        </label>
      </div>

      <label class="check-field">
        <input v-model="form.useSsl" type="checkbox" />
        <span>{{ t("panel.notifications.useSsl") }}</span>
      </label>
    </section>

    <section v-else class="form-surface">
      <label class="stacked-field">
        <span class="field-label">{{ t("panel.notifications.webhookUrl") }}</span>
        <input v-model.trim="form.webhookUrl" type="url" placeholder="https://example.com/hooks/panel" />
        <small class="field-hint">{{ t("panel.notifications.webhookHint") }}</small>
      </label>
    </section>

    <div class="module-toolbar">
      <div class="toolbar-group">
        <button v-bind="writeGate(!canSave)" @click="save">
          {{ busy === "save" ? t("panel.notifications.saving") : t("panel.notifications.save") }}
        </button>
        <button
          v-if="activeTab === 'email'"
          class="secondary compact"
          v-bind="writeGate(!canTestEmail)"
          @click="test('email')">
          {{ busy === "email" ? t("panel.notifications.testing") : t("panel.notifications.testEmail") }}
        </button>
        <button
          v-else
          class="secondary compact"
          v-bind="writeGate(!canTestWebhook)"
          @click="test('webhook')">
          {{ busy === "webhook" ? t("panel.notifications.testing") : t("panel.notifications.testWebhook") }}
        </button>
      </div>
      <span class="field-hint">{{ t("panel.notifications.testHint") }}</span>
    </div>
    </template>
  </section>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from "vue";
import { useI18n } from "vue-i18n";
import PasswordField from "../components/PasswordField.vue";
import { getNotificationSettings, saveNotificationSettings, sendNotificationTest, type NotificationSettings } from "../api/notifications";
import { extractApiMessage, useAuthStore } from "../stores/auth";
import { getSessionEpoch } from "../api/client";
import { useSubscriptionGate } from "../composables/useSubscriptionGate";

const { t } = useI18n();
// Askidaki abonelikte YAZMA dugmeleri bastan devre disi + sebebi title/aria ile soylenir
// (bkz. composables/useSubscriptionGate.ts). Okuma dugmeleri kapiya girmez.
const { writeGate, locked } = useSubscriptionGate();
const activeTab = ref<"email" | "webhook">("email");
const busy = ref("");
// Yüklenmemiş durumu "yapılandırılmamış/boş" göstermek yanıltıcıydı.
const settingsLoaded = ref(false);
const settingsConfirmed = ref(false);
const loadError = ref("");
const savedSettings = ref<NotificationSettings | null>(null);
const auth = useAuthStore(), mountedEpoch = getSessionEpoch(), actor = auth.session?.user?.id, role = auth.session?.user?.role;
let disposed = false, draftRevision = 0;
const current = () => !disposed && getSessionEpoch() === mountedEpoch && auth.session?.user?.id === actor && auth.session?.user?.role === role;
const notice = ref("");
const noticeError = ref(false);

const form = reactive({
  smtpHost: "",
  smtpPort: 587,
  smtpUserName: "",
  smtpPassword: "",
  hasSmtpPassword: false,
  fromEmail: "",
  toEmail: "",
  useSsl: true,
  webhookUrl: ""
});

watch(form, () => { draftRevision++; }, { deep: true, flush: "sync" });
const target = (value: NotificationSettings) => JSON.stringify([value.smtpHost.trim(), value.smtpPort, value.smtpUserName.trim(), value.fromEmail.trim(), value.toEmail.trim(), value.useSsl, value.webhookUrl.trim()]);
const hasUnsavedChanges = computed(() => !!savedSettings.value && (target(form) !== target(savedSettings.value) || !!form.smtpPassword));
const smtpReady = computed(() => Boolean(savedSettings.value?.smtpHost && savedSettings.value.fromEmail && savedSettings.value.toEmail));
const canSave = computed(() => settingsConfirmed.value && !busy.value && !locked.value && Number.isSafeInteger(form.smtpPort) && form.smtpPort >= 1 && form.smtpPort <= 65535);
const canTestEmail = computed(() => settingsConfirmed.value && !busy.value && !locked.value && !hasUnsavedChanges.value && smtpReady.value);
const canTestWebhook = computed(() => settingsConfirmed.value && !busy.value && !locked.value && !hasUnsavedChanges.value && !!savedSettings.value?.webhookUrl);

function publishSettings(value: NotificationSettings, revision: number, preserveDraft = false) {
  const measured = { ...value, smtpPassword: "" };
  savedSettings.value = measured;
  if (!preserveDraft && draftRevision === revision) Object.assign(form, measured);
  settingsLoaded.value = true;
  settingsConfirmed.value = true;
  loadError.value = "";
}

onMounted(loadSettings);
onBeforeUnmount(() => { disposed = true; form.smtpPassword = ""; });

async function loadSettings() {
  if (!current() || busy.value) return;
  const revision = draftRevision, preserveDraft = hasUnsavedChanges.value;
  busy.value = "load";
  try {
    const measured = await getNotificationSettings();
    if (!current()) return;
    publishSettings(measured, revision, preserveDraft);
    notice.value = "";
  } catch (error) {
    if (current()) { settingsConfirmed.value = false; loadError.value = resolveApiMessage(error); notice.value = ""; }
  } finally {
    if (current()) busy.value = "";
  }
}

async function save() {
  if (!current() || !canSave.value) return;
  const submitted = { ...form }, revision = draftRevision;
  busy.value = "save";
  try {
    const saved = await saveNotificationSettings(submitted);
    if (!current()) return;
    if (target(saved) !== target(submitted) || (!!submitted.smtpPassword && !saved.hasSmtpPassword)) {
      settingsConfirmed.value = false;
      showNotice(t("panel.notifications.resultUnconfirmed"), true);
      return;
    }
    publishSettings(saved, revision);
    showNotice(t("panel.notifications.saved"), false);
  } catch (error) {
    if (current()) { settingsConfirmed.value = false; showNotice(resolveApiMessage(error), true); }
  } finally {
    if (current()) busy.value = "";
  }
}

async function test(channel: "email" | "webhook") {
  if (!current() || !(channel === "email" ? canTestEmail.value : canTestWebhook.value)) return;
  const revision = draftRevision;
  busy.value = channel;
  try {
    await sendNotificationTest(channel, t("panel.notifications.testMessage"));
    if (current() && revision === draftRevision) showNotice(t("panel.notifications.testSent"), false);
  } catch (error) {
    if (current() && revision === draftRevision) showNotice(resolveApiMessage(error), true);
  } finally {
    if (current()) busy.value = "";
  }
}

function showNotice(message: string, isError: boolean) {
  notice.value = message;
  noticeError.value = isError;
}

function resolveApiMessage(error: unknown) {
  const key = extractApiMessage(error);
  const translated = t(key);
  return translated === key ? key : translated;
}
</script>
