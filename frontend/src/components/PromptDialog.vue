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
          <!-- Parolayı GÖRMEK bir seçim olmalı: window.prompt'ta parola her zaman açıktı ve
               omuz üstünden okunabiliyordu. Varsayılan gizli, isteyen açar. Bu davranış artık
               PasswordField'da TEK yerde duruyor ve panelin bütün parola alanları onu kullanıyor;
               diyalog da kendi kopyasını taşımaz. -->
          <PasswordField
            ref="inputEl"
            v-model="value"
            :password="Boolean(state.options.password)"
            :placeholder="state.options.placeholder || ''"
            :minlength="state.options.minLength || undefined"
            autocomplete="off"
            @keyup.enter="accept" />
          <small v-if="state.options.hint" class="field-hint">{{ state.options.hint }}</small>
          <!-- Kural ÖNDEN söylenir; kullanıcı "kaydet"e bastıktan sonra sunucudan öğrenmesin. -->
          <small v-else-if="state.options.minLength" class="field-hint">
            {{ t("panel.prompt.minLength", { count: state.options.minLength }) }}
          </small>
        </label>

        <div class="prompt-actions row-actions">
          <button type="button" class="secondary" @click="dismiss">
            {{ t("panel.common.cancel") }}
          </button>
          <button type="button" :disabled="!canAccept" @click="accept">
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
  /**
   * Boş değer gönderilebilsin mi.
   * <p>
   * `window.prompt` boş dizeye izin veriyordu ve bazı akışlar buna DAYANIYOR: posta dağıtım
   * listesinin alıcılarını temizlemenin tek yolu alanı boşaltmaktı. Varsayılan olarak boş
   * değer kapalıdır (parola alanları için doğrusu budur), ama bu akışlar için açılabilir.
   * </p>
   */
  allowEmpty?: boolean;
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
  // YENIDEN GIRIS KORUMASI: ikinci bir çağrı `pending`'i ezerse ilk promise HİÇ çözülmez ve
  // onu bekleyen fonksiyon sonsuza kadar asılı kalır. Aynı koruma ConfirmDialog'da da var.
  if (promptState.open) {
    return Promise.resolve(null);
  }

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
export function closePromptDialog() {
  promptState.open = false;
  promptState.options = null;
  pending?.(null);
  pending = null;
}

export function usePrompt() {
  return { prompt: ask };
}
</script>

<script setup lang="ts">
import { computed, nextTick, ref, watch } from "vue";
import { useI18n } from "vue-i18n";
import { useScrollLock } from "../composables/useScrollLock";

import PasswordField from "./PasswordField.vue";

const { t } = useI18n();
const state = promptState;
useScrollLock(() => state.open);

const value = ref("");
const dialogEl = ref<HTMLElement | null>(null);
const inputEl = ref<InstanceType<typeof PasswordField> | null>(null);
let lastFocused: HTMLElement | null = null;

const titleId = "prompt-dialog-title";
const canAccept = computed(() => {
  if (state.options?.allowEmpty && !state.options?.minLength) {
    return true;
  }

  const min = state.options?.minLength ?? 1;
  return value.value.trim().length >= Math.max(1, min);
});

watch(
  () => state.open,
  async (open) => {
    if (open) {
      value.value = state.options?.defaultValue ?? "";
      lastFocused = document.activeElement instanceof HTMLElement ? document.activeElement : null;
      await nextTick();
      inputEl.value?.focus();
      inputEl.value?.select();
      return;
    }

    value.value = "";
    lastFocused?.focus();
    lastFocused = null;
  }
);

function settle(result: string | null) {
  state.open = false;
  state.options = null;
  // Parola bellekte ASILI KALMAZ: diyalog kapanınca alan temizlenir. "Göster" durumu da
  // kalmaz: PasswordField diyalogla birlikte sökülür (v-if), yani her açılış GİZLİ başlar.
  value.value = "";
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

/**
 * Odak tuzağındaki öğeler DOM'dan okunur, elle sayılmaz.
 *
 * NEDEN: liste elle tutulduğunda "göster/gizle" düğmesi listede YOKTU; ondan Tab'a basınca
 * `index === -1` dalına düşülüyor ve odak başa atılıyordu — yani düğmeye klavyeyle gidilebiliyor
 * ama oradan ileri ÇIKILAMIYORDU. Diyaloğa yeni bir düğme eklendiğinde aynı hatanın tekrar
 * doğmaması için sıra artık DOM sırasıdır.
 */
function focusables(): HTMLElement[] {
  const root = dialogEl.value;
  if (!root) {
    return [];
  }

  return Array.from(root.querySelectorAll<HTMLInputElement | HTMLButtonElement>("input, button")).filter(
    (element) => !element.disabled
  );
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
  /* Onay diyaloğunun (1200) ÜSTÜNDE: bir onay akışı içinden değer sorulursa, sorulan şey
     arkada kalıp kullanılamaz hale gelmemeli. */
  z-index: 1300;
}

.prompt-dialog {
  width: min(30rem, 100%);
  min-width: 0;
  display: grid;
  gap: 0.9rem;
  max-height: 90vh;
  max-height: 90dvh;
  overflow: auto;
  overflow-wrap: anywhere;
  padding: 16px 18px;
  border-radius: var(--radius-lg);
  background: var(--surface-1);
  box-shadow: var(--shadow-elev);
}

.prompt-dialog > * { min-width: 0; }

/* BOY KURALI DUSTU: `1.05rem` = 14.18px, govde metninden (13.5px) 0.68px farkliydi —
   yani bir DIYALOGUN basligi, altindaki cumleyle neredeyse ayni agirlikta okunuyordu.
   Kapsamli oldugu icin ortak baslik merdivenini de eziyordu. Kural dusunce
   `base.css`teki `h3` adimi gelir: 15.93px. */
.prompt-head h3 {
  margin: 0;
}

.prompt-message {
  margin: 0;
  font-size: 0.9rem;
  color: var(--text-muted);
  line-height: 1.5;
}

.prompt-actions {
  display: flex;
  justify-content: flex-end;
  gap: 0.5rem;
}
</style>
