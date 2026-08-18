<template>
  <section class="panel wide sb-view">
    <div class="module-head">
      <div>
        <h2>{{ t("panel.siteBuilder.title") }}</h2>
        <p class="meta">{{ t("panel.siteBuilder.subtitle") }}</p>
      </div>
      <select v-model="selectedSite" class="compact-select" :disabled="loading">
        <option v-if="sitesStore.sites.length === 0" value="">{{ t("panel.php.selectSite") }}</option>
        <option v-for="site in sitesStore.sites" :key="site.name" :value="site.name">{{ site.name }}</option>
      </select>
    </div>

    <p v-if="notice" :class="['notice', 'page-notice', noticeError ? 'error' : '']" role="alert">{{ notice }}</p>

    <p v-if="!selectedSite" class="empty-state">{{ t("panel.siteBuilder.selectSiteHint") }}</p>
    <p v-else-if="!project" class="empty-state" role="status" aria-live="polite">{{ t("panel.common.loading") }}</p>

    <template v-else>
      <div class="module-toolbar">
        <div class="segmented-tabs">
          <button :class="{ active: tab === 'design' }" @click="tab = 'design'">
            {{ t("panel.siteBuilder.tab.design") }}
          </button>
          <button :class="{ active: tab === 'content' }" @click="tab = 'content'">
            {{ t("panel.siteBuilder.tab.content") }}
          </button>
          <button :class="{ active: tab === 'publish' }" @click="openPublishTab">
            {{ t("panel.siteBuilder.tab.publish") }}
          </button>
        </div>
        <div class="sb-actions">
          <button type="button" class="secondary" :disabled="saving" @click="save">
            {{ saving ? t("panel.common.loading") : t("panel.siteBuilder.save") }}
          </button>
        </div>
      </div>

      <div class="sb-layout">
        <div class="sb-editor">
          <!-- ---------------------------------------------------------- TASARIM -->
          <div v-if="tab === 'design'" class="form-surface">
            <label class="stacked-field">
              <span class="field-label">{{ t("panel.siteBuilder.siteTitle") }}</span>
              <input v-model="project.siteTitle" maxlength="120" />
            </label>
            <label class="stacked-field">
              <span class="field-label">{{ t("panel.siteBuilder.tagline") }}</span>
              <input v-model="project.tagline" maxlength="200" />
            </label>
            <label class="stacked-field">
              <span class="field-label">{{ t("panel.siteBuilder.metaDescription") }}</span>
              <textarea v-model="project.metaDescription" rows="2" maxlength="300"></textarea>
            </label>
            <label class="stacked-field">
              <span class="field-label">{{ t("panel.siteBuilder.languageLabel") }}</span>
              <select v-model="project.language">
                <option value="tr">{{ t("panel.language.tr") }}</option>
                <option value="en">{{ t("panel.language.en") }}</option>
              </select>
            </label>

            <h4>{{ t("panel.siteBuilder.templateLabel") }}</h4>
            <div class="sb-choices">
              <button
                v-for="item in catalog?.templates ?? []"
                :key="item.id"
                type="button"
                :class="['sb-choice', { active: project.templateId === item.id }]"
                @click="project.templateId = item.id">
                {{ t(item.labelKey) }}
              </button>
            </div>

            <h4>{{ t("panel.siteBuilder.paletteLabel") }}</h4>
            <div class="sb-choices">
              <button
                v-for="item in catalog?.palettes ?? []"
                :key="item.id"
                type="button"
                :class="['sb-choice', { active: project.paletteId === item.id }]"
                @click="project.paletteId = item.id">
                <span class="sb-swatch" :style="{ background: item.accent, borderColor: item.text }"></span>
                {{ t(item.labelKey) }}
              </button>
            </div>

            <h4>{{ t("panel.siteBuilder.fontLabel") }}</h4>
            <div class="sb-choices">
              <button
                v-for="item in catalog?.fonts ?? []"
                :key="item.id"
                type="button"
                :class="['sb-choice', { active: project.fontId === item.id }]"
                @click="project.fontId = item.id">
                {{ t(item.labelKey) }}
              </button>
            </div>
          </div>

          <!-- ---------------------------------------------------------- İÇERİK -->
          <div v-else-if="tab === 'content'" class="form-surface">
            <details v-for="section in project.sections" :key="section.type" class="sb-section" open>
              <summary>
                {{ t(`panel.siteBuilder.section.${section.type}`) }}
                <span v-if="!section.enabled" class="status-pill">{{ t("panel.status.disabled") }}</span>
              </summary>

              <label class="sb-toggle">
                <input v-model="section.enabled" type="checkbox" />
                <span>{{ t("panel.siteBuilder.sectionEnabled") }}</span>
              </label>

              <template v-if="section.enabled">
                <label class="stacked-field">
                  <span class="field-label">{{ t("panel.siteBuilder.heading") }}</span>
                  <input v-model="section.heading" maxlength="160" />
                </label>
                <label class="stacked-field">
                  <span class="field-label">{{ t("panel.siteBuilder.body") }}</span>
                  <textarea v-model="section.body" rows="4" maxlength="4000"></textarea>
                </label>

                <label v-if="section.type === 'hero' || section.type === 'about'" class="stacked-field">
                  <span class="field-label">{{ t("panel.siteBuilder.image") }}</span>
                  <select v-model="section.imageName">
                    <option value="">{{ t("panel.siteBuilder.noImage") }}</option>
                    <option v-for="image in images" :key="image.name" :value="image.name">{{ image.name }}</option>
                  </select>
                </label>

                <template v-if="section.type === 'contact'">
                  <label class="stacked-field">
                    <span class="field-label">{{ t("panel.siteBuilder.email") }}</span>
                    <input v-model="section.email" maxlength="160" />
                  </label>
                  <label class="stacked-field">
                    <span class="field-label">{{ t("panel.siteBuilder.phone") }}</span>
                    <input v-model="section.phone" maxlength="60" />
                  </label>
                  <label class="stacked-field">
                    <span class="field-label">{{ t("panel.siteBuilder.address") }}</span>
                    <textarea v-model="section.address" rows="2" maxlength="300"></textarea>
                  </label>
                  <!-- DÜRÜSTLÜK: çalışmayan bir form koymaktansa hiç koymamak. Ziyaretçi
                       gönderdiğini sanıp yanıt bekleseydi, bu bir özellik değil bir arıza olurdu. -->
                  <p class="meta sb-note">{{ t("panel.siteBuilder.noContactForm") }}</p>
                </template>

                <template v-if="section.type === 'services' || section.type === 'gallery'">
                  <div v-for="(item, index) in section.items" :key="index" class="sb-item">
                    <label class="stacked-field">
                      <span class="field-label">{{ t("panel.siteBuilder.itemTitle") }}</span>
                      <input v-model="item.title" maxlength="120" />
                    </label>
                    <label v-if="section.type === 'services'" class="stacked-field">
                      <span class="field-label">{{ t("panel.siteBuilder.itemText") }}</span>
                      <textarea v-model="item.text" rows="2" maxlength="1000"></textarea>
                    </label>
                    <label class="stacked-field">
                      <span class="field-label">{{ t("panel.siteBuilder.image") }}</span>
                      <select v-model="item.imageName">
                        <option value="">{{ t("panel.siteBuilder.noImage") }}</option>
                        <option v-for="image in images" :key="image.name" :value="image.name">{{ image.name }}</option>
                      </select>
                    </label>
                    <button type="button" class="secondary" @click="section.items.splice(index, 1)">
                      {{ t("panel.siteBuilder.removeItem") }}
                    </button>
                  </div>
                  <button
                    type="button"
                    class="secondary"
                    :disabled="section.items.length >= 12"
                    @click="section.items.push({ title: '', text: '', imageName: '' })">
                    {{ t("panel.siteBuilder.addItem") }}
                  </button>
                </template>
              </template>
            </details>

            <h4>{{ t("panel.siteBuilder.images") }}</h4>
            <p class="meta">{{ t("panel.siteBuilder.imagesHint", { size: maxImageMb }) }}</p>
            <input ref="fileInput" type="file" accept="image/png,image/jpeg,image/gif,image/webp" @change="uploadImage" />
            <div v-if="images.length" class="sb-gallery">
              <figure v-for="image in images" :key="image.name">
                <img :src="imageUrl(image.name)" :alt="image.name" loading="lazy" />
                <button type="button" class="secondary" @click="removeImage(image.name)">
                  {{ t("panel.siteBuilder.deleteImage") }}
                </button>
              </figure>
            </div>
          </div>

          <!-- ---------------------------------------------------------- YAYIN -->
          <div v-else class="form-surface">
            <p v-if="!plan" class="empty-state" role="status">{{ t("panel.common.loading") }}</p>
            <template v-else>
              <p class="meta">
                {{ t("panel.siteBuilder.publishTarget") }}: <code>{{ plan.targetPath || "-" }}</code>
              </p>

              <p v-if="!plan.canPublish" class="notice error" role="alert">
                {{ t(`sitebuilder.${blockedKeySuffix(plan.blockedReasonKey)}`) }}
              </p>

              <template v-else>
                <p class="meta">
                  {{ t("panel.siteBuilder.existingFiles", { count: plan.existingFileCount }) }}
                </p>

                <h4>{{ t("panel.siteBuilder.publishFiles") }}</h4>
                <ul class="sb-filelist">
                  <li v-for="file in plan.filesToWrite" :key="file"><code>{{ file }}</code></li>
                </ul>

                <!--
                  ÜZERİNE YAZMA UYARISI. Ezilecek dosyalar ADIYLA listelenir; "emin misiniz?"
                  tek başına bilgisiz bir onaydır. Onay kutusu her yayında değil, YALNIZ gerçek
                  bir çakışma varken çıkar — her seferinde çıkan uyarı okunmaz hâle gelir.
                -->
                <div v-if="plan.requiresConfirmation" class="notice warn-banner sb-warn" role="alert">
                  <strong>{{ t("panel.siteBuilder.overwriteTitle") }}</strong>
                  <p>{{ t("panel.siteBuilder.overwriteHint") }}</p>
                  <ul class="sb-filelist">
                    <li v-for="file in plan.conflicts" :key="file"><code>{{ file }}</code></li>
                  </ul>
                  <label class="sb-toggle">
                    <input v-model="overwriteConfirmed" type="checkbox" />
                    <span>{{ t("panel.siteBuilder.overwriteConfirm") }}</span>
                  </label>
                </div>
                <p v-else class="meta">{{ t("panel.siteBuilder.noConflicts") }}</p>

                <p class="meta sb-note">{{ t("panel.siteBuilder.neverDeletes") }}</p>

                <button
                  type="button"
                  :disabled="publishing || (plan.requiresConfirmation && !overwriteConfirmed)"
                  @click="publish">
                  {{ publishing ? t("panel.common.loading") : t("panel.siteBuilder.publish") }}
                </button>

                <p v-if="project.published && project.publishedAtUtc" class="meta">
                  {{ t("panel.siteBuilder.lastPublished") }}: {{ formatDate(project.publishedAtUtc) }}
                </p>
              </template>
            </template>
          </div>
        </div>

        <!-- ---------------------------------------------------------- ÖNİZLEME -->
        <div class="sb-preview">
          <div class="sb-preview-head">
            <h4>{{ t("panel.siteBuilder.preview") }}</h4>
            <button type="button" class="secondary" :disabled="previewLoading" @click="loadPreview">
              {{ t("panel.siteBuilder.refreshPreview") }}
            </button>
          </div>
          <p class="meta">{{ t("panel.siteBuilder.previewHint") }}</p>
          <!--
            sandbox="allow-same-origin": script ÇALIŞTIRMA izni YOK. Önizleme müşterinin yazdığı
            metni içerir; kaçırmada bir gün bir eksik olsa bile burada script çalışamaz. Aynı
            köken izni yalnız görsellerin oturum çerezleriyle yüklenebilmesi için verilmiştir.
          -->
          <iframe
            v-if="previewHtml"
            class="sb-frame"
            sandbox="allow-same-origin"
            :srcdoc="previewHtml"
            :title="t('panel.siteBuilder.preview')"></iframe>
          <p v-else class="empty-state">{{ t("panel.common.loading") }}</p>
        </div>
      </div>
    </template>
  </section>
</template>

<script setup lang="ts">
import { computed, onMounted, ref, watch } from "vue";
import { useI18n } from "vue-i18n";
import { useSitesStore } from "../stores/sites";
import { extractApiMessage } from "../stores/auth";
import {
  deleteSiteBuilderImage,
  getSiteBuilderCatalog,
  getSiteBuilderImages,
  getSiteBuilderPreview,
  getSiteBuilderProject,
  getSiteBuilderPublishPlan,
  publishSiteBuilder,
  saveSiteBuilderProject,
  siteBuilderImageUrl,
  uploadSiteBuilderImage,
  type SiteBuilderCatalog,
  type SiteBuilderImage,
  type SiteBuilderProject,
  type SiteBuilderPublishPlan
} from "../api/siteBuilder";

const { t } = useI18n();
const sitesStore = useSitesStore();

const catalog = ref<SiteBuilderCatalog | null>(null);
const project = ref<SiteBuilderProject | null>(null);
const images = ref<SiteBuilderImage[]>([]);
const plan = ref<SiteBuilderPublishPlan | null>(null);
const previewHtml = ref("");
const fileInput = ref<HTMLInputElement | null>(null);

const tab = ref<"design" | "content" | "publish">("design");
const loading = ref(false);
const saving = ref(false);
const publishing = ref(false);
const previewLoading = ref(false);
const overwriteConfirmed = ref(false);
const notice = ref("");
const noticeError = ref(false);

const selectedSite = computed({
  get: () => sitesStore.selectedSiteName,
  set: (value: string) => sitesStore.selectSite(value)
});

const maxImageMb = computed(() =>
  catalog.value ? Math.round(catalog.value.maxImageBytes / (1024 * 1024)) : 5);

onMounted(async () => {
  await sitesStore.fetchSites().catch(() => undefined);
  try {
    catalog.value = await getSiteBuilderCatalog();
  } catch (error) {
    report(error);
  }
  await load();
});

watch(selectedSite, load);

async function load() {
  if (!selectedSite.value) {
    project.value = null;
    return;
  }

  loading.value = true;
  try {
    project.value = await getSiteBuilderProject(selectedSite.value);
    images.value = await getSiteBuilderImages(selectedSite.value);
    await loadPreview();
  } catch (error) {
    report(error);
    project.value = null;
  } finally {
    loading.value = false;
  }
}

async function save() {
  if (!project.value || !selectedSite.value) {
    return;
  }

  saving.value = true;
  try {
    project.value = await saveSiteBuilderProject(selectedSite.value, project.value);
    inform(t("panel.siteBuilder.savedNotice"));
    await loadPreview();
  } catch (error) {
    report(error);
  } finally {
    saving.value = false;
  }
}

async function loadPreview() {
  if (!selectedSite.value) {
    return;
  }

  previewLoading.value = true;
  try {
    previewHtml.value = await getSiteBuilderPreview(selectedSite.value);
  } catch (error) {
    report(error);
    previewHtml.value = "";
  } finally {
    previewLoading.value = false;
  }
}

/** Yayın sekmesi açılırken plan HER SEFERINDE yeniden alınır: disk bu arada değişmiş olabilir. */
async function openPublishTab() {
  tab.value = "publish";
  overwriteConfirmed.value = false;
  plan.value = null;
  if (!selectedSite.value) {
    return;
  }

  try {
    plan.value = await getSiteBuilderPublishPlan(selectedSite.value);
  } catch (error) {
    report(error);
  }
}

async function publish() {
  if (!selectedSite.value) {
    return;
  }

  publishing.value = true;
  try {
    const result = await publishSiteBuilder(selectedSite.value, overwriteConfirmed.value);
    inform(t("panel.siteBuilder.publishedNotice", { count: result.writtenFiles.length }));
    project.value = await getSiteBuilderProject(selectedSite.value);
    plan.value = await getSiteBuilderPublishPlan(selectedSite.value);
    overwriteConfirmed.value = false;
  } catch (error) {
    report(error);
  } finally {
    publishing.value = false;
  }
}

async function uploadImage(event: Event) {
  const input = event.target as HTMLInputElement;
  const file = input.files?.[0];
  if (!file || !selectedSite.value) {
    return;
  }

  try {
    await uploadSiteBuilderImage(selectedSite.value, file);
    images.value = await getSiteBuilderImages(selectedSite.value);
    inform(t("panel.siteBuilder.imageUploadedNotice"));
  } catch (error) {
    report(error);
  } finally {
    // Aynı dosyayı tekrar seçebilmek için alanı temizle.
    if (fileInput.value) {
      fileInput.value.value = "";
    }
  }
}

async function removeImage(name: string) {
  if (!selectedSite.value) {
    return;
  }

  try {
    await deleteSiteBuilderImage(selectedSite.value, name);
    images.value = await getSiteBuilderImages(selectedSite.value);
    project.value = await getSiteBuilderProject(selectedSite.value);
    await loadPreview();
  } catch (error) {
    report(error);
  }
}

function imageUrl(name: string) {
  return selectedSite.value ? siteBuilderImageUrl(selectedSite.value, name) : "";
}

/** Sunucudan gelen engel anahtarı ("sitebuilder.target_is_link") çeviriye çevrilir. */
function blockedKeySuffix(key: string) {
  return key.startsWith("sitebuilder.") ? key.slice("sitebuilder.".length) : key;
}

function formatDate(value: string) {
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? value : date.toLocaleString();
}

function inform(message: string) {
  notice.value = message;
  noticeError.value = false;
}

function report(error: unknown) {
  notice.value = extractApiMessage(error);
  noticeError.value = true;
}
</script>

<style scoped>
.sb-view {
  display: grid;
  gap: 16px;
}

.sb-layout {
  display: grid;
  gap: 18px;
  grid-template-columns: 1fr;
  align-items: start;
}

@media (min-width: 1100px) {
  .sb-layout {
    grid-template-columns: minmax(0, 1fr) minmax(0, 1fr);
  }
}

.sb-editor {
  min-width: 0;
}

.sb-preview {
  min-width: 0;
  display: grid;
  gap: 8px;
  position: sticky;
  top: 12px;
}

.sb-preview-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
}

.sb-frame {
  width: 100%;
  height: min(70vh, 720px);
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-lg);
  background: #ffffff;
}

.sb-choices {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  margin-bottom: 12px;
}

.sb-choice {
  display: inline-flex;
  align-items: center;
  gap: 8px;
}

.sb-choice.active {
  outline: 2px solid var(--accent-1);
}

.sb-swatch {
  width: 14px;
  height: 14px;
  border-radius: 4px;
  border: 1px solid;
  display: inline-block;
}

.sb-section {
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-lg);
  padding: 10px 12px;
  margin-bottom: 12px;
}

.sb-section summary {
  cursor: pointer;
  font-weight: 600;
}

.sb-item {
  border: 1px dashed var(--panel-border);
  border-radius: var(--radius-lg);
  padding: 10px;
  margin-bottom: 10px;
}

.sb-toggle {
  display: flex;
  align-items: center;
  gap: 8px;
  margin: 8px 0;
}

.sb-gallery {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(120px, 1fr));
  gap: 10px;
  margin-top: 10px;
}

.sb-gallery figure {
  margin: 0;
  display: grid;
  gap: 6px;
}

.sb-gallery img {
  width: 100%;
  height: 90px;
  object-fit: cover;
  border-radius: 8px;
  border: 1px solid var(--panel-border);
}

.sb-filelist {
  margin: 6px 0;
  padding-left: 18px;
  display: grid;
  gap: 2px;
}

.sb-warn {
  display: grid;
  gap: 6px;
}

.sb-note {
  font-size: 0.8rem;
}

.sb-actions {
  display: flex;
  gap: 8px;
}
</style>
