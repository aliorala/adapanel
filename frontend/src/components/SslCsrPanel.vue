<template>
  <section class="form-surface csr-panel">
    <p class="field-hint">{{ t("panel.ssl.csr.hint") }}</p>
    <p v-if="message" :class="['notice', { error: messageError }]" :role="messageError ? 'alert' : 'status'">{{ message }}</p>

    <div class="form-grid two">
      <label>
        <span class="field-label">{{ t("panel.ssl.hostName") }}</span>
        <input v-model.trim="form.hostName" :disabled="busy" autocomplete="off" />
      </label>
      <label>
        <span class="field-label">{{ t("panel.ssl.csr.altNames") }}</span>
        <input v-model.trim="form.altNames" :disabled="busy" autocomplete="off" placeholder="example.com, www.example.com" />
      </label>
      <label>
        <span class="field-label">{{ t("panel.ssl.csr.organization") }}</span>
        <input v-model.trim="form.organization" :disabled="busy" maxlength="64" autocomplete="off" />
      </label>
      <label>
        <span class="field-label">{{ t("panel.ssl.csr.country") }}</span>
        <input v-model.trim="form.country" :disabled="busy" maxlength="2" autocomplete="off" placeholder="TR" />
      </label>
    </div>
    <div class="row-actions">
      <button :disabled="busy || disabled || !form.hostName" @click="create">{{ t("panel.ssl.csr.create") }}</button>
    </div>
    <p class="field-hint">{{ t("panel.ssl.csr.keyStaysOnServer") }}</p>

    <div v-if="created" class="csr-created">
      <strong>{{ t("panel.ssl.csr.createdTitle", { host: created.hostName }) }}</strong>
      <textarea readonly rows="8" :value="created.csrPem" :aria-label="t('panel.ssl.csr.pemLabel')"></textarea>
    </div>

    <div class="data-table" v-if="items.length">
      <div class="data-row table-head">
        <span>{{ t("panel.ssl.hostName") }}</span>
        <span>{{ t("panel.ssl.csr.status") }}</span>
        <span>{{ t("panel.ssl.csr.createdAt") }}</span>
        <span>{{ t("panel.ssl.bindingActions") }}</span>
      </div>
      <div v-for="item in items" :key="item.id" class="data-row">
        <span class="cell-main"><strong>{{ item.hostName }}</strong><small>{{ item.subject }}</small></span>
        <span><span :class="['status-pill', item.status === 'imported' ? 'ok' : 'warn']">{{ t(`panel.ssl.csr.status_${item.status}`) }}</span></span>
        <span class="cell-muted">{{ formatDateTime(item.createdAtUtc, "-") }}</span>
        <span class="row-actions">
          <button v-if="item.status === 'pending'" type="button" class="secondary compact" :disabled="busy" @click="download(item)">{{ t("panel.ssl.csr.download") }}</button>
          <button v-if="item.status === 'pending'" type="button" class="secondary compact" :disabled="busy || disabled || !siteName" @click="pickFile(item)">{{ t("panel.ssl.csr.importSigned") }}</button>
          <button v-if="item.status === 'pending'" type="button" class="danger compact" :disabled="busy" @click="remove(item)">{{ t("panel.ssl.csr.delete") }}</button>
        </span>
      </div>
    </div>
    <p v-else-if="loaded" class="empty-state">{{ t("panel.ssl.csr.none") }}</p>

    <input ref="fileInput" type="file" class="visually-hidden" accept=".cer,.crt,.pem,.p7b,.p7c" @change="onFile" />
  </section>
</template>

<script setup lang="ts">
import { onBeforeUnmount, onMounted, reactive, ref } from "vue";
import { useI18n } from "vue-i18n";
import { createSslCsr, deleteSslCsr, downloadSslCsr, importSignedCertificate, listSslCsrs, type SslCsr } from "../api/ssl";
import { extractApiMessage } from "../stores/auth";
import { formatDateTime } from "../utils/datetime";
import { useConfirm } from "./ConfirmDialog.vue";

const props = defineProps<{ siteName: string; disabled?: boolean }>();
const emit = defineEmits<{ (event: "imported"): void }>();
const { t } = useI18n();
const { confirm } = useConfirm();

const form = reactive({ hostName: "", altNames: "", organization: "", country: "" });
const items = ref<SslCsr[]>([]);
const created = ref<SslCsr | null>(null);
const loaded = ref(false);
const busy = ref(false);
const message = ref("");
const messageError = ref(false);
const fileInput = ref<HTMLInputElement | null>(null);
let pendingImport: SslCsr | null = null;
let disposed = false;
onBeforeUnmount(() => { disposed = true; });

function show(text: string, error: boolean) { message.value = text; messageError.value = error; }
function failure(error: unknown) {
  const key = extractApiMessage(error);
  const translated = t(key);
  show(translated === key ? key : translated, true);
}

async function load() {
  try {
    const result = await listSslCsrs();
    if (disposed) return true;
    items.value = result;
    loaded.value = true;
    return true;
  } catch (error) {
    if (!disposed) failure(error);
    return false;
  }
}
onMounted(() => { void load(); });

async function create() {
  if (busy.value) return;
  busy.value = true;
  try {
    const altNames = form.altNames.split(/[\s,;]+/).filter(Boolean);
    const csr = await createSslCsr({ hostName: form.hostName, altNames, organization: form.organization, country: form.country, state: "", locality: "", keySize: 2048 });
    if (disposed) return;
    created.value = csr;
    // Sertifika uretildi; liste okumasi basarisizsa bunu SOYLE (sessiz yutma yok).
    if (await load()) show(t("panel.ssl.csr.created"), false);
    else show(`${t("panel.ssl.csr.created")}\n${message.value}`, true);
  } catch (error) {
    if (!disposed) failure(error);
  } finally {
    if (!disposed) busy.value = false;
  }
}

async function download(item: SslCsr) {
  busy.value = true;
  try {
    const blob = await downloadSslCsr(item.id);
    const url = URL.createObjectURL(blob);
    const link = document.createElement("a");
    link.href = url;
    link.download = `${item.hostName.replace("*", "wildcard")}.csr`;
    link.click();
    URL.revokeObjectURL(url);
  } catch (error) {
    if (!disposed) failure(error);
  } finally {
    if (!disposed) busy.value = false;
  }
}

function pickFile(item: SslCsr) {
  pendingImport = item;
  fileInput.value?.click();
}

async function onFile(event: Event) {
  const input = event.target as HTMLInputElement;
  const file = input.files?.[0];
  const target = pendingImport;
  input.value = "";
  pendingImport = null;
  if (!file || !target || busy.value) return;
  busy.value = true;
  try {
    const bytes = new Uint8Array(await file.arrayBuffer());
    let binary = "";
    for (let i = 0; i < bytes.length; i += 0x8000) binary += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
    await importSignedCertificate(target.id, { siteName: props.siteName, base64Content: btoa(binary), port: 443 });
    if (disposed) return;
    created.value = null;
    const listed = await load();
    show(listed ? t("panel.ssl.csr.imported") : `${t("panel.ssl.csr.imported")}\n${message.value}`, !listed);
    emit("imported");
  } catch (error) {
    if (!disposed) failure(error);
  } finally {
    if (!disposed) busy.value = false;
  }
}

async function remove(item: SslCsr) {
  if (busy.value) return;
  if (!await confirm({ title: t("panel.ssl.csr.delete"), message: t("panel.ssl.csr.deleteConfirm", { host: item.hostName }), confirmLabel: t("panel.ssl.csr.delete") })) return;
  busy.value = true;
  try {
    await deleteSslCsr(item.id);
    if (disposed) return;
    if (created.value?.id === item.id) created.value = null;
    const listed = await load();
    show(listed ? t("panel.ssl.csr.deleted") : `${t("panel.ssl.csr.deleted")}\n${message.value}`, !listed);
  } catch (error) {
    if (!disposed) failure(error);
  } finally {
    if (!disposed) busy.value = false;
  }
}
</script>

<style scoped>
.csr-created { display: grid; gap: var(--sp-8); }
.csr-created textarea { width: 100%; font-family: var(--font-mono, monospace); font-size: 0.8rem; }
.visually-hidden { position: absolute; width: 1px; height: 1px; overflow: hidden; clip: rect(0 0 0 0); }
</style>
