<template>
  <Teleport to="body">
    <div
      v-if="state.open && state.options"
      class="prompt-backdrop"
      @mousedown.self="dismiss">
      <div
        ref="dialogEl"
        class="prompt-dialog form-surface"
        role="dialog"
        aria-modal="true"
        tabindex="-1"
        :aria-labelledby="titleId"
        @keydown.esc.prevent="dismiss"
        @keydown.tab="trapFocus">
        <header class="prompt-head">
          <h3 :id="titleId">{{ state.options.title }}</h3>
        </header>

        <p v-if="state.options.message" class="prompt-message">{{ state.options.message }}</p>

        <label class="stacked-field">
          <span class="field-label">{{ state.options.label || state.options.title }}</span>
          <span class="prompt-input-row">
            <input
              ref="inputEl"
              v-model="value"
              :type="inputType"
              :placeholder="state.options.placeholder || ''"
              :minlength="state.options.minLength || undefined"
              spellcheck="false"
              autocapitalize="off"
              autocomplete="off"
              @keyup.enter="accept" />
            <!-- Parolayı GÖRMEK bir seçim olmalı: window.prompt'ta parola her zaman açıktı ve
                 omuz üstünden okunabiliyordu. Varsayılan gizli, isteyen açar. -->
            <button
              v-if="state.options.password"
              type="button"
              class="ghost compact"
              :aria-pressed="revealed"
              @click="revealed = !revealed">
              {{ t(revealed ? "panel.prompt.hide" : "panel.prompt.show") }}
            </button>
          </span>
          <small v-if="state.options.hint" class="field-hint">{{ state.options.hint }}</small>
          <!-- Kural ÖNDEN söylenir; kullanıcı "kaydet"e bastıktan sonra sunucudan öğrenmesin. -->
          <small v-else-if="state.options.minLength" class="field-hint">
            {{ t("panel.prompt.minLength", { count: state.options.minLength }) }}
          </small>
        </label>

        <div class="prompt-actions row-actions">
          <button ref="cancelEl" type="button" class="secondary" @click="dismiss">
            {{ t("panel.common.cancel") }}
          </button>
          <button ref="okEl" type="button" :disabled="!canAccept" @click="accept">
            {{ state.options.confirmLabel || t("panel.confirm.continue") }}
          </button>
        </div>
      </div>
    </div>
  </Teleport>
</template>

<script lang="ts">
import { reactive } from "vue";

export interface PromptOptions {
  /** Diyalog başlığı (ör. "Yeni parola"). */
  title: string;
  /** Ne için istendiğini anlatan tek cümle. */
  message?: string;
  /** Alan etiketi; verilmezse başlık kullanılır. */
  label?: string;
  /** Parola modu: değer gizli yazılır, "göster" düğmesi çıkar. */
  password?: boolean;
  /** Başlangıç değeri (metin düzenlemede mevcut değeri göstermek için). */
  defaultValue?: string;
  placeholder?: string;
  /** Asgari uzunluk; ekranda ÖNDEN yazılır ve düğme o uzunluğa kadar pasif kalır. */
  minLength?: number;
  hint?: string;
  confirmLabel?: string;
}

interface PromptState {
  open: boolean;
  options: PromptOptions | null;
}

/**
 * Panel geneli tek metin/parola diyaloğu — `window.prompt` yerine.
 *
 * NEDEN: `window.prompt` parolayı DÜZ METİN gösterir (omuz üstünden okunur, ekran paylaşımında
 * görünür, tarayıcı geçmişine/otomatik doldurmaya karışır) ve tarayıcı "bu site diyalog
 * göstermesin" seçeneğini işaretlediğinde SESSİZCE null döner — o anda akış hiçbir açıklama
 * olmadan ölür. Panelde parola değiştirme, kota girme gibi işlemler tam olarak böyle
 * kırılabiliyordu.
 */
export const promptState = reactive<PromptState>({ open: false, options: null });
let pending: ((value: string | null) => void) | null = null;

function ask(options: PromptOptions): Promise<string | null> {
  promptState.options = options;
  promptState.open = true;
  return new Promise((resolve) => {
    pending = resolve;
  });
}

/**
 * @example
 *   const { prompt } = usePrompt();
 *   const parola = await prompt({ title: "Yeni parola", password: true, minLength: 12 });
 *   if (!parola) return; // iptal
 */
export function usePrompt() {
  return { prompt: ask };
}
</script>

<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref, watch } from "vue";
import { useI18n } from "vue-i18n";

const { t } = useI18n();
const state = promptState;

const value = ref("");
const revealed = ref(false);
const dialogEl = ref<HTMLElement | null>(null);
const inputEl = ref<HTMLInputElement | null>(null);
const cancelEl = ref<HTMLButtonElement | null>(null);
const okEl = ref<HTMLButtonElement | null>(null);
let lastFocused: HTMLElement | null = null;

const titleId = "prompt-dialog-title";
const inputType = computed(() => (state.options?.password && !revealed.value ? "password" : "text"));
const canAccept = computed(() => {
  const min = state.options?.minLength ?? 1;
  return value.value.trim().length >= Math.max(1, min);
});

watch(
  () => state.open,
  async (open) => {
    if (open) {
      value.value = state.options?.defaultValue ?? "";
      revealed.value = false;
      lastFocused = document.activeElement instanceof HTMLElement ? document.activeElement : null;
      document.body.style.overflow = "hidden";
      await nextTick();
      inputEl.value?.focus();
      inputEl.value?.select();
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

function settle(result: string | null) {
  state.open = false;
  state.options = null;
  // Parola bellekte ASILI KALMAZ: diyalog kapanınca alan temizlenir.
  value.value = "";
  revealed.value = false;
  pending?.(result);
  pending = null;
}

function dismiss() {
  settle(null);
}

function accept() {
  if (!canAccept.value) {
    return;
  }

  settle(value.value);
}

function focusables(): HTMLElement[] {
  const candidates: (HTMLInputElement | HTMLButtonElement | null)[] = [inputEl.value, cancelEl.value, okEl.value];
  return candidates.filter((element): element is HTMLInputElement | HTMLButtonElement => Boolean(element) && !element!.disabled);
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
.prompt-backdrop {
  position: fixed;
  inset: 0;
  display: grid;
  place-items: center;
  padding: 1rem;
  background: rgba(0, 0, 0, 0.45);
  z-index: 1000;
}

.prompt-dialog {
  width: min(30rem, 100%);
  display: grid;
  gap: 0.9rem;
  max-height: 90vh;
  overflow: auto;
}

.prompt-head h3 {
  margin: 0;
  font-size: 1.05rem;
}

.prompt-message {
  margin: 0;
  font-size: 0.9rem;
  color: var(--text-muted);
  line-height: 1.5;
}

.prompt-input-row {
  display: flex;
  align-items: center;
  gap: 0.5rem;
}

.prompt-input-row input {
  flex: 1;
  min-width: 0;
}

.prompt-actions {
  display: flex;
  justify-content: flex-end;
  gap: 0.5rem;
}
</style>
