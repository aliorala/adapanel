/*
 * GÜVENLİK DUVARI KURALININ EYLEMİ — TEK KAYNAK.
 *
 * NEDEN AYRI DOSYA: aynı kural PANELDE İKİ YERDE çiziliyor (Güvenlik ekranının "Windows
 * Firewall" sekmesi ve Sunucu Araçları > Windows Firewall). SecurityView bu çeviriyi bir
 * tur önce kendi içinde çözmüştü; FirewallView çözmemişti ve ÖLÇÜLDÜ (kurulu panel, kural
 * listesi): 16 satırın 16'sında Windows'un ham "Allow" değeri Türkçe panelde İngilizce
 * basılıyordu — üstelik hemen üstündeki formun düğmesi aynı eyleme "İzin ver" diyordu.
 * Kopyalamak yerine tek kaynağa alındı ki üçüncü bir ekran eklendiğinde aynı fark tekrar
 * doğmasın.
 *
 * TANINMAYAN DEĞER ÇEVRİLMEZ, HAM BASILIR: Windows'un döndürdüğü değer bu iki dizgeyle
 * sınırlı olmayabilir. Bilinmeyen bir değere "Engelle" demek, kullanıcıya ölçülmemiş bir
 * iddia sunmak olurdu.
 */
export function normalizeFirewallAction(action: string): "allow" | "block" | "unknown" {
  const normal = action.trim().toLowerCase();
  if (normal === "allow") return "allow";
  return normal === "block" ? "block" : "unknown";
}

/**
 * Eylemin kullanıcıya gösterilecek sözcüğü. `t` dışarıdan alınır: bu dosya bir bileşen değil,
 * `useI18n()` yalnız kurulum (setup) bağlamında çağrılabilir.
 */
export function firewallActionLabel(t: (key: string) => string, action: string): string {
  const kind = normalizeFirewallAction(action);
  if (kind === "allow") return t("panel.firewall.allow");
  return kind === "block" ? t("panel.firewall.block") : action;
}
