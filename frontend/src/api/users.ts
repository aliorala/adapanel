import { apiClient } from "./client";

type ApiEnvelope<T> = {
  success: boolean;
  data: T;
  message: string;
  code: number;
  details?: string;
};

/** Sunucuda kalan tek bir kaynak (`UserDeletionLeftoverDto` karşılığı). */
export type UserDeletionLeftover = {
  /** Hangi siteye aitti (site dışı kalıntılarda boş). */
  siteName: string;
  /** `site` | `files` | `database` | `mailDomain` | `ftpUser` | `dnsZone` | `other` */
  kind: string;
  /** Kaynağın ADI: `mysql:musteri_db`, `musteri.com`, `D:\web\musteri.com` … */
  name: string;
  reason: string;
  /** Panel bunu bilerek mi bıraktı (politika) yoksa BAŞARISIZ mı oldu. */
  deliberate: boolean;
};

/** Kullanıcı silmenin sonucu: ne silindi, NE KALDI (`UserDeletionResultDto` karşılığı). */
export type UserDeletionResult = {
  userId: string;
  userName: string;
  /** Kullanıcı KAYDI silindi mi — yanıt 409 olsa bile true olabilir. */
  userDeleted: boolean;
  siteRemovalRequested: boolean;
  deletedSites: string[];
  leftovers: UserDeletionLeftover[];
  /** Yalnız BİLİNÇSİZ kalıntı yoksa true. False ise sunucu 409 döner. */
  completed: boolean;
};

/**
 * Kullanıcıyı siler ve <b>ne kaldığını</b> döner.
 *
 * NEDEN AYRI SARMALAYICI: bu uç eskiden `void` idi, çağıran da sonucu okumuyordu. Cascade'in
 * "veritabanı düşürülemedi / klasör yarım silindi" bilgisi hiçbir yere ulaşmıyor, panel her
 * durumda "kullanıcı silindi" diyordu (denetim Y7). Sarmalayıcı artık yanıt gövdesini olduğu
 * gibi geri veriyor ki ekran kalıntıları ADIYLA gösterebilsin.
 *
 * `removeSites` yalnız yöneticiye açıktır ve sunucu tarafında ayrıca doğrulanır; kalıntı varsa
 * sunucu <b>409</b> döner ve bu çağrı istisna fırlatır (gövdedeki `data` yine kalıntı listesidir).
 */
export async function deleteUser(userId: string, removeSites = false): Promise<UserDeletionResult> {
  const query = removeSites ? "?removeSites=true" : "";
  const response = await apiClient.delete<ApiEnvelope<UserDeletionResult>>(
    `/users/${encodeURIComponent(userId)}${query}`
  );
  return response.data.data;
}

/**
 * 409 (kısmi silme) yanıtının gövdesindeki kalıntı listesini çıkarır.
 *
 * Kalıntılar hata yolunda da GÖRÜNMELİ: 409'da kullanıcı kaydı silinmiştir ve sunucuda ne
 * kaldığını gösteren tek yapısal kaynak bu gövdedir. Ekranın ham `unknown` hatayı kurcalamaması
 * için ayrıştırma burada, tek yerde yapılır.
 */
export function extractDeletionLeftovers(error: unknown): UserDeletionLeftover[] {
  const envelope = (error as { response?: { data?: ApiEnvelope<UserDeletionResult> } })?.response?.data;
  const leftovers = envelope?.data?.leftovers;
  return Array.isArray(leftovers) ? leftovers : [];
}
