<template>
  <section class="panel module-shell" data-testid="htaccess-module-card">
    <div class="section-head">
      <div>
        <h3>{{ t("panel.htaccessModule.title") }}</h3>
        <p class="meta">{{ t("panel.htaccessModule.subtitle") }}</p>
      </div>
      <button class="secondary compact" :disabled="loading || repairing" @click="load">
        {{ loading ? t("panel.common.loading") : t("panel.htaccessModule.refresh") }}
      </button>
    </div>

    <p v-if="notice" :class="['notice', { error: noticeError }]" :role="noticeError ? 'alert' : 'status'">{{ notice }}</p>

    <!-- Okunamadı ≠ kayıt yok: ölçülmemiş durum "kayıtlı değil" diye gösterilmez. -->
    <p v-if="loadError" class="notice error" role="alert">{{ loadError }}</p>
    <p v-else-if="!registration" class="empty-state" role="status" aria-live="polite">{{ t("panel.common.loading") }}</p>

    <template v-else>
      <p v-if="!registration.readable" class="notice error" role="alert" data-testid="htaccess-module-unreadable">
        {{ t("panel.htaccessModule.unreadable") }}<template v-if="registration.readError"> — {{ registration.readError }}</template>
      </p>
      <template v-else>
        <div class="summary-strip">
          <div class="summary-item">
            <span>{{ t("panel.htaccessModule.registered") }}</span>
            <strong :class="['status-pill', registration.present ? 'ok' : 'warn']">{{ registration.present ? t("panel.status.enabled") : t("panel.status.disabled") }}</strong>
          </div>
          <div class="summary-item">
            <span>{{ t("panel.htaccessModule.typeMatches") }}</span>
            <strong :class="['status-pill', registration.typeMatches ? 'ok' : 'warn']">{{ registration.typeMatches ? t("panel.htaccessModule.yes") : t("panel.htaccessModule.no") }}</strong>
          </div>
          <div class="summary-item">
            <span>{{ t("panel.htaccessModule.assembly") }}</span>
            <strong>{{ registration.assembly.version || registration.assembly.presence }}</strong>
          </div>
        </div>
        <p v-if="registration.preConditionExcludesUnmanagedRequests" class="notice warn-banner" role="status">
          {{ t("panel.htaccessModule.preConditionWarning") }}
        </p>
        <p v-if="!registration.confirmed" class="notice warn-banner" role="status">{{ t("panel.htaccessModule.notConfirmed") }}</p>
      </template>

      <div class="row-actions">
        <button class="compact" :disabled="repairing || loading" @click="repair">
          {{ repairing ? t("panel.common.loading") : t("panel.htaccessModule.repair") }}
        </button>
      </div>
      <small class="field-hint">{{ t("panel.htaccessModule.repairHint") }}</small>
    </template>
  </section>
</template>

<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from "vue";
import { useI18n } from "vue-i18n";
import { getHtaccessModuleRegistration, registerHtaccessModule, type HtaccessModuleRegistration } from "../api/htaccess";
import { useConfirm } from "./ConfirmDialog.vue";
import { extractApiMessage } from "../stores/auth";

/**
 * .htaccess MODÜLÜNÜN IIS KAYIT DURUMU (yönetici). Uç (`GET /htaccess/module`) vardı ama hiçbir ekran
 * onu çağırmıyordu: kayıt bozuk olduğunda panel yalnız site ekranında dolaylı bir uyarı verebiliyordu.
 * Onarım (POST) sunucu geneli bir IIS yapılandırma yazımıdır → onay ister, sonucu yeniden ölçer.
 */
const { t } = useI18n();
const { confirm } = useConfirm();
const registration = ref<HtaccessModuleRegistration | null>(null);
const loading = ref(false);
const repairing = ref(false);
const loadError = ref("");
const notice = ref("");
const noticeError = ref(false);
let disposed = false;
let read = 0;
onBeforeUnmount(() => { disposed = true; read++; });

function message(error: unknown) {
  const key = extractApiMessage(error);
  const translated = t(key);
  return translated === key ? key : translated;
}

async function load() {
  const id = ++read;
  loading.value = true;
  loadError.value = "";
  try {
    const result = await getHtaccessModuleRegistration();
    if (disposed || id !== read) return;
    if (!result || typeof result.readable !== "boolean" || !result.assembly) {
      registration.value = null;
      loadError.value = t("panel.htaccessModule.unconfirmed");
      return;
    }
    registration.value = result;
  } catch (error) {
    if (disposed || id !== read) return;
    registration.value = null;
    loadError.value = message(error);
  } finally {
    if (!disposed && id === read) loading.value = false;
  }
}

async function repair() {
  if (repairing.value) return;
  const approved = await confirm({
    title: t("panel.confirm.repairHtaccessModuleTitle"),
    message: t("panel.htaccessModule.repairConfirm"),
    confirmLabel: t("panel.htaccessModule.repair")
  });
  if (!approved || disposed) return;
  repairing.value = true;
  notice.value = "";
  try {
    await registerHtaccessModule();
    if (disposed) return;
    notice.value = t("htaccess.module_registered");
    noticeError.value = false;
    await load();
  } catch (error) {
    if (disposed) return;
    notice.value = message(error);
    noticeError.value = true;
  } finally {
    if (!disposed) repairing.value = false;
  }
}

onMounted(load);
</script>
