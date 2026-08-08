<template>
  <section class="panel module-shell account-workspace">
    <div class="module-head">
      <div>
        <h2>{{ t("panel.settings.tab.account") }}</h2>
        <p class="meta">{{ authStore.session?.user?.userName }} · {{ authStore.session?.user?.email }}</p>
      </div>
    </div>

    <section class="form-surface settings-section">
      <div class="section-head">
        <div>
          <h3>{{ t("panel.account.title") }}</h3>
          <p class="meta">
            {{ t("panel.account.status") }}:
            {{ authStore.session?.user?.isTwoFactorEnabled ? t("panel.account.enabled") : t("panel.account.disabled") }}
          </p>
        </div>
        <span :class="['status-pill', authStore.session?.user?.isTwoFactorEnabled ? 'ok' : 'warn']">
          {{ authStore.session?.user?.isTwoFactorEnabled ? t("panel.account.enabled") : t("panel.account.disabled") }}
        </span>
      </div>

      <div v-if="authStore.twoFactorSetup" class="form-grid two">
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.account.manualKey") }}</span>
          <input :value="authStore.twoFactorSetup.manualEntryKey" readonly />
        </label>
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.account.verificationCode") }}</span>
          <input v-model="twoFactorVerificationCode" inputmode="numeric" maxlength="8" />
        </label>
        <label class="stacked-field settings-wide-field">
          <span class="field-label">{{ t("panel.account.provisioningUri") }}</span>
          <textarea :value="authStore.twoFactorSetup.provisioningUri" rows="3" readonly />
        </label>
        <div class="row-actions settings-wide-field">
          <button class="compact" @click="confirmTwoFactor">{{ t("panel.account.confirm2fa") }}</button>
        </div>
      </div>

      <div v-else class="settings-inline-action">
        <button v-if="!(authStore.session?.user?.isTwoFactorEnabled ?? false)" class="compact" @click="startTwoFactor">
          {{ t("panel.account.start2fa") }}
        </button>
        <template v-else>
          <button class="secondary compact" @click="regenerateRecoveryCodes">{{ t("panel.account.regenerateRecovery") }}</button>
          <label class="stacked-field">
            <span class="field-label">{{ t("panel.account.disablePassword") }}</span>
            <input v-model="disableTwoFactorPassword" type="password" minlength="12" />
          </label>
          <button class="danger compact" @click="disableTwoFactor">{{ t("panel.account.disable2fa") }}</button>
        </template>
      </div>

      <div v-if="recoveryCodes.length" class="recovery-codes">
        <strong>{{ t("panel.account.recoveryTitle") }}</strong>
        <p class="meta">{{ t("panel.account.recoveryHint") }}</p>
        <ul class="recovery-list">
          <li v-for="rc in recoveryCodes" :key="rc">{{ rc }}</li>
        </ul>
        <div class="row-actions">
          <button class="secondary compact" @click="copyRecoveryCodes">{{ t("panel.account.recoveryCopy") }}</button>
          <button class="secondary compact" @click="recoveryCodes = []">{{ t("panel.common.cancel") }}</button>
        </div>
      </div>
    </section>

    <section class="form-surface settings-section">
      <div class="section-head">
        <div>
          <h3>{{ t("panel.account.passwordTitle") }}</h3>
          <p class="meta">{{ t("panel.account.passwordHint") }}</p>
        </div>
      </div>

      <div class="form-grid two">
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.account.currentPassword") }}</span>
          <input v-model="currentPassword" type="password" autocomplete="current-password" />
        </label>
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.account.newPassword") }}</span>
          <input v-model="newPassword" type="password" minlength="12" autocomplete="new-password" />
        </label>
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.account.confirmPassword") }}</span>
          <input v-model="confirmPassword" type="password" minlength="12" autocomplete="new-password" />
        </label>
      </div>

      <!-- Anahtarların düşeceği ÖNCEDEN söylenir: kullanıcı yapay zeka bağlantısını yeniden
           kurması gerekeceğini işlemden sonra değil, öncesinde bilmeli. -->
      <p class="meta">{{ t("panel.account.passwordRevokesTokens") }}</p>

      <div class="settings-inline-action">
        <button class="compact" :disabled="changingPassword" @click="changePassword">
          {{ changingPassword ? t("panel.account.passwordChanging") : t("panel.account.changePassword") }}
        </button>
      </div>
    </section>
  </section>
</template>

<script setup lang="ts">
// "Hesabım": oturum açan kullanıcının kendi hesap güvenliği (iki adımlı doğrulama kurulumu,
// kurtarma kodları, 2FA kapatma). Daha önce SetupWizardView > Ayarlar > Hesap sekmesindeydi;
// davranış birebir korunmuştur.
import { ref } from "vue";
import { useI18n } from "vue-i18n";
import { extractApiDetails, extractApiMessage, useAuthStore } from "../stores/auth";
import { useConfirm } from "../components/ConfirmDialog.vue";
import { copyToClipboard } from "../utils/clipboard";

// Bildirimler üst bandda (MainLayout > banner) gösterilsin diye kabuk bileşene iletilir.
const emit = defineEmits<{ (event: "notify", message: string, isError: boolean): void }>();

const i18n = useI18n();
// params: "{count} anahtar iptal edildi" gibi sayı iceren metinler icin. Verilmezse davranis aynidir.
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
const { confirm } = useConfirm();

const twoFactorVerificationCode = ref("");
const disableTwoFactorPassword = ref("");
const recoveryCodes = ref<string[]>([]);

const currentPassword = ref("");
const newPassword = ref("");
const confirmPassword = ref("");
const changingPassword = ref(false);

async function startTwoFactor() {
  try {
    await authStore.beginTwoFactorSetup();
    notify(t("panel.account.started"), false);
  } catch (error) {
    notify(resolveApiMessage(error), true);
  }
}

async function confirmTwoFactor() {
  try {
    recoveryCodes.value = await authStore.confirmTwoFactorSetup(twoFactorVerificationCode.value);
    twoFactorVerificationCode.value = "";
    notify(t("panel.account.enabledMessage"), false);
  } catch (error) {
    notify(resolveApiMessage(error), true);
  }
}

async function regenerateRecoveryCodes() {
  try {
    recoveryCodes.value = await authStore.regenerateRecoveryCodes();
    notify(t("panel.account.recoveryRegenerated"), false);
  } catch (error) {
    notify(resolveApiMessage(error), true);
  }
}

async function copyRecoveryCodes() {
  // Kurtarma kodları kritik: panel henüz sertifika almamışken (http) tarayıcının pano API'si
  // yoktur ve düğme sessizce ölüyordu. Ortak yardımcı o durumda da kopyalar.
  const oldu = await copyToClipboard(recoveryCodes.value.join("\n"));
  notify(t(oldu ? "panel.account.recoveryCopied" : "panel.account.recoveryCopyFailed"), !oldu);
}

/**
 * İki adımlı doğrulamayı KAPATMAK hesabın güvenlik seviyesini düşürür; onay alınmadan yapılmaz.
 * (Yıkıcı değil ama geri alınması yeni kurulum + yeni kurtarma kodu gerektirir.)
 */
async function disableTwoFactor() {
  const onay = await confirm({
    title: t("panel.account.confirmDisable2faTitle"),
    message: t("panel.account.confirmDisable2faMessage"),
    confirmLabel: t("panel.account.disable2fa")
  });
  if (!onay) {
    return;
  }

  try {
    await authStore.disableTwoFactor(disableTwoFactorPassword.value);
    disableTwoFactorPassword.value = "";
    recoveryCodes.value = [];
    notify(t("panel.account.disabledMessage"), false);
  } catch (error) {
    notify(resolveApiMessage(error), true);
  }
}

async function changePassword() {
  // Eşleşme denetimi İSTEMCİDE yapılır: sunucuya gitmeye gerek yok ve kullanıcı anında görür.
  // Uzunluk/geçerlilik denetimi ise sunucuda da vardır — istemci denetimi atlanabilir bir şeydir.
  if (newPassword.value !== confirmPassword.value) {
    notify(t("panel.account.passwordMismatch"), true);
    return;
  }

  if (newPassword.value.length < 12) {
    notify(t("panel.account.passwordTooShort"), true);
    return;
  }

  changingPassword.value = true;
  try {
    const result = await authStore.changePassword(currentPassword.value, newPassword.value);
    currentPassword.value = "";
    newPassword.value = "";
    confirmPassword.value = "";

    // Kaç anahtarın düştüğü SÖYLENİR. Sessizce kesilirse kullanıcı yapay zeka bağlantısının neden
    // çalışmadığını anlamaz ve hatayı panelde arar.
    notify(
      result.revokedApiTokens > 0
        ? `${t("panel.account.passwordChanged")} ${t("panel.account.passwordTokensRevoked", { count: result.revokedApiTokens })}`
        : t("panel.account.passwordChanged"),
      false
    );
  } catch (error) {
    notify(resolveApiMessage(error), true);
  } finally {
    changingPassword.value = false;
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
.account-workspace {
  align-content: start;
}

.settings-section {
  display: grid;
  gap: 12px;
}

.section-head,
.settings-inline-action {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 10px;
  flex-wrap: wrap;
}

.settings-inline-action {
  justify-content: flex-start;
  align-items: end;
}

.settings-inline-action .stacked-field {
  min-width: min(340px, 100%);
}

.settings-inline-action button,
.row-actions button {
  width: auto;
}

.settings-wide-field {
  grid-column: 1 / -1;
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

input,
select,
textarea,
button {
  width: 100%;
}

@media (max-width: 760px) {
  .settings-wide-field {
    grid-column: auto;
  }
}
</style>
