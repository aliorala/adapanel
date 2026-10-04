<template>
  <section class="panel module-shell account-workspace">
    <div class="module-head">
      <div>
        <h2>{{ t("panel.settings.tab.account") }}</h2>
        <p class="meta">{{ authStore.session?.user?.userName }} · {{ authStore.session?.user?.email }}</p>
      </div>
      <button class="secondary compact" :disabled="busy" @click="refreshAccount">{{ t('panel.common.refresh') }}</button>
    </div>

    <p v-if="accountError" class="notice error account-notice" role="alert"><span>{{ accountError }}</span><button class="secondary compact" :disabled="busy" @click="refreshAccount">{{ t('panel.common.retry') }}</button></p>
    <p v-else-if="!accountConfirmed" class="meta" role="status">{{ t(loadingAccount ? 'panel.common.loading' : 'panel.account.statusUnknown') }}</p>

    <section class="form-surface settings-section">
      <div class="section-head">
        <div>
          <h3>{{ t("panel.account.title") }}</h3>
          <p class="meta">
            {{ t("panel.account.status") }}:
            {{ !accountConfirmed ? t('panel.account.statusUnknown') : authStore.session?.user?.isTwoFactorEnabled ? t("panel.account.enabled") : t("panel.account.disabled") }}
          </p>
        </div>
        <span :class="['status-pill', accountConfirmed ? authStore.session?.user?.isTwoFactorEnabled ? 'ok' : 'warn' : '']">
          {{ !accountConfirmed ? t('panel.account.statusUnknown') : authStore.session?.user?.isTwoFactorEnabled ? t("panel.account.enabled") : t("panel.account.disabled") }}
        </span>
      </div>

      <div v-if="accountConfirmed && setup" class="two-factor-setup">
        <div class="setup-step">
          <h4><span class="step-number">1</span>{{ t('panel.account.setupScanTitle') }}</h4>
          <p class="meta">{{ t('panel.account.setupScanHint') }}</p>
          <div class="setup-key-grid">
            <div class="setup-qr" role="img" :aria-label="t('panel.account.setupQrLabel')">
              <NQrCode :value="setup.provisioningUri" :size="176" :padding="16" type="svg" color="#000000" background-color="#ffffff" />
            </div>
            <div class="manual-key-fields">
              <label class="stacked-field"><span class="field-label">{{ t('panel.account.manualKey') }}</span><input :value="setup.manualEntryKey" class="manual-key" autocomplete="off" spellcheck="false" readonly /></label>
              <p class="field-hint">{{ t('panel.account.setupManualHint') }}</p>
              <button class="secondary compact" :disabled="busy" @click="copySetupKey">{{ t('panel.account.copyKey') }}</button>
              <details class="setup-uri"><summary>{{ t('panel.account.provisioningUri') }}</summary><code>{{ setup.provisioningUri }}</code></details>
            </div>
          </div>
        </div>
        <form class="setup-step verification-step" @submit.prevent="confirmTwoFactor">
          <h4><span class="step-number">2</span>{{ t('panel.account.setupVerifyTitle') }}</h4>
          <p class="meta">{{ t('panel.account.setupVerifyHint') }}</p>
          <label class="stacked-field verification-field"><span class="field-label">{{ t('panel.account.verificationCode') }}</span><input v-model="twoFactorVerificationCode" inputmode="numeric" autocomplete="one-time-code" maxlength="8" /></label>
          <div class="row-actions">
            <button type="submit" class="compact" :disabled="!canConfirmTwoFactor">{{ t(pendingOperation === 'confirm' ? 'panel.common.saving' : 'panel.account.confirm2fa') }}</button>
            <button type="button" class="secondary compact" :disabled="busy" @click="cancelSetup">{{ t('panel.common.cancel') }}</button>
          </div>
        </form>
      </div>

      <div v-else-if="accountConfirmed" class="two-factor-actions">
        <button v-if="!authStore.session?.user?.isTwoFactorEnabled" class="compact" :disabled="!canStartTwoFactor" @click="startTwoFactor">
          {{ t(pendingOperation === 'begin' ? 'panel.common.loading' : 'panel.account.start2fa') }}
        </button>
        <template v-else>
          <p class="meta">{{ t('panel.account.enabledHint') }}</p>
          <button class="secondary compact" :disabled="!canRegenerateRecovery" @click="regenerateRecoveryCodes">{{ t(pendingOperation === 'regenerate' ? 'panel.common.loading' : 'panel.account.regenerateRecovery') }}</button>
          <details class="disable-two-factor">
            <summary>{{ t('panel.account.disable2fa') }}</summary>
            <p class="meta">{{ t('panel.account.confirmDisable2faMessage') }}</p>
            <div class="settings-inline-action">
              <label class="stacked-field"><span class="field-label">{{ t('panel.account.disablePassword') }}</span><PasswordField v-model="disableTwoFactorPassword" autocomplete="current-password" /></label>
              <button class="danger compact" :disabled="!canDisableTwoFactor" @click="disableTwoFactor">{{ t(pendingOperation === 'disable' ? 'panel.common.saving' : 'panel.account.disable2fa') }}</button>
            </div>
          </details>
        </template>
      </div>

      <div v-if="accountConfirmed && recoveryCodes.length" class="recovery-codes">
        <strong>{{ t("panel.account.recoveryTitle") }}</strong>
        <p class="meta">{{ t("panel.account.recoveryHint") }}</p>
        <ul class="recovery-list">
          <li v-for="rc in recoveryCodes" :key="rc">{{ rc }}</li>
        </ul>
        <div class="row-actions">
          <button class="secondary compact" :disabled="busy" @click="copyRecoveryCodes">{{ t("panel.account.recoveryCopy") }}</button>
          <button class="secondary compact" :disabled="busy" @click="recoveryCodes = []">{{ t("panel.account.hideRecoveryCodes") }}</button>
        </div>
      </div>
    </section>

    <form class="form-surface settings-section" @submit.prevent="changePassword">
      <div class="section-head">
        <div>
          <h3>{{ t("panel.account.passwordTitle") }}</h3>
          <p class="meta">{{ t("panel.account.passwordHint") }}</p>
        </div>
      </div>

      <div class="form-grid two">
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.account.currentPassword") }}</span>
          <PasswordField v-model="currentPassword" autocomplete="current-password" />
        </label>
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.account.newPassword") }}</span>
          <PasswordField v-model="newPassword" minlength="12" autocomplete="new-password" />
        </label>
        <label class="stacked-field">
          <span class="field-label">{{ t("panel.account.confirmPassword") }}</span>
          <PasswordField v-model="confirmPassword" minlength="12" autocomplete="new-password" />
        </label>
      </div>

      <!-- Anahtarların düşeceği ÖNCEDEN söylenir: kullanıcı yapay zeka bağlantısını yeniden
           kurması gerekeceğini işlemden sonra değil, öncesinde bilmeli. -->
      <p class="meta">{{ t("panel.account.passwordRevokesTokens") }}</p>

      <div class="settings-inline-action">
        <button type="submit" class="compact" :disabled="!canChangePassword">
          {{ pendingOperation === 'password' ? t("panel.account.passwordChanging") : t("panel.account.changePassword") }}
        </button>
      </div>
    </form>
  </section>
</template>

<script setup lang="ts">
// Account operations and secrets belong to the current mounted session only.
import { computed, onMounted, onUnmounted, ref, toRaw, watch } from "vue";
import { useI18n } from "vue-i18n";
import { NQrCode } from "naive-ui";
import PasswordField from "../components/PasswordField.vue";
import { extractApiDetails, extractApiMessage, useAuthStore, type TwoFactorSetup } from "../stores/auth";
import { getSessionEpoch } from "../api/client";
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
const originalSession = authStore.session, actorId = originalSession?.user?.id, actorRole = originalSession?.user?.role;
const epoch = getSessionEpoch();
let disposed = false, ownedSetup: TwoFactorSetup | null = null, draftRevision = 0, disableRevision = 0, codeRevision = 0;
const current = () => !disposed && getSessionEpoch() === epoch && authStore.session === originalSession &&
  authStore.session?.isAuthenticated === true && authStore.session.user?.isActive === true &&
  authStore.session.user.id === actorId && authStore.session.user.role === actorRole;
const setup = ref<TwoFactorSetup | null>(null);
const pendingOperation = ref<"" | "begin" | "confirm" | "regenerate" | "disable" | "password">("");
const confirming = ref(false), loadingAccount = ref(false), accountConfirmed = ref(false), accountError = ref("");
const busy = computed(() => !!pendingOperation.value || confirming.value || loadingAccount.value);
const ready = () => current() && accountConfirmed.value && !busy.value;
const canStartTwoFactor = computed(() => ready() && !authStore.session?.user?.isTwoFactorEnabled && !setup.value);
const canConfirmTwoFactor = computed(() => ready() && !!setup.value && !authStore.session?.user?.isTwoFactorEnabled && !!twoFactorVerificationCode.value.trim());
const canRegenerateRecovery = computed(() => ready() && authStore.session?.user?.isTwoFactorEnabled === true);
const canDisableTwoFactor = computed(() => ready() && authStore.session?.user?.isTwoFactorEnabled === true && !!disableTwoFactorPassword.value);
const canChangePassword = computed(ready);
const twoFactorVerificationCode = ref("");
const disableTwoFactorPassword = ref("");
const recoveryCodes = ref<string[]>([]);
const currentPassword = ref("");
const newPassword = ref("");
const confirmPassword = ref("");
watch([currentPassword, newPassword, confirmPassword], () => { draftRevision++; }, { flush: "sync" });
watch(disableTwoFactorPassword, () => { disableRevision++; }, { flush: "sync" });
watch(twoFactorVerificationCode, () => { codeRevision++; }, { flush: "sync" });
function clearSetup() {
  if (ownedSetup && authStore.twoFactorSetup && toRaw(authStore.twoFactorSetup) === ownedSetup) authStore.twoFactorSetup = null;
  ownedSetup = null; setup.value = null; twoFactorVerificationCode.value = "";
}
function clearSecrets() {
  clearSetup(); recoveryCodes.value = []; disableTwoFactorPassword.value = "";
  currentPassword.value = ""; newPassword.value = ""; confirmPassword.value = "";
}
watch(() => [authStore.session, authStore.session?.user?.id, authStore.session?.user?.role, authStore.session?.isAuthenticated], () => {
  if (!current()) { accountConfirmed.value = false; clearSecrets(); }
}, { flush: "sync" });
onMounted(refreshAccount);
onUnmounted(() => { disposed = true; clearSecrets(); });
async function refreshAccount() {
  if (!current() || busy.value) return;
  loadingAccount.value = true; accountConfirmed.value = false; accountError.value = "";
  clearSetup(); recoveryCodes.value = [];
  try {
    await authStore.fetchAccountSession(current);
    if (current()) accountConfirmed.value = true;
  } catch (error) { if (current()) accountError.value = resolveApiMessage(error); }
  finally { if (current()) loadingAccount.value = false; }
}
function mutationFailed(error: unknown) {
  if (!current()) return;
  const key = extractApiMessage(error);
  // Rejected credentials/codes can be corrected in place. An ambiguous write
  // requires a fresh account reading before another state-changing action.
  if (!["auth.invalid_two_factor_code", "auth.invalid_credentials", "auth.password_unchanged", "auth.password_too_short", "auth.password_too_weak", "auth.rate_limited"].includes(key)) {
    accountConfirmed.value = false; clearSetup(); recoveryCodes.value = [];
  }
  notify(resolveApiMessage(error), true);
}
async function startTwoFactor() {
  if (!current() || !canStartTwoFactor.value) return;
  pendingOperation.value = "begin";
  try {
    const result = await authStore.beginTwoFactorSetup(current);
    if (!current()) return;
    setup.value = result; ownedSetup = toRaw(result); notify(t("panel.account.started"), false);
  } catch (error) { mutationFailed(error); }
  finally { if (current()) pendingOperation.value = ""; }
}
async function confirmTwoFactor() {
  if (!current() || !canConfirmTwoFactor.value) return;
  const code = twoFactorVerificationCode.value.trim(), revision = codeRevision;
  pendingOperation.value = "confirm";
  try {
    const codes = await authStore.confirmTwoFactorSetup(code, current);
    if (!current()) return;
    ownedSetup = null; setup.value = null; recoveryCodes.value = codes;
    if (revision === codeRevision) twoFactorVerificationCode.value = "";
    notify(t("panel.account.enabledMessage"), false);
  } catch (error) { mutationFailed(error); }
  finally { if (current()) pendingOperation.value = ""; }
}
async function regenerateRecoveryCodes() {
  if (!current() || !canRegenerateRecovery.value) return;
  pendingOperation.value = "regenerate"; confirming.value = true;
  try {
    const accepted = await confirm({ title: t("panel.account.confirmRegenerateTitle"), message: t("panel.account.confirmRegenerateMessage"), confirmLabel: t("panel.account.regenerateRecovery") });
    if (!accepted || !current() || !accountConfirmed.value || authStore.session?.user?.isTwoFactorEnabled !== true) return;
    confirming.value = false;
    recoveryCodes.value = [];
    const codes = await authStore.regenerateRecoveryCodes(current);
    if (current()) { recoveryCodes.value = codes; notify(t("panel.account.recoveryRegenerated"), false); }
  } catch (error) { mutationFailed(error); }
  finally { if (current()) { confirming.value = false; pendingOperation.value = ""; } }
}
async function disableTwoFactor() {
  if (!current() || !canDisableTwoFactor.value) return;
  const password = disableTwoFactorPassword.value, revision = disableRevision;
  pendingOperation.value = "disable"; confirming.value = true;
  try {
    const accepted = await confirm({ title: t("panel.account.confirmDisable2faTitle"), message: t("panel.account.confirmDisable2faMessage"), confirmLabel: t("panel.account.disable2fa") });
    if (!accepted || !current() || !accountConfirmed.value || authStore.session?.user?.isTwoFactorEnabled !== true) return;
    confirming.value = false;
    await authStore.disableTwoFactor(password, current);
    if (!current()) return;
    if (revision === disableRevision) disableTwoFactorPassword.value = "";
    clearSetup(); recoveryCodes.value = [];
    notify(t("panel.account.disabledMessage"), false);
  } catch (error) { mutationFailed(error); }
  finally { if (current()) { confirming.value = false; pendingOperation.value = ""; } }
}
async function changePassword() {
  if (!current() || !canChangePassword.value) return;
  if (newPassword.value !== confirmPassword.value) {
    notify(t("panel.account.passwordMismatch"), true);
    return;
  }

  if (newPassword.value.length < 12) {
    notify(t("panel.account.passwordTooShort"), true);
    return;
  }

  const submitted = { currentPassword: currentPassword.value, newPassword: newPassword.value }, revision = draftRevision;
  pendingOperation.value = "password";
  try {
    const result = await authStore.changePassword(submitted.currentPassword, submitted.newPassword, current);
    if (!current()) return;
    if (revision === draftRevision) { currentPassword.value = ""; newPassword.value = ""; confirmPassword.value = ""; }

    // Kaç anahtarın düştüğü SÖYLENİR. Sessizce kesilirse kullanıcı yapay zeka bağlantısının neden
    // çalışmadığını anlamaz ve hatayı panelde arar.
    notify(
      result.revokedApiTokens > 0
        ? `${t("panel.account.passwordChanged")} ${t("panel.account.passwordTokensRevoked", { count: result.revokedApiTokens })}`
        : t("panel.account.passwordChanged"),
      false
    );
  } catch (error) { mutationFailed(error); }
  finally { if (current()) pendingOperation.value = ""; }
}
function cancelSetup() { if (ready()) clearSetup(); }
async function copySetupKey() {
  if (!ready() || !setup.value) return;
  const captured = setup.value;
  const copied = await copyToClipboard(captured.manualEntryKey);
  if (current() && setup.value === captured && accountConfirmed.value) notify(t(copied ? "panel.account.keyCopied" : "panel.account.keyCopyFailed"), !copied);
}
async function copyRecoveryCodes() {
  if (!ready() || !recoveryCodes.value.length) return;
  const codes = recoveryCodes.value;
  const copied = await copyToClipboard(codes.join("\n"));
  if (current() && recoveryCodes.value === codes && accountConfirmed.value) notify(t(copied ? "panel.account.recoveryCopied" : "panel.account.recoveryCopyFailed"), !copied);
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
.account-notice { display: flex; align-items: center; justify-content: space-between; gap: var(--sp-12); flex-wrap: wrap; }
.account-notice span { flex: 1 1 220px; }
.two-factor-setup, .two-factor-actions { display: grid; gap: var(--sp-16); justify-items: start; width: 100%; }
.setup-step { width: 100%; min-width: 0; }
.setup-step h4 { display: flex; align-items: center; gap: var(--sp-8); margin: 0 0 var(--sp-8); }
.step-number { display: inline-grid; place-items: center; width: 26px; height: 26px; flex: 0 0 26px; border-radius: 50%; background: var(--surface-2); border: 1px solid var(--panel-border); }
.setup-key-grid { display: grid; grid-template-columns: auto minmax(0, 1fr); align-items: start; gap: var(--sp-20); margin-top: var(--sp-16); }
.setup-qr { display: grid; place-items: center; width: fit-content; padding: 0; background: white; border-radius: var(--radius-md); overflow: hidden; }
.setup-qr :deep(.n-qr-code) { box-sizing: content-box; }
.manual-key-fields { display: grid; gap: var(--sp-8); min-width: 0; justify-items: start; }
.manual-key-fields label { width: 100%; }
.manual-key { font-family: monospace; letter-spacing: .04em; }
.setup-uri { width: 100%; color: var(--text-muted); font-size: var(--fs-xs); }
.setup-uri code { display: block; overflow-wrap: anywhere; margin-top: var(--sp-8); }
.setup-uri summary, .disable-two-factor summary { cursor: pointer; }
.verification-step { border-top: 1px solid var(--panel-border); padding-top: var(--sp-16); }
.verification-field { max-width: 240px; margin-bottom: var(--sp-12); }
.verification-field input { font-variant-numeric: tabular-nums; letter-spacing: .18em; }
.disable-two-factor { width: 100%; border-top: 1px solid var(--panel-border); padding-top: var(--sp-12); }
.recovery-codes { padding: var(--sp-16); margin-top: var(--sp-16); border: 1px solid var(--panel-border); border-radius: var(--radius-md); background: var(--surface-2); }
.recovery-list { list-style: none; padding: 0; display: grid; grid-template-columns: repeat(auto-fit, minmax(130px, 1fr)); gap: var(--sp-8); }
.recovery-list li { font-family: monospace; padding: var(--sp-8); border: 1px solid var(--panel-border); border-radius: var(--radius-sm); text-align: center; overflow-wrap: anywhere; }
.account-workspace {
  align-content: start;
}

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
textarea {
  width: 100%;
}

@media (max-width: 760px) {
  .setup-key-grid { grid-template-columns: minmax(0, 1fr); }
  .settings-wide-field {
    grid-column: auto;
  }
}
</style>
