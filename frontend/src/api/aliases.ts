import { apiClient } from "./client";

type ApiEnvelope<T> = { success: boolean; data: T; message: string; code: number };

export type SiteAlias = {
  hostName: string;
  /** true ise ziyaretçi ana alan adına 301 ile yönlendirilir. */
  redirectToPrimary: boolean;
  hasHttps: boolean;
  includesWww: boolean;
};

export type SiteAliasWorkspace = {
  siteName: string;
  /** Yönlendirmenin hedefi olan ana alan adı; boşsa sitenin hiç host adı yok. */
  primaryHostName: string;
  aliases: SiteAlias[];
  /**
   * Kanonik yonlendirme kuralinin web.config'e GERCEKTEN yazildiginin UC DEGERLI olcumu.
   * Yalniz ekle/sil yollarinda dolar; salt okumada OLCULMEDI kalir.
   */
  redirectRule?: {
    measured: boolean;
    ruleExpected: boolean;
    rulePresent: boolean;
    redirectedHostCount: number;
    detail: string;
    applied: boolean;
    notApplied: boolean;
    unverified: boolean;
    noticeKey: string;
  };
  /** Bos ise olculdu ve yolunda; dolu ise gosterilecek uyari anahtari. */
  redirectRuleNoticeKey?: string;
};

export async function getSiteAliases(siteName: string) {
  const response = await apiClient.get<ApiEnvelope<SiteAliasWorkspace>>(
    `/sites/${encodeURIComponent(siteName)}/aliases`
  );
  return response.data.data;
}

export async function addSiteAlias(
  siteName: string,
  payload: { hostName: string; redirectToPrimary: boolean; includeWww: boolean }
) {
  const response = await apiClient.post<ApiEnvelope<SiteAliasWorkspace>>(
    `/sites/${encodeURIComponent(siteName)}/aliases`,
    payload
  );
  return response.data.data;
}

export async function removeSiteAlias(siteName: string, hostName: string) {
  const response = await apiClient.delete<ApiEnvelope<SiteAliasWorkspace>>(
    `/sites/${encodeURIComponent(siteName)}/aliases`,
    { params: { hostName } }
  );
  return response.data.data;
}
