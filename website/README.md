# AdaPanel — Web Sitesi / Website

Tek dosyalık, bağımsız (build gerektirmez) tanıtım sitesi. Türkçe + İngilizce (sağ üstten TR/EN).
Single-file, self-contained marketing site (no build step). Turkish + English (TR/EN toggle, top right).

## Yayınlama / Deploy

**Seçenek 1 — Kendi hostingin (adapanel.host):**
- `index.html` dosyasını sitenin köküne yükle (FTP / dosya yöneticisi). Bitti.
- Upload `index.html` to the site root (FTP / file manager). Done.

**Seçenek 2 — GitHub Pages (ücretsiz):**
1. `aliorala/adapanel` deposunda Settings → Pages.
2. Source = "Deploy from a branch", branch = `main`, klasör = `/website` (ya da `index.html`'i köke/`docs/`'a koy).
3. Birkaç dakika içinde `https://aliorala.github.io/adapanel/` adresinde yayında olur.
4. İstersen Pages'e özel alan adı (custom domain) olarak `adapanel.host` bağlayabilirsin.

## Bağlantılar / Links
İndirme ve "GitHub'da incele" butonları şunlara işaret eder / Download & "View on GitHub" buttons point to:
- `https://github.com/aliorala/adapanel/releases/latest`
- `https://github.com/aliorala/adapanel`

Repo adı/kullanıcı değişirse `index.html` içindeki bu bağlantıları güncelle.
If the repo/user changes, update these links inside `index.html`.
