import { apiClient } from "./client";

type ApiEnvelope<T> = {
  success: boolean;
  data: T;
  message: string;
  code: number;
};

export type FirewallRule = {
  displayName: string;
  direction: string;
  action: string;
  protocol: string;
  localPort: string;
  enabled: boolean;
  /**
   * Bu satır güvenlik duvarından GERİ OKUNDU mu. `false` ise `enabled` bir ölçüm değildir —
   * kural yazıldı ama durumu doğrulanamadı. Listeleme yolundan gelen satırlarda her zaman true.
   */
  verified: boolean;
};

export type PortStatus = {
  port: number;
  rangeEnd: number;
  protocol: string;
  serviceKey: string;
  category: string;
  required: boolean;
  listening: boolean;
  listenScope: string;
  firewallAllowed: boolean;
};

export async function getFirewallRules() {
  const response = await apiClient.get<ApiEnvelope<FirewallRule[]>>("/firewall");
  return response.data.data;
}

export async function getPortStatus() {
  const response = await apiClient.get<ApiEnvelope<PortStatus[]>>("/firewall/ports");
  return response.data.data;
}

export async function allowPort(port: number, protocol: string, ruleName: string) {
  const response = await apiClient.post<ApiEnvelope<FirewallRule>>("/firewall/allow", {
    port,
    protocol,
    ruleName
  });
  return response.data.data;
}

// Panelin kendi portu, uzak masaüstü ve 80/443 sunucu tarafında REDDEDİLİR: panel kendi kapısını
// kilitleyemez (Windows'ta engelleme izni ezer, geri almak da mümkün olmazdı).
export async function blockPort(port: number, protocol: string, ruleName: string) {
  const response = await apiClient.post<ApiEnvelope<FirewallRule>>("/firewall/block", {
    port,
    protocol,
    ruleName
  });
  return response.data.data;
}

// Panelin oluşturduğu bir kuralı kaldırır. Yazma yetkisi olup silme yetkisi olmaması tek yönlü
// bir kapıydı: yanlışlıkla eklenen bir engelleme kuralı panelden geri alınamıyordu.
export async function removeFirewallRule(ruleName: string) {
  await apiClient.delete<ApiEnvelope<object>>(`/firewall/rules/${encodeURIComponent(ruleName)}`);
}
