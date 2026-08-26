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
  /**
   * İletişim FORMU açık mı. Açık olması formun basılacağını garanti etmez: alıcı adresi geçerli
   * olmalı ve panelin genel adresi tanımlı olmalı. Eksik koşul yayın planında ADIYLA gösterilir
   * (`contactFormBlockedReasonKey`) — çünkü gönderilemeyen bir form, ziyaretçinin gönderdiğini
   * sanması demektir ve bu hiç form koymamaktan kötüdür.
   */
  contactFormEnabled: boolean;
};

/** Ana sayfanın YANINDAKİ bir sayfa. Ana sayfa bu listede DEĞİL; içeriği `sections` alanında. */
export type SiteBuilderPage = {
  /** Adres parçası: `/{slug}/`. Yalnız küçük harf, rakam ve tire; sunucu da aynı kalıbı uygular. */
  slug: string;
  title: string;
  metaDescription: string;
  /** Menüde görünsün mü. Kapatmak sayfayı SİLMEZ; adresi bilen ziyaretçi yine ulaşır. */
  showInNav: boolean;
  sections: SiteBuilderSection[];
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
  /** ANA SAYFANIN bölümleri. */
  sections: SiteBuilderSection[];
  /** Ek sayfalar. Boş liste = tek sayfalık site. */
  pages: SiteBuilderPage[];
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
  /**
   * Kurucunun daha önce yazdığı, hâlâ diskte duran ama ARTIK üretilmeyen dosyalar (kaldırılan ya
   * da adı değiştirilen bir sayfa, tasarımdan çıkarılan bir görsel).
   *
   * Yayın bunları SİLMEZ — silme bu depoda müşteri sitesini yok edebilecek hata sınıfı ve geri
   * alınamaz. Ama sessiz de kalınmaz: kaldırılan bir sayfa adresinde durmaya devam eder ve
   * kullanıcı onu sildiğini SANMAMALIDIR. Liste tam da bu sürprizi engellemek için var.
   */
  orphanFiles: string[];
  /** İletişim formu istendiği hâlde basılamıyorsa sebebin çeviri anahtarı. Yayını ENGELLEMEZ. */
  contactFormBlockedReasonKey: string;
  pageCount: number;
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
  /** Bu yayından sonra diskte kalan, artık üretilmeyen kurucu dosyaları. SİLİNMEDİLER. */
  orphanFiles: string[];
  pageCount: number;
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
    sections: project.sections,
    pages: project.pages
  });
  return response.data.data;
}

/** Sayfa adresinin (slug) ARAYÜZ tarafındaki kalıbı. */
export const SITE_BUILDER_SLUG_PATTERN = /^[a-z0-9](?:[a-z0-9-]{0,46}[a-z0-9])?$/;

/** Bir sitede izin verilen en fazla EK sayfa sayısı (sunucudaki sınırla aynı). */
export const SITE_BUILDER_MAX_PAGES = 12;

/**
 * Kullanıcının yazdığından slug ÖNERİR — ama kaydetmez.
 *
 * Öneri ile zorlama farklı şeyler: sunucu geçersiz slug'lı sayfayı DÜŞÜRÜR, düzeltmez. Sebep,
 * kullanıcının yazmadığı bir adrese dosya yazmamak; her kaydetmede başka bir adrese düşen bir
 * "düzeltme", diskte her seferinde yeni bir öksüz sayfa bırakırdı. Burada yalnız ilk öneri
 * üretilir, son sözü kullanıcı söyler.
 */
export function suggestSiteBuilderSlug(title: string) {
  const harita: Record<string, string> = {
    ç: "c", ğ: "g", ı: "i", ö: "o", ş: "s", ü: "u",
    Ç: "c", Ğ: "g", İ: "i", Ö: "o", Ş: "s", Ü: "u"
  };

  return title
    .split("")
    .map((karakter) => harita[karakter] ?? karakter)
    .join("")
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, "-")
    .replace(/^-+|-+$/g, "")
    .slice(0, 48)
    .replace(/-+$/g, "");
}

/**
 * Önizleme HTML'i METİN olarak gelir ve yalıtılmış (sandbox) bir çerçevede gösterilir.
 * Sunucu bunu `text/html` olarak servis etmiyor: etseydi sayfa panelin kendi kökeninde açılır ve
 * kaçırmadaki tek bir eksik, panel oturumunu ele geçiren bir script'e dönüşürdü.
 */
export async function getSiteBuilderPreview(siteName: string, pageSlug = "") {
  const response = await apiClient.get<ApiEnvelope<string>>(`${base(siteName)}/preview`, {
    params: pageSlug ? { page: pageSlug } : undefined
  });
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
