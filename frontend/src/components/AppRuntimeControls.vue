<template>
  <section class="form-surface runtime-controls">
    <div class="module-head">
      <div>
        <h3>{{ t("panel.apps.controlTitle") }}</h3>
        <p class="meta">{{ t("panel.apps.controlHint") }}</p>
      </div>
      <span :class="['status-pill', poolRunning ? 'ok' : 'warn']">{{ poolLabel }}</span>
    </div>

    <p v-if="notice" :class="['notice', { error: noticeError }]" :role="noticeError ? 'alert' : 'status'">{{ notice }}</p>

    <div class="row-actions">
      <button class="secondary compact" v-bind="writeGate(busy || poolRunning === true)" @click="control('start')">{{ t("panel.apps.start") }}</button>
      <button class="secondary compact" v-bind="writeGate(busy || poolRunning === false)" @click="control('stop')">{{ t("panel.apps.stop") }}</button>
      <button class="secondary compact" v-bind="writeGate(busy)" @click="control('restart')">{{ t("panel.apps.restart") }}</button>
      <button class="secondary compact" :disabled="busy" @click="loadLogs">{{ t("panel.apps.showLogs") }}</button>
      <button class="secondary compact danger" v-bind="writeGate(busy)" @click="remove">{{ t("panel.apps.remove") }}</button>
    </div>

    <ul v-if="leftovers.length" class="field-hint" role="alert">
      <li v-for="item in leftovers" :key="item">{{ item }}</li>
    </ul>
    <p v-if="logs !== null" class="field-hint">{{ logs.fileName ? t("panel.apps.logsFile", { file: logs.fileName }) : t("panel.apps.noLogs") }}</p>
    <pre v-if="logs?.output" class="composer-output">{{ logs.output }}</pre>
  </section>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, ref } from "vue";
import { useI18n } from "vue-i18n";
import { isCancel } from "axios";
import { controlSiteApp, getSiteAppLogs, removeSiteApp, type AppRuntimeLogs } from "../api/apps";
import { useConfirm } from "./ConfirmDialog.vue";
import { extractApiMessage } from "../stores/auth";
import { useSubscriptionGate } from "../composables/useSubscriptionGate";

const props = defineProps<{ siteName: string; poolState?: string | null }>();
const emit = defineEmits<{ (e: "changed"): void }>();

const { t } = useI18n();
const { confirm } = useConfirm();
const { writeGate } = useSubscriptionGate();
const busy = ref(false);
const notice = ref("");
const noticeError = ref(false);
const logs = ref<AppRuntimeLogs | null>(null);
const leftovers = ref<string[]>([]);
let disposed = false;
onBeforeUnmount(() => { disposed = true; });

// null = ölçülemedi: ne "çalışıyor" ne "durdu" denir, iki düğme de açık kalır.
const poolRunning = computed<boolean | null>(() => {
  const state = (props.poolState ?? "").toLowerCase();
  if (!state) return null;
  return state === "started" || state === "starting";
});
const poolLabel = computed(() => poolRunning.value === null ? t("panel.apps.poolUnknown") : poolRunning.value ? t("panel.apps.poolRunning") : t("panel.apps.poolStopped"));

function show(message: string, isError: boolean) {
  notice.value = message;
  noticeError.value = isError;
}

function resolveMessage(error: unknown) {
  const key = extractApiMessage(error);
  const translated = t(key);
  return translated === key ? key : translated;
}

async function control(action: "start" | "stop" | "restart") {
  const site = props.siteName;
  busy.value = true;
  try {
    await controlSiteApp(site, action);
    if (disposed || site !== props.siteName) return;
    show(t(`appruntime.${action === "start" ? "started" : action === "stop" ? "stopped" : "restarted"}`), false);
    emit("changed");
  } catch (error) {
    if (!disposed && !isCancel(error)) show(resolveMessage(error), true);
  } finally {
    if (!disposed) busy.value = false;
  }
}

async function loadLogs() {
  const site = props.siteName;
  busy.value = true;
  try {
    const result = await getSiteAppLogs(site, 200);
    if (disposed || site !== props.siteName) return;
    logs.value = result;
  } catch (error) {
    if (!disposed && !isCancel(error)) show(resolveMessage(error), true);
  } finally {
    if (!disposed) busy.value = false;
  }
}

async function remove() {
  const site = props.siteName;
  await confirm({
    title: t("panel.apps.removeTitle", { name: site }),
    message: t("panel.apps.removeConfirm"),
    impact: [t("panel.apps.removeImpact1"), t("panel.apps.removeImpact2"), t("panel.apps.removeImpact3")],
    requireText: site,
    action: async () => {
      busy.value = true;
      try {
        const result = await removeSiteApp(site, site, true);
        if (disposed || site !== props.siteName) return;
        leftovers.value = result.leftovers;
        logs.value = null;
        // Kalıntı varsa BAŞARI mesajı verilmez: kullanıcı kısmi temizliği "tamam" sanmasın.
        if (result.leftovers.length) show(t("appruntime.removed_partial"), true);
        else show(t("appruntime.removed"), false);
        emit("changed");
      } catch (error) {
        if (!disposed && !isCancel(error)) show(resolveMessage(error), true);
      } finally {
        if (!disposed) busy.value = false;
      }
    }
  });
}
</script>
