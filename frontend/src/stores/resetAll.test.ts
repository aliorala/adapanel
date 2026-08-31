import { describe, expect, it } from "vitest";
import { createPinia, defineStore, setActivePinia } from "pinia";
import { resetAllStores } from "./resetAll";

/**
 * Çıkışta bütün store'ların temizlenmesi.
 *
 * Kapatılan sızıntı: çıkış yalnız `auth` ve `subscription` store'larını temizliyordu; geriye
 * siteler / veritabanları / uygulamalar / sistem gibi 9 store veriyle kalıyordu. Paylaşılan bir
 * bilgisayarda sonraki kullanıcı, kendi verisi sunucudan gelene kadar öncekinin listesini
 * görebiliyordu.
 */
describe("resetAllStores", () => {
  it("kayitli BUTUN store'lari baslangic durumuna dondurur", () => {
    const pinia = createPinia();
    setActivePinia(pinia);

    const siteler = defineStore("siteler", {
      state: () => ({ liste: [] as string[] })
    })();
    const veritabanlari = defineStore("veritabanlari", {
      state: () => ({ adlar: [] as string[] })
    })();

    siteler.liste = ["musteri-a.example"];
    veritabanlari.adlar = ["musteri_a_wp"];

    resetAllStores(pinia);

    // ELLE LİSTE DEĞİL, KAYITLI HER STORE: bu testin asıl ölçtüğü şey, çıkış kodunun store
    // adlarını saymıyor olması. Aşağıdaki iki store çıkış kodunda hiç adı geçmeyen store'lardır.
    expect(siteler.liste).toEqual([]);
    expect(veritabanlari.adlar).toEqual([]);
  });

  it("sifirlanamayan bir store CIKISI ENGELLEMEZ", () => {
    const pinia = createPinia();
    setActivePinia(pinia);

    // Kurulum (setup) store'larında `$reset` yoktur ve çağrılınca fırlatır. Kullanıcı, tek bir
    // store yüzünden çıkamamak yerine biraz artıkla çıkmalı -- çıkamamak daha kötü bir sonuçtur.
    const setupStore = defineStore("kurulum-tarzi", () => ({}))();
    const normal = defineStore("normal", {
      state: () => ({ deger: 0 })
    })();
    normal.deger = 42;

    expect(setupStore).toBeDefined();
    expect(() => resetAllStores(pinia)).not.toThrow();
    expect(normal.deger).toBe(0);
  });

  it("etkin pinia yoksa sessizce gecer", () => {
    expect(() => resetAllStores(null)).not.toThrow();
  });
});
