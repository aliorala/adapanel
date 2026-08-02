import { apiClient } from "./client";

type ApiEnvelope<T> = { success: boolean; data: T; message: string; code: number };

/**
 * Panelin KENDİ alan adıyla, GEÇERLİ bir sertifikayla dışarıya açılması.
 *
 * Sözleşme: src/AdaPanel.Core/Dtos/PanelAccess/*.cs +
 * src/AdaPanel.Api/Controllers/PanelAccessController.cs (api/v1/panel-access — yalnız Administrator).
 *
 * NEDEN VAR: panel varsayılan olarak kendinden imzalı bir sertifikayla çalışır. Tarayıcı uyarıyı
 * "devam et" ile geçebilir; panele /mcp üzerinden bağlanan yapay zeka istemcileri GEÇEMEZ.
 *
 * KURAL: burada hiçbir varsayılan alan adı YOKTUR. hostName daima yöneticiden gelir; boşsa
 * özellik pasiftir.
 */

/** "unknown" = ÖLÇEMEDİK demektir, "sorun yok" DEMEK DEĞİLDİR. */
export type PanelAccessStatusLevel = "ok" | "warn" | "fail" | "unknown";

/** Kurulum zincirindeki adımlar — arayüzde bu sırayla, ayrı satırlar halinde gösterilir. */
export type PanelAccessStepKey =
  | "dns"
  | "site"
  | "certificate"
  | "renewal"
  | "proxy"
  | "external";

export type PanelAccessStep = {
  key: PanelAccessStepKey;
  status: PanelAccessStatusLevel;
  /** Rozette gösterilecek kısa değer (çözülen IP, kalan gün, "HTTP 200"). */
  value: string;
  /** Dile bağımsız teknik ayrıntı — çeviri YOK, olduğu gibi gösterilir. */
  detail: string;
};

export type PanelAccessStatus = {
  /** Boş = özellik pasif. */
  hostName: string;
  isConfigured: boolean;
  /** Kurulum tamamsa panelin dış adresi, aksi halde boş. */
  panelUrl: string;
  /** Yapay zeka istemcisine verilecek MCP uç noktası, aksi halde boş. */
  mcpEndpoint: string;
  siteName: string;
  /** Ters vekilin ilettiği yerel panel adresi (ör. https://localhost:8890). */
  backendUrl: string;
  /** letsencrypt | self-signed | none */
  certificateSource: string;
  /** true ise yapay zeka istemcileri bağlanamaz — özelliğin var oluş sebebi. */
  certificateIsSelfSigned: boolean;
  /** Bitişe kalan gün (ölçülemediyse null). Negatif = süresi dolmuş. */
  certificateDaysLeft: number | null;
  overallStatus: PanelAccessStatusLevel;
  /** ISO 8601 (UTC) — JSON'da string gelir. */
  checkedAtUtc: string;
  steps: PanelAccessStep[];
};

export type SetupPanelAccessPayload = {
  hostName: string;
  /** Boşsa panelde kayıtlı ortak ACME e-postası kullanılır. */
  contactEmail: string;
  useStaging: boolean;
};

/** Ölçüm dış ağa çıkar (DNS + TLS + HTTPS) ve birkaç saniye sürer → otomatik polling YAPMA. */
export async function getPanelAccessStatus() {
  const response = await apiClient.get<ApiEnvelope<PanelAccessStatus>>("/panel-access");
  return response.data.data;
}

/**
 * Kurulum: IIS ters vekil sitesi + Let's Encrypt sertifikası. DAKİKALAR sürebilir (ACME
 * doğrulaması) — arayüz bu sırada düğmeyi kilitlemeli, isteği tekrar göndermemeli.
 */
export async function setupPanelAccess(payload: SetupPanelAccessPayload) {
  const response = await apiClient.post<ApiEnvelope<PanelAccessStatus>>("/panel-access/setup", payload);
  return response.data.data;
}
