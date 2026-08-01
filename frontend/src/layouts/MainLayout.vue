<template>
  <div class="app-shell">
    <!--
      Dar ekran: kenar menü içeriğin üstüne binmesin diye dışarı kayan (off-canvas)
      çekmeceye döner. Karartma katmanı hem dış tıklamayla kapatır hem de arka plandaki
      düğmelerin yanlışlıkla basılmasını engeller.
    -->
    <div v-if="isNarrow && menuOpen" class="nav-scrim" @click="closeMenu"></div>

    <aside id="ap-sidebar" ref="sidebarRef" :class="['sidebar', { open: isNarrow && menuOpen }]">
      <div class="brand-card">
        <div class="brand-mark">AP</div>
        <div class="brand-copy">
          <strong>{{ panelName }}</strong>
          <p>{{ subtitle }}</p>
        </div>
      </div>

      <nav class="nav-list">
        <button
          v-for="section in sections"
          :key="section.id"
          :class="['nav-item', { active: section.id === activeSection }]"
          @click="handleNavigate(section.id)">
          <span class="nav-glyph">
            <svg class="nav-icon" viewBox="0 0 24 24" aria-hidden="true" v-html="iconFor(section.id)"></svg>
          </span>
          <span class="nav-copy">
            <strong>{{ section.label }}</strong>
          </span>
          <span v-if="section.badge" class="nav-badge">{{ section.badge }}</span>
        </button>
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

    <div class="main-column">
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
            type="search"
            :placeholder="t('panel.layout.searchPlaceholder')"
            @keydown.esc="searchQuery = ''"
            @keydown.enter="goToFirstResult" />
          <div v-if="searchQuery && searchResults.length" class="search-results">
            <button
              v-for="result in searchResults"
              :key="result.id"
              class="search-result"
              @click="selectSearch(result.id)">
              <span class="nav-glyph">
                <svg class="nav-icon" viewBox="0 0 24 24" aria-hidden="true" v-html="iconFor(result.id)"></svg>
              </span>
              <span>{{ result.label }}</span>
            </button>
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

          <div v-if="role === 'Administrator'" class="bell-wrap">
            <button class="secondary compact icon-btn bell-btn" :title="t('panel.layout.notifications')" @click="showNotifications = !showNotifications">
              <svg viewBox="0 0 24 24" class="nav-icon" aria-hidden="true">
                <path d="M6 9a6 6 0 0 1 12 0c0 5 1.5 6 1.5 6H4.5S6 14 6 9zM10 19a2 2 0 0 0 4 0" />
              </svg>
              <span v-if="recentActivity.length" class="bell-badge">{{ recentActivity.length > 9 ? "9+" : recentActivity.length }}</span>
            </button>
            <div v-if="showNotifications" class="bell-menu">
              <div class="bell-head">{{ t("panel.layout.recentActivity") }}</div>
              <div v-for="item in recentActivity" :key="item.id" class="bell-item">
                <strong>{{ humanizeAction(item.action) }}</strong>
                <small>{{ item.userName }} · {{ shortTime(item.createdAtUtc) }}</small>
              </div>
              <p v-if="recentActivity.length === 0" class="bell-empty">{{ t("panel.layout.noActivity") }}</p>
            </div>
          </div>
          <span class="user-chip" :title="`${userEmail} · v${panelVersion} · ${t('panel.layout.twoFactor')}: ${twoFactorLabel}`">
            <strong>{{ userName }}</strong>
            <small>{{ role }}</small>
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
          <button class="secondary compact icon-btn" :title="t('panel.dashboard.refresh')" @click="$emit('refresh')">↻</button>
          <button class="secondary compact" @click="$emit('logout')">{{ t("panel.auth.logout") }}</button>
        </div>
      </header>

      <main class="content-column">
        <div v-if="authStore.session?.isImpersonating" class="impersonation-banner">
          <span>{{ t("panel.users.impersonatingAs", { name: authStore.session?.user?.userName }) }}</span>
          <button class="compact" @click="returnToAdmin">{{ t("panel.users.returnToAdmin") }}</button>
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
import { useAuthStore } from "../stores/auth";

const authStore = useAuthStore();

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
  migration: '<path d="M4 7h12M12 3l4 4-4 4"/><path d="M20 17H8M12 21l-4-4 4-4"/>'
};

function iconFor(id: string): string {
  return NAV_ICONS[id] ?? '<circle cx="12" cy="12" r="7"/>';
}

// Global arama (profesyonel panel benzeri "git"): menü bölümlerinde ada göre filtrele.
const searchQuery = ref("");
const searchResults = computed(() => {
  const query = searchQuery.value.trim().toLocaleLowerCase();
  if (!query) {
    return [];
  }
  return props.sections
    .filter((section) => section.id !== "exit" && section.label.toLocaleLowerCase().includes(query))
    .slice(0, 8);
});

function selectSearch(id: string) {
  searchQuery.value = "";
  handleNavigate(id);
}

// --- Dar ekran kabuğu: açılır kenar menü -----------------------------------
// 900px altında kenar menü sabit sütun olmaktan çıkıp çekmeceye döner (bkz. stiller).
// Eşiği JS tarafında da biliyoruz ki menü düğmesi geniş ekranda hiç basılamasın (odak sırasına
// girmesin) ve ekran genişleyince açık kalan çekmece kendiliğinden kapansın.
const NARROW_QUERY = "(max-width: 900px)";
// İlk çizimde de doğru olmalı: aksi halde telefonda menü düğmesi bir kare gecikmeli belirir.
const isNarrow = ref(typeof window !== "undefined" && window.matchMedia(NARROW_QUERY).matches);
const menuOpen = ref(false);
const sidebarRef = ref<HTMLElement | null>(null);
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
  if (event.key === "Escape" && menuOpen.value) {
    event.stopPropagation();
    closeMenu();
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
    sidebarRef.value?.querySelector<HTMLElement>(".nav-item")?.focus();
    return;
  }

  document.removeEventListener("keydown", onMenuKeydown, true);
  menuButtonRef.value?.focus?.();
});

onMounted(() => {
  narrowQuery = window.matchMedia(NARROW_QUERY);
  isNarrow.value = narrowQuery.matches;
  narrowQuery.addEventListener("change", onNarrowChange);
});

onBeforeUnmount(() => {
  narrowQuery?.removeEventListener("change", onNarrowChange);
  document.removeEventListener("keydown", onMenuKeydown, true);
  if (menuOpen.value) {
    lockBodyScroll(false);
  }
});

function goToFirstResult() {
  if (searchResults.value.length > 0) {
    selectSearch(searchResults.value[0].id);
  }
}

// Bildirim çanı: son yönetim aktiviteleri (audit log).
const showNotifications = ref(false);
const recentActivity = computed(() => authStore.auditLogs.slice(0, 8));

function humanizeAction(action: string): string {
  return action.replace(/[._]/g, " ").replace(/\b\w/g, (c) => c.toUpperCase());
}

function shortTime(iso: string): string {
  const date = new Date(iso);
  return Number.isNaN(date.getTime()) ? iso : date.toLocaleString();
}

async function returnToAdmin() {
  try {
    await authStore.stopImpersonate();
    window.location.reload();
  } catch (error) {
    void error;
  }
}

type SectionItem = {
  id: string;
  label: string;
  description?: string;
  glyph: string;
  badge?: string;
};

const props = defineProps<{
  sections: SectionItem[];
  activeSection: string;
  title: string;
  description: string;
  subtitle: string;
  panelName: string;
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
  (event: "refresh"): void;
  (event: "logout"): void;
  (event: "toggle-theme"): void;
  (event: "attention-toggle"): void;
  (event: "attention-close"): void;
  (event: "attention-refresh"): void;
}>();

const { t } = useI18n();

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

.brand-mark {
  width: 34px;
  height: 34px;
  background: color-mix(in srgb, var(--accent-1) 12%, transparent);
  color: var(--accent-1);
  font-size: 0.78rem;
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
  gap: 3px;
  align-content: start;
  min-height: 0;
  overflow-x: hidden;
  overflow-y: auto;
  overscroll-behavior: contain;
  padding: 2px 3px 8px 1px;
  scrollbar-color: rgba(148, 163, 184, 0.34) transparent;
  scrollbar-width: thin;
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
  font-size: 0.86rem;
  font-weight: 580;
}

.nav-badge {
  min-width: 28px;
  padding: 4px 8px;
  border-radius: 999px;
  background: color-mix(in srgb, var(--warn-1) 15%, transparent);
  color: var(--warn-1);
  font-size: 0.75rem;
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
  font-size: 0.7rem;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.mini-label,
.eyebrow-row {
  color: var(--text-muted);
  font-size: 0.66rem;
  text-transform: uppercase;
  letter-spacing: 0.045em;
}

.main-column {
  display: grid;
  gap: 9px;
  align-content: start;
  min-width: 0;
}

/* profesyonel paneller gibi ince tek satır üst çubuk. */
.topbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  padding: 7px 12px;
  border-radius: var(--radius-md);
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
  font-size: 0.85rem;
}

.search-results {
  position: absolute;
  z-index: 40;
  top: calc(100% + 6px);
  left: 0;
  right: 0;
  display: grid;
  gap: 2px;
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

.search-result:hover {
  background: var(--surface-2);
}

@media (max-width: 900px) {
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
  font-size: 0.8rem;
}

.user-chip small {
  color: var(--text-muted);
  font-size: 0.7rem;
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

.attention-badge {
  position: absolute;
  top: -4px;
  right: -4px;
  min-width: 15px;
  height: 15px;
  padding: 0 3px;
  border-radius: 999px;
  background: var(--warn-1);
  color: #fff;
  font-size: 0.6rem;
  line-height: 15px;
  text-align: center;
  font-weight: 700;
}

.attention-badge.critical {
  background: var(--danger-1);
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
  top: -4px;
  right: -4px;
  min-width: 15px;
  height: 15px;
  padding: 0 3px;
  border-radius: 999px;
  background: var(--accent-1);
  color: var(--on-accent);
  font-size: 0.6rem;
  line-height: 15px;
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
  font-size: 0.72rem;
  font-weight: 700;
  text-transform: uppercase;
  letter-spacing: 0.05em;
}

.bell-item {
  display: grid;
  gap: 2px;
  padding: 7px 8px;
  border-radius: 7px;
}

.bell-item:hover {
  background: var(--surface-2);
}

.bell-item strong {
  font-size: 0.8rem;
}

.bell-item small {
  color: var(--text-muted);
  font-size: 0.7rem;
}

.bell-empty {
  padding: 10px 8px;
  color: var(--text-muted);
  font-size: 0.82rem;
}

.status-card {
  min-width: 135px;
  padding: 6px 8px;
}

.status-card strong {
  font-size: 0.78rem;
}

.status-card small {
  max-width: 180px;
  overflow: hidden;
  font-size: 0.65rem;
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

/* Ara genişlik (tablet): kenar menü içeriğin üstünde yatay şerit — mevcut davranış. */
@media (min-width: 901px) and (max-width: 1200px) {
  .app-shell {
    grid-template-columns: 1fr;
  }

  .sidebar {
    position: static;
    height: auto;
    overflow: visible;
  }

  .nav-list {
    grid-template-columns: repeat(3, minmax(0, 1fr));
    overflow: visible;
  }
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
@media (max-width: 900px) {
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
    gap: 4px;
    overflow-y: auto;
  }

  .nav-item {
    min-height: 44px;
    padding: 8px 10px;
  }

  .nav-copy strong {
    font-size: 0.92rem;
  }

  /* Yapışkan üst bar için: grid öğesinin sabitlenme alanı kendi satırıyla sınırlıdır,
     esnek (flex) öğede ise kapsayıcının tamamıdır — bu yüzden sütun flex'e çevriliyor. */
  .main-column {
    display: flex;
    flex-direction: column;
  }

  /* Üst bar: 1. satır menü + başlık, 2. satır eylemler (dikkat düğmesi dahil).
     Sticky → "neyin bozuk olduğunu gör" düğmesi kaydırma nerede olursa olsun elde. */
  .topbar {
    position: sticky;
    top: 0;
    z-index: 40;
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
    top: -3px;
    right: -3px;
    min-width: 17px;
    height: 17px;
    font-size: 0.64rem;
    line-height: 17px;
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
</style>
