<template>
  <Teleport to="body">
    <div
      v-if="state.open && state.options"
      class="confirm-backdrop"
      @mousedown.self="dismiss">
      <div
        ref="dialogEl"
        class="confirm-dialog form-surface"
        role="alertdialog"
        aria-modal="true"
        tabindex="-1"
        :aria-labelledby="titleId"
        :aria-describedby="messageId"
        @keydown.esc.prevent="dismiss"
        @keydown.tab="trapFocus">
        <header class="confirm-head">
          <span v-if="tone === 'danger'" class="status-pill danger">{{ t("panel.confirm.badgeDanger") }}</span>
          <h3 :id="titleId">{{ state.options.title }}</h3>
        </header>

        <p :id="messageId" class="confirm-message">{{ state.options.message }}</p>

        <section v-if="impact.length" class="confirm-impact">
          <span class="field-label">{{ t("panel.confirm.impactTitle") }}</span>
          <ul>
            <li v-for="(item, index) in impact" :key="index">{{ item }}</li>
          </ul>
        </section>

        <p v-if="tone === 'danger'" class="confirm-irreversible">{{ t("panel.confirm.irreversible") }}</p>

        <label v-if="highRisk" class="stacked-field">
          <span class="field-label">{{ t("panel.confirm.typeToConfirm", { name: requireText }) }}</span>
          <input
            ref="inputEl"
            v-model="typed"
            :disabled="state.busy"
            :placeholder="requireText"
            spellcheck="false"
            autocapitalize="off"
            autocomplete="off"
            @keyup.enter="accept" />
        </label>

        <div class="confirm-actions row-actions">
          <button ref="cancelEl" type="button" class="secondary" :disabled="state.busy" @click="dismiss">
            {{ state.options.cancelLabel || t("panel.common.cancel") }}
          </button>
          <button
            ref="confirmEl"
            type="button"
            :class="tone === 'danger' ? 'danger' : ''"
            :disabled="!canConfirm"
            @click="accept">
            {{ confirmLabel }}
          </button>
        </div>
      </div>
    </div>
  </Teleport>
</template>

<script lang="ts">
import { reactive } from "vue";

export interface ConfirmOptions {
  /** Diyalog başlığı (ör. "Site silinsin mi?"). */
  title: string;
  /** Ne olacağını anlatan tek paragraf. */
  message: string;
  /** Etkilenecekler listesi — ÇAĞIRAN ekran doldurur (ör. "3 site", "2 veritabanı", "480 MB dosya"). */
  impact?: string[];
  /** Yüksek riskli mod: kullanıcı devam etmek için bu metni (site/kullanıcı adı) birebir yazmalı. */
  requireText?: string;
  /** Varsayılan: panel.common.delete (danger) / panel.confirm.continue (neutral). */
  confirmLabel?: string;
  cancelLabel?: string;
  /** İşlem sürerken düğmede görünen metin. Varsayılan: panel.confirm.deleting (danger) / working (neutral). */
  busyLabel?: string;
  /** "danger" (varsayılan) kırmızı onay düğmesi + "geri alınamaz" uyarısı gösterir. */
  tone?: "danger" | "neutral";
  /**
   * Verilirse diyalog açık kalır, onay düğmesi pasifleşip "siliniyor…" durumuna geçer ve
   * action bitene kadar bekler. Hata raporlaması ÇAĞIRANA aittir (kendi try/catch'i).
   */
  action?: () => Promise<unknown>;
}

interface ConfirmState {
  open: boolean;
  busy: boolean;
  options: ConfirmOptions | null;
}

// Tek merkezi diyalog: <ConfirmDialog /> App.vue'da bir kez mount edilir, her ekran useConfirm() ile çağırır.
export const dialogState = reactive<ConfirmState>({ open: false, busy: false, options: null });
let pending: ((value: boolean) => void) | null = null;

function ask(options: ConfirmOptions): Promise<boolean> {
  if (dialogState.open) {
    // Aynı anda ikinci bir onay istenmez; sessizce reddet (yıkıcı işlem başlamasın).
    return Promise.resolve(false);
  }
  dialogState.options = options;
  dialogState.busy = false;
  dialogState.open = true;
  return new Promise<boolean>((resolve) => {
    pending = resolve;
  });
}

function settle(value: boolean) {
  dialogState.open = false;
  dialogState.busy = false;
  dialogState.options = null;
  const resolve = pending;
  pending = null;
  resolve?.(value);
}

/**
 * Kullanım:
 *   const { confirm } = useConfirm();
 *   if (!(await confirm({ title, message, impact }))) return;
 */
export function useConfirm() {
  return { confirm: ask };
}
</script>

<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref, watch } from "vue";
import { useI18n } from "vue-i18n";

const { t } = useI18n();

// Modül kapsamındaki durum şablona ancak <script setup> bağlaması üzerinden görünür.
const state = dialogState;

const dialogEl = ref<HTMLElement | null>(null);
const inputEl = ref<HTMLInputElement | null>(null);
const cancelEl = ref<HTMLButtonElement | null>(null);
const confirmEl = ref<HTMLButtonElement | null>(null);
const typed = ref("");
let lastFocused: HTMLElement | null = null;

const titleId = "confirm-dialog-title";
const messageId = "confirm-dialog-message";

const tone = computed(() => state.options?.tone ?? "danger");
const impact = computed(() => state.options?.impact ?? []);
const requireText = computed(() => (state.options?.requireText ?? "").trim());
const highRisk = computed(() => requireText.value.length > 0);

const canConfirm = computed(() => {
  if (state.busy) {
    return false;
  }
  return !highRisk.value || typed.value.trim() === requireText.value;
});

const confirmLabel = computed(() => {
  const options = state.options;
  if (!options) {
    return "";
  }
  if (state.busy) {
    return options.busyLabel || t(tone.value === "danger" ? "panel.confirm.deleting" : "panel.confirm.working");
  }
  return options.confirmLabel || t(tone.value === "danger" ? "panel.common.delete" : "panel.confirm.continue");
});

watch(
  () => state.open,
  async (open) => {
    if (open) {
      typed.value = "";
      lastFocused = document.activeElement instanceof HTMLElement ? document.activeElement : null;
      document.body.style.overflow = "hidden";
      await nextTick();
      // Odak diyaloga girer. Yüksek riskte doğrudan metin kutusuna; normalde kapsayıcıya
      // (silme düğmesine DEĞİL — refleksle Enter'a basılmasın).
      if (highRisk.value && inputEl.value) {
        inputEl.value.focus();
      } else {
        dialogEl.value?.focus();
      }
      return;
    }
    document.body.style.overflow = "";
    lastFocused?.focus();
    lastFocused = null;
  }
);

onBeforeUnmount(() => {
  document.body.style.overflow = "";
});

function dismiss() {
  if (state.busy) {
    return;
  }
  settle(false);
}

async function accept() {
  const options = state.options;
  if (!options || !canConfirm.value) {
    return;
  }
  if (!options.action) {
    settle(true);
    return;
  }
  state.busy = true;
  try {
    await options.action();
  } catch (error) {
    // Çağıran ekran kendi notice'ını gösterir; burada yutulur ki promise reddi kaçmasın.
    console.error(error);
  } finally {
    settle(true);
  }
}

function focusables(): HTMLElement[] {
  const candidates: (HTMLInputElement | HTMLButtonElement | null)[] = [inputEl.value, cancelEl.value, confirmEl.value];
  const result: HTMLElement[] = [];
  for (const element of candidates) {
    if (element && !element.disabled) {
      result.push(element);
    }
  }
  return result;
}

// Odak diyalogda hapsolur: son öğeden Tab başa, ilk öğeden Shift+Tab sona döner.
function trapFocus(event: KeyboardEvent) {
  const items = focusables();
  if (!items.length) {
    event.preventDefault();
    dialogEl.value?.focus();
    return;
  }
  const current = document.activeElement as HTMLElement | null;
  const index = current ? items.indexOf(current) : -1;
  if (event.shiftKey) {
    if (index <= 0) {
      event.preventDefault();
      items[items.length - 1].focus();
    }
    return;
  }
  if (index === -1 || index === items.length - 1) {
    event.preventDefault();
    items[0].focus();
  }
}
</script>

<style scoped>
.confirm-backdrop {
  position: fixed;
  inset: 0;
  z-index: 1200;
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 20px;
  background: rgba(6, 9, 14, 0.62);
  backdrop-filter: blur(2px);
}

.confirm-dialog {
  width: min(520px, 100%);
  max-height: calc(100vh - 40px);
  overflow-y: auto;
  padding: 16px 18px;
  border-radius: var(--radius-lg);
  background: var(--surface-1);
  box-shadow: var(--shadow-elev);
}

.confirm-head {
  display: grid;
  gap: 7px;
}

.confirm-head h3 {
  margin: 0;
  font-size: 1.05rem;
  font-weight: 660;
  line-height: 1.25;
  color: var(--text-strong);
}

.confirm-message {
  margin: 0;
  color: var(--text-soft);
  white-space: pre-wrap;
  word-break: break-word;
}

.confirm-impact {
  display: grid;
  gap: 6px;
  padding: 10px 12px;
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-sm);
  background: var(--surface-2);
}

.confirm-impact ul {
  margin: 0;
  padding-left: 18px;
  display: grid;
  gap: 3px;
  color: var(--text-soft);
}

.confirm-impact li {
  word-break: break-word;
}

.confirm-irreversible {
  margin: 0;
  color: var(--danger-text);
  font-size: 0.86rem;
  font-weight: 600;
}

.confirm-actions {
  justify-content: flex-end;
}

.confirm-actions button {
  width: auto;
  min-width: 108px;
}

@media (max-width: 520px) {
  .confirm-actions {
    justify-content: stretch;
  }

  .confirm-actions button {
    flex: 1 1 auto;
  }
}
</style>
