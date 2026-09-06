import { apiClient, buildStreamRequestHeaders } from "./client";
import type { ServerRunningJobs } from "../composables/useRunningTask";

type ApiEnvelope<T> = {
  success: boolean;
  data: T;
  message: string;
  code: number;
};

export type MigrateSitePayload = {
  siteName: string;
  createSite: boolean;
  physicalPath: string;
  pullFiles: boolean;
  fileProtocol: string;
  fileHost: string;
  filePort: number;
  fileUserName: string;
  filePassword: string;
  remotePath: string;
  transferDatabase: boolean;
  databaseEngine: string;
  dbHost: string;
  dbPort: number;
  dbUserName: string;
  dbPassword: string;
  /*
   * KAYNAK VERİTABANINA BAĞLANTI ŞİFRELEMESİ: "auto" | "required" | "disabled".
   *
   * "auto" (varsayılan) uzak kaynakta şifrelemeyi ZORUNLU kılar, loopback'te gereksiz yere
   * kırmaz. "disabled" TLS desteklemeyen eski bir sunucudan göç edenin BİLİNÇLİ çıkışıdır.
   * Boş/tanınmayan değer sunucuda "auto" sayılır — sessizce şifresize düşülmez.
   */
  dbTlsMode: string;
  sourceDatabase: string;
  targetDatabase: string;
  targetDatabaseUser: string;
  targetDatabasePassword: string;
  /*
   * YÖNETİCİNİN BİLEREK KABUL ETTİĞİ UZAK KİMLİK (FTPS sertifikası ya da SFTP host key parmak izi).
   *
   * Boş bırakılırsa kaynak sunucunun kimliği DOĞRULANIR. Doğrulanamazsa göç adımı
   * `migration.tls.untrusted` / `migration.hostkey.untrusted` ile başarısız olur ve sunucu ret
   * sebebini + parmak izini `detail` alanında döner; yönetici parmak izini kaynak sunucudan teyit
   * edip bu alanı doldurarak tekrar dener. "Her sertifikayı kabul et" diye bir değer YOKTUR —
   * kabul yalnız bu tek parmak izine açılır.
   */
  acceptFileHostFingerprint: string;
};

export type MigrationStep = { step: string; success: boolean; message: string; detail?: string | null };
export type MigrateSiteResult = { siteName: string; overallSuccess: boolean; steps: MigrationStep[] };

// Tek akış site göçü: site oluştur + dosya çek (FTP/SFTP) + DB aktar. Adım-adım sonuç döner.
export async function migrateSite(payload: MigrateSitePayload) {
  const response = await apiClient.post<ApiEnvelope<MigrateSiteResult>>("/migration/site", payload);
  return response.data.data;
}

// Canlı göç olayı (SSE ile gelen). Tek tip; alanlar faza göre dolar.
export type MigrationProgress = {
  phase: "plan" | "start" | "result" | "done";
  step?: string;
  success?: boolean | null;
  message?: string;
  detail?: string | null;
  steps?: string[];
  overallSuccess?: boolean | null;
};

// CANLI site göçü: /migration/site/stream'e POST atar, sunucu her aşamayı (plan/başla/sonuç/bitti) text/event-stream
// olarak akıtır; her olay için onEvent çağrılır → "hangi adımda ne yapılıyor" anlık gösterilir. signal ile iptal
// edilebilir (sekme/komponent kapanınca). Akış normal biterse promise çözülür.
export async function migrateSiteStream(
  payload: MigrateSitePayload,
  onEvent: (event: MigrationProgress) => void,
  signal?: AbortSignal
): Promise<void> {
  const headers = await buildStreamRequestHeaders();
  headers["Accept"] = "text/event-stream";

  const response = await fetch("/api/v1/migration/site/stream", {
    method: "POST",
    credentials: "include",
    headers,
    body: JSON.stringify(payload),
    signal
  });

  if (!response.ok || !response.body) {
    throw new Error(`migration_stream_failed_${response.status}`);
  }

  const reader = response.body.getReader();
  const decoder = new TextDecoder();
  let buffer = "";

  for (;;) {
    const { value, done } = await reader.read();
    if (done) {
      break;
    }
    // Satır sonlarını normalize et (proxy \r\n yazabilir); SSE çerçeveleri boş satırla ayrılır.
    buffer += decoder.decode(value, { stream: true }).replace(/\r\n/g, "\n");

    let separator: number;
    while ((separator = buffer.indexOf("\n\n")) !== -1) {
      const frame = buffer.slice(0, separator);
      buffer = buffer.slice(separator + 2);

      const dataLine = frame.split("\n").find((line) => line.startsWith("data:"));
      if (!dataLine) {
        continue;
      }
      const json = dataLine.slice(5).trim();
      if (!json) {
        continue;
      }
      try {
        onEvent(JSON.parse(json) as MigrationProgress);
      } catch {
        // bozuk/yarım çerçeve → atla
      }
    }
  }
}

export type PleskDiscoverPayload = {
  host: string;
  port: number;
  apiKey: string;
  userName: string;
  password: string;
  // Gerekçe MigrateSitePayload.acceptFileHostFingerprint'te; keşif isteği API ANAHTARINI taşır.
  acceptCertificateFingerprint: string;
};
/*
 * İKİ AYRI ALAN, ÇÜNKÜ İKİ AYRI ÖLÇÜM.
 * `hostingType` YALNIZ Plesk keşfinde dolar (Plesk'in `hosting_type` alanı: "virtual", "none"),
 * `accountUser` YALNIZ cPanel/WHM keşfinde (hesabın sistem kullanıcı adı). Eskiden ikisi de tek
 * `hostingType` alanından geliyordu ve ekranda etiketsiz basılıyordu — yani panel "virtual" ile
 * "ali_shop" arasındaki farkı kullanıcıya HİÇ söylemiyordu. Alanlar arka uçta ayrıldı
 * (`PleskDomainDto`); tipin ikisini de tanıması, şablonun hangisini gösterdiğini bilmesi demek.
 * İsteğe bağlı (`?`): her keşif yolu yalnız kendi alanını doldurur, diğeri boş gelir.
 */
export type PleskDomain = { name: string; hostingType?: string; accountUser?: string };
export type PleskDiscoverResult = { domains: PleskDomain[] };

// Plesk REST API'sine bağlanıp domain'leri keşfet (sihirbazı doldurmak için). Best-effort.
export async function discoverPlesk(payload: PleskDiscoverPayload) {
  const response = await apiClient.post<ApiEnvelope<PleskDiscoverResult>>("/migration/plesk/discover", payload);
  return response.data.data;
}

export type CpanelDiscoverPayload = {
  host: string;
  port: number;
  userName: string;
  apiToken: string;
  acceptCertificateFingerprint: string;
};

// cPanel/WHM API'sine bağlanıp hesap/domain'leri keşfet (WHM listaccts). Plesk ile aynı sonuç şekli.
export async function discoverCpanel(payload: CpanelDiscoverPayload) {
  const response = await apiClient.post<ApiEnvelope<PleskDiscoverResult>>("/migration/cpanel/discover", payload);
  return response.data.data;
}

export type MigrationCheck = { name: string; ok: boolean; message: string };
export type MigrationValidationResult = { allPassed: boolean; checks: MigrationCheck[] };

// Göç ÖNCESİ ön-kontrol: site adı + dosya/DB kaynağı erişilebilirliği (göç başlatmadan).
export async function validateMigration(payload: MigrateSitePayload) {
  const response = await apiClient.post<ApiEnvelope<MigrationValidationResult>>("/migration/validate", payload);
  return response.data.data;
}

/**
 * Bu sunucuda ŞU AN süren göç + panel yeniden başladığı için yarıda kalmış göç.
 *
 * NEDEN VAR: SSE akışı canlı adım gösteriyordu ama akış YALNIZ o sekmeye aitti — sayfa
 * yenilenince hem adımlar hem "sürüyor" bilgisi kayboluyordu. Göç ise uzak sunucudan yüzlerce
 * megabayt çekerken saatlerce sürebiliyor.
 */
export async function getMigrationRunningJobs() {
  const response = await apiClient.get<ApiEnvelope<ServerRunningJobs>>("/migration/running");
  return response.data.data;
}
