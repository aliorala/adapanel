import { apiClient } from "./client";

type ApiEnvelope<T> = { success: boolean; data: T; message: string; code: number };

/** İzlenebilen paylaşılan kaynaklar. Süreç listesi bunların İÇİNİ göremez; bu uç görebilir. */
export type ConsumerSource = "mysql" | "mariadb" | "postgresql" | "iis";

/** Sabit durum anahtarı — UI i18n'e çevirir, sunucu ASLA çevrilmiş metin döndürmez. */
export type ConsumerStatusCode = "" | "not_installed" | "credentials_missing" | "unavailable";

/**
 * Paylaşılan bir sürecin İÇİNDEKİ tek bir tüketici: bir aktif SQL sorgusu ya da bir açık HTTP isteği.
 * `siteName` bu satırın asıl değeridir — teknik bilmeyen biri de "alioral.xyz 12 saniyedir sorgu
 * çalıştırıyor" cümlesini buradan okuyabilir. Çözülemediyse `target` (veritabanı/app pool adı) gösterilir.
 */
export type ResourceConsumer = {
  resourceType: "database" | "web";
  source: string;
  siteName: string | null;
  target: string;
  user: string;
  clientHost: string;
  durationSeconds: number;
  state: string;
  command: string;
  /** Kısaltılmış sorgu/URL. Parola içerenler sunucuda maskelenir. */
  statement: string;
  /** true ise statement içindeki değişmez metinler gizlilik için maskelendi. */
  redacted: boolean;
  connectionId: string;
  /** Veritabanı panelde bir müşteriye kayıtlıysa o müşterinin adı (site başka sunucudaysa tek ipucu). */
  ownerName: string;
  /** Bu veritabanı bu sunucudaki bir siteye bağlı mı. */
  siteLinked: boolean;
  /** Bağlantı bu sunucunun dışından mı geliyor (uzak IP) — dışarıdaki bir uygulama kullanıyor demektir. */
  remoteClient: boolean;
};

export type ResourceConsumerSnapshot = {
  source: string;
  displayName: string;
  /** Motor/rol bu sunucuda kurulu mu (false → "kurulu değil", hata değil). */
  installed: boolean;
  /** Kurulu VE bu turda sorgulanabildi mi (false → liste güvenilmez, "boş" demek değil). */
  available: boolean;
  statusCode: ConsumerStatusCode;
  capturedAtUtc: string;
  totalCount: number;
  consumers: ResourceConsumer[];
};

/**
 * Bir kaynağın anlık tüketicilerini getirir (Administrator-only uç: başkalarının sorgularını ifşa eder).
 * `signal` verilirse bileşen kapanınca/kaynak değişince uçuşan istek iptal edilebilir.
 */
export async function getResourceConsumers(
  source: ConsumerSource,
  limit = 50,
  signal?: AbortSignal
): Promise<ResourceConsumerSnapshot> {
  const response = await apiClient.get<ApiEnvelope<ResourceConsumerSnapshot>>(
    `/monitoring/consumers?source=${encodeURIComponent(source)}&limit=${limit}`,
    { signal }
  );
  return response.data.data;
}
