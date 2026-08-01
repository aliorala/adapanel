<template>
  <SetupWizardView />
  <!-- Tüm panelde ortak yıkıcı-işlem onayı (window.confirm yerine). -->
  <ConfirmDialog />
</template>

<script setup lang="ts">
import ConfirmDialog from "./components/ConfirmDialog.vue";
import SetupWizardView from "./views/SetupWizardView.vue";
</script>
