import { computed, ref, watch, type ComputedRef, type Ref } from "vue";

/**
 * İSTEMCİ TARAFI SAYFALAMA — TEK KAYNAK.
 *
 * NEDEN: 70 listeden yalnız 3'ünde sayfalama vardı. 500 posta kutusu ya da 2000 dosyalı bir
 * klasör tek seferde DOM'a basılıyordu; tarayıcı satır başına onlarca düğüm + düğme üretip
 * kilitleniyordu. Kopyala-yapıştır bir kalıp yerine tek composable: davranış (sayfa taşması,
 * liste küçülünce sayfanın geçerli kalması, sayfa boyutu) her ekranda AYNI.
 *
 * Kullanım:
 *   const pager = usePagination(filteredUsers);      // Ref<T[]> ya da computed
 *   v-for="item in pager.items.value"  →  şablonda: v-for="item in pager.items"
 *   <TablePager :state="pager" />
 *
 * NOT: Sayfalama listeyi KISALTMAZ, yalnız görüneni sınırlar. Arama/filtre sayfalamadan ÖNCE
 * uygulanmalı (kaynak olarak filtrelenmiş computed verilir), aksi halde kullanıcı yalnız açık
 * sayfanın içinde arama yaptığını sanır.
 */

export const PAGE_SIZE_OPTIONS = [25, 50, 100] as const;
export const DEFAULT_PAGE_SIZE = 25;

export type PaginationState<T = unknown> = {
  page: Ref<number>;
  size: Ref<number>;
  total: ComputedRef<number>;
  pageCount: ComputedRef<number>;
  items: ComputedRef<T[]>;
  from: ComputedRef<number>;
  to: ComputedRef<number>;
  hasPrevious: ComputedRef<boolean>;
  hasNext: ComputedRef<boolean>;
  go: (delta: number) => void;
  reset: () => void;
};

export function usePagination<T>(
  source: Ref<T[]> | ComputedRef<T[]>,
  pageSize: number = DEFAULT_PAGE_SIZE
): PaginationState<T> {
  const size = ref(pageSize);
  const page = ref(1);

  const total = computed(() => source.value.length);
  const pageCount = computed(() => Math.max(1, Math.ceil(total.value / Math.max(1, size.value))));

  // Liste küçülünce (silme, filtre) açık sayfa listenin dışında kalabilir: boş bir tablo
  // "kayıt yok" gibi görünürdü. Sayfa her zaman geçerli aralığa çekilir.
  watch([total, size], () => {
    if (page.value > pageCount.value) {
      page.value = pageCount.value;
    }
    if (page.value < 1) {
      page.value = 1;
    }
  });

  const start = computed(() => (page.value - 1) * size.value);
  const items = computed(() =>
    total.value <= size.value ? source.value : source.value.slice(start.value, start.value + size.value));
  const from = computed(() => (total.value === 0 ? 0 : start.value + 1));
  const to = computed(() => Math.min(start.value + size.value, total.value));
  const hasPrevious = computed(() => page.value > 1);
  const hasNext = computed(() => page.value < pageCount.value);

  function go(delta: number) {
    const next = page.value + delta;
    page.value = Math.min(pageCount.value, Math.max(1, next));
  }

  function reset() {
    page.value = 1;
  }

  return { page, size, total, pageCount, items, from, to, hasPrevious, hasNext, go, reset };
}
