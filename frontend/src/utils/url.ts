// Backend bazı araç URL'lerini sunucu-yerel host'la (127.0.0.1 / localhost / ::1) döndürür.
// Uzak tarayıcı bunu açamaz (kendi makinesine gider). Paneli gördüğümüz host ile değiştiririz.
// TÜM "tarayıcıda aç" akışları (phpMyAdmin, Adminer, webmail, nginx vb.) bunu kullanmalı.
export function toReachableUrl(raw: string): string {
  if (!raw) {
    return raw;
  }

  try {
    const url = new URL(raw, window.location.origin);
    if (["localhost", "127.0.0.1", "::1"].includes(url.hostname)) {
      url.hostname = window.location.hostname;
    }
    return url.toString();
  } catch {
    return raw;
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
