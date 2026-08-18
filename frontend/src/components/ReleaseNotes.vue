<template>
  <div class="release-notes">
    <template v-for="(blok, ix) in bloklar" :key="ix">
      <hr v-if="blok.tur === 'ayrac'" />
      <component :is="`h${Math.min(blok.seviye + 3, 6)}`" v-else-if="blok.tur === 'baslik'">
        <Satir :parcalar="blok.parcalar" />
      </component>
      <pre v-else-if="blok.tur === 'kod'"><code>{{ blok.icerik }}</code></pre>
      <ul v-else-if="blok.tur === 'liste'">
        <li v-for="(oge, oi) in blok.ogeler" :key="oi"><Satir :parcalar="oge" /></li>
      </ul>
      <blockquote v-else-if="blok.tur === 'alinti'"><Satir :parcalar="blok.parcalar" /></blockquote>
      <p v-else><Satir :parcalar="blok.parcalar" /></p>
    </template>
  </div>
</template>

<script setup lang="ts">
import { computed, h } from "vue";
import { ayristir, type Parca } from "../utils/releaseNotes";

/**
 * Sürüm notlarını okunur ve GÜVENLİ biçimde gösterir.
 *
 * NEDEN VAR: notlar `{{ }}` ile düz metin olarak basılıyordu. HTML satır sonlarını boşluğa
 * çevirdiği için bütün sürüm notu tek satıra çöküyor; `**`, `##`, ``` işaretleri ham
 * görünüyordu. Yani her sürümde yazılan notlar panelde pratikte OKUNMUYORDU.
 *
 * NEDEN v-html DEĞİL: bu metin GitHub API'sinden, yani panelin DIŞINDAN gelir. v-html ile
 * basmak dışarıdan gelen her şeyi çalıştırılabilir HTML yapardı. Bunun yerine metin h() ile
 * gerçek Vue düğümlerine çevrilir; çocuklar dizge olarak verildiği için Vue hepsini kaçırır.
 * Enjeksiyon sınıfı bu tasarımla ortadan kalkar, ayrıca bir temizleyici bağımlılığı gerekmez.
 *
 * Ayrıştırma mantığı bilerek utils/releaseNotes.ts'te: tarayıcı olmadan, gerçek sürüm
 * notlarıyla ölçülebilsin diye (scripts/check-release-notes-parser.mjs).
 */
const props = defineProps<{ text: string }>();

const bloklar = computed(() => ayristir(props.text ?? ""));

/** Metin h() ile düğüme çevrilir; Vue dizge çocukları kaçırdığı için HTML asla yorumlanmaz. */
const Satir = (satirProps: { parcalar: Parca[] }) =>
  satirProps.parcalar.map((parca) =>
    parca.tur === "kalin"
      ? h("strong", parca.deger)
      : parca.tur === "kod"
        ? h("code", parca.deger)
        : parca.deger,
  );
</script>

<style scoped>
.release-notes {
  display: flex;
  flex-direction: column;
  gap: 0.6rem;
  line-height: 1.55;
}

.release-notes :is(h3, h4, h5, h6) {
  margin: 0.4rem 0 0;
  font-size: 1rem;
}

.release-notes p,
.release-notes ul {
  margin: 0;
}

.release-notes ul {
  padding-left: 1.2rem;
}

.release-notes li {
  margin: 0.15rem 0;
}

.release-notes hr {
  width: 100%;
  border: 0;
  border-top: 1px solid var(--border, rgba(148, 163, 184, 0.35));
  margin: 0.2rem 0;
}

.release-notes blockquote {
  margin: 0;
  padding: 0.5rem 0.75rem;
  border-left: 3px solid var(--accent, #3b82f6);
  background: var(--surface-muted, rgba(148, 163, 184, 0.12));
  border-radius: 0 6px 6px 0;
}

/* Kod blokları taşmaz; uzun satırlar sayfayı yatay kaydırmak yerine KENDİ İÇİNDE kayar. */
.release-notes pre {
  margin: 0;
  padding: 0.6rem 0.75rem;
  overflow-x: auto;
  background: var(--surface-muted, rgba(148, 163, 184, 0.12));
  border-radius: 6px;
}

.release-notes pre code {
  white-space: pre;
  font-size: 0.85rem;
}

.release-notes :not(pre) > code {
  padding: 0.1rem 0.3rem;
  border-radius: 4px;
  background: var(--surface-muted, rgba(148, 163, 184, 0.18));
  font-size: 0.88rem;
}
</style>
