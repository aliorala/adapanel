/**
 * SİTE ARAÇLARININ ROL HARİTASI — backend'in `[Authorize(Roles = ...)]` listesiyle AYNI olmalı.
 *
 * Tek kaynak: hem site panosundaki araç kartları (SiteDetailView) hem de "Barındırma ve DNS /
 * Başlarken" listesi (WebsiteWorkspaceView) buradan okur. İkisi ayrı listeler tuttuğunda biri
 * güncellenip diğeri unutuluyordu: müşteriye backend'in İZİN VERDİĞİ SSL/İstatistik/WordPress
 * gizleniyor, tıklanınca hiçbir şey olmayan kartlar ise gösteriliyordu.
 *
 * Her satırın karşılığı (kontrol edildi):
 *   hosting  → SiteHostingSettingsController (GET/PUT ayarlar: müşteri dahil; komut/göç uçları bayi+yönetici)
 *   php      → PhpController (site uçları müşteri dahil; kurulum/genel ini yalnız yönetici)
 *   ssl      → SslController (müşteri dahil; toplu yenileme yalnız yönetici)
 *   stats    → StatisticsController, wp → WordPressController, files → FilesController,
 *   backup   → BackupController, protected → SitesController (korumalı dizinler), mail → MailController,
 *   logs     → LogsController GET site/{ad} (müşteri dahil; döndürme bayi+yönetici)
 *   toolkit  → SiteHostingSettingsController komut uçları (bayi+yönetici)
 *   asp/dns/proxy/ftp/apps → AspController / DnsController / ProxyController / FtpController /
 *                             AppRuntimeController (bayi+yönetici)
 *   webdb/cron/cache/migrate/store → sunucu düzeyi, yalnız yönetici
 */
export type PanelRole = "Administrator" | "Reseller" | "Customer" | string;

const ADMIN = ["Administrator"] as const;
const STAFF = ["Administrator", "Reseller"] as const;
const EVERYONE = ["Administrator", "Reseller", "Customer"] as const;

export const SITE_TOOL_ROLES: Readonly<Record<string, readonly string[]>> = {
  hosting: EVERYONE,
  php: EVERYONE,
  ssl: EVERYONE,
  files: EVERYONE,
  protected: EVERYONE,
  stats: EVERYONE,
  backup: EVERYONE,
  mail: EVERYONE,
  wp: EVERYONE,
  logs: EVERYONE,
  toolkit: STAFF,
  asp: STAFF,
  dns: STAFF,
  proxy: STAFF,
  ftp: STAFF,
  apps: STAFF,
  webdb: ADMIN,
  domaindb: ADMIN,
  cron: ADMIN,
  cache: ADMIN,
  migrate: ADMIN,
  store: ADMIN
};

/** Rol bu aracı kullanabilir mi? Haritada olmayan araç KAPALI sayılır (varsayılan: reddet). */
export function canUseSiteTool(toolId: string, role: PanelRole | undefined | null): boolean {
  if (!role) return false;
  return SITE_TOOL_ROLES[toolId]?.includes(role) ?? false;
}
