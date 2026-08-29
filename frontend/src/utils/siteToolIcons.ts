/**
 * SİTE ARAÇ İKONLARI — tek kaynak.
 *
 * ÖLÇÜLEN SORUN (canlı panel 0.8.0, koyu tema): site satırındaki hızlı araç kutucukları ve site
 * panosundaki araç kartları ikon DEĞİL, uydurulmuş iki-üç harflik damgalar taşıyordu ve bu
 * damgalar **8.1px** (site satırı) / **8.91px** (araç kartı) çiziliyordu — okunabilir boyun çok
 * altında. Damga dili de tutarsızdı: bir kısmı Türkçe kısaltma (KOR = Korumalı dizinler,
 * BK = Yedekleme, IST = İstatistik), bir kısmı İngilizce (FL = Files, FT = FTP, BK), bir kısmı
 * marka (WP, SQL), bir kısmı teknoloji (JS, .N) ve biri de düpedüz simge (⚙, @). Yani aynı
 * satırda beş ayrı kısaltma dili vardı; hiçbiri öğrenilebilir değildi.
 *
 * Kısaltmayı büyütmek çözüm DEĞİLDİ: "KOR" 12px'te de "Korumalı dizinler" demez. Panelin zaten
 * bir ikon dili var (kenar menü, `MainLayout` > `NAV_ICONS`): 24x24 kutu, dolgusuz, `currentColor`
 * çizgi. Araçlar da o dile taşındı. Tam ad ZATEN yazılıydı ve KALDI — site panosunda kartın
 * altında görünür metin, site satırında ise `title` + `aria-label` olarak.
 *
 * AYIRT EDİLEBİLİRLİK, aynı ızgarada yan yana duran 15 ikon için gözetildi:
 *  - `files` düz klasör, `protected` kilitli klasör (ikisi aynı aileden ama biri kilitli),
 *  - `ssl` kilit, `protected` klasör+kilit (kilit tek başına yalnız sertifikada),
 *  - `asp` küp (.NET çalışma zamanı), `apps` terminal penceresi (Node/Python uygulamaları),
 *    `store` alışveriş çantası — üçü de "uygulama" ile ilgili ama üç ayrı biçim.
 *
 * Yeni bir araç eklenip ikonu unutulursa `siteToolIcon` boş bir daireye düşer (kenar menüdeki
 * `iconFor` ile aynı kalıp): araç KAYBOLMAZ, yalnız kimliksiz görünür.
 */
export const SITE_TOOL_ICONS: Record<string, string> = {
  // Barındırma ayarları — dişli (kenar menüdeki "Ayarlar" ile aynı biçim; burada komşusu yok).
  hosting:
    '<circle cx="12" cy="12" r="3"/><path d="M19.4 13.5a7.6 7.6 0 0 0 0-3l2-1.5-2-3.4-2.3 1a7.6 7.6 0 0 0-2.6-1.5L14 2.4h-4l-.5 2.7a7.6 7.6 0 0 0-2.6 1.5l-2.3-1-2 3.4 2 1.5a7.6 7.6 0 0 0 0 3l-2 1.5 2 3.4 2.3-1a7.6 7.6 0 0 0 2.6 1.5l.5 2.7h4l.5-2.7a7.6 7.6 0 0 0 2.6-1.5l2.3 1 2-3.4z"/>',
  // Dosya yöneticisi — düz klasör.
  files: '<path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"/>',
  // PHP — köşeli parantez + eğik çizgi (kod/çalışma zamanı).
  php: '<path d="M8.5 8 4.5 12l4 4M15.5 8l4 4-4 4M13.6 6.2l-3.2 11.6"/>',
  // ASP.NET çalışma zamanı — küp (paket/çalışma zamanı).
  asp: '<path d="M3 7.5 12 3l9 4.5-9 4.5z"/><path d="M3 7.5v9L12 21l9-4.5v-9M12 12v9"/>',
  // Posta — zarf.
  mail: '<rect x="3" y="5" width="18" height="14" rx="2"/><path d="M3.5 6.5 12 12.5l8.5-6"/>',
  // SSL sertifikası — kilit (kilit TEK BAŞINA yalnız burada).
  ssl: '<rect x="4.5" y="10.5" width="15" height="9.5" rx="2"/><path d="M8 10.5V7.5a4 4 0 0 1 8 0v3"/><path d="M12 14v2.5"/>',
  // Veritabanı — silindir.
  webdb:
    '<ellipse cx="12" cy="6" rx="7" ry="3"/><path d="M5 6v6c0 1.7 3.1 3 7 3s7-1.3 7-3V6M5 12v6c0 1.7 3.1 3 7 3s7-1.3 7-3v-6"/>',
  // FTP — karşılıklı aktarım okları.
  ftp: '<path d="M7 20V5M4 8l3-3 3 3M17 4v15M14 16l3 3 3-3"/>',
  // DNS / alan adı — küre.
  dns: '<circle cx="12" cy="12" r="8.5"/><path d="M3.5 12h17M12 3.5c2.5 2.3 2.5 14.7 0 17M12 3.5c-2.5 2.3-2.5 14.7 0 17"/>',
  // İstatistikler — sütun grafiği (kenar menüdeki nabız çizgisinden bilerek farklı).
  stats: '<path d="M4 20h16"/><path d="M6.5 20v-6M12 20V5M17.5 20v-9"/>',
  // WordPress — daire içinde "W".
  wp: '<circle cx="12" cy="12" r="8.5"/><path d="M6.6 9.2 9 15.6l1.9-5M11.4 9.2l2.4 6.4L16 9.9"/>',
  // Korumalı dizinler — kilitli klasör (`files` ile aynı aile, kilit farkı).
  protected:
    '<path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"/><rect x="9.5" y="12.5" width="5" height="4" rx="1"/><path d="M10.6 12.5v-1.1a1.4 1.4 0 0 1 2.8 0v1.1"/>',
  // Yedekleme — geri dönen ok.
  backup: '<path d="M21 12a9 9 0 1 1-3-6.7M21 4v5h-5"/>',
  // Uygulamalar (Node/Python) — terminal penceresi.
  apps: '<rect x="3" y="4.5" width="18" height="15" rx="2"/><path d="M3 8.5h18M7 12.5l2.5 2.5L7 17.5M12.5 17.5H17"/>',
  // Uygulama mağazası — alışveriş çantası.
  store: '<path d="M5.5 8h13l-1 12h-11z"/><path d="M9 8V6a3 3 0 0 1 6 0v2"/>',

  /*
   * SON ALTI ARAÇ (WebsiteWorkspaceView'in damgaları: PX, CR, LOG, CA, </>, GÖÇ).
   *
   * Üçü BİLEREK kenar menünün (`MainLayout` > `NAV_ICONS`) ikonuyla BİREBİR AYNI çizildi:
   * `cron`=`tasks`, `toolkit`=`tools`, `migrate`=`migration`. Aynı kavramın panelde iki ayrı
   * ikonu olması, adlandırmada yeni kapattığımız "bir kavram / bir ad" kuralını görsel tarafta
   * yeniden açardı. ÖLÇÜLEN BEDELİ: `cron` saati, ızgaradaki üçüncü daire oluyor (`dns` küre,
   * `wp` daire+W). Kabul edildi, çünkü her iki ekranda da ikonun YANINDA tam ad yazılı ve
   * kenar menüyle tutarlılık burada ayırt edilebilirlikten ağır basıyor.
   */
  // Ters vekil — trafiğin ORTADAN geçtiği düğüm (`ftp`nin dikey aktarım oklarından farklı).
  proxy: '<rect x="8.5" y="9" width="7" height="6" rx="1.5"/><path d="M2.5 12h6M15.5 12h6"/><path d="M19 9.5l2.5 2.5-2.5 2.5"/>',
  // Zamanlanmış görevler — saat (kenar menüdeki `tasks` ile aynı).
  cron: '<circle cx="12" cy="12" r="8.5"/><path d="M12 7v5l3.5 2"/>',
  // Günlükler — kısalan satır yığını (akan kayıt). `audit`in belge biçiminden BİLEREK farklı:
  // "Günlükler" ile "Denetim Kayıtları" ayrı kavramlar, ayrı görünmeliler.
  logs: '<path d="M4 5.5h16M4 10h16M4 14.5h11M4 19h7"/>',
  // Önbellek — şimşek (hız). Set içinde başka köşeli/dolgusuz zikzak yok.
  cache: '<path d="M13.5 2.5 5 13.5h6l-.5 8L19 10.5h-6z"/>',
  // Geliştirici araçları — İngiliz anahtarı (kenar menüdeki `tools` ile aynı).
  toolkit: '<path d="M14.5 6a3.5 3.5 0 0 0 4.6 4.3L21 12l-9 9-3-3 9-9-1.7-1.9A3.5 3.5 0 0 0 14.5 6z"/><path d="M7 14l-4 4 3 3 4-4"/>',
  // Göç — karşılıklı yatay taşıma okları (kenar menüdeki `migration` ile aynı).
  migrate: '<path d="M4 7h12M12 3l4 4-4 4"/><path d="M20 17H8M12 21l-4-4 4-4"/>',

  /*
   * SİTE ÇALIŞMA ALANININ ÜST SEKMELERİ (araç değil, araç GRUBU).
   *
   * Bu ikiliyi de buraya koymamın sebebi ölçüldü: `WebsiteWorkspaceView`in üst şeridi tam da bu
   * dosyanın başında "düpedüz simge" diye eleştirilen dili kullanıyordu — `▦`, `⚙`, `@`, `★`.
   * Alt listedeki 21 aracı ikona çevirip üstteki 4 sekmeyi yazı tipi simgesi bırakmak, ekranı
   * öncekinden daha karışık yapardı (aynı şeritte iki çizim dili). `hostingdns` ve `mail`
   * sekmeleri yukarıdaki `hosting`/`mail` ikonlarını aynen kullanır; eksik olan yalnız bu ikisi.
   */
  // Site panosu — dört kutulu ızgara (pano = birden çok kartın bir arada görünümü).
  dashboard:
    '<rect x="3.5" y="3.5" width="7" height="7" rx="1.5"/><rect x="13.5" y="3.5" width="7" height="7" rx="1.5"/><rect x="3.5" y="13.5" width="7" height="7" rx="1.5"/><rect x="13.5" y="13.5" width="7" height="7" rx="1.5"/>',
  // Başlarken — yıldız (set içinde `ai` ışıltısı dışında köşeli yıldız yok; ikisi ayrı ekranda).
  getStarted: '<path d="m12 3.5 2.6 5.4 5.9.8-4.3 4.1 1.1 5.9L12 16.9l-5.3 2.8 1.1-5.9-4.3-4.1 5.9-.8z"/>'
};

/**
 * Kimliğin ikonunu döndürür. Bilinmeyen kimlik boş bir daireye düşer — `MainLayout`taki
 * `iconFor` ile AYNI yedek: araç ekranda kalır, yalnız kimliksiz görünür.
 */
export function siteToolIcon(id: string): string {
  return SITE_TOOL_ICONS[id] ?? '<circle cx="12" cy="12" r="7"/>';
}
