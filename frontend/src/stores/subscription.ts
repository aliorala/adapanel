import { defineStore } from "pinia";

import { getSessionEpoch, setReadOnlyMode } from "../api/client";
import { getMySubscription, type SubscriptionUsage } from "../api/subscriptions";

/**
 * Oturum açan kiracının kendi aboneliği — ASKI DURUMUNUN TEK KAYNAĞI.
 *
 * NEDEN: abonelik askıya alındığında sunucu bütün durum değiştiren istekleri
 * `403 subscription.suspended_readonly` ile reddediyor. Banner ve salt okunur mod aynı bayrağı
 * okumazsa panel iki farklı gerçeği anlatır: üstte "her şey yolunda", düğmede kırmızı hata.
 *
 * Bayrak set edilirken `setReadOnlyMode` ile API kapısına da yazılır (bkz. `api/client.ts`):
 * böylece askıdaki kullanıcının yazma isteği ağa hiç çıkmaz ve ekranlar sunucudan gelmiş gibi
 * çevrilebilir bir mesaj görür.
 */
export const useSubscriptionStore = defineStore("subscription", {
  state: () => ({
    usage: null as SubscriptionUsage | null,
    /** Ölçüm YAPILDI mı. false iken "askıda değil" demek, ölçmeden karar vermek olurdu. */
    loaded: false
  }),
  getters: {
    isSuspended: (state): boolean => state.usage?.isSuspended === true,
    /** "traffic" | "expired" | "manual" — arayüz `panel.subscription.reason.*` ile çevirir. */
    suspendReason: (state): string => state.usage?.suspendReason || "manual"
  },
  actions: {
    /**
     * Kendi aboneliğini çeker. Yönetici askıya takılmadığı için ÇAĞRILMAZ; uç zaten
     * yöneticide anlamlı bir kayıt döndürmez.
     */
    async fetchMine() {
      const epoch = getSessionEpoch();
      try {
        this.usage = await getMySubscription();
      } catch {
        if (epoch !== getSessionEpoch()) return;
        // Abonelik kaydı yoksa/okunamadıysa kısıt UYDURMAYIZ: sunucu son sözü zaten söylüyor.
        this.usage = null;
      } finally {
        if (epoch === getSessionEpoch()) {
          this.loaded = true;
          this.applyReadOnly();
        }
      }
    },
    /** Oturum kapanınca: bayrak bir sonraki kullanıcıya sızmamalı. */
    reset() {
      this.usage = null;
      this.loaded = false;
      this.applyReadOnly();
    },
    applyReadOnly() {
      setReadOnlyMode(this.isSuspended ? "subscription.suspended_readonly" : "");
    }
  }
});
