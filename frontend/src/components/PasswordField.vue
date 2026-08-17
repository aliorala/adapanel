<template>
  <span class="password-field">
    <!-- Sıra ÖNEMLİ: `spellcheck`/`autocapitalize` $attrs'tan ÖNCE (çağıran ezebilsin),
         `:type` $attrs'tan SONRA (çağıran yanlışlıkla `type` geçse bile gizlilik kararı
         burada kalsın). `autocomplete` HİÇ verilmez: tarayıcının kayıtlı parolayı doğru
         alana doldurması çağıranın verdiği değere bağlıdır, bileşen onu uydurmaz. -->
    <input
      ref="inputEl"
      spellcheck="false"
      autocapitalize="off"
      autocorrect="off"
      v-bind="$attrs"
      :type="resolvedType"
      :value="modelValue"
      @input="onInput" />
    <button
      v-if="password"
      type="button"
      class="password-reveal"
      :disabled="fieldDisabled"
      :aria-pressed="revealed ? 'true' : 'false'"
      :aria-label="actionLabel"
      :title="actionLabel"
      @click="revealed = !revealed">
      {{ t(revealed ? "panel.password.hide" : "panel.password.show") }}
    </button>
  </span>
</template>

<script setup lang="ts">
import { computed, ref, useAttrs, watch } from "vue";
import { useI18n } from "vue-i18n";

/**
 * PAROLA ALANI — panelin TEK parola girişi (göster/gizle dâhil).
 *
 * NEDEN TEK PARÇA: parolayı GÖRMEK bir seçim olmalı (bkz. PromptDialog'un eski yorumu:
 * `window.prompt` parolayı hep açıkta gösteriyordu). Aynı davranış panelde 38 ayrı alanda
 * gerekiyordu; şablonu 19 dosyaya kopyalamak yerine davranış burada TEK yerde tanımlanır.
 * Kural değişirse (ör. otomatik gizleme süresi) tek dosya değişir.
 *
 * GÜVENLİK SINIRI: bileşen `type` özniteliğinden BAŞKA bir şey yapmaz — değeri panoya
 * kopyalamaz, başka bir elemana metin olarak yazmaz, hiçbir yerde saklamaz. Görünürlük
 * yalnızca tarayıcının kendi alanında değişir.
 *
 * VARSAYILAN GİZLİ: `revealed` her zaman `false` başlar; hiçbir çağıran "açık başlat"
 * diyemez. Alan devre dışı bırakılırsa (uzun süren işlem, askıya alınmış abonelik) açık
 * parola EKRANDA BIRAKILMAZ — aşağıdaki `watch` onu tekrar gizler.
 *
 * @example
 *   <PasswordField v-model="form.password" autocomplete="new-password" minlength="12" />
 */
defineOptions({ inheritAttrs: false });

const props = withDefaults(defineProps<{
  modelValue: string;
  /**
   * Alan parola mı? `false` ise düz metin alanı olur ve göster/gizle düğmesi ÇIKMAZ.
   * Yalnızca PromptDialog için var: aynı diyalog hem metin hem parola soruyor ve iki ayrı
   * girdi elemanı tutmak aynı hatayı iki yerde düzeltmek demekti.
   */
  password?: boolean;
}>(), { password: true });

const emit = defineEmits<{ "update:modelValue": [string] }>();

const { t } = useI18n();
const attrs = useAttrs();
const inputEl = ref<HTMLInputElement | null>(null);
const revealed = ref(false);

const resolvedType = computed(() => (props.password && !revealed.value ? "password" : "text"));
const actionLabel = computed(() => t(revealed.value ? "panel.password.hideAria" : "panel.password.showAria"));

/**
 * Alanın kendi `disabled` durumu düğmeye de uygulanır.
 *
 * KARAR (abonelik kapısı — `useSubscriptionGate.writeGate`): kilitliyken/meşgulken göster
 * düğmesi de kapanır. Göstermek bir yazma işlemi değil, ama kapalı bir alanın yanında ÇALIŞAN
 * bir düğme bırakmak tutarsızdır; dahası kapalı alan gönderilmemiş bir parola tutuyor olabilir
 * ve kullanıcının başka yere baktığı uzun bir işlem boyunca onu ekranda açık tutmanın hiçbir
 * faydası, omuz üstünden okunma riski ise vardır.
 */
const fieldDisabled = computed(() => attrs.disabled === true || attrs.disabled === "" || attrs.disabled === "disabled");

watch(fieldDisabled, (disabled) => {
  if (disabled) {
    revealed.value = false;
  }
});

function onInput(event: Event) {
  emit("update:modelValue", (event.target as HTMLInputElement).value);
}

// PromptDialog odağı ve odak tuzağını yönetiyor; ham elemanı ORAYA açıyoruz ki odak mantığı
// bileşen sınırında kopmasın.
defineExpose({
  el: inputEl,
  focus: () => inputEl.value?.focus(),
  select: () => inputEl.value?.select()
});
</script>

<style scoped>
/* `stretch`: düğme alanın YÜKSEKLİĞİNİ takip eder. Görünümler alan dolgusunu kendi kapsamlı
   CSS'inde büyütebiliyor (ör. SitesView 12/14); sabit bir yükseklik yazsaydık düğme o
   ekranlarda alandan kısa kalır, her görünüm için ayrı sayı yazmak gerekirdi. */
.password-field {
  display: flex;
  align-items: stretch;
  gap: 6px;
  width: 100%;
  min-width: 0;
}

.password-field input {
  flex: 1 1 auto;
  min-width: 0;
}

/* Global "button" gradyanını/gölgesini MİRAS ALMAZ: alanın yanında duran sessiz bir anahtar,
   formun asıl eylemiyle (Kaydet/Oluştur) yarışmamalı. */
.password-field .password-reveal {
  flex: 0 0 auto;
  width: auto;
  min-height: 34px;
  padding: 6px 10px;
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-sm);
  background: var(--surface-3);
  color: var(--text-soft);
  font-size: 0.84rem;
  font-weight: 600;
  box-shadow: none;
  transform: none;
  filter: none;
}

.password-field .password-reveal:not(:disabled):hover {
  border-color: color-mix(in srgb, var(--accent-1) 55%, transparent);
  color: var(--text-strong);
  background: var(--surface-3);
  transform: none;
  filter: none;
  box-shadow: none;
}

.password-field .password-reveal[aria-pressed="true"] {
  border-color: color-mix(in srgb, var(--accent-1) 55%, transparent);
  color: var(--accent-text);
}

.password-field .password-reveal:disabled {
  cursor: not-allowed;
}

@media (max-width: 720px) {
  .password-field .password-reveal {
    min-height: 40px;
  }
}
</style>
