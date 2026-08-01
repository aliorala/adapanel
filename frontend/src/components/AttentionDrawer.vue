<template>
  <!--
    Bildirim çekmecesi: panelin bulduğu sorunu kullanıcının önüne getirir. Her madde ya tek-tık
    düzeltme düğmesi ya da gideceği ekran taşır — uyarıyı gösterip kullanıcıyı yalnız bırakmayız.
  -->
  <div v-if="open" class="attention-scrim" @mousedown.self="requestClose">
    <aside
      ref="panelRef"
      class="attention-drawer"
      role="dialog"
      aria-modal="true"
      :aria-label="t('panel.attention.title')"
      tabindex="-1"
      @keydown.esc.stop.prevent="requestClose"
      @keydown.tab="trapFocus">
      <header class="ad-head">
        <div class="ad-head-copy">
          <h2>{{ t("panel.attention.title") }}</h2>
          <p class="meta">{{ t("panel.attention.subtitle") }}</p>
        </div>
        <div class="row-actions">
          <button class="secondary compact" :disabled="loading" @click="$emit('refresh')">
            {{ loading ? t("panel.common.loading") : t("panel.attention.refresh") }}
          </button>
          <button
            ref="closeRef"
            class="secondary compact"
            :aria-label="t('panel.attention.close')"
            @click="requestClose">
            {{ t("panel.attention.close") }}
          </button>
        </div>
      </header>

      <div v-if="summary" class="ad-counters">
        <span v-if="summary.criticalCount" class="status-pill danger">
          {{ summary.criticalCount }} {{ t("panel.advisor.critical") }}
        </span>
        <span v-if="summary.warningCount" class="status-pill warn">
          {{ summary.warningCount }} {{ t("panel.advisor.warning") }}
        </span>
        <span v-if="summary.infoCount" class="status-pill">
          {{ summary.infoCount }} {{ t("panel.advisor.info") }}
        </span>
        <span v-if="summary.checkedAtUtc" class="meta ad-checked">
          {{ t("panel.advisor.checkedAt") }}: {{ formatUtc(summary.checkedAtUtc) }}
        </span>
      </div>

      <p v-if="loadError" class="notice error">{{ loadError }}</p>

      <p v-if="summary && summary.failedSources.length" class="notice">
        {{ t("panel.attention.failedSources") }}: {{ summary.failedSources.join(", ") }}
      </p>

      <div v-if="summary && summary.items.length === 0 && !loading" class="ad-empty">
        <span class="ad-empty-badge" aria-hidden="true">✓</span>
        <div>
          <strong>{{ t("panel.attention.emptyTitle") }}</strong>
          <p class="meta">{{ t("panel.attention.emptyHint") }}</p>
        </div>
      </div>

      <ul v-else class="ad-list">
        <li v-for="item in items" :key="item.id" :class="['ad-item', item.severity]">
          <span class="ad-dot" :class="item.severity" aria-hidden="true"></span>

          <div class="ad-body">
            <strong>{{ t(item.titleKey) }}</strong>
            <p v-if="item.detail" class="ad-detail"><code>{{ item.detail }}</code></p>
            <p v-if="actionState[item.id]" :class="['ad-result', actionState[item.id].kind]">
              {{ actionState[item.id].message }}
            </p>
          </div>

          <div class="row-actions ad-actions">
            <button
              v-if="hasQuickFix(item)"
              class="compact"
              :disabled="busyId === item.id"
              @click="applyFix(item)">
              {{ busyId === item.id ? t("panel.attention.fixing") : t("panel.attention.fix") }}
            </button>
            <button
              v-if="targetSection(item)"
              class="secondary compact"
              @click="goTo(item)">
              {{ t("panel.attention.goTo") }}
            </button>
          </div>
        </li>
      </ul>
    </aside>
  </div>
</template>

<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, reactive, ref, watch } from "vue";
import { useI18n } from "vue-i18n";
import { hasQuickFix, runAttentionAction, type AttentionItem, type AttentionSummary } from "../api/attention";
import { extractApiMessage } from "../stores/auth";

const props = defineProps<{
  open: boolean;
  summary: AttentionSummary | null;
  loading: boolean;
  loadError?: string;
  /** Kullanıcının rolünde gerçekten var olan bölümler — yoksa "Git" düğmesi gösterilmez. */
  allowedSections?: string[];
}>();

const emit = defineEmits<{
  (event: "close"): void;
  (event: "refresh"): void;
  (event: "navigate", sectionId: string): void;
}>();

const { t } = useI18n();

const panelRef = ref<HTMLElement | null>(null);
const closeRef = ref<HTMLElement | null>(null);
const busyId = ref("");
const actionState = reactive<Record<string, { kind: "ok" | "error"; message: string }>>({});
let previouslyFocused: HTMLElement | null = null;

// Backend zaten critical → warning → info sırasında döndürür; yine de sıralamayı burada
// garantiye alıyoruz (kaynak değişirse liste karışmasın).
const SEVERITY_RANK: Record<string, number> = { critical: 0, warning: 1, info: 2 };
const items = computed(() => [...(props.summary?.items ?? [])]
  .sort((left, right) => (SEVERITY_RANK[left.severity] ?? 2) - (SEVERITY_RANK[right.severity] ?? 2)));

// Üst menüde birebir karşılığı olan bölümler. Backend "dns"/"health"/"updates" gibi mantıksal
// bölümler de döndürebilir; onlar üst bileşende (handleNavigation) gerçek ekrana eşlenir.
const DIRECT_SECTIONS = new Set([
  "home", "sites", "accounts", "mail", "files", "database", "monitor",
  "advisor", "software", "tasks", "security", "tools", "migration", "account", "settings"
]);

function targetSection(item: AttentionItem): string {
  const section = (item.section ?? "").trim();
  if (!section) {
    return "";
  }

  if (!props.allowedSections || props.allowedSections.length === 0) {
    return section;
  }

  // Üst menüde birebir karşılığı olmayan bölümler ("dns", "health", "updates") üst bileşende
  // eşleniyor; burada onları engellemiyoruz.
  return props.allowedSections.includes(section) || !DIRECT_SECTIONS.has(section) ? section : "";
}

function goTo(item: AttentionItem) {
  const section = targetSection(item);
  if (!section) {
    return;
  }

  emit("navigate", section);
  emit("close");
}

async function applyFix(item: AttentionItem) {
  busyId.value = item.id;
  delete actionState[item.id];

  try {
    const message = await runAttentionAction(item);
    const translated = message ? t(message) : "";
    actionState[item.id] = {
      kind: "ok",
      message: translated && translated !== message ? translated : (message || t("panel.attention.fixed"))
    };
    emit("refresh");
  } catch (error) {
    const key = extractApiMessage(error);
    const translated = t(key);
    actionState[item.id] = {
      kind: "error",
      message: `${t("panel.attention.fixFailed")}: ${translated === key ? key : translated}`
    };
  } finally {
    busyId.value = "";
  }
}

function requestClose() {
  emit("close");
}

function onDocumentKeydown(event: KeyboardEvent) {
  if (event.key === "Escape" && props.open) {
    event.stopPropagation();
    requestClose();
  }
}

function focusableElements(): HTMLElement[] {
  const root = panelRef.value;
  if (!root) {
    return [];
  }

  return Array.from(root.querySelectorAll<HTMLElement>(
    'button:not([disabled]), a[href], input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])'
  )).filter((element) => element.offsetParent !== null || element === root);
}

// Odak tuzağı: Tab ile çekmecenin dışına çıkılmaz (ekran okuyucu/klavye kullanıcısı arka planda kaybolmasın).
function trapFocus(event: KeyboardEvent) {
  const focusable = focusableElements();
  if (focusable.length === 0) {
    return;
  }

  const first = focusable[0];
  const last = focusable[focusable.length - 1];
  const active = document.activeElement as HTMLElement | null;

  if (event.shiftKey && (active === first || active === panelRef.value)) {
    event.preventDefault();
    last.focus();
    return;
  }

  if (!event.shiftKey && active === last) {
    event.preventDefault();
    first.focus();
  }
}

// Arka plan kaydırma kilidi: telefonda çekmece alt yaprak gibi tam ekran açıldığı için
// arkadaki sayfa kaymamalı. MainLayout ile aynı sayacı kullanır (iki katman üst üste gelirse
// biri kapanınca kilit erken açılmasın).
function lockBodyScroll(lock: boolean) {
  const root = document.documentElement;
  const next = Math.max(0, Number(root.dataset.apScrollLock ?? "0") + (lock ? 1 : -1));
  root.dataset.apScrollLock = String(next);
  root.classList.toggle("ap-scroll-lock", next > 0);
}

let scrollLocked = false;

function setScrollLock(lock: boolean) {
  if (lock === scrollLocked) {
    return;
  }

  scrollLocked = lock;
  lockBodyScroll(lock);
}

watch(() => props.open, async (isOpen) => {
  if (isOpen) {
    previouslyFocused = document.activeElement as HTMLElement | null;
    document.addEventListener("keydown", onDocumentKeydown, true);
    setScrollLock(true);
    await nextTick();
    (closeRef.value ?? panelRef.value)?.focus();
    return;
  }

  document.removeEventListener("keydown", onDocumentKeydown, true);
  setScrollLock(false);
  for (const key of Object.keys(actionState)) {
    delete actionState[key];
  }
  previouslyFocused?.focus?.();
  previouslyFocused = null;
}, { immediate: true });

onBeforeUnmount(() => {
  document.removeEventListener("keydown", onDocumentKeydown, true);
  setScrollLock(false);
});

function formatUtc(value: string): string {
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? value : date.toLocaleString();
}
</script>

<style scoped>
.attention-scrim {
  position: fixed;
  inset: 0;
  z-index: 60;
  display: flex;
  justify-content: flex-end;
  background: rgba(15, 23, 42, 0.34);
}

.attention-drawer {
  display: grid;
  align-content: start;
  gap: 12px;
  width: min(460px, 100vw);
  height: 100%;
  padding: 16px;
  overflow-y: auto;
  border-left: 1px solid var(--panel-border);
  background: var(--surface-0);
  box-shadow: -18px 0 48px rgba(0, 0, 0, 0.28);
}

.attention-drawer:focus {
  outline: none;
}

.ad-head {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
}

.ad-head h2 {
  margin: 0;
  font-size: 1.02rem;
}

.ad-head-copy .meta {
  margin: 2px 0 0;
  font-size: 0.78rem;
}

.ad-counters {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
}

.ad-checked {
  margin-left: auto;
  font-size: 0.72rem;
}

.ad-empty {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 16px;
  border: 1px solid color-mix(in srgb, var(--ok-1) 40%, var(--panel-border));
  border-radius: var(--radius-md);
  background: color-mix(in srgb, var(--ok-1) 8%, var(--surface-1));
}

.ad-empty p {
  margin: 2px 0 0;
}

.ad-empty-badge {
  display: grid;
  place-items: center;
  width: 34px;
  height: 34px;
  flex-shrink: 0;
  border-radius: 50%;
  background: color-mix(in srgb, var(--ok-1) 20%, transparent);
  color: var(--ok-1);
  font-size: 1.1rem;
  font-weight: 800;
}

.ad-list {
  display: grid;
  gap: 8px;
  margin: 0;
  padding: 0;
  list-style: none;
}

.ad-item {
  display: flex;
  align-items: flex-start;
  gap: 10px;
  padding: 12px;
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-md);
  background: var(--surface-1);
}

.ad-item.critical {
  border-color: color-mix(in srgb, var(--danger-1) 45%, var(--panel-border));
}

.ad-item.warning {
  border-color: color-mix(in srgb, var(--warn-1) 40%, var(--panel-border));
}

.ad-dot {
  width: 9px;
  height: 9px;
  margin-top: 6px;
  flex-shrink: 0;
  border-radius: 50%;
  background: var(--text-muted);
}

.ad-dot.critical {
  background: var(--danger-1);
}

.ad-dot.warning {
  background: var(--warn-1);
}

.ad-body {
  display: grid;
  gap: 3px;
  flex: 1;
  min-width: 0;
}

.ad-body strong {
  font-size: 0.86rem;
}

.ad-detail {
  margin: 0;
  color: var(--text-soft);
  font-size: 0.78rem;
  word-break: break-word;
}

.ad-detail code {
  font-family: ui-monospace, "Cascadia Code", Consolas, monospace;
}

.ad-result {
  margin: 0;
  font-size: 0.76rem;
  font-weight: 600;
}

.ad-result.ok {
  color: var(--ok-1);
}

.ad-result.error {
  color: var(--danger-1);
}

.ad-actions {
  display: flex;
  flex-direction: column;
  gap: 6px;
  flex-shrink: 0;
}

/*
  Dar ekran: çekmece sağdan değil AŞAĞIDAN açılan yaprak gibi davranır — telefonda
  başparmağın eriştiği bölge alt yarıdır, "Onar"/"Git" düğmeleri oraya düşer.
  Odak tuzağı, Escape ve kapatma davranışı JS tarafında; burada yalnız yerleşim var.
*/
@media (max-width: 620px) {
  .attention-scrim {
    flex-direction: column;
    justify-content: flex-end;
  }

  .attention-drawer {
    /* Yapışkan başlık için ızgara yerine esnek sütun: ızgara öğesinin sabitlenme alanı
       kendi satırıyla sınırlı olduğundan position: sticky orada çalışmaz. */
    display: flex;
    flex-direction: column;
    width: 100%;
    height: auto;
    max-height: 92dvh;
    padding: 0 12px calc(14px + env(safe-area-inset-bottom));
    border-left: 0;
    border-top: 1px solid var(--panel-border);
    border-radius: var(--radius-lg) var(--radius-lg) 0 0;
    box-shadow: 0 -18px 48px rgba(0, 0, 0, 0.32);
  }

  .attention-drawer > * {
    flex-shrink: 0;
  }

  /* Başlık (ve Kapat düğmesi) liste kaydırılırken de elde kalsın. */
  .ad-head {
    position: sticky;
    top: 0;
    z-index: 1;
    margin: 0 -12px 0;
    padding: 12px 12px 8px;
    background: var(--surface-0);
  }

  .ad-checked {
    margin-left: 0;
    flex: 1 1 100%;
  }

  .ad-item {
    flex-wrap: wrap;
    padding: 12px 12px 10px;
  }

  /* Eylemler tam genişlikte ve parmakla basılabilir: en kritik iki düğme bunlar. */
  .ad-actions {
    flex-direction: row;
    width: 100%;
    margin-top: 4px;
  }

  .ad-actions button {
    flex: 1 1 0;
    min-height: 42px;
  }

  .ad-head .row-actions button {
    min-height: 40px;
  }
}
</style>
