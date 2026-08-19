import { apiClient } from "./client";

type ApiEnvelope<T> = {
  success: boolean;
  data: T;
  message: string;
  code: number;
};

export type SiteBuilderOption = {
  id: string;
  labelKey: string;
};

export type SiteBuilderPalette = {
  id: string;
  labelKey: string;
  accent: string;
  surface: string;
  text: string;
};

export type SiteBuilderCatalog = {
  templates: SiteBuilderOption[];
  palettes: SiteBuilderPalette[];
  fonts: SiteBuilderOption[];
  sectionTypes: string[];
  maxImageBytes: number;
  allowedImageExtensions: string[];
};

export type SiteBuilderItem = {
  title: string;
  text: string;
  imageName: string;
};

export type SiteBuilderSection = {
  type: string;
  enabled: boolean;
  heading: string;
  body: string;
  imageName: string;
  items: SiteBuilderItem[];
  email: string;
  phone: string;
  address: string;
};

export type SiteBuilderProject = {
  siteName: string;
  templateId: string;
  paletteId: string;
  fontId: string;
  siteTitle: string;
  tagline: string;
  metaDescription: string;
  language: string;
  sections: SiteBuilderSection[];
  updatedAtUtc: string | null;
  published: boolean;
  publishedAtUtc: string | null;
  publishedPath: string;
  managedFiles: string[];
};

/**
 * Yayın planı. `conflicts` boş değilse kullanıcı ONAY vermeden yayın reddedilir —
 * kutuda ezilecek dosyalar ADIYLA gösterilir, çünkü neyin ezileceğini görmeden verilen onay
 * bilgisiz bir onaydır.
 */
export type SiteBuilderPublishPlan = {
  siteName: string;
  targetPath: string;
  filesToWrite: string[];
  conflicts: string[];
  existingFileCount: number;
  requiresConfirmation: boolean;
  blockedReasonKey: string;
  canPublish: boolean;
};

export type SiteBuilderPublishResult = {
  siteName: string;
  targetPath: string;
  writtenFiles: string[];
  overwrittenFiles: string[];
  publishedAtUtc: string;
  previewUrl: string;
};

export type SiteBuilderImage = {
  name: string;
  sizeBytes: number;
  uploadedAtUtc: string;
};

const base = (siteName: string) => `/sitebuilder/${encodeURIComponent(siteName)}`;

export async function getSiteBuilderCatalog() {
  const response = await apiClient.get<ApiEnvelope<SiteBuilderCatalog>>("/sitebuilder/catalog");
  return response.data.data;
}

export async function getSiteBuilderProject(siteName: string) {
  const response = await apiClient.get<ApiEnvelope<SiteBuilderProject>>(base(siteName));
  return response.data.data;
}

export async function saveSiteBuilderProject(siteName: string, project: SiteBuilderProject) {
  const response = await apiClient.put<ApiEnvelope<SiteBuilderProject>>(base(siteName), {
    templateId: project.templateId,
    paletteId: project.paletteId,
    fontId: project.fontId,
    siteTitle: project.siteTitle,
    tagline: project.tagline,
    metaDescription: project.metaDescription,
    language: project.language,
    sections: project.sections
  });
  return response.data.data;
}

/**
 * Önizleme HTML'i METİN olarak gelir ve yalıtılmış (sandbox) bir çerçevede gösterilir.
 * Sunucu bunu `text/html` olarak servis etmiyor: etseydi sayfa panelin kendi kökeninde açılır ve
 * kaçırmadaki tek bir eksik, panel oturumunu ele geçiren bir script'e dönüşürdü.
 */
export async function getSiteBuilderPreview(siteName: string) {
  const response = await apiClient.get<ApiEnvelope<string>>(`${base(siteName)}/preview`);
  return response.data.data;
}

export async function getSiteBuilderPublishPlan(siteName: string) {
  const response = await apiClient.get<ApiEnvelope<SiteBuilderPublishPlan>>(`${base(siteName)}/publish-plan`);
  return response.data.data;
}

export async function publishSiteBuilder(siteName: string, overwriteExistingFiles: boolean) {
  const response = await apiClient.post<ApiEnvelope<SiteBuilderPublishResult>>(`${base(siteName)}/publish`, {
    overwriteExistingFiles
  });
  return response.data.data;
}

export async function getSiteBuilderImages(siteName: string) {
  const response = await apiClient.get<ApiEnvelope<SiteBuilderImage[]>>(`${base(siteName)}/images`);
  return response.data.data;
}

export async function uploadSiteBuilderImage(siteName: string, file: File) {
  const form = new FormData();
  form.append("file", file);
  const response = await apiClient.post<ApiEnvelope<SiteBuilderImage>>(`${base(siteName)}/images`, form);
  return response.data.data;
}

export async function deleteSiteBuilderImage(siteName: string, imageName: string) {
  await apiClient.delete(`${base(siteName)}/images/${encodeURIComponent(imageName)}`);
}

/** Panel içinde görseli göstermek için adres (aynı köken, oturum çerezi ile). */
export function siteBuilderImageUrl(siteName: string, imageName: string) {
  return `${apiClient.defaults.baseURL ?? ""}${base(siteName)}/images/${encodeURIComponent(imageName)}`;
}
