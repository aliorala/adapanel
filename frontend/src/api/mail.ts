import { apiClient } from "./client";

type ApiEnvelope<T> = {
  success: boolean;
  data: T;
  message: string;
  code: number;
};

export type MailDomain = { name: string; active: boolean; accountCount: number; maxSizeMB: number; allocatedMB: number };
// `active` UC DEGERLI: true/false sunucunun cevabi, null/undefined ise "olculemedi".
// Backend (MailAccountDto.Active) artik hesabin durumunu SORUYOR; sabit true dondurmuyor.
export type MailAccount = { address: string; active: boolean | null; maxSizeMB: number; currentSizeMB: number };
export type MailAlias = { address: string; target: string; active: boolean };
// Plesk'in "kimler gonderebilir" karsiligi. Stalwart'in MailingList nesnesinde boyle bir alan
// YOK; kural RCPT asamasina baglanan sistem sieve script'iyle uygulanir (canli dogrulandi).
export type MailListPostingPolicy = "anyone" | "subscribers" | "allowed";

export type MailList = {
  address: string;
  description: string;
  recipients: string[];
  // Listenin EK ADRESLERI (Stalwart MailingList.aliases). Bu adreslerden birine gelen posta da
  // ayni abonelere dagitilir. Her zaman TAM adrestir: alias listenin kendi alan adindan baska bir
  // alan adinda olabilir, yalniz yerel kisim gosterilseydi ikisi ekranda ayirt edilemezdi.
  aliases: string[];
  postingPolicy: MailListPostingPolicy;
  allowedSenders: string[];
};
export type MailListener = { name: string; protocol: string; binds: string[] };

export type MailSpamSettings = {
  installed: boolean;
  configured: boolean;
  enabled: boolean;
  scoreSpam: number;
  scoreReject: number;
  scoreDiscard: number;
  greylistEnabled: boolean;
  greylistMinutes: number;
  trustContacts: boolean;
  trustReplies: boolean;
};

export type MailWorkspace = {
  installed: boolean;
  configured: boolean;
  selectedDomain: string;
  domains: MailDomain[];
  accounts: MailAccount[];
  aliases: MailAlias[];
  lists: MailList[];
  statusMessage?: string;
};

export async function getMailWorkspace(domain?: string) {
  const response = await apiClient.get<ApiEnvelope<MailWorkspace>>("/mail", {
    params: domain ? { domain } : {}
  });
  return response.data.data;
}

export async function configureMail(adminPassword: string) {
  const response = await apiClient.post<ApiEnvelope<MailWorkspace>>("/mail/configure", { adminPassword });
  return response.data.data;
}

export async function createMailDomain(domainName: string) {
  const response = await apiClient.post<ApiEnvelope<MailWorkspace>>("/mail/domains", { domainName });
  return response.data.data;
}

export async function deleteMailDomain(domainName: string) {
  const response = await apiClient.delete<ApiEnvelope<MailWorkspace>>(`/mail/domains/${encodeURIComponent(domainName)}`);
  return response.data.data;
}

export async function createMailAccount(domain: string, address: string, password: string, maxSizeMB: number) {
  const response = await apiClient.post<ApiEnvelope<MailWorkspace>>("/mail/accounts", {
    domain,
    address,
    password,
    maxSizeMB
  });
  return response.data.data;
}

export async function deleteMailAccount(address: string) {
  const response = await apiClient.delete<ApiEnvelope<MailWorkspace>>(`/mail/accounts/${encodeURIComponent(address)}`);
  return response.data.data;
}

export async function setMailAccountPassword(address: string, password: string) {
  const response = await apiClient.post<ApiEnvelope<MailWorkspace>>("/mail/accounts/password", { address, password });
  return response.data.data;
}

export async function createMailAlias(domain: string, address: string, target: string) {
  const response = await apiClient.post<ApiEnvelope<MailWorkspace>>("/mail/aliases", { domain, address, target });
  return response.data.data;
}

export async function deleteMailAlias(address: string) {
  const response = await apiClient.delete<ApiEnvelope<MailWorkspace>>(`/mail/aliases/${encodeURIComponent(address)}`);
  return response.data.data;
}
export async function createMailList(
  domain: string,
  address: string,
  recipients: string[],
  description = "",
  postingPolicy: MailListPostingPolicy = "anyone",
  allowedSenders: string[] = []
) {
  const response = await apiClient.post<ApiEnvelope<MailWorkspace>>("/mail/lists", {
    domain,
    address,
    description,
    recipients,
    postingPolicy,
    allowedSenders
  });
  return response.data.data;
}

// Gonderim politikasini degistirir. Aboneler GONDERILMEZ -> sunucudaki kume korunur.
export async function setMailListPolicy(
  address: string,
  postingPolicy: MailListPostingPolicy,
  allowedSenders: string[]
) {
  const response = await apiClient.put<ApiEnvelope<MailWorkspace>>("/mail/lists/recipients", {
    address,
    postingPolicy,
    allowedSenders
  });
  return response.data.data;
}

// Aboneleri KOMPLE degistirir (toplu duzenleme).
export async function setMailListRecipients(address: string, recipients: string[]) {
  const response = await apiClient.put<ApiEnvelope<MailWorkspace>>("/mail/lists/recipients", { address, recipients });
  return response.data.data;
}

// Tek abone ekler/cikarir. `recipients` GONDERILMEZ: sunucu guncel kumeyi kendi okuyup ustune
// fark uygular. Buradan tam liste gonderilseydi, ekran tazelenmeden once baskasinin ekledigi
// abone sessizce silinirdi.
export async function subscribeToMailList(address: string, subscriber: string) {
  const response = await apiClient.put<ApiEnvelope<MailWorkspace>>("/mail/lists/recipients", {
    address,
    add: [subscriber]
  });
  return response.data.data;
}

export async function unsubscribeFromMailList(address: string, subscriber: string) {
  const response = await apiClient.put<ApiEnvelope<MailWorkspace>>("/mail/lists/recipients", {
    address,
    remove: [subscriber]
  });
  return response.data.data;
}
// EK ADRES (alias) ekleme/cikarma. LISTE ADRESI de gonderilir: sunucu iki alan adini da
// dogrulayabilsin diye. Yalniz alias gonderilseydi, kendi alan adindaki bir adresi BASKASININ
// listesine baglamak (ya da ondan sokmek) denetimden kacardi.
export async function addMailListAlias(listAddress: string, alias: string) {
  const response = await apiClient.post<ApiEnvelope<MailWorkspace>>("/mail/lists/aliases", {
    listAddress,
    alias
  });
  return response.data.data;
}

// DELETE govdesi ara katmanlarda guvenilir tasinmaz -> iki adres de sorgu dizesinde gider.
export async function removeMailListAlias(listAddress: string, alias: string) {
  const response = await apiClient.delete<ApiEnvelope<MailWorkspace>>("/mail/lists/aliases", {
    params: { listAddress, alias }
  });
  return response.data.data;
}

export async function deleteMailList(address: string) {
  const response = await apiClient.delete<ApiEnvelope<MailWorkspace>>(`/mail/lists/${encodeURIComponent(address)}`);
  return response.data.data;
}
// Panelin disaridan erisilen kok adresi. Liste iletilerine konan List-Unsubscribe baglantisi
// bundan kurulur; BOS ise baslik hic eklenmez (tahmin edilmis, calismayan baglanti gondermeyiz).
export async function getMailPublicBaseUrl() {
  const response = await apiClient.get<ApiEnvelope<string>>("/mail/public-base-url");
  return response.data.data;
}

export async function setMailPublicBaseUrl(baseUrl: string) {
  const response = await apiClient.put<ApiEnvelope<string>>("/mail/public-base-url", { baseUrl });
  return response.data.data;
}

export async function getMailListeners() {
  const response = await apiClient.get<ApiEnvelope<MailListener[]>>("/mail/listeners");
  return response.data.data;
}

export async function setMailDomainQuota(domain: string, maxSizeMB: number) {
  const response = await apiClient.post<ApiEnvelope<MailWorkspace>>("/mail/domains/quota", { domain, maxSizeMB });
  return response.data.data;
}

export async function setMailAccountQuota(address: string, maxSizeMB: number) {
  const response = await apiClient.post<ApiEnvelope<MailWorkspace>>("/mail/accounts/quota", { address, maxSizeMB });
  return response.data.data;
}

export type MailAutoResponder = {
  address: string;
  enabled: boolean;
  subject: string;
  message: string;
};

export async function getAutoResponder(address: string) {
  const response = await apiClient.get<ApiEnvelope<MailAutoResponder>>("/mail/accounts/autoresponder", { params: { address } });
  return response.data.data;
}

export async function setAutoResponder(payload: MailAutoResponder) {
  const response = await apiClient.post<ApiEnvelope<MailAutoResponder>>("/mail/accounts/autoresponder", payload);
  return response.data.data;
}

export type MailDnsRecord = {
  type: string;
  host: string;
  value: string;
  priority: number | null;
  purpose: string;
};

export type MailDns = {
  domain: string;
  mailHost: string;
  serverAddress: string;
  records: MailDnsRecord[];
};

export type ApplyMailDnsResult = {
  zoneName: string;
  appliedCount: number;
};

export async function getMailDns(domain: string) {
  const response = await apiClient.get<ApiEnvelope<MailDns>>(`/mail/dns/${encodeURIComponent(domain)}`);
  return response.data.data;
}

export type MailDiagnosticCheck = {
  key: string;
  status: "ok" | "fail" | "unknown";
  expected: string;
  found: string;
};

export type MailDiagnostics = {
  domain: string;
  mailHost: string;
  serverAddress: string;
  checks: MailDiagnosticCheck[];
};

/// Panelin domainin MX/A/SPF/DKIM/DMARC/PTR kayıtlarını canlı kontrolü.
export async function getMailDiagnostics(domain: string) {
  const response = await apiClient.get<ApiEnvelope<MailDiagnostics>>(`/mail/diagnostics/${encodeURIComponent(domain)}`);
  return response.data.data;
}

export async function applyMailDns(domain: string) {
  const response = await apiClient.post<ApiEnvelope<ApplyMailDnsResult>>(`/mail/dns/${encodeURIComponent(domain)}/apply`);
  return response.data;
}

export type RdnsCheck = {
  expectedHostName: string;
  ipAddress: string;
  ptrHostName: string;
  hasPtr: boolean;
  matches: boolean;
  forwardConfirmed: boolean;
  statusMessage: string;
};

export async function checkRdns(host?: string) {
  const query = host ? `?host=${encodeURIComponent(host)}` : "";
  const response = await apiClient.get<ApiEnvelope<RdnsCheck>>(`/mail/rdns${query}`);
  return response.data.data;
}

export type MailCertificate = {
  active: boolean;
  domain: string;
  hostName: string;
  source: string;
  notAfterUtc: string | null;
  suggestedContactEmail: string;
};

export async function getMailCertificate() {
  const response = await apiClient.get<ApiEnvelope<MailCertificate>>("/mail/certificate");
  return response.data.data;
}

export async function issueMailCertificate(domain: string, hostName: string, contactEmail: string) {
  const response = await apiClient.post<ApiEnvelope<MailCertificate>>("/mail/certificate", {
    domain,
    hostName,
    contactEmail,
    useStaging: false
  });
  return response.data.data;
}

export type WebmailOpen = {
  url: string;
};

/// Roundcube webmail'i (gerekirse kurup) açacak URL'i döndürür.
export async function openWebmail() {
  const response = await apiClient.post<ApiEnvelope<WebmailOpen>>("/mail/webmail/open", {});
  return response.data.data;
}

export type WebmailDiagnostics = {
  healthy: boolean;
  report: string;
  url?: string | null;
};

/// Webmail tanılama: "neden açılmıyor"u tek raporda toplar (IIS binding, FastCGI eklentileri,
/// Roundcube kök yanıtı, hata günlüğü). Uç ve çevirileri vardı ama arayüzde çağıran yoktu —
/// yani webmail açılmadığında hazır olan tek teşhis aracına kullanıcı erişemiyordu.
export async function diagnoseWebmail() {
  const response = await apiClient.get<ApiEnvelope<WebmailDiagnostics>>("/mail/webmail/diagnose");
  return response.data.data;
}

export async function getMailSpam() {
  const response = await apiClient.get<ApiEnvelope<MailSpamSettings>>("/mail/spam");
  return response.data.data;
}

export async function updateMailSpam(settings: MailSpamSettings) {
  const response = await apiClient.put<ApiEnvelope<MailSpamSettings>>("/mail/spam", settings);
  return response.data.data;
}

export type MailOutboundLimit = {
  domain: string;
  /** Saatte gönderilebilecek en fazla ileti; 0 = limitsiz. */
  messagesPerHour: number;
  /**
   * Limit mail sunucusunda GERÇEKTEN kurulu mu. "0" iki ayrı şey olabilirdi — "limitsiz seçildi"
   * ve "kurulamadı"; ikisini karıştırmak sınırsız sanılan bir sunucu demek olurdu.
   */
  configured: boolean;
};

export async function getOutboundLimit(domain: string) {
  const response = await apiClient.get<ApiEnvelope<MailOutboundLimit>>("/mail/domains/outbound-limit", {
    params: { domain }
  });
  return response.data.data;
}

/** Giden posta limiti (0 = kaldır). Yalnız yönetici. */
export async function setOutboundLimit(domain: string, messagesPerHour: number) {
  const response = await apiClient.put<ApiEnvelope<MailOutboundLimit>>("/mail/domains/outbound-limit", {
    domain,
    messagesPerHour
  });
  return response.data.data;
}

/** Kuyruktaki iletinin TEK alıcısı için teslim durumu. */
export type MailQueueRecipient = {
  address: string;
  /** Durumun KISA adı (rozet). */
  status: string;
  /**
   * Sunucunun durum nesnesinin HAM metni. Sunucu tarafında bilerek ayrıştırılmadı (alan adları
   * doğrulanmadı) — arayüz de AYRIŞTIRMAZ, olduğu gibi gösterir. Uydurma bir alandan okumak,
   * hata sebebini sessizce boş gösterirdi.
   */
  statusDetail: string;
  retryCount: number;
  retryDueUtc: string | null;
  queueName: string;
};

export type MailQueueMessage = {
  id: string;
  /** Zarf göndericisi. Boş = geri dönüş iletisi (bounce). */
  returnPath: string;
  createdAtUtc: string | null;
  nextRetryUtc: string | null;
  sizeBytes: number;
  receivedFromIp: string;
  recipients: MailQueueRecipient[];
};

export type MailQueue = {
  /** Posta sunucusu kurulu mu? */
  installed: boolean;
  /**
   * Yönetim arayüzüne bağlanılıp kuyruk GERÇEKTEN okunabildi mi? false iken liste boş gelir —
   * bunu "kuyruk boş" diye göstermek, sunucu çökmüşken yöneticiye "her şey yolunda" demek olurdu.
   */
  configured: boolean;
  /** Sunucudaki gerçek kuyruk uzunluğu; `messages` kırpılmış olabilir. */
  totalCount: number;
  messages: MailQueueMessage[];
};

/** Sunucunun kırptığı üst sınır; arayüz de aynı sınırı kullanır ki istenen ile gelen aynı olsun. */
export const MAIL_QUEUE_MAX_LIMIT = 500;

/** Giden posta kuyruğu. Yalnız yönetici. */
export async function getMailQueue(limit: number) {
  const response = await apiClient.get<ApiEnvelope<MailQueue>>("/mail/queue", {
    params: { limit: Math.min(Math.max(1, limit), MAIL_QUEUE_MAX_LIMIT) }
  });
  return response.data.data;
}

/** Tek iletinin ayrıntısı (alıcı başına durum). 404 = `mail.queue_message_not_found`. */
export async function getMailQueueMessage(id: string) {
  const response = await apiClient.get<ApiEnvelope<MailQueueMessage>>(`/mail/queue/${encodeURIComponent(id)}`);
  return response.data.data;
}

/** İletiyi teslim edilmeden siler (geri dönüşü yok). Yalnız yönetici. */
export async function deleteMailQueueMessage(id: string) {
  const response = await apiClient.delete<ApiEnvelope<boolean>>(`/mail/queue/${encodeURIComponent(id)}`);
  return response.data.data;
}

export type MailRelay = {
  installed: boolean;
  configured: boolean;
  enabled: boolean;
  host: string;
  port: number;
  /** `smtp` ya da `lmtp`. */
  protocol: string;
  implicitTls: boolean;
  allowInvalidCerts: boolean;
  username: string;
  /**
   * Panelde saklı bir röle parolası VAR MI — parolanın kendisi hiçbir yanıtta dönmez. Arayüz de
   * parolayı göstermeye çalışmaz; yalnızca "kayıtlı" durumunu yazar.
   */
  hasPassword: boolean;
};

export type SetMailRelayPayload = {
  enabled: boolean;
  host: string;
  port: number;
  protocol: string;
  implicitTls: boolean;
  allowInvalidCerts: boolean;
  username: string;
  /** BOŞ gönderilirse sunucudaki saklı parola KORUNUR (silinmez). */
  password: string;
};

/** Giden posta rölesi (smarthost) ayarı. Yalnız yönetici. */
export async function getMailRelay() {
  const response = await apiClient.get<ApiEnvelope<MailRelay>>("/mail/relay");
  return response.data.data;
}

/** Röleyi kurar ya da (enabled=false) kaldırır. Yalnız yönetici. */
export async function setMailRelay(payload: SetMailRelayPayload) {
  const response = await apiClient.put<ApiEnvelope<MailRelay>>("/mail/relay", payload);
  return response.data.data;
}

export type MailClientServer = {
  /** `imap`, `pop3` ya da `smtp`. */
  protocol: string;
  host: string;
  port: number;
  /** `SSL` (doğrudan TLS) ya da `STARTTLS`. */
  socketType: string;
};

export type MailClientSettings = {
  domain: string;
  hostName: string;
  servers: MailClientServer[];
};

/**
 * Bir alan adının posta istemcisi ayarları — sunucunun GERÇEK dinleyicilerinden türetilir.
 * 404 (`mail.client_settings_unavailable`) = alan adı panelde tanımsız ya da dinleyici okunamadı.
 */
export async function getMailClientSettings(domain: string) {
  const response = await apiClient.get<ApiEnvelope<MailClientSettings>>(
    `/mail/client-settings/${encodeURIComponent(domain)}`);
  return response.data.data;
}
