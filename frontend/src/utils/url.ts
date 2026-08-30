// Backend bazı araç URL'lerini sunucu-yerel host'la (127.0.0.1 / localhost / ::1) döndürür.
// Uzak tarayıcı bunu açamaz (kendi makinesine gider). Paneli gördüğümüz host ile değiştiririz.
// TÜM "tarayıcıda aç" akışları (phpMyAdmin, Adminer, webmail, nginx vb.) bunu kullanmalı.
//
// ŞEMA ELEMESİ — bu fonksiyonun çıktısı `window.open`, `location.href` ve `:href`e GİDER.
//
// ÖLÇÜLDÜ: eleme yokken `new URL("javascript:alert(1)", origin)` HATA ATMAZ; `hostname` boş
// dizedir, aşağıdaki localhost listesinde yer almaz ve dizge OLDUĞU GİBİ geri dönerdi. Daha
// kötüsü `URL` normalleştirme yaptığı için "  JaVaScRiPt:alert(1)" girdisi baştaki boşluğu
// atılmış, küçük harfe çevrilmiş "javascript:alert(1)" olarak ÇIKARDI — yani fonksiyon
// yükü elemek şöyle dursun, çalışır hâle GETİRİYORDU.
//
// Bu neden önemli: çağıranların ikisi (MailView webmail, AppToolkit wp-login) sekmeyi
// `window.open("", "_blank")` ile açar. `about:blank` sekmesi AÇANIN kökenini miras alır;
// oraya yazılan `javascript:` PANELİN kökeninde çalışır, yani yöneticinin oturumunda.
//
// Bugün çağıranların hepsi arka ucun ürettiği adresi veriyor, yani ölçülebilen bir sömürü
// zinciri YOK. Ama üstteki yorum "TÜM açma akışları bunu kullanmalı" diyor: bu fonksiyon
// kiracı verisi taşıyan ilk adreste sessizce XSS'e döner. Kapı, açılmadan kapatılır.
const IZINLI_SEMALAR = new Set(["http:", "https:"]);

export function toReachableUrl(raw: string): string {
  if (!raw) {
    return raw;
  }

  try {
    const url = new URL(raw, window.location.origin);
    // Yalnız gerçekten "tarayıcıda açılabilir" iki şema geçer. Beklenen tüm girdiler
    // (`http(s)://host`, IIS bağlaması, phpMyAdmin/webmail adresi) zaten bu ikisinden biri.
    if (!IZINLI_SEMALAR.has(url.protocol)) {
      return "";
    }
    if (["localhost", "127.0.0.1", "::1"].includes(url.hostname)) {
      url.hostname = window.location.hostname;
    }
    return url.toString();
  } catch {
    // Ayrıştırılamayan adres artık HAM DÖNMÜYOR. Aynı dosyadaki `maskSecretsInUri`nin
    // disiplini: tanımadığımız bir dizgeyi "açılabilir" ilan etmek, elemediğimiz şeye
    // temiz demek olurdu. Çağıranlar boş dizgeyi zaten "adres yok" diye ele alır.
    return "";
  }
}

/**
 * Bir URI'yi EKRANDA GÖSTERİLEBİLİR hâle getirir: kimlik taşıyan parçaları çıkarır.
 *
 * NEDEN: yedek kaydındaki `remoteUri` sunucuda yalnız `user:pass@` kısmından arındırılıyor
 * (BackupService.SanitizeUri, UriBuilder ile). SORGU DİZESİ arındırılmıyor — S3 "presigned"
 * hedefinde yedek adresi `?X-Amz-Credential=…&X-Amz-Signature=…` taşır ve o imza, süresi
 * bitene kadar o nesneye YAZMA/OKUMA yetkisidir. Yedek listesi ekran görüntüsü paylaşılan bir
 * yerdir; imzayı oraya basmak, hedef depoyu ekran görüntüsüyle birlikte devretmek olurdu.
 *
 * Bu yüzden gösterime yalnız `şema://host/yol` kalır. Bir şey çıkarıldıysa `masked` true döner
 * ve çağıran bunu kullanıcıya SÖYLER — sessizce kırpılmış bir adres, yanlış bir adres olurdu.
 */
export function maskSecretsInUri(raw: string): { text: string; masked: boolean } {
  const value = (raw ?? "").trim();
  if (!value) {
    return { text: "", masked: false };
  }

  try {
    const url = new URL(value);
    const hadCredentials = url.username !== "" || url.password !== "";
    const hadQuery = url.search !== "" || url.hash !== "";
    url.username = "";
    url.password = "";
    url.search = "";
    url.hash = "";
    return { text: url.toString(), masked: hadCredentials || hadQuery };
  } catch {
    // Ayrıştırılamayan adres: içinde ne olduğunu BİLMİYORUZ, bu yüzden ham hâlde basılmaz.
    // Tanımadığımız bir dizgeyi "temiz" ilan etmek, temizlemediğimiz şeye temiz demek olurdu.
    return { text: "", masked: true };
  }
}

/// Aracı yeni sekmede güvenli host ile açar.
export function openReachable(raw: string): void {
  const url = toReachableUrl(raw);
  if (url) {
    window.open(url, "_blank", "noopener");
  }
}
