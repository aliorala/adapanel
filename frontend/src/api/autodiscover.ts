import { apiClient } from "./client";

/**
 * POSTA İSTEMCİSİ OTOMATİK YAPILANDIRMASI (autodiscover / autoconfig) — YÖNETİM UÇLARI.
 *
 * Neden AYRI dosya (mail.ts'e eklenmedi): bu üç uç `MailController` altında DEĞİL, ayrı bir
 * denetleyicide (`MailAutodiscoverSetupController`, `api/v1/mail/autoconfig`) yaşıyor ve
 * `[Authorize(Roles = "Administrator")]` ile sınırlı — `mail.ts` içindeki uçların çoğu ise
 * kiracıya da açık. İki farklı yetki sınırını tek dosyada toplamak, çağıranın hangi ucun kime
 * açık olduğunu dosyaya bakarak anlamasını imkânsız kılardı.
 *
 * Alan adları ve yollar `MailAutodiscoverPublicationDto` ile `MailAutodiscoverSetupController`
 * okunarak yazıldı; tahmin edilmedi.
 */

type ApiEnvelope<T> = {
  success: boolean;
  data: T;
  message: string;
  code: number;
};

/**
 * Zincirin tek bir halkası.
 *
 * `id` sunucudaki ölçüm sırasını izler: `mail` → `site` → `rewrite` → `binding` → `https` → `dns`.
 * Arayüz bu listeyi SUNUCUDAN geldiği sırayla basar; kendi sırasını uydurmaz.
 */
export type MailAutodiscoverStep = {
  /** `mail`, `site`, `rewrite`, `binding`, `https`, `dns`. */
  id: string;
  /**
   * `ok` / `warn` / `fail` / `unknown`.
   *
   * `unknown` "sorun yok" DEĞİLDİR — sunucu o adımı ölçemedi demektir (bkz. DTO açıklaması).
   * Arayüz ikisini asla aynı renge boyamaz.
   */
  status: string;
  /** Kısa değer: site adı, kural sayısı, eksik ana adlar… */
  value: string;
  /** Ölçümün ham gerekçesi — "neden kırmızı" sorusunun cevabı. */
  detail: string;
};

/** Kullanıcının kendi DNS sağlayıcısında açması gereken kayıt. */
export type MailAutodiscoverDnsRecord = {
  type: string;
  /** Bölgeye GÖRE ad (ör. `autodiscover`), tam nitelikli ad değil. */
  host: string;
  value: string;
};

export type MailAutodiscoverPublication = {
  domain: string;
  /** Alan adını 80/443'ten servis eden IIS sitesi; bulunamadıysa boş. */
  siteName: string;
  /** Vekilin ilettiği yerel panel adresi (ör. `https://localhost:8890`). */
  backendUrl: string;
  /** Üç yönlendirme kuralının üçü de yerinde mi, yani istek GERÇEKTEN panele düşüyor mu. */
  isPublished: boolean;
  steps: MailAutodiscoverStep[];
  /** İstemcilerin gerçekte çağıracağı adresler — elle doğrulama için. */
  probeUrls: string[];
  /** Alan adının bölgesi bu sunucudaki Windows DNS'ten yayınlanıyor mu. */
  dnsManagedByPanel: boolean;
  /** Panel kaydı kendisi yazdıysa BOŞTUR: "yapıldı" ile "yapman gerekiyor" karışmasın. */
  requiredDnsRecords: MailAutodiscoverDnsRecord[];
  checkedAtUtc: string;
};

/**
 * Zinciri ÖLÇER, hiçbir şey yazmaz. Yalnız yönetici (403 = rol yetersiz).
 */
export async function getMailAutoconfig(domain: string) {
  const response = await apiClient.get<ApiEnvelope<MailAutodiscoverPublication>>(
    `/mail/autoconfig/${encodeURIComponent(domain)}`);
  return response.data.data;
}

/**
 * Otomatik yapılandırmayı alan adının servis edildiği siteye bağlar. Yalnız yönetici.
 *
 * ZARF KOMPLE döndürülür (yalnız `data` değil): sunucu burada iki farklı başarı mesajı üretir —
 * `mail.autoconfig_published` ve `mail.autoconfig_published_partial`. İkisini de "yayınlandı"
 * diye göstermek, zinciri yarım kalmış bir kurulumu çalışıyormuş gibi sunmak olurdu.
 *
 * `contactEmail` Let's Encrypt kaydı için sunucuya sorgu dizesinde geçer; boş bırakılabilir
 * (sertifika adımı best-effort'tur ve başarısızlığı yayını öldürmez).
 */
export async function publishMailAutoconfig(domain: string, contactEmail: string) {
  const response = await apiClient.post<ApiEnvelope<MailAutodiscoverPublication>>(
    `/mail/autoconfig/${encodeURIComponent(domain)}`,
    {},
    { params: contactEmail ? { contactEmail } : {} });
  return response.data;
}

/**
 * Yayını geri alır: panelin YAZDIĞI kurallar ve EKLEDİĞİ hizmet bağlamaları kaldırılır
 * (kullanıcının kendi kurallarına dokunulmaz). Yalnız yönetici.
 */
export async function unpublishMailAutoconfig(domain: string) {
  const response = await apiClient.delete<ApiEnvelope<MailAutodiscoverPublication>>(
    `/mail/autoconfig/${encodeURIComponent(domain)}`);
  return response.data;
}
