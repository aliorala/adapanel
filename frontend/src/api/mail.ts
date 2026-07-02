import { apiClient } from "./client";

type ApiEnvelope<T> = {
  success: boolean;
  data: T;
  message: string;
  code: number;
};

export type MailDomain = { name: string; active: boolean; accountCount: number; maxSizeMB: number; allocatedMB: number };
export type MailAccount = { address: string; active: boolean; maxSizeMB: number; currentSizeMB: number };
export type MailAlias = { address: string; target: string; active: boolean };
export type MailList = { address: string; recipients: string[] };
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
export async function createMailList(domain: string, address: string, recipients: string[]) {
  const response = await apiClient.post<ApiEnvelope<MailWorkspace>>("/mail/lists", { domain, address, recipients });
  return response.data.data;
}
export async function setMailListRecipients(address: string, recipients: string[]) {
  const response = await apiClient.put<ApiEnvelope<MailWorkspace>>("/mail/lists/recipients", { address, recipients });
  return response.data.data;
}
export async function deleteMailList(address: string) {
  const response = await apiClient.delete<ApiEnvelope<MailWorkspace>>(`/mail/lists/${encodeURIComponent(address)}`);
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

export async function getMailSpam() {
  const response = await apiClient.get<ApiEnvelope<MailSpamSettings>>("/mail/spam");
  return response.data.data;
}

export async function updateMailSpam(settings: MailSpamSettings) {
  const response = await apiClient.put<ApiEnvelope<MailSpamSettings>>("/mail/spam", settings);
  return response.data.data;
}
