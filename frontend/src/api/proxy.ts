import { apiClient } from "./client";

type ApiEnvelope<T> = {
  success: boolean;
  data: T;
  message: string;
  code: number;
};

export type RewriteRuleRecord = {
  name: string;
  matchPattern: string;
  rewriteUrl: string;
  stopProcessing: boolean;
  appendQueryString: boolean;
  ignoreCase: boolean;
};

export type ReverseProxyConfiguration = {
  siteName: string;
  webConfigPath: string;
  isArrEnabled: boolean;
  isProxyEnabled: boolean;
  destinationUrl: string;
  mountPath: string;
  hasHtmlLinkRewrite: boolean;
  preserveHostHeader: boolean;
  reverseRewriteHostInResponseHeaders: boolean;
  rewriteRules: RewriteRuleRecord[];
};

export type SaveReverseProxyPayload = {
  destinationUrl: string;
  mountPath: string;
  enableHtmlLinkRewrite: boolean;
};

export type SaveRewriteRulePayload = {
  name: string;
  matchPattern: string;
  rewriteUrl: string;
  stopProcessing: boolean;
  appendQueryString: boolean;
  ignoreCase: boolean;
};

export async function getSiteProxyConfiguration(siteName: string) {
  const response = await apiClient.get<ApiEnvelope<ReverseProxyConfiguration>>(`/sites/${encodeURIComponent(siteName)}/proxy`);
  return response.data.data;
}

export async function saveSiteReverseProxy(siteName: string, payload: SaveReverseProxyPayload) {
  const response = await apiClient.post<ApiEnvelope<ReverseProxyConfiguration>>(`/sites/${encodeURIComponent(siteName)}/proxy`, payload);
  return response.data.data;
}

export async function removeSiteReverseProxy(siteName: string) {
  const response = await apiClient.delete<ApiEnvelope<ReverseProxyConfiguration>>(`/sites/${encodeURIComponent(siteName)}/proxy`);
  return response.data.data;
}

export async function saveSiteRewriteRule(siteName: string, payload: SaveRewriteRulePayload) {
  const response = await apiClient.post<ApiEnvelope<ReverseProxyConfiguration>>(`/sites/${encodeURIComponent(siteName)}/rewrite`, payload);
  return response.data.data;
}
