<template>
  <section class="panel store-panel">
    <div class="store-hero">
      <div>
        <span class="eyebrow">{{ t("panel.appStore.catalog") }}</span>
        <h3>{{ t("panel.appStore.title") }}</h3>
        <p class="meta">{{ t("panel.appStore.subtitle") }}</p>
      </div>
      <div class="hero-stats">
        <span><strong>{{ templates.length }}</strong>{{ t("panel.appStore.applicationCount") }}</span>
        <span><strong>{{ featuredCount }}</strong>{{ t("panel.appStore.automaticCount") }}</span>
        <button class="secondary compact" @click="refresh">
          {{ t("panel.appStore.refresh") }}
        </button>
      </div>
    </div>

    <p v-if="notice" :class="['notice', 'page-notice', { error: noticeError }]" :role="noticeError ? 'alert' : 'status'">
      {{ notice }}
    </p>

    <details v-if="noticeDetails" class="error-details" open>
      <summary>{{ t("panel.appStore.errorDetails") }}</summary>
      <pre>{{ noticeDetails }}</pre>
    </details>

    <div v-if="wpAdmin.adminPassword" class="notice wp-creds" role="status">
      <strong>{{ t("panel.appStore.wpReady") }}</strong>
      <div class="wp-cred-line">{{ t("panel.appStore.wpAdminUrl") }}: <a :href="wpAdmin.adminUrl" target="_blank" rel="noopener">{{ wpAdmin.adminUrl }}</a></div>
      <div class="wp-cred-line">{{ t("panel.appStore.wpUser") }}: <code>{{ wpAdmin.adminUser }}</code></div>
      <div class="wp-cred-line">{{ t("panel.appStore.wpPass") }}: <code>{{ wpAdmin.adminPassword }}</code></div>
      <small>{{ t("panel.appStore.wpCredHint") }}</small>
    </div>

    <p v-if="sitesUnavailable" class="notice warn" role="alert">
      {{ t("panel.appStore.sitesUnavailable") }}
    </p>

    <p v-else-if="sitesStore.sites.length === 0" class="notice warn">
      {{ t("panel.appStore.noSites") }}
    </p>

    <div class="store-toolbar">
      <div class="category-tabs" role="tablist">
        <!-- Seçili sekme GÖRSEL olarak yalnız renkle belliydi; `aria-selected` olmadan ekran
             okuyucu bütün kategori düğmelerini aynı okuyordu. -->
        <button
          v-for="category in categories"
          :key="category"
          type="button"
          role="tab"
          :aria-selected="activeCategory === category"
          :class="['category-tab', { active: activeCategory === category }]"
          @click="activeCategory = category">
          {{ categoryLabel(category) }}
        </button>
      </div>
      <label class="store-search">
        <span>{{ t("panel.appStore.search") }}</span>
        <input v-model="search" :placeholder="t('panel.appStore.searchPlaceholder')" />
      </label>
    </div>

    <div class="template-table">
      <div class="template-row table-head">
        <span>{{ t("panel.appStore.application") }}</span>
        <span>{{ t("panel.appStore.environment") }}</span>
        <span>{{ t("panel.appStore.automation") }}</span>
        <span>{{ t("panel.php.targetSite") }}</span>
        <span>{{ t("panel.software.action") }}</span>
      </div>

      <article
        v-for="template in filteredTemplates"
        :key="template.id"
        :class="['template-row', { featured: template.featured }]">
        <div class="template-copy">
          <div class="app-mark" :data-runtime="template.runtime.toLowerCase()">
            {{ appInitials(template.displayName) }}
          </div>
          <div>
            <div class="title-line">
              <strong>{{ template.displayName }}</strong>
              <span v-if="template.featured" class="featured-pill">{{ t("panel.appStore.recommended") }}</span>
            </div>
            <p class="meta">{{ template.description }}</p>
            <div class="requirement-list">
              <span v-for="requirement in template.requirements" :key="requirement">
                {{ requirement }}
              </span>
            </div>
          </div>
        </div>

        <div class="runtime-cell">
          <span class="runtime-pill">{{ template.runtime }}</span>
          <small>{{ categoryLabel(template.category) }}</small>
        </div>

        <div class="automation-list">
          <span v-for="step in template.automaticSetup" :key="step">{{ step }}</span>
        </div>

        <label class="site-select">
          <span class="visually-hidden">{{ t("panel.php.targetSite") }}</span>
          <select v-model="siteMap[template.id]">
            <option value="">{{ t("panel.php.selectSite") }}</option>
            <option v-for="site in sitesStore.sites" :key="site.name" :value="site.name">
              {{ site.name }}
            </option>
          </select>
        </label>

        <button
          class="install-button"
          v-bind="writeGate(busyTemplate !== '' || sitesUnavailable || sitesStore.sites.length === 0 || !siteMap[template.id])"
          @click="install(template.id)">
          {{ busyTemplate === template.id ? t("panel.appStore.installing") : t("panel.appStore.install") }}
        </button>
      </article>

      <p v-if="loading && templates.length === 0" class="empty-state">
        {{ t("panel.common.loading") }}
      </p>
      <p v-else-if="filteredTemplates.length === 0" class="empty-state">
        {{ t("panel.appStore.noResults") }}
      </p>
    </div>

    <div v-if="busyTemplate" class="install-progress" role="status">
      <span class="progress-dot"></span>
      <div>
        <strong>{{ busyTemplateName }}</strong>
        <p>{{ t("panel.appStore.installHint") }}</p>
      </div>
    </div>
  </section>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from "vue";
import { useI18n } from "vue-i18n";
import { getAppTemplates, installAppTemplate, type AppTemplate } from "../api/store";
import { useConfirm } from "../components/ConfirmDialog.vue";
import { extractApiDetails, extractApiMessage } from "../stores/auth";
import { useSitesStore } from "../stores/sites";
import { useSubscriptionGate } from "../composables/useSubscriptionGate";

const { t } = useI18n();
// Askidaki abonelikte YAZMA dugmeleri bastan devre disi + sebebi title/aria ile soylenir
// (bkz. composables/useSubscriptionGate.ts). Okuma dugmeleri kapiya girmez.
const { writeGate } = useSubscriptionGate();
const { confirm } = useConfirm();
const sitesStore = useSitesStore();

const templates = ref<AppTemplate[]>([]);
const loading = ref(false);
const busyTemplate = ref("");
const notice = ref("");
const noticeDetails = ref("");
const noticeError = ref(false);
const sitesUnavailable = ref(false);
const search = ref("");
const activeCategory = ref("all");
const siteMap = reactive<Record<string, string>>({});
const wpAdmin = reactive({ adminUrl: "", adminUser: "", adminPassword: "" });

const categories = computed(() => [
  "all",
  ...Array.from(new Set(templates.value.map((template) => template.category))).sort()
]);

/**
 * Kategori adı backend'den SABİT ve dilden bağımsız gelir ("CMS", "Database", "E-Ticaret"…).
 * Ham basılınca İngilizce arayüzde "E-Ticaret", Türkçe arayüzde "Database" görünüyordu.
 * i18n anahtarı yoksa HAM DEĞER gösterilir — backend yeni bir kategori eklerse boşluk çıkmaz.
 */
function categoryLabel(category: string): string {
  if (category === "all") {
    return t("panel.appStore.all");
  }
  const key = `panel.appStore.category.${category.toLocaleLowerCase("en").replace(/[^a-z0-9]+/g, "")}`;
  const translated = t(key);
  return translated === key ? category : translated;
}

const filteredTemplates = computed(() => {
  const query = search.value.trim().toLocaleLowerCase();
  return templates.value
    .filter((template) => {
      const categoryMatches = activeCategory.value === "all" || template.category === activeCategory.value;
      const searchMatches = !query || [
        template.displayName,
        template.description,
        template.runtime,
        template.category,
        ...template.requirements,
        ...template.automaticSetup
      ].some((value) => value.toLocaleLowerCase().includes(query));
      return categoryMatches && searchMatches;
    })
    .sort((left, right) => Number(right.featured) - Number(left.featured) ||
      left.displayName.localeCompare(right.displayName));
});

const featuredCount = computed(() => templates.value.filter((template) => template.featured).length);
const busyTemplateName = computed(() =>
  templates.value.find((template) => template.id === busyTemplate.value)?.displayName ?? busyTemplate.value
);

watch(() => sitesStore.selectedSiteName, (siteName) => {
  if (!siteName) {
    return;
  }

  for (const template of templates.value) {
    if (!siteMap[template.id]) {
      siteMap[template.id] = siteName;
    }
  }
}, { immediate: true });

onMounted(async () => {
  await refresh();
});

async function refresh() {
  loading.value = true;
  try {
    sitesUnavailable.value = false;
    if (sitesStore.sites.length === 0 && !sitesStore.loading) {
      await sitesStore.fetchSites();
    }

    templates.value = await getAppTemplates();
    for (const template of templates.value) {
      if (!siteMap[template.id] && sitesStore.selectedSiteName) {
        siteMap[template.id] = sitesStore.selectedSiteName;
      }
    }
  } catch (error) {
    const key = extractApiMessage(error);
    if (key === "sites.iis_not_available" || key === "sites.iis_install_required") {
      sitesUnavailable.value = true;
      templates.value = await getAppTemplates().catch(() => templates.value);
      showNotice("", false);
      return;
    }

    showNotice(resolveApiMessage(error), true, extractApiDetails(error));
  } finally {
    loading.value = false;
  }
}

async function install(templateId: string, cleanInstall = false) {
  busyTemplate.value = templateId;
  showNotice("", false);
  wpAdmin.adminUrl = "";
  wpAdmin.adminUser = "";
  wpAdmin.adminPassword = "";

  try {
    const result = await installAppTemplate({
      templateId,
      siteName: siteMap[templateId] || "",
      targetPath: "",
      cleanInstall
    });
    // WordPress otomatik kurulduysa giriş bilgilerini göster (bir kez).
    wpAdmin.adminUrl = result.adminUrl || "";
    wpAdmin.adminUser = result.adminUser || "";
    wpAdmin.adminPassword = result.adminPassword || "";
    showNotice(t("panel.appStore.installed"), false);
    await sitesStore.fetchSites();
  } catch (error) {
    // profesyonel paneller gibi: hedef klasör boş değilse "yedekle + temizle + kur" teklif et, onaylanırsa tekrar dene.
    if (!cleanInstall && extractApiMessage(error) === "store.site_has_content") {
      const targetSite = siteMap[templateId] || "";
      const templateName = templates.value.find((item) => item.id === templateId)?.displayName || templateId;
      const impact: string[] = [];
      if (targetSite) {
        impact.push(`${t("panel.files.siteLabel")}: ${targetSite}`);
      }
      impact.push(`${t("panel.appStore.application")}: ${templateName}`);

      // Yüksek riskli onay: hedef klasörün mevcut içeriği temizlenir → site adı yazılmadan devam edilemez.
      const approved = await confirm({
        title: t("panel.confirm.cleanInstallTitle", { name: targetSite }),
        message: t("panel.appStore.cleanInstallConfirm"),
        impact,
        requireText: targetSite,
        confirmLabel: t("panel.appStore.install"),
      });
      if (approved) {
        busyTemplate.value = "";
        await install(templateId, true);
        return;
      }
    }
    showNotice(resolveApiMessage(error), true, extractApiDetails(error));
  } finally {
    busyTemplate.value = "";
  }
}

function showNotice(message: string, isError: boolean, details = "") {
  notice.value = message;
  noticeError.value = isError;
  noticeDetails.value = details;
}

function resolveApiMessage(error: unknown) {
  const key = extractApiMessage(error);
  const translated = t(key);
  return translated === key ? key : translated;
}

function appInitials(displayName: string) {
  return displayName
    .split(/\s+/)
    .map((part) => part[0])
    .join("")
    .slice(0, 2)
    .toUpperCase();
}
</script>

<style scoped>
.store-panel,
.template-table,
.template-copy,
.automation-list {
  display: grid;
  gap: 12px;
}

.store-hero,
.store-toolbar,
.hero-stats,
.category-tabs,
.title-line,
.requirement-list {
  display: flex;
  gap: 10px;
}

.store-hero,
.store-toolbar {
  justify-content: space-between;
  align-items: flex-start;
}

.store-hero {
  padding: 18px;
  overflow: hidden;
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-md);
  background:
    radial-gradient(circle at 86% -20%, color-mix(in srgb, var(--accent-1) 20%, transparent), transparent 42%),
    linear-gradient(135deg, var(--surface-2), var(--surface-0));
}

.eyebrow {
  color: var(--accent-1);
  font-size: 0.74rem;
  font-weight: 800;
  letter-spacing: 0.12em;
  text-transform: uppercase;
}

.hero-stats {
  align-items: center;
  flex-wrap: wrap;
}

.hero-stats > span {
  display: grid;
  min-width: 92px;
  gap: 2px;
  padding: 8px 11px;
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-sm);
  color: var(--text-muted);
  font-size: 0.72rem;
  background: rgba(12, 16, 24, 0.5);
}

.hero-stats strong {
  color: var(--text-strong);
  font-size: 1rem;
}

.store-toolbar {
  padding: 12px;
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-sm);
  background: var(--input-bg);
}

.category-tabs,
.requirement-list {
  flex-wrap: wrap;
}

.category-tab {
  width: auto;
  padding: 7px 12px;
  background: var(--surface-2);
  color: var(--text-soft);
}

.category-tab.active {
  color: var(--on-accent);
  background: linear-gradient(135deg, var(--accent-1), var(--accent-2));
}

.store-search {
  display: grid;
  min-width: min(100%, 310px);
  gap: 5px;
}

.store-search span {
  color: var(--text-muted);
  font-size: 0.76rem;
}

.template-row {
  display: grid;
  grid-template-columns: minmax(270px, 1.45fr) 0.55fr minmax(180px, 0.8fr) minmax(190px, 0.75fr) auto;
  gap: 12px;
  align-items: center;
  padding: 11px 12px;
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-sm);
  background: var(--input-bg);
}

.template-row.featured {
  border-color: color-mix(in srgb, var(--accent-1) 34%, transparent);
  background: linear-gradient(90deg, color-mix(in srgb, var(--accent-2) 15%, transparent), var(--input-bg) 38%);
}

.table-head {
  color: var(--text-muted);
  font-size: 0.72rem;
  font-weight: 800;
  letter-spacing: 0.05em;
  text-transform: uppercase;
  background: var(--surface-1);
}

.template-copy {
  grid-template-columns: 42px 1fr;
  align-items: flex-start;
}

.app-mark {
  display: grid;
  width: 42px;
  height: 42px;
  place-items: center;
  border: 1px solid color-mix(in srgb, var(--accent-1) 24%, transparent);
  border-radius: 12px;
  color: var(--accent-1);
  font-size: 0.8rem;
  font-weight: 900;
  background: color-mix(in srgb, var(--accent-1) 10%, transparent);
}

.title-line {
  align-items: center;
  flex-wrap: wrap;
}

.featured-pill,
.runtime-pill,
.requirement-list span,
.automation-list span {
  width: fit-content;
  border-radius: 999px;
  font-size: 0.72rem;
  font-weight: 700;
}

.featured-pill {
  padding: 3px 7px;
  color: var(--on-accent);
  background: var(--accent-1);
}

.runtime-cell {
  display: grid;
  gap: 6px;
}

.runtime-pill {
  padding: 4px 9px;
  color: var(--warn-1);
  background: color-mix(in srgb, var(--warn-1) 14%, transparent);
}

.runtime-cell small {
  color: var(--text-muted);
}

.requirement-list {
  margin-top: 8px;
}

.requirement-list span {
  padding: 3px 7px;
  color: var(--text-soft);
  background: var(--surface-1);
}

.automation-list {
  gap: 4px;
}

.automation-list span {
  position: relative;
  padding-left: 13px;
  color: var(--text-soft);
}

.automation-list span::before {
  position: absolute;
  top: 0.42em;
  left: 0;
  width: 6px;
  height: 6px;
  border-radius: 50%;
  background: var(--accent-1);
  content: "";
}

.site-select select {
  min-width: 0;
}

.install-button {
  width: fit-content;
  white-space: nowrap;
}

.meta,
.empty-state,
.install-progress p {
  margin: 0;
  color: var(--text-muted);
  line-height: 1.45;
}

.error-details {
  border: 1px solid color-mix(in srgb, var(--danger-1) 34%, transparent);
  border-radius: var(--radius-sm);
  background: color-mix(in srgb, var(--danger-1) 24%, transparent);
}

.error-details summary {
  padding: 10px 12px;
  color: var(--danger-text);
  cursor: pointer;
  font-weight: 700;
}

.error-details pre {
  max-height: 260px;
  margin: 0;
  padding: 0 12px 12px;
  overflow: auto;
  color: var(--danger-text);
  font-size: 0.74rem;
  white-space: pre-wrap;
}

.install-progress {
  display: flex;
  gap: 12px;
  align-items: center;
  padding: 13px;
  border: 1px solid color-mix(in srgb, var(--accent-1) 28%, transparent);
  border-radius: var(--radius-sm);
  background: color-mix(in srgb, var(--accent-1) 8%, transparent);
}

.progress-dot {
  width: 12px;
  height: 12px;
  border: 2px solid color-mix(in srgb, var(--accent-1) 30%, transparent);
  border-top-color: var(--accent-1);
  border-radius: 50%;
  animation: spin 0.8s linear infinite;
}

.visually-hidden {
  position: absolute;
  width: 1px;
  height: 1px;
  overflow: hidden;
  clip: rect(0, 0, 0, 0);
}

.notice.warn {
  color: var(--warn-text);
  background: color-mix(in srgb, var(--warn-1) 12%, transparent);
}

.notice.wp-creds {
  display: grid;
  gap: 4px;
  background: color-mix(in srgb, var(--accent-1) 10%, transparent);
}
.wp-cred-line code {
  font-family: ui-monospace, "Cascadia Code", Consolas, monospace;
  background: color-mix(in srgb, var(--accent-1) 16%, transparent);
  color: var(--accent-1);
  padding: 2px 8px;
  border-radius: 6px;
  user-select: all;
}
.wp-creds small {
  opacity: 0.75;
  margin-top: 2px;
}

@keyframes spin {
  to { transform: rotate(360deg); }
}

@media (max-width: 1180px) {
  .template-row {
    grid-template-columns: 1fr 0.6fr 0.8fr;
  }

  .template-row .site-select {
    grid-column: 1 / 3;
  }

  .table-head {
    display: none;
  }
}

@media (max-width: 760px) {
  .store-hero,
  .store-toolbar {
    flex-direction: column;
  }

  .template-row {
    grid-template-columns: 1fr;
  }

  .template-row .site-select {
    grid-column: auto;
  }

  .site-select,
  .site-select select,
  .install-button,
  .store-search {
    width: 100%;
  }
}
</style>
