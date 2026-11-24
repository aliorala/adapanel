<template>
  <p v-if="installed === false" class="notice error av-no-protection" role="alert">{{ t("panel.antivirus.noProtection") }}</p>
</template>

<script setup lang="ts">
import { useI18n } from "vue-i18n";

// Antivirüs kurulu değilken "koruma yok" uyarısı. installed === null (durum bilinmiyor)
// uyarı göstermez: bilinmeyen durumu ne "korunuyor" ne "korunmuyor" diye ilan ederiz.
defineProps<{ installed: boolean | null }>();
const { t } = useI18n();
</script>
