import { computed, ref } from "vue";
import { useI18n } from "vue-i18n";

import { useNotice } from "./useNotice";

/**
 * TOPLU SEÇİM + TOPLU İŞLEM — TEK KAYNAK.
 *
 * NEDEN (seçim): Toplu seçim yalnız MigrationView'da vardı. 200 posta kutusunu tek tek silmek
 * 200 onay diyaloğu demekti; kullanıcı onayları refleksle geçmeye başlıyordu — yani onay
 * mekanizması güvenlik olmaktan çıkıyordu.
 *
 * NEDEN (dürüst özet): Döngüyle silmede tek `try/catch` ilk hatada döngüyü kesiyor ya da hatayı
 * yutuyordu. İkisi de yalan: kullanıcı "8 silindi" görüp 2'sinin durduğunu fark etmiyordu.
 * `runBulk` HER ÖĞEYİ dener, başarısızları SEBEBİYLE toplar ve özet ikisini de söyler.
 */

export type BulkFailure = { key: string; reason: string };

export type BulkOutcome = {
  total: number;
  done: number;
  failures: BulkFailure[];
};

export function useBulkSelection() {
  const selected = ref<string[]>([]);

  const count = computed(() => selected.value.length);
  const isEmpty = computed(() => selected.value.length === 0);

  function isSelected(key: string) {
    return selected.value.includes(key);
  }

  function toggle(key: string) {
    selected.value = isSelected(key)
      ? selected.value.filter((item) => item !== key)
      : [...selected.value, key];
  }

  /** Verilen anahtarların TAMAMI seçili mi (görünen sayfa için "tümünü seç" kutusu). */
  function allSelected(keys: string[]) {
    return keys.length > 0 && keys.every((key) => isSelected(key));
  }

  /** Görünen sayfadaki anahtarları topluca seçer/bırakır. */
  function toggleAll(keys: string[]) {
    if (allSelected(keys)) {
      selected.value = selected.value.filter((key) => !keys.includes(key));
      return;
    }
    const next = new Set(selected.value);
    for (const key of keys) {
      next.add(key);
    }
    selected.value = [...next];
  }

  function clear() {
    selected.value = [];
  }

  /**
   * Listede artık var olmayan anahtarları düşürür (silme/yenileme sonrası). Aksi halde seçim
   * sayacı ekranda olmayan öğeleri saymaya devam ediyordu.
   */
  function keepOnly(keys: string[]) {
    selected.value = selected.value.filter((key) => keys.includes(key));
  }

  return { selected, count, isEmpty, isSelected, toggle, toggleAll, allSelected, clear, keepOnly };
}

export function useBulkAction() {
  const { t } = useI18n();
  const { resolveApiMessage } = useNotice();
  const busy = ref(false);

  /**
   * Her öğeyi TEK TEK dener; bir hata döngüyü kesmez. Sonuç: kaç tanesi oldu, kaç tanesi olmadı
   * ve HANGİSİ neden olmadı.
   */
  async function runBulk<T>(
    items: T[],
    keyOf: (item: T) => string,
    worker: (item: T) => Promise<unknown>
  ): Promise<BulkOutcome> {
    busy.value = true;
    const failures: BulkFailure[] = [];
    let done = 0;

    try {
      for (const item of items) {
        try {
          await worker(item);
          done += 1;
        } catch (error) {
          failures.push({ key: keyOf(item), reason: resolveApiMessage(error) });
        }
      }
    } finally {
      busy.value = false;
    }

    return { total: items.length, done, failures };
  }

  /** Toplu SİLME özeti. Kısmi başarısızlıkta hangi öğenin neden silinemediği satır satır yazılır. */
  function deleteSummary(outcome: BulkOutcome): string {
    if (outcome.failures.length === 0) {
      return t("panel.bulk.deletedAll", { total: outcome.total });
    }

    const head = t("panel.bulk.deletedPartial", {
      total: outcome.total,
      done: outcome.done,
      failed: outcome.failures.length
    });
    const lines = outcome.failures.map((failure) =>
      t("panel.bulk.failedItem", { name: failure.key, reason: failure.reason }));
    return [head, ...lines].join("\n");
  }

  /**
   * Onay diyaloğunun "etkilenecekler" listesi. Uzun listeler diyaloğu ekran dışına taşıyordu;
   * ilk `limit` tanesi ADIYLA yazılır, kalanı sayıyla söylenir — "hangileri" sorusu yine yanıtlı.
   */
  function impactList(keys: string[], limit = 12): string[] {
    if (keys.length <= limit) {
      return [...keys];
    }
    return [...keys.slice(0, limit), t("panel.bulk.andMore", { count: keys.length - limit })];
  }

  return { busy, runBulk, deleteSummary, impactList };
}
