<template>
  <div class="code-sample">
    <pre class="code-sample-body"><code>{{ code }}</code></pre>
    <button type="button" class="secondary compact code-sample-copy" @click="copy">
      {{ copied ? copiedLabel : copyLabel }}
    </button>
  </div>
</template>

<script setup lang="ts">
import { onBeforeUnmount, ref } from "vue";
import { copyToClipboard } from "../utils/clipboard";

/**
 * Kısa, kopyalanabilir kod/yol kutusu. Kullanıcıya "ne yazacağını" anlatmak yerine
 * doğrudan gösterir; tek tıkla panoya alınır.
 */
const props = withDefaults(defineProps<{
  /** Gösterilecek metin (kod parçası, dosya yolu vb.). */
  code: string;
  /** Kopyala düğmesinin metni. */
  copyLabel?: string;
  /** Kopyalandıktan sonra kısa süre gösterilen metin. */
  copiedLabel?: string;
}>(), { copyLabel: "Kopyala", copiedLabel: "Kopyalandı" });

const copied = ref(false);
let resetTimer: ReturnType<typeof setTimeout> | null = null;

async function copy() {
  // Ortak yardımcı güvenli olmayan bağlamda (panel henüz sertifika almadan) da kopyalar.
  // Yine de olmazsa "kopyalandı" işareti GÖSTERİLMEZ: metin zaten ekranda, seçilebilir.
  if (!(await copyToClipboard(props.code))) {
    return;
  }

  copied.value = true;
  if (resetTimer) clearTimeout(resetTimer);
  resetTimer = setTimeout(() => (copied.value = false), 1800);
}

onBeforeUnmount(() => {
  if (resetTimer) clearTimeout(resetTimer);
});
</script>

<style scoped>
.code-sample {
  display: flex;
  align-items: flex-start;
  gap: 8px;
  min-width: 0;
}

.code-sample-body {
  flex: 1 1 auto;
  min-width: 0;
  margin: 0;
  padding: 9px 11px;
  overflow-x: auto;
  border: 1px solid var(--panel-border);
  border-radius: var(--radius-sm);
  background: var(--surface-1);
  color: var(--text-strong);
  font: 0.84rem/1.5 "Cascadia Code", "Consolas", monospace;
  white-space: pre;
}

.code-sample-copy {
  flex: 0 0 auto;
}

@media (max-width: 620px) {
  .code-sample {
    flex-direction: column;
    align-items: stretch;
  }
}
</style>
