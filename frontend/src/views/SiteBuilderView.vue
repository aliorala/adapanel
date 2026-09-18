<template>
  <section class="panel sb-view">
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

    <!-- Rol DURUMA GÖRE: başarı mesajı `alert` DEĞİLDİR. `alert` ekran okuyucuyu o an ne
         okuyorsa keser; "Kaydedildi" için bu, panelin diğer 40 ekranında `status` ile
         yapılan şeyin tersiydi (bkz. MigrationView, SecurityView, WebsiteWorkspaceView). -->
    <p v-if="notice" :class="['notice', 'page-notice', noticeError ? 'error' : '']" :role="noticeError ? 'alert' : 'status'">{{ notice }}</p>

    <p v-if="!selectedSite" class="empty-state">{{ t("panel.siteBuilder.selectSiteHint") }}</p>
    <p v-else-if="!project && loading" class="empty-state" role="status" aria-live="polite">{{ t("panel.common.loading") }}</p>
    <!-- "YÜKLENİYOR" SONSUZA KADAR YAZIYORDU. `load()` hata yakaladığında `project`i `null`
         yapıyor, `loading` de `finally`de false'a düşüyor; koşul yalnız `!project` olduğu için
         ekranda kalıcı bir "Yükleniyor…" kalıyordu. Kullanıcının elindeki tek işaret, üst
         banttaki bir anlık hata mesajıydı — o da kaybolunca ekran sonsuza kadar yükleniyor
         görünüyordu, üstelik ÇIKIŞ YOLU (tekrar deneme) yoktu.
         Panelin ortak "okunamadı" bileşeni ve `useSectionError` kalıbı devralındı. -->
    <SectionLoadError
      v-else-if="!project"
      :message="projectLoad.message.value"
      :busy="loading"
      @retry="load" />

    <template v-else>
      <div class="module-toolbar">
        <div class="segmented-tabs" role="tablist">
          <button role="tab" :aria-selected="tab === 'design'" :class="{ active: tab === 'design' }" @click="tab = 'design'">
            {{ t("panel.siteBuilder.tab.design") }}
          </button>
          <button role="tab" :aria-selected="tab === 'content'" :class="{ active: tab === 'content' }" @click="tab = 'content'">
            {{ t("panel.siteBuilder.tab.content") }}
          </button>
          <button role="tab" :aria-selected="tab === 'pages'" :class="{ active: tab === 'pages' }" @click="tab = 'pages'">
            {{ t("panel.siteBuilder.tab.pages") }}
          </button>
          <button role="tab" :aria-selected="tab === 'publish'" :class="{ active: tab === 'publish' }" @click="openPublishTab">
            {{ t("panel.siteBuilder.tab.publish") }}
          </button>
        </div>
        <!-- TEK BİRİNCİL EYLEM, SEKMEYE GÖRE.
             ÖNCESİ ÖLÇÜLDÜ: "Kaydet" araç çubuğunda İKİNCİL (gri) duruyordu, oysa tasarım/içerik/
             sayfalar sekmelerinde ekranın TEK gerçek eylemi oydu — o üç sekmede hiçbir birincil
             düğme yoktu ve göz tutunacak yer bulamıyordu. Buna karşılık aynı ekranda 15 kadar
             SEÇİM ÇİPİ birincil mavi çiziliyordu (bkz. `.sb-choice`), yani vurgu tam ters yerdeydi.
             Yayın sekmesinde ise sayfanın cevabı "Yayınla"dır; iki birincil düğmeyi aynı anda
             göstermemek için "Kaydet" orada ikincile düşer. Aynı kalıp Güvenlik ekranında da var
             (form açıkken "Yeni kural" ikincile düşer). -->
        <div class="sb-actions">
          <button type="button" :class="tab === 'publish' ? 'secondary' : ''" :disabled="saving" @click="save">
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

            <!--
              SEÇİM ÇİPLERİ — EKRANIN EN BÜYÜK GÖRSEL HATASIYDI.
              `.sb-choice` sınıf olarak vardı ama düğmeye hiçbir AĞIRLIK vermiyordu; base.css'te
              sınıfsız her `button` BİRİNCİL mavi gradyandır. Sonuç ölçüldü: tasarım sekmesinde
              üç grup halinde ~15 adet birincil mavi düğme yan yana duruyordu ve panelin gerçek
              eylemi ("Kaydet") onların yanında GRİ kalıyordu.
              Üstelik SEÇİLİ olan çip yalnızca `outline: 2px solid var(--accent-1)` ile
              işaretleniyordu — mavi bir düğmenin üzerine mavi halka; ölçüldü, dolgu ile halka
              arasındaki kontrast 1.00:1'e yakın, yani hangisinin seçili olduğu GÖRÜNMÜYORDU.
              Şimdi çip nötr bir yüzey; seçili olan üç kanaldan birden konuşuyor: dolgu tonu,
              vurgu rengindeki kenar ve `aria-pressed` (ekran okuyucu ile klavye kullanıcısı için
              tek kanal renk olamaz).
            -->
            <h3>{{ t("panel.siteBuilder.templateLabel") }}</h3>
            <div class="sb-choices">
              <button
                v-for="item in catalog?.templates ?? []"
                :key="item.id"
                type="button"
                :aria-pressed="project.templateId === item.id"
                :class="['sb-choice', { active: project.templateId === item.id }]"
                @click="project.templateId = item.id">
                {{ t(item.labelKey) }}
              </button>
            </div>

            <h3>{{ t("panel.siteBuilder.paletteLabel") }}</h3>
            <div class="sb-choices">
              <button
                v-for="item in catalog?.palettes ?? []"
                :key="item.id"
                type="button"
                :aria-pressed="project.paletteId === item.id"
                :class="['sb-choice', { active: project.paletteId === item.id }]"
                @click="project.paletteId = item.id">
                <span class="sb-swatch" :style="{ background: item.accent, borderColor: item.text }"></span>
                {{ t(item.labelKey) }}
              </button>
            </div>

            <h3>{{ t("panel.siteBuilder.fontLabel") }}</h3>
            <div class="sb-choices">
              <button
                v-for="item in catalog?.fonts ?? []"
                :key="item.id"
                type="button"
                :aria-pressed="project.fontId === item.id"
                :class="['sb-choice', { active: project.fontId === item.id }]"
                @click="project.fontId = item.id">
                {{ t(item.labelKey) }}
              </button>
            </div>
          </div>

          <!-- ---------------------------------------------------------- İÇERİK -->
          <div v-else-if="tab === 'content'" class="form-surface">
            <!--
              HANGİ SAYFAYI düzenlediğimiz her zaman görünür. Sayfa seçici olmasaydı kullanıcı
              ana sayfayı düzenlediğini sanıp bir alt sayfaya yazardı; yazdığı metnin nereye
              gittiğini ancak yayından sonra anlardı.
            -->
            <label v-if="project.pages.length" class="stacked-field">
              <span class="field-label">{{ t("panel.siteBuilder.editingPage") }}</span>
              <select v-model="activePageSlug">
                <option value="">{{ t("panel.siteBuilder.homePage") }}</option>
                <option v-for="page in project.pages" :key="page.slug" :value="page.slug">
                  {{ page.title || page.slug }} (/{{ page.slug }}/)
                </option>
              </select>
            </label>

            <template v-if="activePage">
              <label class="stacked-field">
                <span class="field-label">{{ t("panel.siteBuilder.pageTitle") }}</span>
                <input v-model="activePage.title" maxlength="120" />
              </label>
              <label class="stacked-field">
                <span class="field-label">{{ t("panel.siteBuilder.metaDescription") }}</span>
                <textarea v-model="activePage.metaDescription" rows="2" maxlength="300"></textarea>
              </label>
            </template>

            <!--
              KAPALI BÖLÜM KAPALI AÇILIR — bilgi GİZLENMEZ, sırası düzelir.
              ÖLÇÜLDÜ: beş bölümün BEŞİ DE `open` ile açık başlıyordu (başlık + gövde + görsel +
              iletişim alanları + öğe listesi). Yeni bir sayfada yalnız "hakkında" etkin gelir
              (bkz. `addPage`), yani kullanıcı dört adet KAPALI bölümün boş alanlarını kaydırarak
              geçmek zorundaydı; sayfanın gerçekten yayınlanacak tek bölümü bu yığının içinde
              kayboluyordu.
              `<details>` YERİNDE DURUYOR ve içerik SİLİNMİYOR: kapalı bölüm tek satır kalır,
              rozeti "Kapalı" yazar, tek tıkla açılır, klavyeyle gezilir ve tarayıcının sayfa içi
              araması kapalıyken de bulup açar. Panonun referans bloğunda alınan kararın aynısı.
              Bağlama TEK YÖNLÜ: kullanıcı bir bölümü elle açtıysa, `enabled` değişmediği sürece
              Vue o durumu geri almaz.
            -->
            <details v-for="section in activeSections" :key="section.type" class="sb-section" :open="section.enabled">
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
                  <!--
                    İLETİŞİM FORMU. Kutu işaretli olsa bile form ancak GERÇEKTEN çalışacaksa
                    basılır: alıcı adresi geçerli olmalı ve panelin genel adresi tanımlı olmalı.
                    Eksik koşul yayın sekmesinde ADIYLA yazılır — "belki çalışır" diye bir hâl
                    bırakılmadı, çünkü gönderilemeyen bir form ziyaretçinin gönderdiğini sanması
                    demektir ve bu hiç form koymamaktan kötüdür.
                  -->
                  <label class="sb-toggle">
                    <input v-model="section.contactFormEnabled" type="checkbox" />
                    <span>{{ t("panel.siteBuilder.contactFormEnabled") }}</span>
                  </label>
                  <p class="meta sb-note">{{ t("panel.siteBuilder.contactFormHint") }}</p>
                  <p v-if="section.contactFormEnabled" class="meta sb-note">
                    {{ t("panel.siteBuilder.contactFormSpamHint") }}
                  </p>
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

            <h3>{{ t("panel.siteBuilder.images") }}</h3>
            <p class="meta">{{ t("panel.siteBuilder.imagesHint", { size: maxImageMb }) }}</p>
            <input ref="fileInput" type="file" accept="image/png,image/jpeg,image/gif,image/webp" @change="uploadImage" />
            <!-- GÖRSEL SİLME TEHLİKE TONUNDA. Bu ekrandaki tek GERİ DÖNÜŞSÜZ düğme budur: dosya
                 sunucudan silinir (`removeImage` yorumu). Yanındaki bütün düğmeler ("Öğe ekle",
                 "Sayfayı kaldır", "Önizlemeyi yenile") taslak üzerinde çalışır ve kaydedilmeden
                 geri alınabilir; hepsi AYNI gri kutuydu. Galeride silme düğmeleri yan yana
                 dizildiği için yanlış kareye tek tık, yayındaki bir sayfanın görselini yok
                 etmeye yetiyordu. Onay diyaloğu AYNEN duruyor; ton, onaydan ÖNCEKİ kanaldır. -->
            <div v-if="images.length" class="sb-gallery">
              <figure v-for="image in images" :key="image.name">
                <img :src="imageUrl(image.name)" :alt="image.name" loading="lazy" />
                <button type="button" class="danger compact" @click="removeImage(image.name)">
                  {{ t("panel.siteBuilder.deleteImage") }}
                </button>
              </figure>
            </div>
          </div>

          <!-- ---------------------------------------------------------- SAYFALAR -->
          <div v-else-if="tab === 'pages'" class="form-surface">
            <p class="meta">{{ t("panel.siteBuilder.pagesHint") }}</p>

            <!--
              SİLME UYARISI HER ZAMAN GÖRÜNÜR, sayfa kaldırılırken de tekrar edilir. Sebep:
              kullanıcı "sayfayı kaldır" derken onu sitesinden de sildiğini SANIR. Yayın hiçbir
              dosyayı silmez; eski HTML adresinde durmaya devam eder ve yayın sekmesinde "öksüz
              dosya" olarak listelenir. Bunu söylememek, kullanıcıyı yanlış bir inançla bırakmak
              olurdu.
            -->
            <p class="meta sb-note">{{ t("panel.siteBuilder.pageRemovalNote") }}</p>

            <div v-for="(page, index) in project.pages" :key="index" class="sb-item">
              <label class="stacked-field">
                <span class="field-label">{{ t("panel.siteBuilder.pageTitle") }}</span>
                <input v-model="page.title" maxlength="120" @blur="fillSlugFromTitle(page)" />
              </label>
              <label class="stacked-field">
                <span class="field-label">{{ t("panel.siteBuilder.pageSlug") }}</span>
                <input v-model="page.slug" maxlength="48" />
              </label>
              <p v-if="slugProblem(page, index)" class="notice error" role="alert">
                {{ t(slugProblem(page, index)!) }}
              </p>
              <p v-else class="meta sb-note">{{ t("panel.siteBuilder.pageAddress", { slug: page.slug }) }}</p>

              <label class="sb-toggle">
                <input v-model="page.showInNav" type="checkbox" />
                <span>{{ t("panel.siteBuilder.pageShowInNav") }}</span>
              </label>

              <div class="sb-actions">
                <button type="button" class="secondary" @click="editPage(page.slug)">
                  {{ t("panel.siteBuilder.editPageContent") }}
                </button>
                <button type="button" class="secondary" @click="removePage(index)">
                  {{ t("panel.siteBuilder.removePage") }}
                </button>
              </div>
            </div>

            <button type="button" class="secondary" :disabled="project.pages.length >= maxPages" @click="addPage">
              {{ t("panel.siteBuilder.addPage") }}
            </button>
            <p v-if="project.pages.length >= maxPages" class="meta sb-note">
              {{ t("panel.siteBuilder.pageLimitReached", { count: maxPages }) }}
            </p>
          </div>

          <!-- ---------------------------------------------------------- YAYIN -->
          <div v-else class="form-surface">
            <!-- Proje yüklemesindeki AYNI hata: plan çekilemediğinde `plan` null kalıyor ve
                 sekme sonsuza kadar "Yükleniyor…" gösteriyordu. Burada bedeli daha yüksek:
                 kullanıcı yayın sekmesini tam da yayınlamak için açıyor. -->
            <p v-if="!plan && !planLoad.message.value" class="empty-state" role="status" aria-live="polite">
              {{ t("panel.common.loading") }}
            </p>
            <SectionLoadError
              v-else-if="!plan"
              :message="planLoad.message.value"
              @retry="openPublishTab" />
            <template v-else>
              <p class="meta">
                {{ t("panel.siteBuilder.publishTarget") }}: <code>{{ plan.targetPath || "-" }}</code>
              </p>

              <p v-if="!plan.canPublish" class="notice error" role="alert">
                {{ blockedReasonText(plan.blockedReasonKey) }}
              </p>

              <template v-else>
                <p class="meta">
                  {{ t("panel.siteBuilder.existingFiles", { count: plan.existingFileCount }) }}
                </p>

                <p class="meta">{{ t("panel.siteBuilder.publishPages", { count: plan.pageCount }) }}</p>

                <!--
                  FORM NEDEN BASILAMIYOR. Kullanıcı kutuyu işaretler, yayınlar ve sitesinde form
                  GÖRMEZ; sebebini söylemezsek elinde kalan tek bilgi "panel bozuk" olur. Bu
                  YAYINI ENGELLEMEZ (sayfa formsuz yayınlanır), o yüzden ayrı bir uyarı.
                -->
                <p v-if="plan.contactFormBlockedReasonKey" class="notice warn-banner" role="alert">
                  {{ blockedReasonText(plan.contactFormBlockedReasonKey) }}
                </p>

                <h3>{{ t("panel.siteBuilder.publishFiles") }}</h3>
                <ul class="sb-filelist">
                  <li v-for="file in plan.filesToWrite" :key="file"><code>{{ file }}</code></li>
                </ul>

                <!--
                  ÖKSÜZ DOSYALAR. Kullanıcı bir sayfayı kurucudan kaldırdığında eski HTML
                  adresinde durmaya devam eder. Yayın onu SİLMEZ — silme bu depoda müşteri
                  sitesini yok edebilecek hata sınıfı ve geri alınamaz. Ama sessiz kalmak da
                  kullanıcıyı "sildim" inancıyla bırakmak olurdu; dosya ADIYLA gösterilir, silme
                  kararı sahibinindir (Dosya Yöneticisi ekranı).
                -->
                <template v-if="plan.orphanFiles.length">
                  <h3>{{ t("panel.siteBuilder.orphanTitle") }}</h3>
                  <p class="meta">{{ t("panel.siteBuilder.orphanHint") }}</p>
                  <ul class="sb-filelist">
                    <li v-for="file in plan.orphanFiles" :key="file"><code>{{ file }}</code></li>
                  </ul>
                </template>

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
            <h3>{{ t("panel.siteBuilder.preview") }}</h3>
            <div class="sb-actions">
              <!-- Önizleme hangi SAYFAYI gösteriyor: çok sayfalı bir sitede bu soru sorulmadan
                   önizleme yanıltıcı olurdu. -->
              <select v-if="project.pages.length" v-model="previewSlug" class="compact-select">
                <option value="">{{ t("panel.siteBuilder.homePage") }}</option>
                <option v-for="page in project.pages" :key="page.slug" :value="page.slug">
                  {{ page.title || page.slug }}
                </option>
              </select>
              <button type="button" class="secondary" :disabled="previewLoading" @click="loadPreview">
                {{ t("panel.siteBuilder.refreshPreview") }}
              </button>
            </div>
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
import SectionLoadError from "../components/SectionLoadError.vue";
import { useNotice, useSectionError } from "../composables/useNotice";
import { useConfirm } from "../components/ConfirmDialog.vue";
import { formatDateTime } from "../utils/datetime";
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
  suggestSiteBuilderSlug,
  uploadSiteBuilderImage,
  SITE_BUILDER_MAX_PAGES,
  SITE_BUILDER_SLUG_PATTERN,
  type SiteBuilderCatalog,
  type SiteBuilderImage,
  type SiteBuilderPage,
  type SiteBuilderProject,
  type SiteBuilderPublishPlan
} from "../api/siteBuilder";

const { t } = useI18n();
const { confirm } = useConfirm();
const sitesStore = useSitesStore();

const catalog = ref<SiteBuilderCatalog | null>(null);
const project = ref<SiteBuilderProject | null>(null);
const images = ref<SiteBuilderImage[]>([]);
const plan = ref<SiteBuilderPublishPlan | null>(null);
const previewHtml = ref("");
const fileInput = ref<HTMLInputElement | null>(null);

const tab = ref<"design" | "content" | "pages" | "publish">("design");

/** İçerik sekmesinin düzenlediği sayfa. Boş dizge = ana sayfa. */
const activePageSlug = ref("");

/** Önizlemenin gösterdiği sayfa. İçerik sekmesinden AYRI: kullanıcı bir sayfayı düzenlerken
 *  başka bir sayfanın çıktısını kontrol etmek isteyebilir. */
const previewSlug = ref("");
const maxPages = SITE_BUILDER_MAX_PAGES;
const loading = ref(false);
const saving = ref(false);
const publishing = ref(false);
const previewLoading = ref(false);
const overwriteConfirmed = ref(false);
// Bildirim + hata ÇEVİRİSİ tek kaynaktan (composables/useNotice.ts). Elle yazılmış `report()`
// kopyası `t()` çağrısını atlıyordu ve kullanıcıya HAM ANAHTAR basıyordu.
const { notice, noticeError, showNotice, showError, clearNotice } = useNotice();
// "Yolda" ile "okunamadı" AYRI: ikisi de `project === null` durumundadır ve ekran ikisini de
// "Yükleniyor…" diye gösteriyordu. Aynı ayrım yayın planı için de gerekli (`planLoad`).
const projectLoad = useSectionError();
const planLoad = useSectionError();

const selectedSite = computed({
  get: () => sitesStore.selectedSiteName,
  set: (value: string) => sitesStore.selectSite(value)
});

const maxImageMb = computed(() =>
  catalog.value ? Math.round(catalog.value.maxImageBytes / (1024 * 1024)) : 5);

/** Düzenlenen sayfa nesnesi; ana sayfada `null`. */
const activePage = computed(() =>
  project.value?.pages.find((page) => page.slug === activePageSlug.value) ?? null);

/**
 * İçerik sekmesinin üzerinde çalıştığı bölüm listesi.
 *
 * Ana sayfa ile ek sayfalar AYNI bölüm modelini kullanıyor; bu yüzden düzenleyici işaretlemesi
 * tek kopya. İki ayrı düzenleyici yazılsaydı biri güncellenip diğeri geride kalırdı ve fark, en
 * kötü yerde (kullanıcının göremediği bir alanda) ortaya çıkardı.
 */
const activeSections = computed(() => activePage.value?.sections ?? project.value?.sections ?? []);

onMounted(async () => {
  await sitesStore.fetchSites().catch(() => undefined);
  try {
    catalog.value = await getSiteBuilderCatalog();
  } catch (error) {
    report(error);
  }
  await load();
});

watch(selectedSite, () => {
  clearNotice();
  void load();
});

// Önizleme sayfası değişince çerçeve YENİDEN yüklenir; aksi hâlde açılır listede başka bir sayfa
// seçili görünürken ekranda hâlâ eski sayfa dururdu.
watch(previewSlug, () => {
  void loadPreview();
});

async function load() {
  if (!selectedSite.value) {
    project.value = null;
    return;
  }

  loading.value = true;
  activePageSlug.value = "";
  previewSlug.value = "";
  try {
    project.value = await getSiteBuilderProject(selectedSite.value);
    images.value = await getSiteBuilderImages(selectedSite.value);
    projectLoad.clear();
    await loadPreview();
  } catch (error) {
    report(error);
    projectLoad.capture(error);
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
    const oncekiSayfaSayisi = project.value.pages.length;
    project.value = await saveSiteBuilderProject(selectedSite.value, project.value);

    // SUNUCU SAYFA DÜŞÜRDÜ MÜ? Geçersiz ya da yinelenen adresli sayfalar kaydedilirken düşer.
    // Sessiz kalsaydık kullanıcı "kaydettim" görüp sayfasının kaybolduğunu ancak yayında
    // fark ederdi.
    if (project.value.pages.length < oncekiSayfaSayisi) {
      showError(t("panel.siteBuilder.pagesDropped", {
        count: oncekiSayfaSayisi - project.value.pages.length
      }));
    } else {
      inform(t("panel.siteBuilder.savedNotice"));
    }

    // Düşen bir sayfa hâlâ seçili olabilir; seçimi geçerli bir sayfaya çek.
    if (activePageSlug.value && !project.value.pages.some((page) => page.slug === activePageSlug.value)) {
      activePageSlug.value = "";
    }

    if (previewSlug.value && !project.value.pages.some((page) => page.slug === previewSlug.value)) {
      previewSlug.value = "";
    }

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
    previewHtml.value = await getSiteBuilderPreview(selectedSite.value, previewSlug.value);
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
  planLoad.clear();
  if (!selectedSite.value) {
    return;
  }

  try {
    plan.value = await getSiteBuilderPublishPlan(selectedSite.value);
  } catch (error) {
    report(error);
    planLoad.capture(error);
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

/*
 * ONAY ZORUNLU: bu düğme sunucudaki dosyayı GERİ DÖNÜŞSÜZ siliyor ve tek tıkla çalışıyordu.
 * Resim listesinde silme düğmeleri yan yana; yanlış satıra tek tık, yayındaki bir sayfanın
 * görselini yok etmeye yetiyordu. Panelin ortak onay diyaloğu (57 ekran onu kullanıyor)
 * neyin kaybedileceğini de yazar.
 */
async function removeImage(name: string) {
  if (!selectedSite.value) {
    return;
  }

  const approved = await confirm({
    title: t("panel.siteBuilder.confirmDeleteImageTitle"),
    message: t("panel.siteBuilder.confirmDeleteImage", { name })
  });

  if (!approved) {
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

// ---------------------------------------------------------------- sayfalar

/**
 * Yeni sayfa. Bölümler kapalı başlar (yalnız "hakkında" açık).
 *
 * Hepsi açık başlasaydı yeni sayfa ana sayfanın kopyası gibi görünür; kullanıcı da farkı
 * anlamadan aynı içeriği ikinci bir adreste yayınlardı (arama motorları için de kötü).
 */
function addPage() {
  if (!project.value || project.value.pages.length >= maxPages) {
    return;
  }

  const tipler = catalog.value?.sectionTypes ?? ["hero", "about", "services", "gallery", "contact"];
  project.value.pages.push({
    slug: "",
    title: "",
    metaDescription: "",
    showInNav: true,
    sections: tipler.map((type) => ({
      type,
      enabled: type === "about",
      heading: "",
      body: "",
      imageName: "",
      items: [],
      email: "",
      phone: "",
      address: "",
      contactFormEnabled: false
    }))
  });
}

/**
 * SAYFAYI KALDIRMA. Onay diyaloğu, diskteki dosyanın SİLİNMEDİĞİNİ de söyler.
 *
 * Kullanıcı "kaldır" derken sayfayı sitesinden de sildiğini sanar; oysa yayın hiçbir dosyayı
 * silmez ve eski HTML adresinde durmaya devam eder. Bunu onay anında söylemek, yayın sekmesinde
 * "öksüz dosya" listesini görünce şaşırmasını engeller.
 */
async function removePage(index: number) {
  const page = project.value?.pages[index];
  if (!project.value || !page) {
    return;
  }

  const approved = await confirm({
    title: t("panel.siteBuilder.confirmRemovePageTitle"),
    message: t("panel.siteBuilder.confirmRemovePage", { name: page.title || page.slug || "-" })
  });

  if (!approved) {
    return;
  }

  project.value.pages.splice(index, 1);
  if (activePageSlug.value === page.slug) {
    activePageSlug.value = "";
  }

  if (previewSlug.value === page.slug) {
    previewSlug.value = "";
  }
}

function editPage(slug: string) {
  activePageSlug.value = slug;
  previewSlug.value = slug;
  tab.value = "content";
}

/** Başlıktan slug ÖNERİR — yalnız alan boşken. Doldurulmuş bir adresi ezmek, kullanıcının
 *  bilerek verdiği adresi sessizce değiştirmek olurdu. */
function fillSlugFromTitle(page: SiteBuilderPage) {
  if (!page.slug.trim()) {
    page.slug = suggestSiteBuilderSlug(page.title);
  }
}

/**
 * Sayfa adresinin sorunu (varsa) — çeviri anahtarı olarak.
 *
 * Kalıp SUNUCUDAKİYLE aynı. Arayüzde göstermek zorunlu, çünkü sunucu geçersiz sayfayı sessizce
 * DÜŞÜRÜR (düzeltmez); uyarı olmasaydı kullanıcı kaydeder, sayfası kaybolur ve sebebini hiç
 * öğrenemezdi.
 */
function slugProblem(page: SiteBuilderPage, index: number): string | null {
  const slug = page.slug.trim();
  if (!slug) {
    return "panel.siteBuilder.pageSlugRequired";
  }

  if (!SITE_BUILDER_SLUG_PATTERN.test(slug)) {
    return "panel.siteBuilder.pageSlugInvalid";
  }

  const cakisma = (project.value?.pages ?? [])
    .some((other, otherIndex) => otherIndex !== index && other.slug.trim().toLowerCase() === slug.toLowerCase());

  return cakisma ? "panel.siteBuilder.pageSlugDuplicate" : null;
}

/** Sunucudan gelen engel anahtarı ("sitebuilder.target_is_link") çeviriye çevrilir. */
function blockedKeySuffix(key: string) {
  return key.startsWith("sitebuilder.") ? key.slice("sitebuilder.".length) : key;
}

/**
 * Engel sebebinin OKUNUR metni.
 *
 * ÇEVİRİSİ YOKSA HAM ANAHTAR BASILMAZ. Şablon `t(\`sitebuilder.${suffix}\`)` yazıyordu; sunucu
 * yeni bir sebep eklediği anda (bu sözlükte 19 sebep var ve liste sunucuda büyüyor) ekranda
 * "sitebuilder.target_whatever" gibi bir kod belirirdi — üstelik tam da yayının ENGELLENDİĞİNİ
 * anlatan satırda, yani kullanıcının en çok açıklamaya ihtiyaç duyduğu yerde. Aynı korumanın
 * kalıbı bu depoda zaten var (`UsersView.kalintiMetni`, `AppsView.runtimeLabel`).
 * Boş anahtar da elenir: `t("sitebuilder.")` çağrısı da ham metin basardı.
 */
function blockedReasonText(key: string): string {
  const suffix = blockedKeySuffix(key ?? "");
  if (!suffix) {
    return t("panel.siteBuilder.publishBlockedUnknown");
  }

  const tamAnahtar = `sitebuilder.${suffix}`;
  const cevrilen = t(tamAnahtar);
  return cevrilen === tamAnahtar ? t("panel.siteBuilder.publishBlockedUnknown") : cevrilen;
}

/** Tarih bicimi panelin geri kalaniyla AYNI (bkz. utils/datetime.ts). */
function formatDate(value: string) {
  return formatDateTime(value);
}

function inform(message: string) {
  showNotice(message);
}

function report(error: unknown) {
  showError(error);
}
</script>

<style scoped>
.sb-view {
  display: grid;
  gap: var(--sp-16);
}

/* 18px ölçek DIŞIYDI (bkz. styles/base.css `--sp-*`); 16'ya taşındı ve böylece düzenleyici ile
   önizleme arasındaki boşluk, panelin kendi dikey ritmiyle (16) aynı sayı oldu.
   `align-items: start` KORUNDU ve kritik: bu iki sütun (düzenleyici / önizleme) çok farklı
   uzunlukta; `stretch` olsaydı kısa olan uzun olana gerilir ve panoda ölçülen "başlıkla içerik
   arasında yüzlerce piksel boşluk" hatası burada da çıkardı. */
.sb-layout {
  display: grid;
  gap: var(--sp-16);
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
  gap: var(--sp-8);
  position: sticky;
  top: var(--sp-12);
}

.sb-preview-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--sp-10);
  /* Uzun bir sayfa adı ile yanındaki "Yenile" düğmesi dar panelde birbirini eziyordu. */
  flex-wrap: wrap;
}

.sb-frame {
  width: 100%;
  height: min(70vh, 720px);
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-lg);
  /* MÜŞTERİNİN SİTESİNİN önizlendiği iframe tuvali. Panelin yüzeyi değil, yabancı içeriğin
     zemini: siteler beyaz varsayar. Panel temasına bağlanırsa koyu temada müşterinin kendi
     sayfası yanlış renkte görünür. BİLEREK sabit. */
  background: #ffffff;
}

.sb-choices {
  display: flex;
  flex-wrap: wrap;
  gap: var(--sp-8);
  margin-bottom: var(--sp-12);
}

/* SEÇİM ÇİPİ — birincil düğme DEĞİL, bir seçenek. Gerekçe ve ölçüm şablondaki yorumda.
   `button`un gradyanı, gölgesi ve hover'da 1px zıplaması burada sıfırlanıyor: bunlar
   "basılacak eylem" dilidir, oysa çip bir DURUM taşır. Kalıp panelin sekme şeridiyle
   (`.segmented-tabs button`, base.css) aynı: düz yüzey + seçilide vurgu. */
.sb-choice {
  display: inline-flex;
  align-items: center;
  gap: var(--sp-8);
  padding: var(--sp-6) var(--sp-12);
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-md);
  background: var(--surface-1);
  color: var(--text-strong);
  font-weight: 600;
  box-shadow: none;
}

.sb-choice:not(:disabled):hover {
  border-color: color-mix(in srgb, var(--accent-1) 45%, transparent);
  background: var(--surface-2);
  transform: none;
  filter: none;
  box-shadow: none;
}

/* SEÇİLİ ÇİP ÜÇ KANAL: dolgu tonu + vurgu kenarı + `aria-pressed` (şablonda).
   Eski `outline: 2px solid var(--accent-1)` KALDIRILDI çünkü `outline` odak halkasının
   kanalıdır (base.css `:focus-visible`); seçime de aynı çizgiyi vermek, klavyeyle gezen
   kullanıcıda "seçili" ile "odakta"yı ayırt edilemez yapıyordu.
   Metin `--accent-text` ile yazılıyor, `--accent-1` ile DEĞİL: ölçüldü, açık temada vurgunun
   %12'lik tonu üzerinde `--accent-1` 3.7:1 (AA eşiği 4.5), `--accent-text` 4.6:1. */
.sb-choice.active {
  border-color: var(--accent-1);
  background: color-mix(in srgb, var(--accent-1) 12%, transparent);
  color: var(--accent-text);
}

.sb-swatch {
  width: 14px;
  height: 14px;
  border-radius: var(--radius-sm);
  border: 1px solid;
  display: inline-block;
  /* Çip küçüldüğü için renk karesi ezilmemeli: flex çocuğu olarak daralabiliyordu. */
  flex: none;
}

.sb-section {
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-lg);
  padding: var(--sp-10) var(--sp-12);
  margin-bottom: var(--sp-12);
}

/* Özet satırı artık bir bölüm BAŞLIĞI gibi okunmalı: kapalıyken ekranda o bölümden geriye kalan
   tek şey odur. Rozetle arasına gerçek bir boşluk konuyor (öncesinde `status-pill` metne
   yapışıyordu, çünkü `summary` düz satır içi akış). */
/* `display` BİLEREK DEĞİŞTİRİLMEDİ (panonun katlanır referans bloğunda alınan kararın aynısı):
   `summary` varsayılan olarak `list-item`tır ve açılır üçgeni o değerden gelir. `flex`/`grid`
   yazmak işaretçiyi SESSİZCE yok eder — "burada katlanmış bir şey var" diyen tek görsel ipucu
   odur ve bölümler artık kapalı da başlayabildiği için o ipucu kritik.
   Boy `--fs-h4`e çıkarıldı: kapalı bir bölümden ekranda geriye kalan TEK şey bu satır, yani
   artık bir bölüm başlığı; eskiden gövde metniyle aynı boydaydı ve yalnız 600 kalınlıkla
   ayrılıyordu. */
.sb-section summary {
  cursor: pointer;
  font-size: var(--fs-h4);
  font-weight: 640;
}

/* Rozet başlığa YAPIŞIYORDU: `summary` satır içi akış, aradaki tek boşluk kaynak kodundaki
   satır sonuydu ve bazı sarmalarda o da kayboluyordu. */
.sb-section summary .status-pill {
  margin-left: var(--sp-8);
}

.sb-item {
  border: 1px dashed var(--panel-border);
  border-radius: var(--radius-lg);
  padding: var(--sp-10);
  margin-bottom: var(--sp-10);
}

.sb-toggle {
  display: flex;
  align-items: center;
  gap: var(--sp-8);
  margin: var(--sp-8) 0;
}

.sb-gallery {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(120px, 1fr));
  gap: var(--sp-10);
  margin-top: var(--sp-10);
}

.sb-gallery figure {
  margin: 0;
  display: grid;
  gap: var(--sp-6);
}

.sb-gallery img {
  width: 100%;
  height: 90px;
  object-fit: cover;
  border-radius: var(--radius-sm);
  border: 1px solid var(--panel-border);
}

.sb-filelist {
  margin: var(--sp-6) 0;
  padding-left: var(--sp-20);
  display: grid;
  gap: var(--sp-2);
}

/* Bu ekranda `<code>` yalnız DOSYA YOLU taşıyor (yayın hedefi, yazılacak dosyalar, öksüz
   dosyalar, çakışmalar) ve hiçbiri biçimlendirilmiyordu: gövde yazısıyla aynı değişken
   genişlikli fontta akıyorlardı. Ölçüldü, sorun somut: "iletisim/index.html" ile
   "iletisim-2/index.html" arasındaki fark göz taramasıyla yakalanamıyor, oysa yayın sekmesinin
   TEK işi kullanıcıya hangi dosyanın ezileceğini göstermek. `--font-mono` base.css'te tanımlı
   tek kaynak; ekran kendi yedek listesini uydurmuyor. */
code {
  font-family: var(--font-mono);
  font-size: var(--fs-xs);
  overflow-wrap: anywhere;
}

.sb-warn {
  display: grid;
  gap: var(--sp-6);
}

/* 0.8rem ölçek dışıydı; `--fs-xs` (0.82rem) en yakın adım (fark 0.27px). */
.sb-note {
  font-size: var(--fs-xs);
}

.sb-actions {
  display: flex;
  gap: var(--sp-8);
  /* Sayfa kartındaki iki düğme dar sütunda birbirini eziyordu. */
  flex-wrap: wrap;
}
</style>
