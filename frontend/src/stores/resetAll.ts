import { invalidateSessionRequests } from "../api/client";
import { getActivePinia, type Pinia, type Store } from "pinia";

/**
 * Çıkışta BÜTÜN store'ları başlangıç durumuna döndürür.
 *
 * NEDEN VAR — ölçüldü: çıkış yalnız `auth` store'unun kendi alanlarını ve `subscription`'ı
 * temizliyordu. Geriye 9 store kalıyordu (siteler, veritabanları, uygulamalar, PHP, vekil,
 * yazılım, sistem, asp, setup) ve içlerindeki veri bellekte duruyordu.
 *
 * Görünen sonucu şu: paylaşılan bir bilgisayarda A çıkıp B giriş yaptığında, B'nin kendi verisi
 * sunucudan gelene kadar ekranda A'nın site listesi/veritabanı adları görünebiliyordu. Veri
 * diske yazılmıyor, ama "bir an için görünmek" bir müşteriye başka bir müşterinin envanterini
 * göstermeye yeter.
 *
 * NEDEN ELLE LİSTE DEĞİL: çıkışta tek tek `xStore.reset()` çağırmak, listenin bayatlamasına
 * açıktır — 12. store eklendiğinde kimse buraya gelip eklemeyi hatırlamaz ve sızıntı SESSİZCE
 * geri döner. Bu depoda "kural bir yerde yazılı ama yeni yol onu almıyor" tekrar eden hata
 * sınıfıdır. Burada kayıtlı olan HER store'u dolaşmak, yeni store'u otomatik kapsar.
 */
export function resetAllStores(pinia: Pinia | null = getActivePinia() ?? null): void {
  invalidateSessionRequests();
  if (!pinia) {
    return;
  }

  // `_s` Pinia'nın kayıtlı store haritası. Genel API'de bir "hepsini dolaş" yolu yok;
  // alternatif, tam da kaçınmak istediğimiz elle tutulan listedir.
  const stores = (pinia as unknown as { _s?: Map<string, Store> })._s;
  if (!stores) {
    return;
  }

  stores.forEach((store) => {
    // $reset yalnız seçenek (option) store'larında vardır. Kurulum store'u bir setup store ise
    // fırlatır; tek bir store'un sıfırlanamaması ÇIKIŞI ENGELLEMEMELİDİR — kullanıcı çıkamamak
    // yerine biraz artıkla çıksın.
    try {
      store.$reset();
    } catch {
      /* bu store sıfırlanamıyor; çıkış yine de tamamlanır */
    }
  });
}
