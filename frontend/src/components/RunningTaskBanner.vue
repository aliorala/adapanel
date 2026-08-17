<template>
  <!--
    "SÜRÜYOR" şeridi — TEK KAYNAK (bkz. composables/useRunningTask.ts).

    `role="status"` + `aria-live="polite"`: ekran okuyucu işin başladığını ve geçen süreyi
    duyar. Şerit ancak iş GERÇEKTEN sürerken çizilir; "belki sürüyor" diye asılı kalmaz.
  -->
  <p v-if="running" class="notice running-task" role="status" aria-live="polite">
    <span class="rt-spinner" aria-hidden="true"></span>
    <strong class="rt-label">{{ label }}</strong>
    <span class="meta rt-elapsed">{{ t("panel.running.elapsed", { time: elapsedText }) }}</span>
    <span class="meta rt-hint">{{ hint || t("panel.running.hint") }}</span>
  </p>
</template>

<script setup lang="ts">
import { useI18n } from "vue-i18n";

defineProps<{
  running: boolean;
  /** Ne sürüyor (çevrilmiş metin) — ör. "Site yedeği alınıyor: ornek.com". */
  label: string;
  /** Geçen süre, "m:ss" (useRunningTask.elapsedText). */
  elapsedText: string;
  /** Ekrana özel ek açıklama; boşsa genel "sayfadan ayrılmayın" ipucu yazılır. */
  hint?: string;
}>();

const { t } = useI18n();
</script>

<style scoped>
.running-task {
  display: flex;
  align-items: center;
  gap: 10px;
  flex-wrap: wrap;
}

.rt-label {
  min-width: 0;
  overflow-wrap: anywhere;
}

.rt-elapsed {
  font-variant-numeric: tabular-nums;
  font-weight: 700;
}

.rt-hint {
  flex: 1 1 100%;
  font-size: 0.78rem;
}

.rt-spinner {
  width: 14px;
  height: 14px;
  flex-shrink: 0;
  border: 2px solid color-mix(in srgb, var(--accent-1) 30%, transparent);
  border-top-color: var(--accent-1);
  border-radius: 50%;
  animation: rt-spin 0.9s linear infinite;
}

@keyframes rt-spin {
  to {
    transform: rotate(360deg);
  }
}

/* Hareketi azalt tercihinde döner halka DURUR ama şerit yine görünür kalır: durumu yalnız
   animasyona bağlamak, animasyonu kapatan kullanıcıya hiçbir şey söylememek olurdu. */
@media (prefers-reduced-motion: reduce) {
  .rt-spinner {
    animation: none;
    border-top-color: color-mix(in srgb, var(--accent-1) 60%, transparent);
  }
}
</style>
