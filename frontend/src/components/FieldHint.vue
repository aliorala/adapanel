<template>
  <span ref="rootEl" class="field-hint-anchor" @keydown.esc.stop="close">
    <button
      type="button"
      class="field-hint-toggle"
      :aria-label="ariaLabel"
      :aria-expanded="open ? 'true' : 'false'"
      :aria-describedby="open ? popoverId : undefined"
      @click.stop.prevent="toggle"
      @mouseenter="hovering = true"
      @mouseleave="hovering = false"
      @focus="onFocus"
      @blur="pinned = false">?</button>
    <span v-if="open" :id="popoverId" role="tooltip" :class="['field-hint-popover', align]">
      <slot>{{ text }}</slot>
    </span>
  </span>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, ref, useId, watch } from "vue";

/**
 * Alan etiketinin yanına konan küçük "?" ipucu. Uzun açıklamaları formdan çıkarıp
 * istendiğinde açılan küçük bir popover'a taşır.
 * Erişilebilirlik: klavyeyle odaklanınca açılır, Escape ile kapanır; dokunmatikte tek dokunuş açar/kapatır.
 */
const props = withDefaults(defineProps<{
  /** Popover metni. Slot verilirse slot içeriği kullanılır. */
  text?: string;
  /** Ekran okuyucu için düğme adı — genelde alanın etiketi. */
  label?: string;
  /** Popover'ın hizası; sağ kenara yakın alanlarda "end" kullanın. */
  align?: "start" | "end";
}>(), { text: "", label: "", align: "start" });

const popoverId = `field-hint-${useId()}`;
const rootEl = ref<HTMLElement | null>(null);
// pinned: tıklama/klavye odağıyla sabitlendi, hovering: fare üzerinde.
const pinned = ref(false);
const hovering = ref(false);
const open = computed(() => pinned.value || hovering.value);
const ariaLabel = computed(() => props.label || props.text || "?");

function toggle() {
  if (open.value) close();
  else pinned.value = true;
}

function close() {
  pinned.value = false;
  hovering.value = false;
}

// Fareyle tıklarken de focus olayı gelir; yalnız klavye odağında (focus-visible) sabitliyoruz,
// aksi halde tıklama "aç" + focus "aç" çakışıp kapatmayı imkânsız kılardı.
function onFocus(event: FocusEvent) {
  const target = event.target as HTMLElement | null;
  if (!target) return;
  try {
    if (target.matches(":focus-visible")) pinned.value = true;
  } catch {
    pinned.value = true;
  }
}

function onDocumentPointerDown(event: Event) {
  if (!rootEl.value?.contains(event.target as Node)) close();
}

watch(open, (value) => {
  if (value) document.addEventListener("pointerdown", onDocumentPointerDown, true);
  else document.removeEventListener("pointerdown", onDocumentPointerDown, true);
});

onBeforeUnmount(() => document.removeEventListener("pointerdown", onDocumentPointerDown, true));
</script>

<style scoped>
.field-hint-anchor {
  position: relative;
  display: inline-flex;
  vertical-align: middle;
  margin-inline-start: 5px;
}

/* Global "button" gradyanını/gölgesini MİRAS ALMAZ: sade, etiket yanında duran küçük daire. */
.field-hint-anchor .field-hint-toggle {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 17px;
  height: 17px;
  min-height: 0;
  padding: 0;
  border: 1px solid var(--panel-border);
  border-radius: 50%;
  background: var(--surface-1);
  color: var(--text-muted);
  /* 0.72rem = 9.72px idi — olcek tabani `--fs-2xs` (10.53px); altina inen kural yok. */
  font-size: var(--fs-2xs);
  font-weight: 700;
  line-height: 1;
  box-shadow: none;
  transform: none;
  filter: none;
}

.field-hint-anchor .field-hint-toggle:not(:disabled):hover,
.field-hint-anchor .field-hint-toggle[aria-expanded="true"] {
  border-color: color-mix(in srgb, var(--accent-1) 55%, transparent);
  color: var(--text-strong);
  background: var(--surface-1);
  transform: none;
  filter: none;
  box-shadow: none;
}

.field-hint-anchor .field-hint-toggle:focus-visible {
  outline: 2px solid color-mix(in srgb, var(--accent-1) 70%, transparent);
  outline-offset: 2px;
}

.field-hint-popover {
  position: absolute;
  top: calc(100% + 6px);
  z-index: 60;
  width: max-content;
  max-width: min(280px, 70vw);
  padding: 8px 10px;
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-sm);
  background: var(--surface-1);
  box-shadow: var(--shadow-elev);
  color: var(--text-muted);
  font-size: 0.82rem;
  font-weight: 400;
  line-height: 1.45;
  text-align: start;
  white-space: normal;
}

.field-hint-popover.start {
  left: 0;
}

.field-hint-popover.end {
  right: 0;
}
</style>
