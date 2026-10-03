import { apiClient } from "./client";
import { UserFacingError } from "../utils/apiError";

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

function measuredConfiguration(envelope: ApiEnvelope<ReverseProxyConfiguration>, siteName: string): ReverseProxyConfiguration {
  const value = envelope?.data;
  const flags = ["isArrEnabled", "isProxyEnabled", "hasHtmlLinkRewrite", "preserveHostHeader", "reverseRewriteHostInResponseHeaders"] as const;
  const strings = ["webConfigPath", "destinationUrl", "mountPath"] as const;
  if (envelope?.success !== true || !value || typeof value.siteName !== "string" || value.siteName.toLowerCase() !== siteName.toLowerCase() ||
    flags.some(key => typeof value[key] !== "boolean") || strings.some(key => typeof value[key] !== "string") || !Array.isArray(value.rewriteRules) ||
    value.rewriteRules.some(rule => !rule || [rule.name, rule.matchPattern, rule.rewriteUrl].some(field => typeof field !== "string") ||
      [rule.stopProcessing, rule.appendQueryString, rule.ignoreCase].some(field => typeof field !== "boolean"))) {
    throw new UserFacingError("panel.proxy.resultUnconfirmed");
  }
  return value;
}

export async function getSiteProxyConfiguration(siteName: string) {
  const response = await apiClient.get<ApiEnvelope<ReverseProxyConfiguration>>(`/sites/${encodeURIComponent(siteName)}/proxy`);
  return measuredConfiguration(response.data, siteName);
}

export async function saveSiteReverseProxy(siteName: string, payload: SaveReverseProxyPayload) {
  const response = await apiClient.post<ApiEnvelope<ReverseProxyConfiguration>>(`/sites/${encodeURIComponent(siteName)}/proxy`, payload);
  return measuredConfiguration(response.data, siteName);
}

export async function removeSiteReverseProxy(siteName: string) {
  const response = await apiClient.delete<ApiEnvelope<ReverseProxyConfiguration>>(`/sites/${encodeURIComponent(siteName)}/proxy`);
  return measuredConfiguration(response.data, siteName);
}

export async function saveSiteRewriteRule(siteName: string, payload: SaveRewriteRulePayload) {
  const response = await apiClient.post<ApiEnvelope<ReverseProxyConfiguration>>(`/sites/${encodeURIComponent(siteName)}/rewrite`, payload);
  return measuredConfiguration(response.data, siteName);
}
