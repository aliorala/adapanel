<template>
  <SetupWizardView />
  <!-- Tüm panelde ortak yıkıcı-işlem onayı (window.confirm yerine). -->
  <ConfirmDialog />
  <!-- Panel geneli metin/parola diyalogu (window.prompt yerine). -->
  <PromptDialog />
</template>

<script setup lang="ts">
import ConfirmDialog from "./components/ConfirmDialog.vue";
import PromptDialog from "./components/PromptDialog.vue";
import SetupWizardView from "./views/SetupWizardView.vue";
</script>
