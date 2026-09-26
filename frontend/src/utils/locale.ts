import { ref } from 'vue';

// Keep formatting reactive when the user switches the panel language.
const formattingLocale = ref('tr-TR');
export function setFormattingLocale(language: string) {
  formattingLocale.value = language.toLowerCase().startsWith('en') ? 'en-US' : 'tr-TR';
}
export function getFormattingLocale(): string {
  return formattingLocale.value;
}
