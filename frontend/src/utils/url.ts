// Backend bazı araç URL'lerini sunucu-yerel host'la (127.0.0.1 / localhost / ::1) döndürür.
// Uzak tarayıcı bunu açamaz (kendi makinesine gider). Paneli gördüğümüz host ile değiştiririz.
// TÜM "tarayıcıda aç" akışları (phpMyAdmin, Adminer, webmail, nginx vb.) bunu kullanmalı.
export function toReachableUrl(raw: string): string {
  if (!raw) {
    return raw;
  }

  try {
    const url = new URL(raw, window.location.origin);
    if (["localhost", "127.0.0.1", "::1"].includes(url.hostname)) {
      url.hostname = window.location.hostname;
    }
    return url.toString();
  } catch {
    return raw;
  }
}

/// Aracı yeni sekmede güvenli host ile açar.
export function openReachable(raw: string): void {
  const url = toReachableUrl(raw);
  if (url) {
    window.open(url, "_blank", "noopener");
  }
}
