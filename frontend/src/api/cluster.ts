import { apiClient } from "./client";

type ApiEnvelope<T> = {
  success: boolean;
  data: T;
  message: string;
  code: number;
};

/**
 * Merkeze kayıtlı bir hizmet düğümü (Plesk Multi Server'daki "service node").
 *
 * API BELİRTECİ BURADA YOK ve olmayacak: uzak düğümün anahtarı o sunucuda yönetici yetkisi
 * demektir; arayüze dönerse tarayıcı geçmişinde ve ekran görüntüsünde yaşamaya başlar.
 * Arayüzün bilmesi gereken tek şey anahtarın var olup olmadığıdır — `hasToken`.
 */
export type ClusterNode = {
  id: string;
  label: string;
  /** Normalize edilmiş kök adres: `https://sunucu.ornek.test:8888` */
  address: string;
  enabled: boolean;
  verifyTlsCertificate: boolean;
  hasToken: boolean;
  createdAtUtc: string;
  /** Düğümden en son BAŞARIYLA yanıt alındığı an; hiç alınmadıysa null. */
  lastSeenAtUtc: string | null;
  lastError: string;
};

/** Düğümün kendi raporu — panelin kendi `system/overview` ve `server-health` biçimleriyle aynı. */
export type ClusterNodeInfo = {
  panelVersion: string;
  machineName: string;
  secureEntryPath: string;
  siteCount: number;
  defaultSiteRoot: string;
  overview: {
    cpuUsagePercent: number;
    memoryUsagePercent: number;
    totalMemoryBytes: number;
    usedMemoryBytes: number;
    uptime: string;
    disks: { name: string; label: string; usagePercent: number; totalBytes: number; freeBytes: number }[];
  };
  services: { name: string; label: string; status: string }[];
};

/** Toplu görünümün bir satırı. Ulaşılamayan düğüm satırı DÜŞMEZ; `reachable` false olur. */
export type ClusterNodeStatus = {
  node: ClusterNode;
  reachable: boolean;
  /** "cluster.node_unreachable" / "cluster.node_disabled" … Ulaşıldıysa boş. */
  statusKey: string;
  detail: string;
  durationMs: number;
  info: ClusterNodeInfo | null;
};

export type ClusterOverview = {
  nodes: ClusterNodeStatus[];
  /** YALNIZ ulaşılabilen düğümlerin siteleri. `unreachableCount > 0` ise bu sayı EKSİKTİR. */
  totalSites: number;
  reachableCount: number;
  unreachableCount: number;
  generatedAtUtc: string;
};

export type ClusterPlacement = {
  nodeId: string | null;
  nodeLabel: string;
  reasonKey: string;
  candidateCount: number;
};

export type ClusterNodeLink = {
  nodeId: string;
  url: string;
  /** Şu an daima true: bağlantı oturum DEVRETMEZ, düğümde giriş yapılır. */
  requiresSignIn: boolean;
};

export type ClusterSiteCreation = {
  nodeId: string;
  nodeLabel: string;
  siteName: string;
  placementWasAutomatic: boolean;
  remoteMessage: string;
};

export type SaveClusterNodePayload = {
  label: string;
  address: string;
  /** Düzenlemede BOŞ bırakılırsa mevcut anahtar korunur. */
  apiToken: string;
  enabled: boolean;
  verifyTlsCertificate: boolean;
};

export async function getClusterNodes() {
  const response = await apiClient.get<ApiEnvelope<ClusterNode[]>>("/cluster/nodes");
  return response.data.data;
}

export async function getClusterOverview() {
  const response = await apiClient.get<ApiEnvelope<ClusterOverview>>("/cluster/overview");
  return response.data.data;
}

export async function registerClusterNode(payload: SaveClusterNodePayload) {
  const response = await apiClient.post<ApiEnvelope<ClusterNode>>("/cluster/nodes", payload);
  return response.data.data;
}

export async function updateClusterNode(nodeId: string, payload: SaveClusterNodePayload) {
  const response = await apiClient.put<ApiEnvelope<ClusterNode>>(
    `/cluster/nodes/${encodeURIComponent(nodeId)}`,
    payload
  );
  return response.data.data;
}

export async function removeClusterNode(nodeId: string) {
  await apiClient.delete<ApiEnvelope<object>>(`/cluster/nodes/${encodeURIComponent(nodeId)}`);
}

export async function probeClusterNode(nodeId: string) {
  const response = await apiClient.post<ApiEnvelope<ClusterNodeStatus>>(
    `/cluster/nodes/${encodeURIComponent(nodeId)}/test`,
    {}
  );
  return response.data.data;
}

export async function getClusterNodeLink(nodeId: string) {
  const response = await apiClient.get<ApiEnvelope<ClusterNodeLink>>(
    `/cluster/nodes/${encodeURIComponent(nodeId)}/panel-link`
  );
  return response.data.data;
}

export async function createClusterSite(payload: { nodeId: string | null; name: string; hostName: string }) {
  const response = await apiClient.post<ApiEnvelope<ClusterSiteCreation>>("/cluster/sites", payload);
  return response.data.data;
}
