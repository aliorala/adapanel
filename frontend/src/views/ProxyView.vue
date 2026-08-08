<template>
  <section class="panel proxy-panel">
    <div class="proxy-header">
      <div>
        <h2>{{ t("panel.proxy.title") }}</h2>
        <p class="meta">{{ t("panel.proxy.subtitle") }}</p>
      </div>

      <button
        class="secondary compact"
        :disabled="!selectedSiteName || proxyStore.loadingSiteName === selectedSiteName"
        @click="refreshConfiguration">
        {{ t("panel.proxy.refresh") }}
      </button>
    </div>

    <p v-if="notice" :class="['notice', 'page-notice', { error: noticeError }]" :role="noticeError ? 'alert' : 'status'">
      {{ notice }}
    </p>

    <div class="module-toolbar">
      <div class="segmented-tabs">
        <button :class="{ active: activeSection === 'proxy' }" @click="activeSection = 'proxy'">{{ t("panel.proxy.configTitle") }}</button>
        <button :class="{ active: activeSection === 'rewrite' }" @click="activeSection = 'rewrite'">{{ t("panel.proxy.rewriteTitle") }}</button>
      </div>
      <label class="site-select">
        <select v-model="selectedSiteName">
          <option value="">{{ t("panel.php.selectSite") }}</option>
          <option v-for="site in sitesStore.sites" :key="site.name" :value="site.name">
            {{ site.name }}
          </option>
        </select>
      </label>
    </div>

    <!-- Yapılandırma sunucudan gelmeden özet "devre dışı", form da varsayılan değerlerle
         görünüyordu; kullanıcı proxy'yi kapalı sanıp yanlış karar veriyordu. -->
    <p v-if="selectedSiteName && !currentConfiguration" class="empty-state" role="status" aria-live="polite">
      {{ t("panel.common.loading") }}
    </p>

    <template v-else-if="selectedSiteName">
      <div class="summary-strip">
        <article class="summary-item">
          <span class="tile-label">{{ t("panel.proxy.arrStatus") }}</span>
          <strong>{{ currentConfiguration?.isArrEnabled ? t("panel.proxy.arrEnabled") : t("panel.proxy.arrDisabled") }}</strong>
        </article>

        <article class="summary-item">
          <span class="tile-label">{{ t("panel.proxy.proxyStatus") }}</span>
          <strong>{{ currentConfiguration?.isProxyEnabled ? t("panel.proxy.proxyEnabled") : t("panel.proxy.proxyDisabled") }}</strong>
        </article>

        <article class="summary-item">
          <span class="tile-label">{{ t("panel.proxy.preserveHostHeader") }}</span>
          <strong>{{ currentConfiguration?.preserveHostHeader ? t("panel.status.enabled") : t("panel.status.disabled") }}</strong>
        </article>

        <article class="summary-item">
          <span class="tile-label">{{ t("panel.proxy.responseHeaderRewrite") }}</span>
          <strong>{{ currentConfiguration?.reverseRewriteHostInResponseHeaders ? t("panel.status.enabled") : t("panel.status.disabled") }}</strong>
        </article>
      </div>

      <div class="proxy-columns">
        <section v-if="activeSection === 'proxy'" class="proxy-card">
          <div class="section-head">
            <div>
              <h3>{{ t("panel.proxy.configTitle") }}</h3>
              <p class="meta">{{ t("panel.proxy.configSubtitle") }}</p>
            </div>
            <span :class="['pill', currentConfiguration?.isProxyEnabled ? 'ok' : 'muted']">
              {{ currentConfiguration?.isProxyEnabled ? t("panel.status.enabled") : t("panel.status.disabled") }}
            </span>
          </div>

          <label>
            <span>{{ t("panel.proxy.destinationUrl") }}</span>
            <input v-model="proxyForm.destinationUrl" placeholder="http://127.0.0.1:3000" />
          </label>

          <label>
            <span>{{ t("panel.proxy.mountPath") }}</span>
            <input v-model="proxyForm.mountPath" placeholder="/" />
          </label>

          <label class="checkbox">
            <input v-model="proxyForm.enableHtmlLinkRewrite" type="checkbox" />
            <span>{{ t("panel.proxy.htmlLinkRewrite") }}</span>
          </label>

          <div class="action-row">
            <button
              :disabled="!selectedSiteName || proxyStore.savingSiteName === selectedSiteName"
              @click="saveProxy">
              {{ proxyStore.savingSiteName === selectedSiteName ? t("panel.proxy.saving") : t("panel.proxy.save") }}
            </button>

            <button
              class="danger"
              :disabled="!currentConfiguration?.isProxyEnabled || proxyStore.removingSiteName === selectedSiteName"
              @click="removeProxy">
              {{ proxyStore.removingSiteName === selectedSiteName ? t("panel.proxy.removing") : t("panel.proxy.remove") }}
            </button>
          </div>

          <dl class="detail-grid">
            <div>
              <dt>{{ t("panel.proxy.destinationUrl") }}</dt>
              <dd>{{ currentConfiguration?.destinationUrl || "-" }}</dd>
            </div>
            <div>
              <dt>{{ t("panel.proxy.mountPath") }}</dt>
              <dd>{{ currentConfiguration?.mountPath || "/" }}</dd>
            </div>
            <div>
              <dt>{{ t("panel.proxy.webConfig") }}</dt>
              <dd>{{ currentConfiguration?.webConfigPath || "-" }}</dd>
            </div>
            <div>
              <dt>{{ t("panel.proxy.htmlLinkRewrite") }}</dt>
              <dd>{{ currentConfiguration?.hasHtmlLinkRewrite ? t("panel.status.enabled") : t("panel.status.disabled") }}</dd>
            </div>
          </dl>
        </section>

        <section v-if="activeSection === 'rewrite'" class="proxy-card">
          <div class="section-head">
            <div>
              <h3>{{ t("panel.proxy.rewriteTitle") }}</h3>
              <p class="meta">{{ t("panel.proxy.rewriteSubtitle") }}</p>
            </div>
            <span class="pill muted">{{ currentConfiguration?.rewriteRules.length ?? 0 }}</span>
          </div>

          <label>
            <span>{{ t("panel.proxy.ruleName") }}</span>
            <input v-model="rewriteForm.name" />
          </label>

          <label>
            <span>{{ t("panel.proxy.matchPattern") }}</span>
            <input v-model="rewriteForm.matchPattern" placeholder="^legacy/(.*)$" />
          </label>

          <label>
            <span>{{ t("panel.proxy.rewriteUrl") }}</span>
            <input v-model="rewriteForm.rewriteUrl" placeholder="/archive/{R:1}" />
          </label>

          <div class="toggle-grid">
            <label class="checkbox">
              <input v-model="rewriteForm.stopProcessing" type="checkbox" />
              <span>{{ t("panel.proxy.stopProcessing") }}</span>
            </label>

            <label class="checkbox">
              <input v-model="rewriteForm.appendQueryString" type="checkbox" />
              <span>{{ t("panel.proxy.appendQueryString") }}</span>
            </label>

            <label class="checkbox">
              <input v-model="rewriteForm.ignoreCase" type="checkbox" />
              <span>{{ t("panel.proxy.ignoreCase") }}</span>
            </label>
          </div>

          <button
            :disabled="!selectedSiteName || proxyStore.rewritingSiteName === selectedSiteName"
            @click="saveRewriteRule">
            {{ proxyStore.rewritingSiteName === selectedSiteName ? t("panel.proxy.savingRule") : t("panel.proxy.saveRule") }}
          </button>

          <div v-if="currentConfiguration?.rewriteRules.length" class="rule-list">
            <article v-for="rule in currentConfiguration.rewriteRules" :key="rule.name" class="rule-item">
              <div class="section-head">
                <strong>{{ rule.name }}</strong>
                <span class="pill muted">{{ rule.stopProcessing ? t("panel.proxy.stopProcessing") : t("panel.proxy.passThrough") }}</span>
              </div>
              <dl class="detail-grid">
                <div>
                  <dt>{{ t("panel.proxy.matchPattern") }}</dt>
                  <dd>{{ rule.matchPattern }}</dd>
                </div>
                <div>
                  <dt>{{ t("panel.proxy.rewriteUrl") }}</dt>
                  <dd>{{ rule.rewriteUrl }}</dd>
                </div>
                <div>
                  <dt>{{ t("panel.proxy.appendQueryString") }}</dt>
                  <dd>{{ rule.appendQueryString ? t("panel.status.enabled") : t("panel.status.disabled") }}</dd>
                </div>
                <div>
                  <dt>{{ t("panel.proxy.ignoreCase") }}</dt>
                  <dd>{{ rule.ignoreCase ? t("panel.status.enabled") : t("panel.status.disabled") }}</dd>
                </div>
              </dl>
            </article>
          </div>

          <p v-else class="meta">
            {{ t("panel.proxy.noRules") }}
          </p>
        </section>
      </div>
    </template>

    <p v-else class="meta">
      {{ t("panel.proxy.selectSiteHint") }}
    </p>
  </section>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from "vue";
import { useI18n } from "vue-i18n";
import { useProxyStore } from "../stores/proxy";
import { useSitesStore } from "../stores/sites";
import { useConfirm } from "../components/ConfirmDialog.vue";
import { useNotice } from "../composables/useNotice";

const { t } = useI18n();
const proxyStore = useProxyStore();
const sitesStore = useSitesStore();
const { confirm } = useConfirm();
const { notice, noticeError, showNotice, resolveApiMessage } = useNotice();
const activeSection = ref<"proxy" | "rewrite">("proxy");

const selectedSiteName = computed({
  get: () => sitesStore.selectedSiteName,
  set: (value: string) => sitesStore.selectSite(value)
});

const currentConfiguration = computed(() =>
  selectedSiteName.value ? proxyStore.getConfiguration(selectedSiteName.value) : null);

const proxyForm = reactive({
  destinationUrl: "",
  mountPath: "/",
  enableHtmlLinkRewrite: true
});

const rewriteForm = reactive({
  name: "",
  matchPattern: "^legacy/(.*)$",
  rewriteUrl: "/archive/{R:1}",
  stopProcessing: true,
  appendQueryString: true,
  ignoreCase: true
});

onMounted(async () => {
  try {
    if (sitesStore.sites.length === 0 && !sitesStore.loading) {
      await sitesStore.fetchSites();
    }
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
});

watch(selectedSiteName, async (siteName) => {
  if (!siteName) {
    return;
  }

  try {
    await proxyStore.fetchConfiguration(siteName);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
}, { immediate: true });

watch(currentConfiguration, (configuration) => {
  if (!configuration) {
    return;
  }

  proxyForm.destinationUrl = configuration.destinationUrl || "";
  proxyForm.mountPath = configuration.mountPath || "/";
  proxyForm.enableHtmlLinkRewrite = configuration.hasHtmlLinkRewrite;
}, { immediate: true });

async function refreshConfiguration() {
  if (!selectedSiteName.value) {
    showNotice(t("panel.proxy.selectSiteHint"), true);
    return;
  }

  try {
    await proxyStore.fetchConfiguration(selectedSiteName.value);
    showNotice(t("panel.proxy.refreshed"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
}

async function saveProxy() {
  if (!selectedSiteName.value) {
    showNotice(t("panel.proxy.selectSiteHint"), true);
    return;
  }

  try {
    await proxyStore.saveProxy(selectedSiteName.value, { ...proxyForm });
    showNotice(t("proxy.proxy_enabled"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
}

async function removeProxy() {
  if (!selectedSiteName.value) {
    showNotice(t("panel.proxy.selectSiteHint"), true);
    return;
  }

  // Kaldırma sitenin YÖNLENDİRMESİNİ keser (site anında erişilemez hâle gelebilir): onaysız yapılmaz.
  const site = selectedSiteName.value;
  await confirm({
    title: t("panel.proxy.confirmRemoveTitle"),
    message: t("panel.proxy.confirmRemoveMessage", { name: site }),
    impact: [site, currentConfiguration.value?.destinationUrl || ""].filter((line) => line.length > 0),
    confirmLabel: t("panel.proxy.remove"),
    action: async () => {
      try {
        await proxyStore.removeProxy(site);
        proxyForm.destinationUrl = "";
        proxyForm.mountPath = "/";
        proxyForm.enableHtmlLinkRewrite = true;
        showNotice(t("proxy.proxy_removed"), false);
      } catch (error) {
        showNotice(resolveApiMessage(error), true);
      }
    }
  });
}

async function saveRewriteRule() {
  if (!selectedSiteName.value) {
    showNotice(t("panel.proxy.selectSiteHint"), true);
    return;
  }

  try {
    await proxyStore.saveRewriteRule(selectedSiteName.value, { ...rewriteForm });
    rewriteForm.name = "";
    showNotice(t("proxy.rewrite_rule_saved"), false);
  } catch (error) {
    showNotice(resolveApiMessage(error), true);
  }
}

</script>

<style scoped>
.proxy-panel,
.status-grid,
.proxy-columns,
.proxy-card,
.detail-grid,
.rule-list,
.toggle-grid {
  display: grid;
  gap: 12px;
}

.proxy-header,
.section-head,
.action-row {
  display: flex;
  gap: 12px;
  justify-content: space-between;
  align-items: flex-start;
}

.status-grid,
.proxy-columns,
.detail-grid {
  grid-template-columns: repeat(4, minmax(0, 1fr));
}

.proxy-columns,
.detail-grid {
  grid-template-columns: repeat(2, minmax(0, 1fr));
}

.status-tile,
.proxy-card,
.rule-item {
  display: grid;
  gap: 12px;
  padding: 14px;
  border-radius: var(--radius-md);
  border: 1px solid var(--panel-border);
  background: linear-gradient(180deg, var(--surface-2), var(--surface-2));
}

.tile-label,
.detail-grid dt,
.meta {
  color: var(--text-muted);
}

.detail-grid {
  margin: 0;
}

.detail-grid div {
  display: grid;
  gap: 4px;
}

.detail-grid dd,
.meta {
  margin: 0;
  word-break: break-word;
}

.pill {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  min-height: 34px;
  padding: 4px 12px;
  border-radius: 999px;
  font-size: 0.82rem;
  font-weight: 700;
}

.pill.ok {
  background: color-mix(in srgb, var(--accent-1) 14%, transparent);
  color: var(--accent-1);
}

.pill.muted {
  background: color-mix(in srgb, var(--warn-1) 12%, transparent);
  color: var(--warn-text);
}

.site-select {
  width: min(260px, 52vw);
}

.action-row {
  align-items: stretch;
}

.action-row button {
  flex: 1 1 0;
}

.toggle-grid {
  grid-template-columns: repeat(3, minmax(0, 1fr));
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
textarea,
select,
button {
  width: 100%;
  border-radius: var(--radius-sm);
  font: inherit;
}

input,
textarea,
select {
  border: 1px solid var(--panel-border);
  padding: 12px 14px;
  background: var(--input-bg);
  color: var(--text-strong);
}

@media (max-width: 980px) {
  .status-grid,
  .proxy-columns,
  .detail-grid,
  .toggle-grid {
    grid-template-columns: 1fr;
  }
}

@media (max-width: 760px) {
  .proxy-header,
  .section-head,
  .action-row {
    flex-direction: column;
    align-items: flex-start;
  }

  .action-row button {
    width: 100%;
  }
}
</style>
