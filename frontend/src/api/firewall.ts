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

export async function blockPort(port: number, protocol: string, ruleName: string) {
  const response = await apiClient.post<ApiEnvelope<FirewallRule>>("/firewall/block", {
    port,
    protocol,
    ruleName
  });
  return response.data.data;
}
