<template>
  <!-- Bölüm paketi (chunk) indirilirken görünen ara durum. Boş ekran "veri yok" sanılıyordu. -->
  <p class="panel module-shell empty-state view-loading" role="status" aria-live="polite">
    {{ t("panel.common.loading") }}
  </p>
</template>

<script setup lang="ts">
import { useI18n } from "vue-i18n";

const { t } = useI18n();
</script>

<style scoped>
.view-loading {
  display: grid;
  place-content: center;
  min-height: 160px;
  margin: 0;
}
</style>
