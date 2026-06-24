<template>
  <section class="detail-card">
    <template v-if="site">
      <div class="detail-top">
        <div>
          <h3>{{ t("panel.sites.detailTitle") }}</h3>
          <p class="meta">{{ site.name }}</p>
        </div>

        <div class="detail-actions">
          <button
            class="secondary compact"
            :disabled="!primaryUrl"
            @click="openSite">
            {{ t("panel.sites.open") }}
          </button>
          <button
            class="secondary compact"
            :disabled="busySiteName === site.name || isStopped"
            @click="$emit('start', site.name)">
            {{ t("panel.sites.start") }}
          </button>
          <button
            class="secondary compact"
            :disabled="busySiteName === site.name || isStarted"
            @click="$emit('stop', site.name)">
            {{ t("panel.sites.stop") }}
          </button>
          <button
            class="danger compact"
            :disabled="busySiteName === site.name"
            @click="$emit('delete', site.name)">
            {{ t("panel.sites.delete") }}
          </button>
        </div>
      </div>

      <dl class="detail-grid">
        <div>
          <dt>{{ t("panel.sites.state") }}</dt>
          <dd>{{ translateState(site.state) }}</dd>
        </div>
        <div>
          <dt>{{ t("panel.sites.appPool") }}</dt>
          <dd>{{ site.applicationPoolName || "-" }}</dd>
        </div>
        <div>
          <dt>{{ t("panel.sites.physicalPath") }}</dt>
          <dd>{{ site.physicalPath }}</dd>
        </div>
        <div>
          <dt>{{ t("panel.sites.autoStart") }}</dt>
          <dd>{{ site.autoStart ? t("panel.status.enabled") : t("panel.status.disabled") }}</dd>
        </div>
      </dl>

      <!-- profesyonel panel benzeri site önizlemesi (canlı iframe — ekran görüntüsü servisi gerektirmez). -->
      <section v-if="primaryUrl" class="preview-block">
        <div class="section-head">
          <h4>{{ t("panel.sites.previewTitle") }}</h4>
          <button class="secondary compact" type="button" @click="showPreview = !showPreview">
            {{ showPreview ? t("panel.common.cancel") : t("panel.sites.previewShow") }}
          </button>
        </div>
        <div v-if="showPreview" class="preview-frame">
          <iframe :src="primaryUrl" :title="site.name" loading="lazy" referrerpolicy="no-referrer"></iframe>
        </div>
      </section>

      <!-- profesyonel panel benzeri "domain araçları" ızgarası: özet altında tek tık kısayollar. -->
      <section class="tools-block">
        <div class="section-head">
          <h4>{{ t("panel.sites.toolsTitle") }}</h4>
        </div>
        <div class="tools-grid">
          <button
            v-for="tool in siteTools"
            :key="tool.id"
            type="button"
            class="tool-card"
            @click="$emit('open-tool', tool.id)">
            <span class="tool-card-glyph">{{ tool.glyph }}</span>
            <span class="tool-card-label">{{ tool.label }}</span>
          </button>
        </div>
      </section>

      <!-- profesyonel panel benzeri subdomain ekleme: <etiket>.<alanadı> için yeni yönetilen site.
           Yeni site oluşturur → yalnız admin (bayi/müşteri site oluşturamaz). -->
      <section v-if="isAdmin" class="subdomain-block">
        <div class="section-head">
          <h4>{{ t("panel.sites.subdomainTitle") }}</h4>
        </div>
        <form class="subdomain-form" @submit.prevent="submitSubdomain">
          <div class="subdomain-input">
            <input v-model.trim="subdomainLabel" placeholder="blog" pattern="[a-zA-Z0-9-]+" />
            <span>.{{ site.name }}</span>
          </div>
          <button type="submit" class="compact" :disabled="!subdomainLabel || busySiteName === site.name">
            {{ t("panel.sites.subdomainAdd") }}
          </button>
        </form>
      </section>

      <div class="binding-block">
        <div class="section-head">
          <h4>{{ t("panel.sites.bindings") }}</h4>
          <button class="secondary compact" type="button" @click="showBindingForm = !showBindingForm">
            {{ showBindingForm ? t("panel.common.cancel") : t("panel.sites.addBinding") }}
          </button>
        </div>

        <div v-if="site.bindings.length > 0" class="binding-list">
          <article v-for="binding in site.bindings" :key="binding.bindingInformation" class="binding-item">
            <div>
              <strong>{{ binding.protocol.toUpperCase() }}</strong>
              <p class="meta">{{ binding.bindingInformation }}</p>
            </div>

            <button
              class="secondary compact"
              :disabled="busyBindingKey === toBindingKey(binding)"
              @click="emitRemoveBinding(binding)">
              {{ t("panel.sites.removeBinding") }}
            </button>
          </article>
        </div>

        <p v-else class="meta">
          {{ t("panel.sites.bindingNone") }}
        </p>
      </div>

      <form v-if="showBindingForm" class="binding-form" @submit.prevent="submitBinding">
        <label>
          <span>{{ t("panel.sites.protocol") }}</span>
          <select v-model="bindingForm.protocol">
            <option value="http">{{ t("panel.sites.http") }}</option>
          </select>
        </label>

        <label>
          <span>{{ t("panel.sites.hostName") }}</span>
          <input v-model="bindingForm.hostName" />
        </label>

        <label>
          <span>{{ t("panel.sites.ipAddress") }}</span>
          <select v-model="bindingForm.ipAddress">
            <option value="*">{{ t("panel.sites.allIps") }}</option>
            <option v-for="ip in serverIps" :key="ip.address" :value="ip.address">{{ ip.address }} ({{ ip.family }})</option>
          </select>
        </label>

        <label>
          <span>{{ t("panel.sites.port") }}</span>
          <input v-model.number="bindingForm.port" type="number" min="1" max="65535" required />
        </label>

        <button type="submit" :disabled="busySiteName === site.name">
          {{ t("panel.sites.addBinding") }}
        </button>
      </form>
    </template>

    <p v-else class="meta">
      {{ t("panel.sites.detailEmpty") }}
    </p>
  </section>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref } from "vue";
import { useI18n } from "vue-i18n";
import type { SiteBinding, SiteBindingPayload, SiteDefaultsResult, SiteRecord } from "../api/sites";
import { getServerIpAddresses, type ServerIpAddress } from "../api/system";
import { openReachable } from "../utils/url";
import { useAuthStore } from "../stores/auth";

// Site oluşturma içeren işlemler (subdomain) + server-level araçlar yalnız admin'e gösterilir.
const isAdmin = computed(() => useAuthStore().session?.user?.role === "Administrator");

const props = defineProps<{
  site: SiteRecord | null;
  busySiteName: string;
  busyBindingKey: string;
  defaultsReport: SiteDefaultsResult | null;
}>();

const emit = defineEmits<{
  (event: "start", siteName: string): void;
  (event: "stop", siteName: string): void;
  (event: "delete", siteName: string): void;
  (event: "prepare-defaults", siteName: string): void;
  (event: "open-tool", tool: string): void;
  (event: "add-subdomain", label: string): void;
  (event: "add-binding", payload: { siteName: string; binding: SiteBindingPayload }): void;
  (event: "remove-binding", payload: { siteName: string; binding: SiteBindingPayload }): void;
}>();
const { t } = useI18n();

// panel "Websites & Domains" domain araç kısayolları.
const siteTools = computed(() => [
  { id: "hosting", glyph: "⚙", label: t("panel.sites.tools.hosting") },
  { id: "files", glyph: "FL", label: t("panel.sites.tools.files") },
  { id: "php", glyph: "P", label: t("panel.sites.tools.php") },
  { id: "asp", glyph: ".N", label: t("panel.sites.tools.asp") },
  { id: "mail", glyph: "@", label: t("panel.sites.tools.mail") },
  { id: "ssl", glyph: "SSL", label: t("panel.sites.tools.ssl") },
  // webdb server-level (admin-only) → bayi/müşteride gizli.
  ...(isAdmin.value ? [{ id: "webdb", glyph: "DB", label: t("panel.sites.tools.webdb") }] : []),
  { id: "ftp", glyph: "FT", label: t("panel.sites.tools.ftp") },
  { id: "dns", glyph: "DNS", label: t("panel.sites.tools.dns") },
  { id: "stats", glyph: "IST", label: t("panel.sites.tools.stats") },
  { id: "wp", glyph: "WP", label: "WordPress" },
  { id: "protected", glyph: "KOR", label: t("panel.sites.tools.protected") },
  { id: "backup", glyph: "BK", label: t("panel.sites.tools.backup") },
  { id: "apps", glyph: "JS", label: t("panel.sites.tools.apps") },
  // yazılım mağazası server-level (admin-only).
  ...(isAdmin.value ? [{ id: "store", glyph: "AP", label: t("panel.sites.tools.store") }] : [])
]);

const bindingForm = reactive<SiteBindingPayload>({
  protocol: "http",
  hostName: "",
  ipAddress: "*",
  port: 80
});

const subdomainLabel = ref("");
const showBindingForm = ref(false);
const showPreview = ref(false);

// Çoklu IP (panel dedicated IP): binding'de seçilebilecek sunucu IP'leri.
const serverIps = ref<ServerIpAddress[]>([]);
onMounted(async () => {
  serverIps.value = await getServerIpAddresses().catch(() => []);
});

function submitSubdomain() {
  const label = subdomainLabel.value.trim().toLowerCase();
  if (!label) {
    return;
  }
  emit("add-subdomain", label);
  subdomainLabel.value = "";
}

const isStarted = computed(() => props.site?.state.toLowerCase() === "started");
const isStopped = computed(() => props.site?.state.toLowerCase() === "stopped");
const primaryUrl = computed(() => {
  const binding = props.site?.bindings[0];
  if (!binding) {
    return "";
  }

  const host = binding.hostName || (binding.ipAddress === "*" ? "localhost" : binding.ipAddress);
  const defaultPort = (binding.protocol === "http" && binding.port === 80)
    || (binding.protocol === "https" && binding.port === 443);
  return `${binding.protocol}://${host}${defaultPort ? "" : `:${binding.port}`}`;
});

function openSite() {
  if (primaryUrl.value) {
    // *:port (host adı yok) bağlamasında URL "localhost" olur; uzak tarayıcı için paneli
    // gördüğümüz host'a çevir (toReachableUrl).
    openReachable(primaryUrl.value);
  }
}

function submitBinding() {
  if (!props.site) {
    return;
  }

  emit("add-binding", {
    siteName: props.site.name,
    binding: { ...bindingForm }
  });

  bindingForm.hostName = "";
  bindingForm.ipAddress = "*";
  bindingForm.port = 80;
  showBindingForm.value = false;
}

function emitRemoveBinding(binding: SiteBinding) {
  if (!props.site) {
    return;
  }

  emit("remove-binding", {
    siteName: props.site.name,
    binding: {
      protocol: binding.protocol,
      hostName: binding.hostName,
      ipAddress: binding.ipAddress,
      port: binding.port
    }
  });
}

function toBindingKey(binding: SiteBinding): string {
  return `${binding.protocol}|${binding.ipAddress}|${binding.port}|${binding.hostName}`;
}

function translateState(state: string): string {
  const key = `panel.sites.runtime.${state.toLowerCase()}`;
  const translated = t(key);
  return translated === key ? state : translated;
}
</script>

<style scoped>
.detail-card,
.detail-grid,
.defaults-block,
.binding-block,
.binding-form,
.binding-list {
  display: grid;
  gap: 14px;
}

.detail-card {
  padding: 15px;
  border-radius: var(--radius-lg);
  border: 1px solid var(--panel-border);
  background: linear-gradient(180deg, var(--surface-2), var(--surface-2));
}

.detail-top,
.detail-actions,
.section-head,
.binding-item {
  display: flex;
  gap: 12px;
}

.detail-top,
.section-head,
.binding-item {
  justify-content: space-between;
  align-items: flex-start;
}

.detail-grid {
  grid-template-columns: repeat(2, minmax(0, 1fr));
  margin: 0;
}

.detail-grid div {
  display: grid;
  gap: 4px;
}

.detail-grid dt,
.meta {
  color: var(--text-muted);
}

.detail-grid dd,
.meta {
  margin: 0;
}

.tools-block,
.subdomain-block,
.preview-block {
  display: grid;
  gap: 10px;
}

.preview-frame {
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-md, 8px);
  overflow: hidden;
  background: #fff;
  height: 320px;
}

.preview-frame iframe {
  width: 100%;
  height: 100%;
  border: 0;
}

.subdomain-form {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 10px;
}

.subdomain-input {
  display: inline-flex;
  align-items: center;
  gap: 6px;
}

.subdomain-input input {
  width: 160px;
}

.subdomain-input span {
  color: var(--text-muted);
  font-size: 0.86rem;
}

.tools-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(116px, 1fr));
  gap: 8px;
}

.tool-card {
  display: grid;
  justify-items: center;
  align-content: center;
  gap: 8px;
  min-height: 78px;
  padding: 12px 8px;
  border-radius: var(--radius-md, 12px);
  border: 1px solid var(--panel-border);
  background: var(--surface-1);
  color: var(--text-strong);
  cursor: pointer;
  transition: border-color 0.15s ease, transform 0.05s ease;
}

.tool-card:hover {
  border-color: color-mix(in srgb, var(--accent-1) 45%, transparent);
}

.tool-card:active {
  transform: translateY(1px);
}

.tool-card-glyph {
  display: grid;
  place-items: center;
  width: 34px;
  height: 34px;
  border-radius: 9px;
  background: color-mix(in srgb, var(--accent-1) 14%, transparent);
  color: var(--accent-1);
  font-size: 0.66rem;
  font-weight: 800;
}

.tool-card-label {
  font-size: 0.78rem;
  text-align: center;
}

.defaults-grid {
  display: flex;
  gap: 6px;
  flex-wrap: wrap;
}

.default-chip {
  display: inline-flex;
  gap: 6px;
  align-items: center;
  padding: 5px 8px;
  border: 1px solid var(--panel-border);
  border-radius: 999px;
  background: rgba(148, 163, 184, 0.07);
  color: var(--text-muted);
  font-size: 0.76rem;
}

.default-chip i {
  width: 6px;
  height: 6px;
  border-radius: 999px;
  background: currentColor;
}

.default-chip.ready {
  border-color: color-mix(in srgb, var(--accent-1) 18%, transparent);
  background: color-mix(in srgb, var(--accent-1) 9%, transparent);
  color: var(--accent-1);
}

.default-chip.failed {
  border-color: color-mix(in srgb, var(--warn-1) 20%, transparent);
  background: color-mix(in srgb, var(--warn-1) 10%, transparent);
  color: var(--warn-text);
}

.binding-item {
  padding: 9px 10px;
  border-bottom: 1px solid var(--panel-border);
  background: rgba(12, 16, 24, 0.4);
}

.binding-form {
  grid-template-columns: repeat(4, minmax(0, 1fr));
  align-items: end;
}

label {
  display: grid;
  gap: 8px;
}

input,
select,
button {
  width: 100%;
  border-radius: var(--radius-sm);
  font: inherit;
}

input,
select {
  border: 1px solid var(--panel-border);
  padding: 12px 14px;
  background: var(--input-bg);
  color: var(--text-strong);
}

button.compact {
  padding: 10px 12px;
}

button:disabled {
  opacity: 0.65;
  cursor: wait;
}

@media (max-width: 960px) {
  .detail-grid,
  .binding-form {
    grid-template-columns: 1fr;
  }
}

@media (max-width: 760px) {
  .detail-top,
  .detail-actions,
  .section-head,
  .binding-item {
    flex-direction: column;
    align-items: flex-start;
  }
}
</style>
