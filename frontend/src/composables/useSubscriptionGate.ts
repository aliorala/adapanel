import { computed, type ComputedRef } from "vue";
import { useI18n } from "vue-i18n";

import { useSubscriptionStore } from "../stores/subscription";

/**
 * ASKIDA SALT-OKUNUR — DÜĞME SEVİYESİ KAPI (TEK KAYNAK).
 *
 * NEDEN: askıdaki abonelikte sunucu bütün yazma isteklerini `403 subscription.suspended_readonly`
 * ile reddediyor; `api/client.ts` isteği ağa hiç çıkarmıyor. Ama düğmeler AKTİF görünüyordu:
 * kullanıcı basıyor, kırmızı hata alıyor, tekrar deniyor. Bir düğmenin basılamayacağını
 * BASILMADAN ÖNCE söylemek gerekir.
 *
 * NEDEN SEZGİ DEĞİL İŞARET: "yazma yapan düğme"yi biçimden (ör. `button:not(.secondary)`) ayırt
 * etmek kırılgandır — `secondary` sınıfı görsel bir tercihtir, yazma/okuma ayrımı değil. Bu yüzden
 * ayrım ELLE işaretlenir: yazma yapan her düğme `v-bind="writeGate(...)"` alır. İşaretin tek
 * yerde tanımlı olması, kuralın (devre dışı + SEBEP) her ekranda aynı olmasını sağlar.
 *
 * Kullanım (şablon):
 *   const { writeGate } = useSubscriptionGate();
 *   <button v-bind="writeGate(busy)" @click="save">Kaydet</button>
 *   <button v-bind="writeGate(busy || !name, t('panel.x.tip'))" @click="create">Oluştur</button>
 *
 * `writeGate` düğmenin KENDİ devre dışı koşulunu da alır: iki ayrı `:disabled` kaynağı olmaz,
 * bu yüzden biri diğerini ezemez.
 *
 * KAPSAM: bayrak yalnız kiracı (Customer/Reseller) oturumunda ölçülür — yöneticide `fetchMine`
 * hiç çağrılmaz ve `reset()` bayrağı temizler (bkz. SetupWizardView `loadDashboardData`). Yani
 * yönetici ekranlarında bu kapı fiilen etkisizdir; ayrıca rol koşulu yazmak gerekmez.
 *
 * İSTİSNA: `/auth/*` (çıkış, parola değiştirme, iki adımlı doğrulama) askıya TAKILMAZ; o
 * ekranların düğmelerine bu kapı UYGULANMAZ, yoksa askıdaki kullanıcı çıkış bile yapamaz görünür.
 */

export type WriteGateAttrs = {
  disabled: boolean;
  /** Devre dışıyken SEBEBİ söyler; kullanıcı neden basamadığını anlamalı. */
  title?: string;
  /**
   * `disabled` düğmeyi odak sırasından çıkarır ve bazı ekran okuyucular `title`'ı okumaz;
   * `aria-disabled` durumu ayrıca duyurur.
   */
  "aria-disabled"?: "true";
};

export function useSubscriptionGate(): {
  locked: ComputedRef<boolean>;
  lockReason: ComputedRef<string>;
  writeGate: (disabled?: boolean, title?: string) => WriteGateAttrs;
} {
  const { t } = useI18n();
  const subscription = useSubscriptionStore();

  const locked = computed(() => subscription.isSuspended);
  const lockReason = computed(() => (locked.value ? t("panel.subscription.readOnlyAction") : ""));

  function writeGate(disabled = false, title?: string): WriteGateAttrs {
    if (locked.value) {
      return { disabled: true, title: lockReason.value, "aria-disabled": "true" };
    }
    // Kilit yokken düğme KENDİ koşuluna döner; kapı hiçbir şey eklemez.
    return title ? { disabled, title } : { disabled };
  }

  return { locked, lockReason, writeGate };
}
