using System.Net;

namespace AdaPanel.Infrastructure.Iis;

/// <summary>
/// Yeni bir site oluşturulduğunda klasöre yazılan karşılama sayfası.
/// </summary>
/// <remarks>
/// <para>
/// <b>Bu sayfa HERKESE AÇIKTIR.</b> Site kurulduğu andan içerik yüklenene kadar alan adını açan
/// herkes onu görür — arama motorları ve rastgele tarayıcılar dahil. Bu yüzden içinde ne olduğu
/// bir tasarım tercihi değil, bir güvenlik kararıdır:
/// </para>
/// <list type="bullet">
/// <item><b>SUNUCUDAKİ FİZİKSEL YOL YAZILMAZ.</b> "Dosyalarınızı şuraya yükleyin:
/// <c>C:\inetpub\wwwroot\musteri.com</c>" cümlesi yardımcı görünür ama sunucunun klasör düzenini
/// ziyaretçiye açar. Yol bilgisi panelde zaten var; sayfanın işi onu internete taşımak değil.
/// Plesk ve cPanel'in varsayılan sayfaları da yol yazmaz.</item>
/// <item><b>DIŞ KAYNAK YOKTUR</b> (yazı tipi, betik, resim). Sayfa internete çıkamayan bir
/// sunucuda da, katı bir içerik güvenlik politikası altında da aynı görünmelidir; ayrıca her
/// yeni site, üçüncü bir tarafa ziyaretçi bilgisi sızdırmamalıdır.</item>
/// <item><b>Site adı HTML olarak kaçırılır.</b> Ad panel tarafından doğrulanıyor olsa da, buradaki
/// kaçırma o doğrulamanın doğruluğuna bağlı kalmamak içindir.</item>
/// </list>
/// <para>
/// <b>Neden ayrı bir sınıf:</b> içerik IIS'e, dosya sistemine ve PowerShell'e dokunmadan
/// sınanabilmeli. Eskiden metin <c>WebAdministrationSiteService</c>'in içinde gömülüydü ve
/// yalnız gerçek bir IIS kurulumunda görülebiliyordu; sonuç, aylarca kimsenin fark etmediği
/// bozuk Türkçe ("yayinda", "degistirebilirsiniz") ve yalnız koyu temada çalışan bir sayfaydı.
/// </para>
/// </remarks>
internal static class StarterPage
{
    /// <summary>
    /// <param name="siteName">Panelde göründüğü hâliyle site adı.</param>
    /// <param name="hostName">Bağlamadaki host adı; boşsa <c>localhost</c> varsayılır.</param>
    /// <param name="port">Bağlamadaki port.</param>
    /// </summary>
    public static string Render(string siteName, string hostName, int port)
    {
        var ad = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(siteName) ? "Yeni site" : siteName.Trim());

        // ADRES YALNIZ BİLGİ KATIYORSA GÖSTERİLİR.
        //
        // İlk yazımda adres her zaman yazılıyordu ve önizlemede görüldü: site adı ile host adı
        // aynı olduğunda (en olağan durum) aynı metin ALT ALTA İKİ KEZ çıkıyor, sayfa hatalı
        // görünüyordu. Ziyaretçi zaten o adreste; adres ancak ondan FARKLIYSA bir şey söyler
        // (ör. varsayılan olmayan port: "site.example:8080").
        //
        // Varsayılan port gizlenir: ":80" yazmak adresi olduğundan karmaşık gösterir ve
        // kullanıcıya onu öyle yazması gerektiğini düşündürür.
        var konak = string.IsNullOrWhiteSpace(hostName) ? "localhost" : hostName.Trim();
        var adres = port is 80 or 443 ? konak : $"{konak}:{port}";
        var adresSatiri = string.Equals(adres, siteName?.Trim(), StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : $"<p class=\"adres\">{WebUtility.HtmlEncode(adres)}</p>";

        return $$"""
            <!doctype html>
            <html lang="tr">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <meta name="robots" content="noindex">
              <title>{{ad}}</title>
              <style>
                /* Iki tema da destekleniyor: sayfa ziyaretcinin sistem tercihine uyar.
                   Eskiden yalniz koyu tema vardi ve acik temadaki kullanicilarda sirittiriyordu. */
                :root {
                  color-scheme: light dark;
                  --zemin: #eef1f5;
                  --kart: #ffffff;
                  --cizgi: #e3e8ee;
                  --metin: #16202b;
                  --soluk: #5c6b7a;
                  --vurgu: #0f7b52;
                  --vurgu-zemin: #e7f5ee;
                  --hero-a: #0f7b52;
                  --hero-b: #0b5f6e;
                  --uyari-zemin: #fff6e5;
                  --uyari-cizgi: #f0d9a8;
                  --uyari-metin: #7a5410;
                }
                @media (prefers-color-scheme: dark) {
                  :root {
                    --zemin: #0a1016;
                    --kart: #121b24;
                    --cizgi: #22303f;
                    --metin: #e9f1f8;
                    --soluk: #94a6b6;
                    --vurgu: #52d08c;
                    --vurgu-zemin: #14291f;
                    --hero-a: #10402f;
                    --hero-b: #0d3440;
                    --uyari-zemin: #2a2113;
                    --uyari-cizgi: #4d3d1d;
                    --uyari-metin: #f0cf8e;
                  }
                }
                * { box-sizing: border-box; }
                body {
                  margin: 0;
                  min-height: 100vh;
                  display: grid;
                  place-items: center;
                  padding: 20px;
                  background: var(--zemin);
                  color: var(--metin);
                  font-family: "Segoe UI", system-ui, -apple-system, sans-serif;
                  line-height: 1.6;
                }
                main {
                  width: min(820px, 100%);
                  background: var(--kart);
                  border: 1px solid var(--cizgi);
                  border-radius: 20px;
                  overflow: hidden;
                }
                .hero {
                  padding: clamp(28px, 5vw, 44px);
                  background: linear-gradient(135deg, var(--hero-a), var(--hero-b));
                  color: #fff;
                }
                .rozet {
                  display: inline-flex;
                  align-items: center;
                  gap: 8px;
                  padding: 6px 13px;
                  border-radius: 999px;
                  background: #ffffff26;
                  font-size: .84rem;
                  font-weight: 600;
                }
                .nokta { width: 8px; height: 8px; border-radius: 50%; background: #7ef0b0; }
                h1 {
                  margin: 16px 0 8px;
                  font-size: clamp(1.6rem, 5.5vw, 2.4rem);
                  line-height: 1.2;
                  overflow-wrap: anywhere;
                }
                .hero p { margin: 0; color: #ffffffd9; max-width: 56ch; }
                .adres { margin-top: 10px !important; font-size: .92rem; color: #ffffffb3 !important; overflow-wrap: anywhere; }
                .govde { padding: clamp(24px, 5vw, 40px); }
                h2 { margin: 0 0 4px; font-size: 1.08rem; }
                .alt { margin: 0 0 18px; color: var(--soluk); font-size: .92rem; }
                .kartlar {
                  display: grid;
                  gap: 14px;
                  grid-template-columns: repeat(auto-fit, minmax(210px, 1fr));
                }
                .kart {
                  padding: 18px;
                  border: 1px solid var(--cizgi);
                  border-radius: 14px;
                  background: var(--zemin);
                }
                .simge {
                  width: 34px;
                  height: 34px;
                  display: grid;
                  place-items: center;
                  border-radius: 9px;
                  background: var(--vurgu-zemin);
                  color: var(--vurgu);
                  margin-bottom: 11px;
                }
                .kart h3 { margin: 0 0 4px; font-size: .97rem; }
                .kart p { margin: 0; color: var(--soluk); font-size: .88rem; line-height: 1.55; }
                .uyari {
                  display: flex;
                  gap: 12px;
                  margin-top: 22px;
                  padding: 15px 17px;
                  border: 1px solid var(--uyari-cizgi);
                  border-radius: 13px;
                  background: var(--uyari-zemin);
                  color: var(--uyari-metin);
                  font-size: .91rem;
                }
                .uyari svg { flex: none; margin-top: 2px; }
                .uyari strong { font-weight: 700; }
                .dip {
                  margin-top: 22px;
                  padding-top: 17px;
                  border-top: 1px solid var(--cizgi);
                  color: var(--soluk);
                  font-size: .85rem;
                }
                .dip p { margin: 0 0 6px; }
                .marka { display: flex; justify-content: space-between; gap: 12px; flex-wrap: wrap; margin-top: 12px; font-size: .8rem; }
              </style>
            </head>
            <body>
              <main>
                <header class="hero">
                  <p class="rozet"><span class="nokta"></span>Site yayında</p>
                  <h1>{{ad}}</h1>
                  <p>Bu sayfayı görüyorsanız site doğru kuruldu ve çalışıyor. Henüz içerik yüklenmediği
                     için panelin hazırladığı karşılama sayfası gösteriliyor.</p>
                  {{adresSatiri}}
                </header>

                <section class="govde">
                  <h2>Sırada ne var?</h2>
                  <p class="alt">Sitenizi yayına almak için üç yoldan birini seçin.</p>

                  <div class="kartlar">
                    <article class="kart">
                      <div class="simge" aria-hidden="true">
                        <svg width="19" height="19" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"/></svg>
                      </div>
                      <h3>Dosya Yöneticisi</h3>
                      <p>Panelden sitenin klasörünü açıp dosyalarınızı doğrudan yükleyin.</p>
                    </article>

                    <article class="kart">
                      <div class="simge" aria-hidden="true">
                        <svg width="19" height="19" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 3v12"/><path d="m7 10 5 5 5-5"/><path d="M4 18h16"/></svg>
                      </div>
                      <h3>FTP ile bağlanın</h3>
                      <p>Panelde tanımlı FTP hesabınızla sitenizi bilgisayarınızdan aktarın.</p>
                    </article>

                    <article class="kart">
                      <div class="simge" aria-hidden="true">
                        <svg width="19" height="19" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 3 14.4 9 21 9.6l-5 4.3 1.5 6.4L12 17l-5.5 3.3L8 13.9l-5-4.3L9.6 9z"/></svg>
                      </div>
                      <h3>Uygulama kurun</h3>
                      <p>WordPress gibi hazır bir uygulamayı panelden tek adımda kurun.</p>
                    </article>
                  </div>

                  <div class="uyari">
                    <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><circle cx="12" cy="12" r="9"/><path d="M12 8v5"/><path d="M12 16.5h.01"/></svg>
                    <span>Dosyalarınızı yükledikten sonra bu <strong>index.html</strong> dosyasını silmeyi
                      unutmayın. Yerinde kalırsa kendi açılış sayfanızı gölgeler ve site değişmemiş görünür.</span>
                  </div>

                  <div class="dip">
                    <p>Alan adınız henüz bu sunucuya yönlenmiyorsa siteniz dışarıdan açılmayabilir; DNS
                       değişikliklerinin yayılması birkaç saat sürebilir.</p>
                    <div class="marka">
                      <span>AdaPanel</span>
                      <span>Site is live — upload your files and delete <strong>index.html</strong>.</span>
                    </div>
                  </div>
                </section>
              </main>
            </body>
            </html>
            """;
    }
}
