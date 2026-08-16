<template>
  <section class="panel module-shell">
    <div class="module-head">
      <div>
        <h2>{{ t("panel.notifications.title") }}</h2>
        <p>{{ t("panel.notifications.subtitle") }}</p>
      </div>
      <button class="secondary compact" :disabled="busy === 'load'" @click="loadSettings">
        {{ t("panel.notifications.refresh") }}
      </button>
    </div>

    <p v-if="notice" :class="['notice', 'page-notice', { error: noticeError }]" :role="noticeError ? 'alert' : 'status'">{{ notice }}</p>

    <!-- Ayarlar gelmeden form boş / "yapılandırılmamış" görünüyordu; kayıtlı SMTP'si olan
         yönetici bunu "ayar yok" sanıp boş formu üstüne kaydedebiliyordu. -->
    <p v-if="!settingsLoaded" class="empty-state" role="status" aria-live="polite">
      {{ t("panel.common.loading") }}
    </p>

    <template v-else>
    <div class="summary-strip">
      <div class="summary-item">
        <span>{{ t("panel.notifications.smtpStatus") }}</span>
        <strong>{{ smtpReady ? t("panel.notifications.configured") : t("panel.notifications.notConfigured") }}</strong>
      </div>
      <div class="summary-item">
        <span>{{ t("panel.notifications.webhookStatus") }}</span>
        <strong>{{ form.webhookUrl ? t("panel.notifications.configured") : t("panel.notifications.notConfigured") }}</strong>
      </div>
      <div class="summary-item">
        <span>{{ t("panel.notifications.encryption") }}</span>
        <strong>{{ form.useSsl ? "TLS / SSL" : t("panel.notifications.disabled") }}</strong>
      </div>
      <div class="summary-item">
        <span>{{ t("panel.notifications.recipient") }}</span>
        <strong>{{ form.toEmail || "-" }}</strong>
      </div>
    </div>

    <div class="segmented-tabs">
      <button :class="{ active: activeTab === 'email' }" @click="activeTab = 'email'">
        {{ t("panel.notifications.emailTab") }}
      </button>
      <button :class="{ active: activeTab === 'webhook' }" @click="activeTab = 'webhook'">
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
          <input
            v-model="form.smtpPassword"
            type="password"
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
        <button v-bind="writeGate(busy === 'save')" @click="save">
          {{ busy === "save" ? t("panel.notifications.saving") : t("panel.notifications.save") }}
        </button>
        <button
          v-if="activeTab === 'email'"
          class="secondary compact"
          v-bind="writeGate(busy === 'email' || !smtpReady)"
          @click="test('email')">
          {{ busy === "email" ? t("panel.notifications.testing") : t("panel.notifications.testEmail") }}
        </button>
        <button
          v-else
          class="secondary compact"
          v-bind="writeGate(busy === 'webhook' || !form.webhookUrl)"
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
import { computed, onMounted, reactive, ref } from "vue";
import { useI18n } from "vue-i18n";
import { getNotificationSettings, saveNotificationSettings, sendNotificationTest } from "../api/notifications";
import { extractApiMessage } from "../stores/auth";
import { useSubscriptionGate } from "../composables/useSubscriptionGate";

const { t } = useI18n();
// Askidaki abonelikte YAZMA dugmeleri bastan devre disi + sebebi title/aria ile soylenir
// (bkz. composables/useSubscriptionGate.ts). Okuma dugmeleri kapiya girmez.
const { writeGate } = useSubscriptionGate();
const activeTab = ref<"email" | "webhook">("email");
const busy = ref("");
// Yüklenmemiş durumu "yapılandırılmamış/boş" göstermek yanıltıcıydı.
const settingsLoaded = ref(false);
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

const smtpReady = computed(() =>
  Boolean(form.smtpHost && form.fromEmail && form.toEmail));

onMounted(loadSettings);

async function loadSettings() {
  busy.value = "load";
  try {
    Object.assign(form, await getNotificationSettings(), { smtpPassword: "" });
    settingsLoaded.value = true;
    notice.value = "";
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    busy.value = "";
  }
}

async function save() {
  busy.value = "save";
  try {
    const saved = await saveNotificationSettings({ ...form });
    Object.assign(form, saved, { smtpPassword: "" });
    showNotice(t("panel.notifications.saved"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    busy.value = "";
  }
}

async function test(channel: "email" | "webhook") {
  busy.value = channel;
  try {
    await sendNotificationTest(channel, t("panel.notifications.testMessage"));
    showNotice(t("panel.notifications.testSent"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  } finally {
    busy.value = "";
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
