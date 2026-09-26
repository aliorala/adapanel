<template>
  <div class="app-shell">
    <a class="skip-link" href="#panel-main">{{ t("panel.layout.skipToContent") }}</a>
    <!--
      Dar ekran: kenar menü içeriğin üstüne binmesin diye dışarı kayan (off-canvas)
      çekmeceye döner. Karartma katmanı hem dış tıklamayla kapatır hem de arka plandaki
      düğmelerin yanlışlıkla basılmasını engeller.
    -->
    <div v-if="isNarrow && menuOpen" class="nav-scrim" @click="closeMenu"></div>

    <aside id="ap-sidebar" ref="sidebarRef" :inert="isNarrow && !menuOpen" :class="['sidebar', { open: isNarrow && menuOpen }]" @transitionend.self="focusOpenMenu">
      <!--
        ÖZEL MARKA: logo varsa ürünün "AP" işareti YERİNE geçer (yanına değil) — beyaz etikette
        sağlayıcının işareti müşterinin ekranında hiç görünmemeli. Logo adresi bir data URI'dir
        (bkz. api/panelSettings.ts) ve marka adı `{{ }}` ile, yani HTML olarak yorumlanmadan basılır.
        Bağlantı hedefi sunucuda http/https'e kısıtlanır; `rel` yine de yazılır çünkü hedef sayfa
        bayinin belirlediği bir dış adrestir (sekme ele geçirmeye karşı).
      -->
      <component
        :is="logoLinkUrl ? 'a' : 'div'"
        class="brand-card"
        :href="logoLinkUrl || undefined"
        :target="logoLinkUrl ? '_blank' : undefined"
        :rel="logoLinkUrl ? 'noopener noreferrer' : undefined">
        <img v-if="logoUrl" class="brand-logo" :src="logoUrl" :alt="panelName" />
        <div v-else class="brand-mark">AP</div>
        <div class="brand-copy">
          <strong>{{ panelName }}</strong>
          <p>{{ subtitle }}</p>
        </div>
      </component>

      <button v-if="isNarrow" class="secondary compact nav-close" :aria-label="t('panel.attention.close')" @click="closeMenu">×</button>
      <nav ref="navListRef" class="nav-list" :aria-label="t('panel.layout.menu')">
        <!--
          GRUPLU MENÜ: `role="group"` + `aria-labelledby` başlığı maddelere BAĞLAR. Ekran okuyucu
          "Barındırma grubu, Posta" diye okur; düz listede 21 madde bağlamsız sıralanıyordu.
          Başlık `<h2>` çünkü üst bardaki sayfa başlığı `<h1>`: gezinme başlıkları onun altında
          kalmalı, aksi halde belge başlık ağacı iki köklü olurdu.
        -->
        <div
          v-for="group in navGroups"
          :key="group.id"
          class="nav-group"
          role="group"
          :aria-labelledby="`nav-group-${group.id}`">
          <h2 :id="`nav-group-${group.id}`" :class="['nav-group-title', { 'sr-only': group.hideTitle }]">
            {{ group.label }}
          </h2>

          <!--
            `aria-current="page"`: hangi bölümde olduğumuz GÖRSEL olarak yalnız `.active` sınıfının
            rengiyle söyleniyordu. Ekran okuyucu 40 menü maddesini birbirinin AYNI okuyordu; kullanıcı
            nerede olduğunu ancak sayfa içeriğini dinleyerek tahmin edebiliyordu.
            `aria-current` bunun standart karşılığıdır ve ekran okuyucu "geçerli sayfa" diye duyurur.
          -->
          <div class="nav-group-items">
            <button
              v-for="section in group.sections"
              :key="section.id"
              :class="['nav-item', { active: section.id === activeSection }]"
              :aria-current="section.id === activeSection ? 'page' : undefined"
              @click="handleNavigate(section.id)">
              <span class="nav-glyph">
                <svg class="nav-icon" viewBox="0 0 24 24" aria-hidden="true" v-html="iconFor(section.id)"></svg>
              </span>
              <span class="nav-copy">
                <strong>{{ section.label }}</strong>
              </span>
              <span v-if="section.badge" class="nav-badge" :title="section.badgeTitle">{{ section.badge }}</span>
            </button>
          </div>
        </div>
      </nav>

      <div class="sidebar-foot">
        <div class="mini-card">
          <span class="mini-label">{{ t("panel.layout.server") }}</span>
          <strong>{{ machineName }}</strong>
        </div>
        <div class="mini-card">
          <span class="mini-label">{{ t("panel.layout.securePath") }}</span>
          <strong>/{{ secureEntryPath || "-" }}</strong>
        </div>
      </div>
    </aside>

    <div class="main-column" :inert="isNarrow && menuOpen">
      <header class="topbar">
        <!-- Menü düğmesi yalnız dar ekranda: geniş ekranda kenar menü zaten görünür. -->
        <button
          v-if="isNarrow"
          ref="menuButtonRef"
          class="secondary compact icon-btn nav-toggle"
          :title="t('panel.layout.menu')"
          :aria-label="t('panel.layout.menu')"
          :aria-expanded="menuOpen"
          aria-controls="ap-sidebar"
          @click="toggleMenu">
          <svg viewBox="0 0 24 24" class="nav-icon" aria-hidden="true">
            <path d="M4 7h16M4 12h16M4 17h16" />
          </svg>
        </button>

        <div class="topbar-copy">
          <h1>
            <span class="status-dot" :title="t('panel.layout.online')"></span>
            {{ title }}
          </h1>
        </div>

        <div class="topbar-search">
          <input
            v-model="searchQuery"
            class="search-input"
            :aria-label="t('panel.layout.searchPlaceholder')"
            type="search"
            :placeholder="t('panel.layout.searchPlaceholder')"
            @focus="warmEntityIndex"
            @keydown.esc="searchQuery = ''"
            @keydown.enter="goToFirstResult" />
          <div v-if="searchQuery.trim()" class="search-results">
            <button
              v-for="result in searchResults"
              :key="result.key"
              class="search-result"
              @click="selectSearch(result)">
              <span class="nav-glyph">
                <svg class="nav-icon" viewBox="0 0 24 24" aria-hidden="true" v-html="iconFor(result.sectionId)"></svg>
              </span>
              <span class="search-result-copy">
                <strong>{{ result.label }}</strong>
                <small>{{ result.hint }}</small>
              </span>
              <span class="search-kind">{{ kindLabel(result.kind) }}</span>
            </button>

            <p v-if="searchResults.length === 0" class="search-note" role="status">
              {{ entityIndexLoading ? t("panel.common.loading") : t("panel.layout.searchNoResults") }}
            </p>

            <!-- Varlık dizini kurulamadıysa SESSİZ KALMAZ: aksi halde kullanıcı var olan bir
                 veritabanını "yok" sanırdı. -->
            <p v-if="entityIndexError" class="search-note error" role="alert">{{ entityIndexError }}</p>
          </div>
        </div>

        <div class="topbar-actions">
          <!--
            Dikkat düğmesi: panelin bulduğu kritik+uyarı sayısı rozette. Bulguyu kullanıcının
            önüne getirir; "doğru sekmeye gidersen görürsün" tasarımını bitirir.
          -->
          <button
            v-if="role === 'Administrator'"
            class="secondary compact icon-btn attention-btn"
            :title="t('panel.attention.open')"
            :aria-label="t('panel.attention.open')"
            :aria-expanded="attentionOpen"
            @click="$emit('attention-toggle')">
            <svg viewBox="0 0 24 24" class="nav-icon" aria-hidden="true">
              <path d="M12 3.5 21 20H3z" />
              <path d="M12 10v4M12 17h.01" />
            </svg>
            <span v-if="attentionAlertCount" class="attention-badge" :class="{ critical: (attentionSummary?.criticalCount ?? 0) > 0 }">
              {{ attentionAlertCount > 9 ? "9+" : attentionAlertCount }}
            </span>
          </button>

          <div v-if="role === 'Administrator'" ref="bellWrapRef" class="bell-wrap">
            <button
              class="secondary compact icon-btn bell-btn"
              :title="t('panel.layout.notifications')"
              :aria-label="t('panel.layout.notifications')"
              aria-haspopup="true"
              :aria-expanded="showNotifications"
              @click="zilKutusunuAcKapa">
              <svg viewBox="0 0 24 24" class="nav-icon" aria-hidden="true">
                <path d="M6 9a6 6 0 0 1 12 0c0 5 1.5 6 1.5 6H4.5S6 14 6 9zM10 19a2 2 0 0 0 4 0" />
              </svg>
              <span v-if="recentActivity.length" class="bell-badge">{{ recentActivity.length > 9 ? "9+" : recentActivity.length }}</span>
            </button>
            <div v-if="showNotifications" class="bell-menu">
              <div class="bell-head">{{ t("panel.layout.recentActivity") }}</div>
              <!--
                ÖNEM DÜZEYİ RENKTEN İBARET DEĞİL: sol şerit görsel ipucu, yanındaki rozet ise
                düzeyin ADIDIR ("Hata" / "Dikkat" / "Bilgi"). Renk ayırt edemeyen ya da rozeti
                küçük bulan kullanıcı da başarısız bir kurulumu başarılı bir girişten ayırabilir.
              -->
              <div v-for="item in recentActivity" :key="item.id" :class="['bell-item', activityTone(item.action)]">
                <span class="bell-item-title">
                  <!-- Panelin ORTAK durum rozeti (`.status-pill`, base.css) kullanılıyor; zil
                       kutusuna ikinci bir rozet üslubu icat edilmedi. -->
                  <span class="status-pill" :class="tonePillClass(item.action)">{{ toneLabel(activityTone(item.action)) }}</span>
                  <strong>{{ activityLabel(item.action) }}</strong>
                </span>
                <small>{{ item.userName }} · {{ shortTime(item.createdAtUtc) }}</small>
              </div>
              <p v-if="recentActivity.length === 0" class="bell-empty">{{ t("panel.layout.noActivity") }}</p>
            </div>
          </div>
          <span class="user-chip" :title="`${userEmail} · v${panelVersion} · ${t('panel.layout.twoFactor')}: ${twoFactorLabel}`">
            <strong>{{ userName }}</strong>
            <small>{{ t(`panel.role.${role.toLowerCase()}`, role) }}</small>
          </span>
          <button
            class="secondary compact theme-toggle"
            :title="t('panel.layout.toggleTheme')"
            :aria-label="t('panel.layout.toggleTheme')"
            @click="$emit('toggle-theme')">
            <svg v-if="resolvedTheme === 'light'" viewBox="0 0 24 24" aria-hidden="true">
              <path d="M20.2 15.2A8.2 8.2 0 0 1 8.8 3.8 8.7 8.7 0 1 0 20.2 15.2Z" />
            </svg>
            <svg v-else viewBox="0 0 24 24" aria-hidden="true">
              <circle cx="12" cy="12" r="4" />
              <path d="M12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4" />
            </svg>
          </button>
          <button class="secondary compact icon-btn" :title="t('panel.dashboard.refresh')" :aria-label="t('panel.dashboard.refresh')" @click="$emit('refresh')">↻</button>
          <button class="secondary compact" @click="$emit('logout')">{{ t("panel.auth.logout") }}</button>
        </div>
      </header>

      <main id="panel-main" tabindex="-1" class="content-column">
        <div v-if="authStore.session?.isImpersonating" class="impersonation-banner">
          <span>{{ t("panel.users.impersonatingAs", { name: authStore.session?.user?.userName }) }}</span>
          <button class="compact" :disabled="returningToAdmin" @click="returnToAdmin">{{ t("panel.users.returnToAdmin") }}</button>
          <!-- Hata BURADA görünür. Önceden sessizce yutuluyordu ve "Yöneticiye dön" başarısız
               olunca kullanıcının elinde çıkış yolu kalmıyordu: bant duruyor, sayfa yenilenmiyor,
               sebep hiç öğrenilemiyor. -->
          <span v-if="returnError" class="impersonation-error" role="alert">{{ returnError }}</span>
        </div>
        <slot name="banner" />
        <slot />
      </main>
    </div>

    <AttentionDrawer
      :open="attentionOpen === true"
      :summary="attentionSummary ?? null"
      :loading="attentionLoading === true"
      :load-error="attentionError"
      :allowed-sections="sections.map((section) => section.id)"
      @close="$emit('attention-close')"
      @refresh="$emit('attention-refresh')"
      @navigate="$emit('navigate', $event)" />
  </div>
</template>

<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from "vue";
import { useI18n } from "vue-i18n";
import AttentionDrawer from "../components/AttentionDrawer.vue";
import type { AttentionSummary } from "../api/attention";
import { getMailWorkspace } from "../api/mail";
import { getMyDatabases } from "../api/myDatabases";
import { extractApiDetails, extractApiMessage, useAuthStore } from "../stores/auth";
import { useDatabasesStore } from "../stores/databases";
import { useSitesStore } from "../stores/sites";

const authStore = useAuthStore();
const sitesStore = useSitesStore();
const databasesStore = useDatabasesStore();

// profesyonel panel benzeri stroke ikonlar (bölüm id → SVG iç içeriği). 24x24, currentColor stroke.
const NAV_ICONS: Record<string, string> = {
  home: '<path d="M3 11l9-7 9 7M5 10v9h5v-5h4v5h5v-9"/>',
  sites: '<circle cx="12" cy="12" r="8.5"/><path d="M3.5 12h17M12 3.5c2.5 2.3 2.5 14.7 0 17M12 3.5c-2.5 2.3-2.5 14.7 0 17"/>',
  mail: '<rect x="3" y="5" width="18" height="14" rx="2"/><path d="M3.5 6.5 12 12.5l8.5-6"/>',
  files: '<path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"/>',
  database: '<ellipse cx="12" cy="6" rx="7" ry="3"/><path d="M5 6v6c0 1.7 3.1 3 7 3s7-1.3 7-3V6M5 12v6c0 1.7 3.1 3 7 3s7-1.3 7-3v-6"/>',
  monitor: '<path d="M3 12h4l2.5-7 4 14 2.5-7H21"/>',
  software: '<path d="M3 7l9-4 9 4-9 4-9-4zM3 7v10l9 4 9-4V7M12 11v10"/>',
  tasks: '<circle cx="12" cy="12" r="8.5"/><path d="M12 7v5l3.5 2"/>',
  security: '<path d="M12 3l7 3v5c0 4.5-3 8-7 10-4-2-7-5.5-7-10V6z"/>',
  tools: '<path d="M14.5 6a3.5 3.5 0 0 0 4.6 4.3L21 12l-9 9-3-3 9-9-1.7-1.9A3.5 3.5 0 0 0 14.5 6z"/><path d="M7 14l-4 4 3 3 4-4"/>',
  settings: '<circle cx="12" cy="12" r="3"/><path d="M19.4 13.5a7.6 7.6 0 0 0 0-3l2-1.5-2-3.4-2.3 1a7.6 7.6 0 0 0-2.6-1.5L14 .9h-4l-.5 2.7a7.6 7.6 0 0 0-2.6 1.5l-2.3-1-2 3.4 2 1.5a7.6 7.6 0 0 0 0 3l-2 1.5 2 3.4 2.3-1a7.6 7.6 0 0 0 2.6 1.5l.5 2.7h4l.5-2.7a7.6 7.6 0 0 0 2.6-1.5l2.3 1 2-3.4z"/>',
  backup: '<path d="M21 12a9 9 0 1 1-3-6.7M21 4v5h-5"/>',
  exit: '<path d="M15 4h3a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2h-3M10 8l-4 4 4 4M6 12h12"/>',
  migration: '<path d="M4 7h12M12 3l4 4-4 4"/><path d="M20 17H8M12 21l-4-4 4-4"/>',
  /*
   * AŞAĞIDAKİ 10 İKON SONRADAN EKLENDİ — ÖNCESİNDE HEPSİ AYNI BOŞ DAİREYDİ.
   *
   * ÖLÇÜLDÜ (canlı panel 0.7.9, yönetici menüsü): 21 maddenin 9'u `iconFor` yedeğine düşüyordu —
   * Müşteriler & Bayiler, Site kurucu, SEO araçları, Danışman, Kayıtlar, Faturalama, Sunucular,
   * Yapay Zeka, Hesap. Ekranda dokuz tane BİRBİRİNİN AYNI `<circle r="7"/>` çiziliyordu; müşteri
   * menüsündeki "Kullanıcılar" (subusers) ile birlikte sayı 10. İkon o maddelerde bilgi taşımıyor,
   * yalnızca yer kaplıyordu: göz taraması ölüyor, kullanıcı her seferinde ETİKETİ OKUMAK zorunda
   * kalıyordu. Bir bölümün üç ayrı "insan" ikonu olması (accounts/subusers/account) da bilinçli
   * bir ayrımdır: kalabalık = müşteriler, kişi+artı = abonelik altı kullanıcılar, tek kişi = kendi
   * hesabım. Yedek (`iconFor`) DURUYOR ama artık hiçbir bölüm ona düşmüyor; bunu
   * PanelNavigationIconTests sabitliyor.
   */
  accounts: '<circle cx="9" cy="8.5" r="3.2"/><path d="M2.5 19.5c0-3.4 2.9-5.2 6.5-5.2s6.5 1.8 6.5 5.2"/><path d="M16.2 5.9a3.2 3.2 0 0 1 0 5.2M18 14.6c2.1.7 3.5 2.4 3.5 4.9"/>',
  subusers: '<circle cx="9.5" cy="8.5" r="3.2"/><path d="M3 19.5c0-3.4 2.9-5.2 6.5-5.2 1.1 0 2.1.2 3 .5"/><path d="M18 13.5v7M14.5 17h7"/>',
  account: '<circle cx="12" cy="8" r="3.6"/><path d="M5 20c0-3.7 3.1-5.6 7-5.6s7 1.9 7 5.6"/>',
  sitebuilder: '<rect x="3" y="4" width="18" height="16" rx="2"/><path d="M3 9h18M9.5 9v11"/><path d="M12.5 12.5h5.5M12.5 16h3.5"/>',
  seo: '<circle cx="10.5" cy="10.5" r="6.5"/><path d="M15.4 15.4 20.5 20.5"/><path d="M7.6 12.2l2-2.6 2 1.8 2.3-3"/>',
  advisor: '<path d="M12 3a6 6 0 0 0-3.6 10.8c.7.5 1.1 1.3 1.1 2.2h5c0-.9.4-1.7 1.1-2.2A6 6 0 0 0 12 3z"/><path d="M9.5 19h5M10.5 21.5h3"/>',
  audit: '<path d="M6 3h8l5 5v12a1 1 0 0 1-1 1H6a1 1 0 0 1-1-1V4a1 1 0 0 1 1-1z"/><path d="M14 3v5h5"/><path d="M8.5 13h7M8.5 16.5h4.5"/>',
  billing: '<rect x="2.5" y="6" width="19" height="12" rx="2"/><circle cx="12" cy="12" r="2.6"/><path d="M6 12h.01M18 12h.01"/>',
  cluster: '<rect x="3" y="4" width="18" height="6.5" rx="1.6"/><rect x="3" y="13.5" width="18" height="6.5" rx="1.6"/><path d="M6.5 7.2h.01M6.5 16.7h.01M10 7.2h5M10 16.7h5"/>',
  /*
   * ÖLÇÜLDÜ, İLK DENEME GERİ ALINDI: yapay zeka için önce bir yonga (çevresi bacaklı kare)
   * çizildi. 17px'te — menüdeki gerçek boy — yongayı AYARLAR'ın dişli çarkından ayırmak
   * mümkün olmadı: ikisi de "ortada gövde + çevreye dağılan kısa çizgiler". İki bölüm menüde
   * KOMŞU (Hesap ve ayarlar grubu), yani en kötü yerde çakışıyordu. Işıltı (sparkle) evrensel
   * yapay zeka işaretidir ve bu ikon setinde başka hiçbir şeye benzemez.
   */
  ai: '<path d="M11.5 3l1.7 4.3 4.3 1.7-4.3 1.7L11.5 15 9.8 10.7 5.5 9l4.3-1.7z"/><path d="M17.5 14.5l.9 2.1 2.1.9-2.1.9-.9 2.1-.9-2.1-2.1-.9 2.1-.9z"/>'
};

function iconFor(id: string): string {
  return NAV_ICONS[id] ?? '<circle cx="12" cy="12" r="7"/>';
}

/*
 * MENÜ GRUPLARI — 22 maddelik DÜZ liste yerine "işi ne zaman yaparsın" kümeleri.
 *
 * ÖLÇÜLEN SORUN: yönetici menüsü tek bir düz listede 21 madde çiziyordu, hepsi aynı görsel
 * ağırlıkta ve 900px'lik bir ekranda listenin son üç maddesi (Ayarlar, Çıkış, Hesap) ancak
 * kaydırarak görülüyordu. Göz için hiçbir durak yok: aranan madde ancak baştan sona okunarak
 * bulunuyor.
 *
 * ÖLÇÜT ALFABE YA DA KOD YAPISI DEĞİL, KULLANIM ANI:
 *  - hosting  : site yayına alınırken/işletilirken dokunulanlar (gün içinde birden çok kez).
 *               "backup" bilerek "files"in yanında: kiracı menüsüne onu ekleyen kod da
 *               (injectBackupSection) Dosyalar'ın hemen ardına koyuyor, cPanel de yedeği
 *               Dosyalar altında tutar. Komşuluğu bozmak öğrenilmiş yeri bozardı.
 *  - business : para ve hesap işleri — ayda birkaç kez, farklı bir kafayla açılır.
 *  - insight  : "durum ne?" soruları + tekrar eden bakım (istatistik, danışman, SEO, kayıtlar,
 *               planlı işlemler). Planlı işlemler buraya kondu çünkü kurulup SONUCU
 *               kayıtlardan izlenen bir bakım işidir; sunucu araçlarına konsaydı müşteri
 *               menüsünde köksüz kalırdı.
 *  - server   : makinenin kendisine dokunan, seyrek ve riskli işler.
 *  - personal : oturumun kendi ayarları; her rolde en altta, çıkış en sonda.
 *
 * HİÇBİR MADDE ÇIKARILMADI: kalabalığın çözümü özelliği gizlemek değil, sıralamak. Bir kimlik
 * bu haritada yoksa "other" grubuna düşer (aşağıya bak) — yani yeni bir bölüm eklendiğinde
 * menüden KAYBOLMAZ, yalnızca yanlış yerde durur ve testi kızartır.
 */
const NAV_GROUPS: { id: string; labelKey: string; hideTitle?: boolean; ids: string[] }[] = [
  // Tek maddelik "Genel" grubunun başlığı EKRANDA yok ama ekran okuyucuda VAR: bir satır için
  // 20px başlık harcamak yersiz, gruba adsız bırakmak ise `aria-labelledby`yi boşa düşürürdü.
  { id: "overview", labelKey: "panel.navigation.group.overview", hideTitle: true, ids: ["home"] },
  { id: "hosting", labelKey: "panel.navigation.group.hosting", ids: ["sites", "sitebuilder", "mail", "files", "backup", "database"] },
  { id: "business", labelKey: "panel.navigation.group.business", ids: ["accounts", "subusers", "billing"] },
  { id: "insight", labelKey: "panel.navigation.group.insight", ids: ["monitor", "advisor", "seo", "audit", "tasks"] },
  { id: "server", labelKey: "panel.navigation.group.server", ids: ["software", "security", "tools", "migration", "cluster"] },
  { id: "personal", labelKey: "panel.navigation.group.personal", ids: ["ai", "account", "settings", "exit"] }
];

/**
 * Rol süzgecinden geçmiş bölümleri gruplara dağıtır.
 *
 * KAYIP MADDE İMKÂNSIZ: dağıtım sonunda hangi bölüm bir gruba girmediyse "Diğer" grubuna
 * eklenir. Grup haritasını güncellemeyi unutmak maddeyi menüden SİLMEZ; en fazla yanlış yere
 * koyar. (Eski tasarımda böyle bir risk yoktu çünkü grup da yoktu — bu yüzden bedelini
 * ödemeden gruplamanın tek yolu bu yedek gruptur.)
 */
const navGroups = computed(() => {
  const kalan = new Map(props.sections.map((section) => [section.id, section]));
  const gruplar: { id: string; label: string; hideTitle: boolean; sections: SectionItem[] }[] = [];

  for (const grup of NAV_GROUPS) {
    const uyeler = grup.ids
      .map((id) => kalan.get(id))
      .filter((section): section is SectionItem => section !== undefined);
    for (const uye of uyeler) {
      kalan.delete(uye.id);
    }

    if (uyeler.length > 0) {
      gruplar.push({ id: grup.id, label: t(grup.labelKey), hideTitle: grup.hideTitle === true, sections: uyeler });
    }
  }

  if (kalan.size > 0) {
    gruplar.push({
      id: "other",
      label: t("panel.navigation.group.other"),
      hideTitle: false,
      sections: [...kalan.values()]
    });
  }

  return gruplar;
});

/*
 * GLOBAL ARAMA — MENÜ ETİKETİ DEĞİL, GERÇEK VARLIKLAR.
 *
 * Önceden yalnız `props.sections` ada göre süzülüyordu: kullanıcı "example.com" yazınca hiçbir
 * şey bulamıyor, aradığı siteye menüden elle gitmek zorunda kalıyordu.
 *
 * Şimdi arama; bölüm + site + alan adı (bağlama host'ları) + veritabanı + posta alan adı
 * üzerinde çalışır ve seçim DOĞRU EKRANI SEÇİLİ VARLIKLA açar.
 *
 * HER TUŞTA AĞ İSTEĞİ YOK: siteler zaten pano tarafından yüklenmiş durumda; veritabanı ve posta
 * alan adları arama kutusuna İLK ODAKLANILDIĞINDA bir kez çekilir (oturum başına bir kez),
 * sonrası tamamen bellekten süzülür.
 *
 * POSTA KUTULARI ARAMAYA DAHİL DEĞİL: `GET /mail` yalnız TEK bir alan adının kutularını döndürür;
 * tüm kutuları veren bir uç yok. Alan adı başına istek atmak arama kutusunu ağ trafiğine
 * çevirirdi, bu yüzden bu varlık türü bilerek dışarıda bırakıldı.
 */
type SearchKind = "section" | "site" | "domain" | "database" | "mailDomain";
type SearchHit = {
  key: string;
  kind: SearchKind;
  label: string;
  hint: string;
  /** Sonuç seçilince gidilecek menü bölümü (ikon da bundan gelir). */
  sectionId: string;
  siteName?: string;
  engine?: string;
  domain?: string;
};

const searchQuery = ref("");
const entityIndexLoading = ref(false);
const entityIndexError = ref("");
const mailDomains = ref<string[]>([]);
const ownedDatabases = ref<{ engine: string; name: string }[]>([]);
let entityIndexRequested = false;

function kindLabel(kind: SearchKind): string {
  return t(`panel.layout.searchKind.${kind}`);
}

/** Bölüm menüde yoksa (rol yetkisi) o varlık türü aramaya girmez: 403'e yollamak yardım değildir. */
function sectionAllowed(sectionId: string) {
  return props.sections.some((section) => section.id === sectionId);
}

const siteHits = computed<SearchHit[]>(() => {
  if (!sectionAllowed("sites")) {
    return [];
  }
  const hits: SearchHit[] = [];
  const seenHosts = new Set<string>();
  for (const site of sitesStore.sites) {
    hits.push({
      key: `site:${site.name}`,
      kind: "site",
      label: site.name,
      hint: site.physicalPath,
      sectionId: "sites",
      siteName: site.name
    });
    for (const binding of site.bindings ?? []) {
      const host = (binding.hostName ?? "").trim();
      if (!host || host.toLowerCase() === site.name.toLowerCase() || seenHosts.has(host.toLowerCase())) {
        continue;
      }
      seenHosts.add(host.toLowerCase());
      hits.push({
        key: `domain:${host}`,
        kind: "domain",
        label: host,
        hint: site.name,
        sectionId: "sites",
        siteName: site.name
      });
    }
  }
  return hits;
});

const databaseHits = computed<SearchHit[]>(() => {
  if (!sectionAllowed("database")) {
    return [];
  }
  const hits: SearchHit[] = [];
  const seen = new Set<string>();
  const push = (engine: string, name: string, hint: string) => {
    const key = `db:${engine}:${name}`;
    if (seen.has(key)) {
      return;
    }
    seen.add(key);
    hits.push({ key, kind: "database", label: name, hint, sectionId: "database", engine });
  };

  for (const workspace of Object.values(databasesStore.workspaces)) {
    for (const database of workspace.databases) {
      push(workspace.engine, database.name, workspace.displayName || workspace.engine);
    }
  }
  for (const database of ownedDatabases.value) {
    push(database.engine, database.name, database.engine);
  }
  return hits;
});

const mailHits = computed<SearchHit[]>(() => {
  if (!sectionAllowed("mail")) {
    return [];
  }
  return mailDomains.value.map((domain) => ({
    key: `mail:${domain}`,
    kind: "mailDomain",
    label: domain,
    hint: t("panel.layout.searchKind.mailDomain"),
    sectionId: "mail",
    domain
  }));
});

const sectionHits = computed<SearchHit[]>(() =>
  props.sections
    .filter((section) => section.id !== "exit")
    .map((section) => ({
      key: `section:${section.id}`,
      kind: "section" as const,
      label: section.label,
      hint: section.description ?? "",
      sectionId: section.id
    })));

const searchResults = computed<SearchHit[]>(() => {
  const query = searchQuery.value.trim().toLocaleLowerCase();
  if (!query) {
    return [];
  }
  // Bölümler önce (bilinen hedef), sonra varlıklar.
  const all = [...sectionHits.value, ...siteHits.value, ...mailHits.value, ...databaseHits.value];
  return all
    .filter((hit) =>
      hit.label.toLocaleLowerCase().includes(query) || hit.hint.toLocaleLowerCase().includes(query))
    .slice(0, 12);
});

/**
 * Veritabanı + posta alan adı dizini: oturum başına BİR KEZ, arama kutusuna odaklanılınca.
 * Yeni bir uç istenmez; var olan uçlar kullanılır. Yetkisi olmayan rol için istek hiç atılmaz.
 */
async function warmEntityIndex() {
  if (entityIndexRequested) {
    return;
  }
  entityIndexRequested = true;
  entityIndexLoading.value = true;
  const failures: string[] = [];

  if (sitesStore.sites.length === 0 && !sitesStore.loading) {
    try {
      await sitesStore.fetchSites();
    } catch (error) {
      failures.push(resolveApiMessage(error));
    }
  }

  if (sectionAllowed("database")) {
    try {
      if (props.role === "Administrator") {
        const engines = await databasesStore.fetchEngines();
        for (const engine of engines.filter((item) => item.ready)) {
          await databasesStore.fetchWorkspace(engine.engine);
        }
      } else {
        ownedDatabases.value = await getMyDatabases();
      }
    } catch (error) {
      failures.push(resolveApiMessage(error));
    }
  }

  if (sectionAllowed("mail")) {
    try {
      const workspace = await getMailWorkspace();
      mailDomains.value = workspace.domains.map((domain) => domain.name);
    } catch (error) {
      failures.push(resolveApiMessage(error));
    }
  }

  entityIndexLoading.value = false;
  // Başarısızlık yutulmaz: eksik dizinle "bulunamadı" demek yalan olurdu.
  entityIndexError.value = failures.length > 0
    ? `${t("panel.layout.searchIndexFailed")} ${failures.join(" · ")}`
    : "";
}

function selectSearch(hit: SearchHit) {
  searchQuery.value = "";

  if (hit.kind === "site" || hit.kind === "domain") {
    emit("open-site", hit.siteName ?? hit.label);
    closeMenu();
    return;
  }

  if (hit.kind === "database" && hit.engine) {
    databasesStore.selectEngine(hit.engine);
    handleNavigate("database");
    return;
  }

  if (hit.kind === "mailDomain" && hit.domain) {
    emit("open-mail-domain", hit.domain);
    closeMenu();
    return;
  }

  handleNavigate(hit.sectionId);
}

// --- Dar ekran kabuğu: açılır kenar menü -----------------------------------
// 900px altında kenar menü sabit sütun olmaktan çıkıp çekmeceye döner (bkz. stiller).
// Eşiği JS tarafında da biliyoruz ki menü düğmesi geniş ekranda hiç basılamasın (odak sırasına
// girmesin) ve ekran genişleyince açık kalan çekmece kendiliğinden kapansın.
const NARROW_QUERY = "(max-width: 1200px)";
// İlk çizimde de doğru olmalı: aksi halde telefonda menü düğmesi bir kare gecikmeli belirir.
const isNarrow = ref(typeof window !== "undefined" && window.matchMedia(NARROW_QUERY).matches);
const menuOpen = ref(false);
const sidebarRef = ref<HTMLElement | null>(null);
/** Kaydırılan menü kutusu — seçili maddenin görünürlüğü BURADAN ölçülür (bkz. aşağıdaki işlev). */
const navListRef = ref<HTMLElement | null>(null);
const menuButtonRef = ref<HTMLElement | null>(null);
let narrowQuery: MediaQueryList | null = null;

function onNarrowChange(event: MediaQueryListEvent) {
  isNarrow.value = event.matches;
  if (!event.matches) {
    menuOpen.value = false;
  }
}

function toggleMenu() {
  menuOpen.value = !menuOpen.value;
}

function closeMenu() {
  menuOpen.value = false;
}

// Menüden bir bölüm seçilince çekmece kapanır: telefonda seçim sonrası ekranı kapatan
// menüyle baş başa kalınmaz.
function handleNavigate(sectionId: string) {
  closeMenu();
  emit("navigate", sectionId);
}

function onMenuKeydown(event: KeyboardEvent) {
  if (event.key === 'Tab' && menuOpen.value) {
    const items = [...(sidebarRef.value?.querySelectorAll<HTMLElement>('button:not(:disabled), a[href], input:not(:disabled), [tabindex="0"]') ?? [])].filter(item => item.getClientRects().length);
    const first = items[0], last = items[items.length - 1];
    if (first && last && (!sidebarRef.value?.contains(document.activeElement) || (event.shiftKey && document.activeElement === first) || (!event.shiftKey && document.activeElement === last))) {
      event.preventDefault();
      (event.shiftKey ? last : first).focus();
    }
  }
  if (event.key === "Escape" && menuOpen.value) {
    event.stopPropagation();
    closeMenu();
  }
}

function focusOpenMenu() {
  if (menuOpen.value && !sidebarRef.value?.contains(document.activeElement)) {
    sidebarRef.value?.querySelector<HTMLElement>(".nav-item")?.focus();
  }
}

// Arka plan kaydırma kilidi: çekmece/aşağıdan açılan yaprak açıkken sayfa kaymasın.
// Aynı anda birden fazla katman açılabildiği için sayaçla çalışır (AttentionDrawer aynı sayacı kullanır).
function lockBodyScroll(lock: boolean) {
  const root = document.documentElement;
  const next = Math.max(0, Number(root.dataset.apScrollLock ?? "0") + (lock ? 1 : -1));
  root.dataset.apScrollLock = String(next);
  root.classList.toggle("ap-scroll-lock", next > 0);
}

watch(menuOpen, async (open) => {
  lockBodyScroll(open);

  if (open) {
    document.addEventListener("keydown", onMenuKeydown, true);
    await nextTick();
    requestAnimationFrame(focusOpenMenu);
    return;
  }

  document.removeEventListener("keydown", onMenuKeydown, true);
  await nextTick();
  menuButtonRef.value?.focus?.();
});

/*
 * SEÇİLİ MADDE GÖRÜŞE KAYDIRILIR.
 *
 * ÖLÇÜLEN HATA (canlı 0.8.0, 1534x955, yönetici menüsü): kenar menü kaydırmalıdır
 * (`.nav-list` scrollHeight 1013px, görünen 789px) ve açılışta kaydırma konumu HER ZAMAN 0'dır.
 * Sayfa "Sunucular" bölümündeyken yenilendiğinde o maddenin 40px'lik yüksekliğinin yalnız
 * **16.2px**'i görünüyordu; altındaki dört madde (Yapay Zeka, Hesap, Ayarlar, Çıkış) hiç
 * görünmüyordu. Kesilen kenarın hemen altında "SUNUCU / GÜVENLİ YOL" kutusu durduğu için
 * bu, ekranda "seçili madde kutunun ALTINDA kalmış" gibi okunuyordu.
 * ÖLÇÜLDÜ ve DOĞRULANDI — kutu maddenin ÜSTÜNE BİNMİYOR: kenar menü üç satırlık bir ızgara
 * (`auto / 1fr / auto`), liste alt kenarı y=876.4, kutu üst kenarı y=886.4, yani aralarında
 * 10px boşluk var. Yani "alt boşluk ekle" yanlış teşhis olurdu: sorun ÖRTÜŞME değil,
 * listenin seçili maddeye hiç kaydırılmamasıydı.
 *
 * Çözüm kaydırma konumunu düzeltmek. `block: "nearest"` BİLEREK: madde zaten tamamen
 * görünüyorsa hiçbir şey yapmaz (menü her bölüm değişiminde zıplamaz), yalnız kırpıldığında
 * en az hareketle içeri alır. `scroll-margin-block` (stiller) maddeyi kenara YAPIŞTIRMAZ,
 * 12px nefes bırakır.
 */
async function seciliMaddeyiGoruseKaydir() {
  await nextTick();
  const list = navListRef.value;
  if (!list) {
    return;
  }

  const active = list.querySelector<HTMLElement>(".nav-item.active");
  if (!active) {
    return;
  }

  const listBox = list.getBoundingClientRect();
  const itemBox = active.getBoundingClientRect();
  // Tamamen görünüyorsa DOKUNMA: gereksiz kaydırma, kullanıcının menüde bulduğu yeri bozar.
  if (itemBox.top >= listBox.top && itemBox.bottom <= listBox.bottom) {
    return;
  }

  active.scrollIntoView({ block: "nearest", inline: "nearest" });
}

onMounted(() => {
  narrowQuery = window.matchMedia(NARROW_QUERY);
  isNarrow.value = narrowQuery.matches;
  narrowQuery.addEventListener("change", onNarrowChange);
  void seciliMaddeyiGoruseKaydir();
});

onBeforeUnmount(() => {
  narrowQuery?.removeEventListener("change", onNarrowChange);
  document.removeEventListener("keydown", onMenuKeydown, true);
  // Zil kutusu açıkken bileşen sökülürse belge dinleyicileri ARDA KALIRDI: sonraki her
  // tıklama artık var olmayan bir bileşenin işlevini çağırırdı.
  document.removeEventListener("pointerdown", zilDisiTiklama, true);
  document.removeEventListener("keydown", zilKlavye, true);
  if (menuOpen.value) {
    lockBodyScroll(false);
  }
});

function goToFirstResult() {
  if (searchResults.value.length > 0) {
    selectSearch(searchResults.value[0]);
  }
}

// Bildirim çanı: son yönetim aktiviteleri (audit log).
const showNotifications = ref(false);
const bellWrapRef = ref<HTMLElement | null>(null);
const recentActivity = computed(() => authStore.auditLogs.slice(0, 8));

/**
 * OLAY ADI ÇEVİRİDEN GELİR.
 *
 * ÖLÇÜLEN HATA (canlı 0.8.0, arayüz dili Türkçe): zil listesi "Software Install Failed",
 * "Update Install Scheduled", "Terminal Command Executed", "Auth Login Succeeded" yazıyordu.
 * Bunlar çeviri DEĞİLDİ — ham denetim kimliğinin (`software.install.failed`) noktaları boşluğa
 * çevrilip baş harfleri büyütülmüş hâliydi. Yani Türkçe panelde İngilizce, üstelik hiç kimsenin
 * yazmadığı, kod kimliğinden türetilmiş bir İngilizce görünüyordu.
 *
 * Anahtarlar SUNUCU KAYNAĞI TARANARAK çıkarıldı, elle tahmin edilmedi: `IAuditService`in iki
 * yazma metodunun (`WriteAsync` / `WriteForSubscriptionAsync`) ilk argümanındaki 168 kimliğin
 * HEPSİ `tr.json` + `en.json` içine yazıldı.
 *
 * YEDEK KALDIRILMADI: sunucuya yarın yeni bir eylem eklenirse anahtar bulunamaz ve eski
 * davranış (ham kimliği okunur hâle getirmek) devreye girer. Sessiz boşluk bırakmaktansa
 * çirkin ama DOĞRU bir metin göstermek yeğlenir — bilgi gizlenmez.
 */
function activityLabel(action: string): string {
  const key = `panel.activity.action.${action}`;
  return te(key) ? t(key) : action.replace(/[._]/g, " ").replace(/\b\w/g, (c) => c.toUpperCase());
}

/**
 * ÖNEM DÜZEYİ.
 *
 * ÖLÇÜLEN HATA: "Software Install Failed" ile "Auth Login Succeeded" listede BİREBİR aynı
 * çiziliyordu — aynı yazı boyu, aynı ağırlık, aynı renk, aynı ikon (hiç). Başarısız bir kurulum
 * ile başarılı bir giriş arasında hiçbir görsel fark yoktu.
 *
 * Düzey eylem kimliğinin SON PARÇASINDAN okunur; sunucu ayrı bir "severity" alanı göndermiyor
 * ve uydurmak yerine var olan veriden türetmek doğru. Tablolar 168 kimliğin son parçaları
 * sayılarak çıkarıldı:
 *  - hata  : `failed`, `rejected` — bir işin YAPILAMADIĞINI söyleyenler.
 *  - dikkat: bir şeyi kaldıran/kapatan/askıya alan/engelleyenler. Bunlar hata değildir (çoğu
 *            bilinçli yönetici işidir) ama listede gözden kaçmaması gerekenlerdir.
 *  - bilgi : geri kalan her şey (oluşturma, güncelleme, açma, tamamlama).
 * Bilinmeyen bir son parça `bilgi`ye düşer: yanlış alarm üretmez.
 */
const TONE_ERROR = new Set(["failed", "rejected"]);
const TONE_WARN = new Set([
  "blocked",
  "deactivated",
  "deleted",
  "disabled",
  "enforced",
  "purged",
  "removed",
  "rejected",
  "revoked",
  "revoked_on_reset",
  "site_leftover",
  "stopped",
  "suspended",
  "unpublished"
]);

type ActivityTone = "error" | "warn" | "info";

function activityTone(action: string): ActivityTone {
  const last = action.slice(action.lastIndexOf(".") + 1);
  if (TONE_ERROR.has(last)) {
    return "error";
  }

  return TONE_WARN.has(last) ? "warn" : "info";
}

function toneLabel(tone: ActivityTone): string {
  return t(`panel.activity.tone.${tone}`);
}

/** Ortak `.status-pill` sözlüğüne çeviri: "bilgi" düzeyi rozetin NÖTR hâlidir (ek sınıf yok). */
function tonePillClass(action: string): string {
  const tone = activityTone(action);
  if (tone === "error") {
    return "danger";
  }

  return tone === "warn" ? "warn" : "";
}

/*
 * ZİL KUTUSU KAPANIŞI.
 *
 * ÖLÇÜLEN HATA (canlı 0.8.0): kutu YALNIZCA zile ikinci kez basınca kapanıyordu. Dışarı
 * tıklamak kapatmıyordu, `Escape` kapatmıyordu ve BÖLÜM DEĞİŞTİRMEK de kapatmıyordu — Posta
 * ekranına geçildiğinde 280px'lik kutu hâlâ üstte durup içeriğin ortasını örtüyordu. Ayrıca
 * dikkat çekmecesi zil kutusu açıkken açılabiliyordu; iki katman üst üste biniyordu.
 *
 * Dört kapanış yolu da BURADA, tek yerde: dışarı tıklama, `Escape`, bölüm değişimi ve öteki
 * katmanın açılması. Dinleyiciler yalnız kutu AÇIKKEN bağlanır (kapalıyken sayfanın her
 * tıklamasını dinlemenin anlamı yok) ve bileşen sökülürken kesin olarak çözülür.
 *
 * `pointerdown` seçildi, `click` DEĞİL: `click` düğmenin kendi `@click`inden SONRA gelirdi ve
 * kutuyu aç-kapa-aç zincirine sokardı. Kapatma basma anında, açma ise düğmenin kendi olayında
 * olur; ikisi çakışmaz çünkü sarmalayıcının içindeki basmalar ayıklanıyor.
 */
function zilKutusunuAcKapa() {
  const acilacak = !showNotifications.value;
  showNotifications.value = acilacak;

  // İKİ KATMANDAN BİRİ AÇILINCA ÖTEKİ KAPANIR: çekmecenin durumunu üst bileşen tutuyor,
  // bu yüzden kapatma isteği olayla gönderilir.
  if (acilacak && props.attentionOpen) {
    emit("attention-close");
  }
}

function zilDisiTiklama(event: PointerEvent) {
  const wrap = bellWrapRef.value;
  if (wrap && event.target instanceof Node && wrap.contains(event.target)) {
    return;
  }

  showNotifications.value = false;
}

function zilKlavye(event: KeyboardEvent) {
  if (event.key === "Escape") {
    showNotifications.value = false;
  }
}

watch(showNotifications, (open) => {
  if (open) {
    document.addEventListener("pointerdown", zilDisiTiklama, true);
    document.addEventListener("keydown", zilKlavye, true);
    return;
  }

  document.removeEventListener("pointerdown", zilDisiTiklama, true);
  document.removeEventListener("keydown", zilKlavye, true);
});

function shortTime(iso: string): string {
  const date = new Date(iso);
  return Number.isNaN(date.getTime()) ? iso : date.toLocaleString();
}

function resolveApiMessage(error: unknown): string {
  const key = extractApiMessage(error);
  const translated = t(key);
  const message = translated === key ? key : translated;
  const details = extractApiDetails(error);
  return details ? `${message}

${details}` : message;
}

const returningToAdmin = ref(false);
const returnError = ref("");

async function returnToAdmin() {
  returningToAdmin.value = true;
  returnError.value = "";
  try {
    await authStore.stopImpersonate();
    window.location.reload();
  } catch (error) {
    returnError.value = resolveApiMessage(error);
  } finally {
    returningToAdmin.value = false;
  }
}

type SectionItem = {
  id: string;
  label: string;
  description?: string;
  glyph: string;
  badge?: string;
  /**
   * Rozetin üzerine gelince görünen açıklama: hangi eksikler olduğunu ADIYLA söyler.
   * Sayı tek başına "bir şey var" der ama NE olduğunu söylemez; kullanıcı öğrenmek için
   * tıklamak zorunda kalır.
   */
  badgeTitle?: string;
};

const props = defineProps<{
  sections: SectionItem[];
  activeSection: string;
  title: string;
  description: string;
  subtitle: string;
  panelName: string;
  /** Özel marka logosu (data URI). Boş/verilmemiş = ürünün kendi işareti gösterilir. */
  logoUrl?: string;
  /** Logoya tıklanınca açılacak adres; sunucu tarafında yalnız http/https kabul edilir. */
  logoLinkUrl?: string;
  machineName: string;
  secureEntryPath: string;
  userName: string;
  userEmail: string;
  role: string;
  panelVersion: string;
  isTwoFactorEnabled: boolean;
  resolvedTheme: string;
  // "Dikkat" çekmecesi: veriyi üst bileşen (SetupWizardView) tutar, layout yalnız gösterir.
  attentionSummary?: AttentionSummary | null;
  attentionLoading?: boolean;
  attentionError?: string;
  attentionOpen?: boolean;
}>();

const emit = defineEmits<{
  (event: "navigate", sectionId: string): void;
  /** Aramadan seçilen site/alan adı: Web Siteleri bölümü O SİTENİN panosuyla açılır. */
  (event: "open-site", siteName: string): void;
  /** Aramadan seçilen posta alan adı: Posta bölümü o alan adı seçili açılır. */
  (event: "open-mail-domain", domain: string): void;
  (event: "refresh"): void;
  (event: "logout"): void;
  (event: "toggle-theme"): void;
  (event: "attention-toggle"): void;
  (event: "attention-close"): void;
  (event: "attention-refresh"): void;
}>();

const { t, te } = useI18n();

/*
 * BÖLÜM DEĞİŞİNCE ÜST BARDAKİ AÇILIR KATMAN KAPANIR.
 * ÖLÇÜLDÜ: zil kutusu Posta ekranına geçildikten sonra da açık kalıyor ve içeriğin ortasını
 * örtüyordu. Aynı `watch` seçili menü maddesini de görüşe kaydırır (bkz. yukarıdaki işlev):
 * ikisi de "bölüm değişti" olayının doğal sonucudur, iki ayrı izleyici açmaya gerek yok.
 */
watch(
  () => props.activeSection,
  () => {
    showNotifications.value = false;
    void seciliMaddeyiGoruseKaydir();
  });

/* İKİ KATMAN ÜST ÜSTE BİNMESİN: dikkat çekmecesi açılırsa zil kutusu kapanır. Tersi
   `zilKutusunuAcKapa` içinde (orada çekmeceye kapanma isteği gönderiliyor). */
watch(
  () => props.attentionOpen,
  (open) => {
    if (open) {
      showNotifications.value = false;
    }
  });

// Rozet: kritik + uyarı (bilgi maddeleri kırmızı nokta üretmez, gürültü olmasın).
const attentionAlertCount = computed(() =>
  (props.attentionSummary?.criticalCount ?? 0) + (props.attentionSummary?.warningCount ?? 0));

const twoFactorLabel = computed(() =>
  props.isTwoFactorEnabled ? t("panel.status.enabled") : t("panel.status.disabled"));
</script>

<style scoped>
.app-shell {
  display: grid;
  grid-template-columns: 218px minmax(0, 1fr);
  gap: 12px;
  min-height: 100vh;
  padding: 12px;
}

.sidebar,
.status-card,
.brand-card,
.mini-card,
.topbar {
  border: 1px solid var(--panel-border);
  background: var(--surface-0);
  box-shadow: var(--panel-shadow);
}

.sidebar {
  display: grid;
  grid-template-rows: auto minmax(0, 1fr) auto;
  gap: 10px;
  min-height: 0;
  padding: 10px 9px;
  border-radius: var(--radius-md);
  position: sticky;
  top: 12px;
  height: calc(100vh - 24px);
  overflow: hidden;
}

.brand-card {
  display: grid;
  grid-template-columns: auto 1fr;
  gap: 10px;
  padding: 9px;
  border-radius: var(--radius-sm);
  box-shadow: none;
}

.brand-mark,
.nav-glyph {
  display: grid;
  place-items: center;
  border-radius: 18px;
  font-weight: 800;
  letter-spacing: 0.02em;
}

/*
 * BU DOSYADAKİ BÜTÜN YAZI BOYLARI ÖLÇEĞE BAĞLANDI (`--fs-*`), ham `rem` sayıları kaldırıldı.
 * NEDEN — sayıldı, canlı 0.8.0: üst bar + kenar menü tek başına ON SEKİZ ayrı yazı boyu
 * yazıyordu (0.6 / 0.62 / 0.64 / 0.65 / 0.66 / 0.7 / 0.72 / 0.75 / 0.78 / 0.8 / 0.82 / 0.84 /
 * 0.85 / 0.86 / 0.92 / 0.98rem + iki clamp). Bunların ONU ölçekte hiç yoktu ve ALTISI
 * `--fs-2xs`in (10.53px) ALTINDAYDI: 8.1px (dikkat + zil rozetleri), 8.37px (menü grup
 * başlıkları), 8.91px (mini etiketler, arama türü rozeti), 9.45px (mini kart değeri),
 * 9.72px (zil kutusu başlığı), 10.13px (menü rozeti). Hepsi bir şeyin ADI ya da SAYISIYDI —
 * "süs" olan tek bir tanesi bile yoktu. Taban artık `--fs-2xs`; altına inen kural yok.
 */
.brand-mark {
  width: 34px;
  height: 34px;
  background: color-mix(in srgb, var(--accent-1) 12%, transparent);
  color: var(--accent-1);
  font-size: var(--fs-2xs);
}

/* Özel marka logosu: yüklenen görselin boyutu ne olursa olsun kenar menüyü BOZAMAZ. Yükseklik
   ürün işaretiyle aynı (34px), genişlik oranına göre serbest ama üst sınırlı. */
.brand-logo {
  width: auto;
  max-width: 92px;
  height: 34px;
  object-fit: contain;
  align-self: center;
}

/* Logo bir bağlantıysa kart yine kart gibi görünmeli (altı çizili mavi metin değil). */
a.brand-card {
  color: inherit;
  text-decoration: none;
}

.brand-copy {
  display: grid;
  gap: 4px;
  align-content: center;
}

.brand-copy p {
  display: none;
}

.brand-copy p,
.nav-copy small,
.status-card small {
  margin: 0;
  color: var(--text-muted);
}

.nav-list {
  display: grid;
  gap: 0;
  align-content: start;
  min-height: 0;
  overflow-x: hidden;
  overflow-y: auto;
  overscroll-behavior: contain;
  padding: 2px 3px 8px 1px;
  scrollbar-color: rgba(148, 163, 184, 0.34) transparent;
  scrollbar-width: thin;
}

/* Grup arası boşluk `gap` yerine BURADA: ilk grubun ("Genel") başlığı ekranda görünmediği için
   `gap` onun da üstüne boşluk koyar, menü markanın altından kopardı. Ayırıcı yalnız gruplar
   ARASINA gerekir.

   ÇİZGİ YOK, BİLEREK: başlığın kendisi zaten ayırıcıdır. Denendi ve ÖLÇÜLDÜ — hairline + başlık
   birlikte grup başına 9px daha yer yiyordu (5 grup = 45px, bir buçuk menü satırı) ve iki kez
   aynı şeyi söylüyordu. Menü zaten kaydırmalı; boşluk madde göstermek için harcanır. */
.nav-group + .nav-group {
  margin-top: 10px;
}

/* Başlık maddelerden BELİRGİN ŞEKİLDE hafif: küçük punto + harf aralığı + soluk renk. Aynı
   ağırlıkta olsaydı 5 yeni satır kalabalığı azaltmaz, ARTIRIRDI. Kenar menüdeki `.mini-label`
   ile aynı görsel dil (o da büyük harf, soluk) — panelde ikinci bir başlık üslubu icat edilmedi. */
.nav-group-title {
  margin: 0;
  padding: 0 9px 3px;
  color: var(--text-muted);
  /* 0.62rem = 8.37px idi. Grup başlığı menünün TEK durak işaretidir; 8px'te büyük harf +
     0.07em aralık okunmaz, gri bir çizgiye dönüşür. Tabana çekildi: 10.53px. Bedeli ölçüldü —
     grup başına satır yüksekliği 10.0 -> 12.6px, altı grupta toplam +15.6px; liste zaten
     kaydırmalı ve seçili madde artık görüşe kaydırılıyor (aşağıdaki `watch`). */
  font-size: var(--fs-2xs);
  font-weight: 700;
  text-transform: uppercase;
  letter-spacing: 0.07em;
  line-height: 1.2;
}

.nav-group-items {
  display: grid;
  gap: 3px;
}

.nav-item, .nav-item:hover, .nav-item:active {
  box-shadow: none;
  transform: none;
  filter: none;
}

.nav-item {
  display: grid;
  grid-template-columns: auto 1fr auto;
  gap: 8px;
  align-items: center;
  min-height: 36px;
  padding: 6px 8px;
  border-radius: var(--radius-sm);
  border: 1px solid transparent;
  border-left: 2px solid transparent;
  background: transparent;
  color: var(--text-strong);
  text-align: left;
  /* Seçili madde görüşe kaydırılırken (bkz. `seciliMaddeyiGoruseKaydir`) kutunun kenarına
     YAPIŞMASIN: 12px, üstündeki grup başlığının da bir kısmını görünür bırakır, yani madde
     bağlamsız bir satır olarak değil, grubunun içinde belirir. */
  scroll-margin-block: 12px;
}

.nav-item:hover {
  border-color: color-mix(in srgb, var(--accent-1) 24%, transparent);
  background: var(--surface-2);
}

.nav-item.active {
  border-color: color-mix(in srgb, var(--accent-1) 14%, transparent);
  border-left-color: var(--accent-1);
  background: var(--surface-3);
  box-shadow: none;
}

.nav-glyph {
  display: grid;
  place-items: center;
  width: 26px;
  height: 26px;
  border-radius: 6px;
  background: var(--surface-2);
  color: var(--text-soft);
}

.nav-icon {
  width: 17px;
  height: 17px;
  fill: none;
  stroke: currentColor;
  stroke-width: 1.7;
  stroke-linecap: round;
  stroke-linejoin: round;
}

.nav-item.active .nav-glyph {
  background: color-mix(in srgb, var(--accent-1) 18%, transparent);
  color: var(--accent-1);
}

.nav-copy {
  display: grid;
  gap: 2px;
}

.nav-copy strong {
  font-size: var(--fs-sm);
  font-weight: 580;
}

.nav-badge {
  min-width: 28px;
  padding: 4px 8px;
  border-radius: 999px;
  background: color-mix(in srgb, var(--warn-1) 15%, transparent);
  color: var(--warn-1);
  font-size: var(--fs-2xs);
  font-weight: 700;
  text-align: center;
}

.sidebar-foot {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 5px;
}

.mini-card,
.status-card {
  display: grid;
  gap: 3px;
  padding: 7px 8px;
  border-radius: var(--radius-sm);
  box-shadow: none;
  overflow: hidden;
}

.mini-card strong {
  overflow: hidden;
  font-size: var(--fs-2xs);
  text-overflow: ellipsis;
  white-space: nowrap;
}

.mini-label,
.eyebrow-row {
  color: var(--text-muted);
  font-size: var(--fs-2xs);
  text-transform: uppercase;
  letter-spacing: 0.045em;
}

/* IZGARA -> ESNEK SÜTUN. Tek sebebi üst barın YAPIŞKAN olabilmesi.
   `position: sticky` bir ızgara ÖĞESİ için o öğenin KENDİ SATIRIYLA sınırlıdır: üst bar
   ızgaranın ilk satırındayken sayfa kaydıkça satırıyla birlikte yukarı çıkıp ekrandan
   ÇIKAR (ölçüldü, canlı 0.8.0: 560px kaydırmada üst bar `top: -548px`, yani arama kutusu ve
   "Çıkış yap" tamamen görünmez). Esnek kapsayıcıda yapışma alanı kapsayıcının TAMAMIDIR.
   Aynı çeviri dar ekran için ZATEN yapılmıştı (aşağıdaki 900px bloğu); burada tekrarlanmıyor,
   yukarı taşınıyor — dar ekranda doğru olan şey geniş ekranda da doğruydu.
   `align-content: start` DÜŞTÜ: esnek sütunda karşılığı `justify-content: flex-start` ve o
   zaten varsayılan. `gap` aynı kalır (esnek düzende de geçerli). */
.main-column {
  display: flex;
  flex-direction: column;
  gap: 9px;
  min-width: 0;
  /* İÇERİK ÜST SINIRI — panelin genişlik kararı BURADA uygulanır, değeri `--content-max`
     belirtecinden gelir (gerekçe ve ölçümler `styles/base.css` içinde, tek kaynak).

     `.content-column` DEĞİL, `.main-column` sınırlanıyor: üst çubuk (`.topbar`) da bu
     sütunun içindedir ve ikisi AYNI genişlikte durmak zorundadır. Yalnız içerik
     sınırlansaydı 2560px'de üst çubuk 2306px, altındaki paneller 1600px olurdu; çubuğun sağ
     ucundaki yenile/çıkış düğmeleri hiçbir içeriğin hizasında olmayan 706px'lik boşlukta
     asılı kalırdı. Sınır sonrası ÖLÇÜLDÜ: üst çubuk 1600px, içerik sütunu 1600px, ilk panel
     1600px — üçü tam hizada.

     `max-width` HİÇBİR ŞEYİ KIRPMAZ: tablolar kendi `.data-table` kabında (`overflow-x:
     auto`) kayar, sayfa gövdesi yatayda kaymaz. Altı ekranda (Ana Sayfa, Ayarlar, Güvenlik,
     Sunucu Araçları, Hesap, Denetim Kayıtları) ölçüldü: kırpılan öge 0, kaydırma çubuğu
     açan tablo 0.

     Dar ekran ATKILANMAZ: sınır ancak içerik 1600px'i aşabildiğinde, yani görüntü alanı
     1854px'i (1600 + 218 kenar menü + 12 boşluk + 24 dolgu) geçtiğinde devreye girer. */
  max-width: var(--content-max);
}

/* profesyonel paneller gibi ince tek satır üst çubuk. */
.topbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  padding: 7px 12px;
  border-radius: var(--radius-md);
  /* YAPIŞKAN. `top: 12px` = `.app-shell` üst dolgusu, yani kenar menünün (`.sidebar`,
     `top: 12px`) TAM HİZASINDA durur; iki sütun birlikte yapışır, biri diğerinin altından
     kaymaz. Zemini opak (`--surface-0`) ve kenarlığı olduğu için altından geçen tablo
     satırları görünmez.
     z-index KATMAN SIRASI: içerik panelleri (katmansız) < üst bar (30) < arama/zil açılırları
     (40, üst barın İÇİNDE) < dar ekran karartması (50) < dikkat çekmecesi (60). 30 seçildi ki
     üst bar içeriğin üstünde kalsın ama çekmece ve karartma onun ÜSTÜNDE açılabilsin. */
  position: sticky;
  top: 12px;
  z-index: 30;
}

/* ÜST BOŞLUĞUN MASKESİ. Üst bar tepeden 12px İÇERİDE durduğu için (kenar menüyle aynı hizada),
   o 12px'lik şeritten kaydırılan içerik GÖRÜNÜYORDU: ölçüldü, 600px kaydırmada barın üstünde
   özet kartının "Adres bağlamaları: 2 / Yönetilebilir" satırının alt 10px'i okunuyordu — yarım
   kesilmiş metin şeridi. Şerit sayfa zeminiyle kapatılıyor; 13px = 12px boşluk + barın 1px üst
   kenarlığı. Bar zaten yapışkan olduğu için maske de onunla birlikte taşınır.
   Dar ekranda (`top: 0`) maske görüş alanının ÜSTÜNE düşer, yani hiçbir şey yapmaz. */
.topbar::before {
  content: "";
  position: absolute;
  top: -13px;
  right: 0;
  left: 0;
  height: 13px;
  background: var(--page-bg);
}

.topbar-copy {
  min-width: 0;
}

.topbar-copy h1 {
  display: flex;
  align-items: center;
  gap: 8px;
  margin: 0;
  font-size: clamp(1rem, 1.2vw, 1.2rem);
  font-weight: 650;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.topbar-actions {
  display: flex;
  gap: 6px;
  align-items: center;
  flex-shrink: 0;
}

.topbar-search {
  position: relative;
  flex: 1 1 260px;
  max-width: 360px;
}

.search-input {
  width: 100%;
  padding: 7px 12px;
  border-radius: 999px;
  border: 1px solid var(--panel-border);
  background: var(--surface-1);
  color: var(--text-strong);
  font-size: var(--fs-sm);
}

.search-results {
  position: absolute;
  z-index: 40;
  top: calc(100% + 6px);
  left: 0;
  right: 0;
  display: grid;
  gap: 2px;
  max-height: min(60vh, 420px);
  overflow-y: auto;
  padding: 6px;
  border-radius: var(--radius-md);
  border: 1px solid var(--panel-border);
  background: var(--surface-0);
  box-shadow: 0 12px 40px rgba(0, 0, 0, 0.35);
}

.search-result {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 8px 10px;
  border-radius: 8px;
  background: transparent;
  color: var(--text-strong);
  text-align: left;
  cursor: pointer;
}

.search-result-copy {
  display: grid;
  gap: 1px;
  min-width: 0;
  flex: 1 1 auto;
}

.search-result-copy strong {
  font-size: var(--fs-sm);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.search-result-copy small {
  color: var(--text-muted);
  font-size: var(--fs-2xs);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.search-kind {
  flex: 0 0 auto;
  padding: 2px 8px;
  border-radius: 999px;
  background: var(--surface-2);
  color: var(--text-muted);
  font-size: var(--fs-2xs);
  font-weight: 700;
  text-transform: uppercase;
  letter-spacing: 0.03em;
}

.search-note {
  margin: 0;
  padding: 8px 10px;
  color: var(--text-muted);
  font-size: var(--fs-2xs);
}

.search-note.error {
  color: var(--danger-text);
}

.search-result:hover {
  background: var(--surface-2);
}

@media (max-width: 1200px) {
  .topbar-search {
    display: none;
  }
}

.user-chip {
  display: inline-flex;
  align-items: baseline;
  gap: 6px;
  padding: 4px 10px;
  border-radius: 999px;
  border: 1px solid var(--panel-border);
  background: var(--surface-1);
}

.user-chip strong {
  font-size: var(--fs-xs);
}

.user-chip small {
  color: var(--text-muted);
  font-size: var(--fs-2xs);
}

.icon-btn {
  font-size: 1rem;
  line-height: 1;
}

.attention-btn {
  position: relative;
}

.attention-btn .nav-icon {
  width: 16px;
  height: 16px;
  fill: none;
  stroke: currentColor;
  stroke-width: 1.7;
  stroke-linecap: round;
  stroke-linejoin: round;
}

/* ROZET BOYU — sayı okunacak kadar büyük olmalı, yoksa rozet bir NOKTA'dır.
   ÖLÇÜLDÜ (canlı 0.8.0): yazı 0.6rem = **8.1px**, kutu 15px. Kontrast düzeltilmişti ama
   OKUNURLUK düzeltilmemişti: 8.1px'te "3" ile "8" bir metre uzaktan ayırt edilemiyor ve
   rozetin tek işi o sayıyı söylemek. Yazı tabana (`--fs-2xs`, 10.53px) çıkarıldı; kutu
   15 -> 18px büyütüldü (10.53px yazı 15px kutuda kenara değiyordu) ve konum -4 -> -5px'e
   kaydırıldı ki rozet düğmenin köşesinde AYNI oranda dursun. */
.attention-badge {
  position: absolute;
  top: -5px;
  right: -5px;
  min-width: 18px;
  height: 18px;
  padding: 0 4px;
  border-radius: 999px;
  background: var(--warn-1);
  /* ÖLÇÜLDÜ, eski gömülü #fff: kehribar dolgu üzerinde koyu temada 2.21:1, açık temada 4.09:1 —
     0.6rem/700 bir sayı için AA eşiği (4.5) her iki temada da geçilmiyordu. Bu rozet panelde kaç
     sorun bulunduğunu söyleyen TEK sayıdır; okunmazsa çekmeceyi açma sebebi de görünmez.
     `--on-warn` ile 9.51 (koyu) / 5.13 (açık). */
  color: var(--on-warn);
  font-size: var(--fs-2xs);
  line-height: 18px;
  text-align: center;
  font-weight: 700;
}

.attention-badge.critical {
  background: var(--danger-1);
  /* Dolgu değişince mürekkep de değişmeli: kırmızının payı temaya göre TERS yönde (koyu temada
     siyah 6.13 / beyaz 3.42, açık temada beyaz 5.05 / siyah 4.15). `--on-danger` bu dönüşü taşır;
     üstteki `--on-warn` burada kalsaydı açık temada 4.15 ile eşiğin altında kalırdık. */
  color: var(--on-danger);
}

.bell-wrap {
  position: relative;
}

.bell-btn {
  position: relative;
}

.bell-btn .nav-icon {
  width: 16px;
  height: 16px;
  fill: none;
  stroke: currentColor;
  stroke-width: 1.7;
  stroke-linecap: round;
  stroke-linejoin: round;
}

.bell-badge {
  position: absolute;
  top: -5px;
  right: -5px;
  min-width: 18px;
  height: 18px;
  padding: 0 4px;
  border-radius: 999px;
  background: var(--accent-1);
  color: var(--on-accent);
  /* Kardeşi `.attention-badge` ile AYNI karar (gerekçesi orada): 8.1px -> `--fs-2xs`. */
  font-size: var(--fs-2xs);
  line-height: 18px;
  text-align: center;
  font-weight: 700;
}

.bell-menu {
  position: absolute;
  z-index: 40;
  top: calc(100% + 6px);
  right: 0;
  width: 280px;
  max-height: 360px;
  overflow-y: auto;
  padding: 6px;
  border-radius: var(--radius-md);
  border: 1px solid var(--panel-border);
  background: var(--surface-0);
  box-shadow: 0 12px 40px rgba(0, 0, 0, 0.35);
}

.bell-head {
  padding: 6px 8px;
  color: var(--text-muted);
  font-size: var(--fs-2xs);
  font-weight: 700;
  text-transform: uppercase;
  letter-spacing: 0.05em;
}

.bell-item {
  display: grid;
  gap: 2px;
  padding: 7px 8px;
  border-radius: 7px;
  /* ÖNEM ŞERİDİ. Rozet düzeyin ADINI söyler, şerit ise listeyi TARANABİLİR yapar: kullanıcı
     sekiz satırı okumadan hangisinin kırmızı olduğunu görür. Şerit tek başına bilgi taşımaz
     (renk tek kanal değil), rozetin görsel eşidir. */
  border-left: 3px solid transparent;
  padding-left: 7px;
}

.bell-item.error {
  border-left-color: var(--danger-1);
}

.bell-item.warn {
  border-left-color: var(--warn-1);
}

.bell-item:hover {
  background: var(--surface-2);
}

/* Rozet + ad aynı satırda; ad uzunsa alt satıra sarar (280px'lik kutuda kesilmez). */
.bell-item-title {
  display: flex;
  align-items: center;
  gap: 6px;
  flex-wrap: wrap;
}

.bell-item strong {
  font-size: var(--fs-xs);
}

.bell-item small {
  color: var(--text-muted);
  font-size: var(--fs-2xs);
}

.bell-empty {
  padding: 10px 8px;
  color: var(--text-muted);
  font-size: var(--fs-xs);
}

.status-card {
  min-width: 135px;
  padding: 6px 8px;
}

.status-card strong {
  font-size: var(--fs-2xs);
}

.status-card small {
  max-width: 180px;
  overflow: hidden;
  font-size: var(--fs-2xs);
  text-overflow: ellipsis;
  white-space: nowrap;
}

.theme-toggle {
  display: grid;
  min-width: 32px;
  padding: 5px;
  place-items: center;
  line-height: 1;
}

.theme-toggle svg {
  width: 16px;
  height: 16px;
  fill: none;
  stroke: currentColor;
  stroke-linecap: round;
  stroke-linejoin: round;
  stroke-width: 1.8;
}

.theme-toggle svg:first-child path {
  fill: currentColor;
  stroke: none;
}

.status-dot {
  width: 10px;
  height: 10px;
  border-radius: 999px;
  background: var(--accent-1);
  box-shadow: 0 0 0 6px color-mix(in srgb, var(--accent-1) 8%, transparent);
}

.eyebrow-row {
  display: inline-flex;
  align-items: center;
  gap: 10px;
}

.content-column {
  display: grid;
  gap: 10px;
  min-width: 0;
}

.impersonation-error {
  color: var(--danger-text);
  font-weight: 600;
  flex-basis: 100%;
}

.impersonation-banner {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  flex-wrap: wrap;
  padding: 10px 14px;
  border-radius: var(--radius-lg);
  border: 1px solid color-mix(in srgb, var(--warn-1) 45%, transparent);
  background: color-mix(in srgb, var(--warn-1) 14%, transparent);
  color: var(--warn-text);
  font-weight: 600;
}

/* Menü düğmesi masaüstünde hiç basılmaz (v-if ile de render edilmez); burası yalnız görünüm. */
.nav-toggle {
  display: grid;
  place-items: center;
  width: 40px;
  min-width: 40px;
  height: 40px;
  padding: 0;
  flex-shrink: 0;
}

.nav-toggle .nav-icon {
  width: 19px;
  height: 19px;
}

/* Çekmece karartması: AttentionDrawer (z-index 60) bunun ÜSTÜNDE kalmalı. */
.nav-scrim {
  position: fixed;
  inset: 0;
  z-index: 50;
  background: rgba(15, 23, 42, 0.44);
}

@media (max-width: 960px) {
  .topbar-panels {
    justify-content: space-between;
  }
}

/*
  Dar ekran kabuğu (telefon / küçük tablet).
  Alt bar yerine hamburger seçildi: menüde 13+ bölüm var ve bunlar role göre değişiyor;
  alt bara ancak 4-5 tanesi sığar, gerisi gizlenirdi. Olay akışının giriş kapısı zaten
  üst bardaki DİKKAT düğmesi — bu yüzden üst bar yapışkan (sticky) yapılıp dikkat düğmesi
  her an ekranda tutulur, tam bölüm listesi ise çekmecede saklı kalır.
*/
@media (max-width: 1200px) {
  .app-shell {
    grid-template-columns: minmax(0, 1fr);
    /* Yapışkan üst bar tepeye yaslansın diye üst boşluk yok. */
    padding: 0 8px 8px;
  }

  .sidebar {
    position: fixed;
    top: 0;
    left: 0;
    z-index: 56;
    width: min(288px, 86vw);
    height: 100vh;
    height: 100dvh;
    padding: 12px 10px calc(12px + env(safe-area-inset-bottom));
    border-radius: 0;
    border-width: 0 1px 0 0;
    box-shadow: 18px 0 48px rgba(0, 0, 0, 0.32);
    overflow: hidden;
    /* Kapalıyken görünmez + sekme sırasının dışında (visibility: hidden odak almaz). */
    visibility: hidden;
    transform: translateX(-102%);
    transition: transform 0.22s ease, visibility 0.22s ease;
  }

  .sidebar.open {
    visibility: visible;
    transform: none;
  }

  .nav-list {
    grid-template-columns: minmax(0, 1fr);
    overflow-y: auto;
  }

  /* Çekmecede grup başlıkları GİZLENMEZ: çekmece tam genişlikte açılır, asıl kaybolan şey
     dokunmatikte listeyi tarayabilmek. Aralık dokunma hedefleriyle birlikte büyür. */
  .nav-group-items {
    gap: 4px;
  }

  .nav-item {
    min-height: 44px;
    padding: 8px 10px;
  }

  .nav-copy strong {
    font-size: var(--fs-md);
  }

  /* (`.main-column` esnek sütuna çevirisi artık VARSAYILAN — yukarı taşındı, burada tekrar yok.) */

  /* Üst bar: 1. satır menü + başlık, 2. satır eylemler (dikkat düğmesi dahil).
     Yapışkanlık artık varsayılan; burada değişen yalnız KONUM: dar ekranda kabuğun üst
     dolgusu 0 olduğu için bar tepeye (`top: 0`) yaslanır, geniş ekrandaki 12px'e değil. */
  .topbar {
    top: 0;
    flex-wrap: wrap;
    row-gap: 8px;
    padding: 8px 10px;
    border-width: 0 0 1px;
    border-radius: 0 0 var(--radius-md) var(--radius-md);
  }

  .topbar-copy {
    flex: 1 1 auto;
  }

  .topbar-actions {
    flex: 1 1 100%;
    justify-content: space-between;
    gap: 6px;
  }

  .icon-btn,
  .theme-toggle {
    display: grid;
    place-items: center;
    width: 40px;
    min-width: 40px;
    height: 40px;
    padding: 0;
  }

  .attention-badge,
  .bell-badge {
    top: -4px;
    right: -4px;
    min-width: 20px;
    height: 20px;
    font-size: var(--fs-2xs);
    line-height: 20px;
  }

  .bell-menu {
    width: min(300px, calc(100vw - 32px));
  }
}

@media (max-width: 620px) {
  /* Kullanıcı adı/rol yalnız bilgi; olay anında yer kaplamasın (bilgi user-chip title'ında kalır). */
  .user-chip {
    display: none;
  }

  .topbar-copy h1 {
    font-size: 0.98rem;
  }
}
@media (min-width: 701px) and (max-width: 1200px) {
  .topbar { flex-wrap: nowrap; }
  .topbar-actions { flex: 0 1 auto; justify-content: flex-end; }
}
.skip-link { position: fixed; left: 12px; top: 8px; z-index: 100; padding: 12px 16px; background: var(--surface-0); color: var(--accent-text); border: 2px solid var(--focus-ring); border-radius: var(--radius-sm); transform: translateY(-150%); }
.skip-link:focus { transform: none; }
.nav-close { position: absolute; right: 12px; top: 18px; width: 34px; font-size: 1.4rem; }
@media (max-width: 1200px) { .brand-card { padding-right: 44px; } }
</style>
