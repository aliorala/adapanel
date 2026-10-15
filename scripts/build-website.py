"""Build the separate adapanel.host static site. Never copied to the panel wwwroot.

Python 3 standard library only. Publish artifacts/website-public via FTP, not the
source template, this script, or the AdaPanel repository.
"""
from pathlib import Path
from html import escape, unescape
from html.parser import HTMLParser
import json
import re
import shutil

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / "artifacts/website-public"
BASE = "https://adapanel.host"
RELEASE = "https://github.com/aliorala/adapanel/releases/latest"
PAGES = {"tr": ("/", "/kurulum/"), "en": ("/en/", "/en/installation/")}


class Localize(HTMLParser):
    """Render translations at build time, so crawlers and no-JS users see them."""
    def __init__(self, lang):
        super().__init__(convert_charrefs=False)
        self.lang, self.output, self.skip = lang, [], 0

    def handle_starttag(self, tag, attrs):
        void = tag in {"meta", "link", "img", "br", "hr", "input", "source", "wbr"}
        if self.skip:
            if not void: self.skip += 1
            return
        values = dict(attrs)
        translated = values.get("data-" + self.lang)
        cleaned = [(k, v) for k, v in attrs if k not in {"data-tr", "data-en"}]
        self.output.append("<" + tag + "".join(" " + k + ("" if v is None else '="' + escape(v, quote=True) + '"') for k, v in cleaned) + ">")
        if translated is not None and not void:
            self.output.append(translated if "<" in translated else escape(translated))
            self.skip = 1

    def handle_startendtag(self, tag, attrs):
        self.handle_starttag(tag, attrs)
        if tag not in {"meta", "link", "img", "br", "hr", "input", "source", "wbr"}:
            self.handle_endtag(tag)

    def handle_endtag(self, tag):
        if self.skip:
            self.skip -= 1
            if self.skip: return
        self.output.append("</" + tag + ">")

    def handle_data(self, data):
        if not self.skip: self.output.append(data)

    def handle_entityref(self, name): self.handle_data("&" + name + ";")
    def handle_charref(self, name): self.handle_data("&#" + name + ";")
    def handle_decl(self, decl): self.output.append("<!" + decl + ">")


COPY = {
    "Windows barındırma, <span class='grad-text'>tek panelden</span>.": ("Windows hosting kontrol paneli.<br><span class='grad-text'>Sunucunuz sizin elinizde.</span>", "Windows hosting control panel.<br><span class='grad-text'>Your server. Your control.</span>"),
    "Web sitelerini, e-postayı, veritabanlarını, DNS'i, SSL'i ve FTP'yi tek modern arayüzden yönetin. Plesk'in yaptığını — Windows için, sade ve hızlı.": ("AdaPanel, kendi Windows Server sunucunuzdaki IIS sitelerini, e-postayı, veritabanlarını, DNS, SSL ve FTP hizmetlerini Türkçe ve İngilizce tek arayüzden yönetmenizi sağlar.", "AdaPanel brings IIS websites, email, databases, DNS, SSL and FTP on your own Windows Server into one interface, available in English and Turkish."),
    "Ücretsiz indir": ("Kurulumu indir", "Download installer"),
    "IIS · PHP · e-posta · DB otomatik": ("IIS · PHP · e-posta · veritabanı", "IIS · PHP · email · databases"),
    "Bir sunucuyu işletmek için her şey": ("Günlük sunucu yönetimi, tek yerde", "Everyday server management in one place"),
    "Kurulumdan günlük yönetime, ihtiyacınız olan tüm araçlar tek panelde — ayrı yazılım kurmadan.": ("Web, posta ve veritabanı hizmetlerini ortak bir arayüzden yönetin. Bileşenleri ihtiyacınıza göre kurun ve yapılandırın.", "Manage web, mail and database services through a shared interface. Install and configure the components your workloads need."),
    "Alan adı, posta kutusu ve alias yönetimi; Roundcube webmail, SPF/DKIM/DMARC otomatik.": ("Alan adları, posta kutuları ve alias yönetimi; Stalwart ve Roundcube, SPF, DKIM ve DMARC kayıtlarının kurulumu ve denetimi.", "Domains, mailboxes and aliases with Stalwart and Roundcube; setup and checks for SPF, DKIM and DMARC records."),
    "Bölge yönetimi, kayıt şablonları, DNSSEC ve posta kayıtlarının otomatik yazımı.": ("Windows DNS ile canlı kayıt eşitlemesi, şablonlar ve NAT için yerel/genel IP eşleştirmesi. Onar eksik kayıtları ekler; varsayılanlara dönüş ayrı bir işlemdir.", "Live record synchronization with Windows DNS, templates and private/public IP mapping for NAT. Repair adds missing records; resetting defaults is a separate action."),
    "Plesk/cPanel veya ham sunucudan site + dosya + veritabanını canlı, adım adım taşıyın.": ("Plesk, cPanel veya sunucudan dosya ve veritabanı taşıma akışları. DNS geçişinden önce taşınan uygulamayı ve yedek geri yüklemeyi sınayın.", "Migration workflows for files and databases from Plesk, cPanel or a server. Test the migrated application and backup restoration before changing DNS."),
    "Üç adımda yayında": ("Kurun, doğrulayın, yayına alın", "Install, verify, go live"),
    "Tek kurulum dosyasını çalıştır; IIS, PHP, e-posta ve veritabanı otomatik kurulur.": ("Kurulumu yönetici olarak çalıştırın; gerekli IIS rollerini ve kullanacağınız bileşenleri hazırlayın. Windows yeniden başlatma isterse tamamlayın.", "Run the installer as administrator, prepare the required IIS roles and your chosen components, and complete any restart Windows requests."),
    "site & alan adı": ("kullanıcı rolü", "user roles"),
    "Tek kurulum dosyasını indirin; gerisini AdaPanel halleder.": ("Kurulum rehberini inceleyin, son sürümü indirin ve kendi Windows sunucunuzda deneyin.", "Read the installation guide, download the latest release and try it on your own Windows server."),
    "Windows Server 2019/2022 (veya Windows 10/11) · yönetici hakları": ("Windows Server · IIS · yönetici yetkisi · kurulum öncesi yedek", "Windows Server · IIS · administrator access · backup before setup"),
}

FAQ = {
    "tr": [
        ("AdaPanel nedir?", "AdaPanel, kendi Windows Server sunucunuza kurduğunuz bir hosting kontrol panelidir. IIS web sitelerini, e-postayı, veritabanlarını, DNS, SSL ve FTP hizmetlerini tek arayüzde yönetir. Sunucu veya barındırma hizmeti sağlamaz."),
        ("AdaPanel kimler için uygundur?", "Windows ve IIS üzerinde site yayınlayan geliştiriciler, ajanslar ve sunucu yöneticileri için tasarlanmıştır. Yönetici, bayi ve müşteri rolleriyle kaynakları ve erişimi ayırabilirsiniz."),
        ("Kurulum için ne gerekir?", "Yönetici yetkisine sahip bir Windows sunucusu, IIS ve seçtiğiniz uygulamaların bileşenleri gerekir. Kurulum öncesinde yedek alın; Windows rol kurulumu yeniden başlatma gerektirebilir. İlk panel kurulumu sunucunun kendisinden tamamlanır."),
        ("Evdeki sunucu veya NAT arkasında kullanılabilir mi?", "Evet, ancak yerel IIS adresi ile internette yayınlanacak genel IP ayrı ayarlanmalıdır. Modemde gerekli portları doğru sunucuya yönlendirin. Aynı genel IP üzerinde başka bir web hizmeti varsa 80/443 paylaşımını ters vekil veya ayrı IP ile planlayın. CGNAT ve sağlayıcı port kısıtlamaları ayrıca değerlendirilmelidir."),
        ("SEO, AEO ve GEO araçları ne yapar?", "SEO arama motoru görünürlüğünün teknik temelidir; AEO yanıt sistemlerinin, GEO ise üretken yapay zekâ sistemlerinin içeriği anlamasına odaklanır. AdaPanel; başlık, canonical, dil, robots, yapılandırılmış veri ve okunabilir metin sinyallerini tarar. Bulgular düzeltme önerileriyle gösterilir. Bu denetim sitenizin içeriğini otomatik değiştirmez ve sıralama ya da yapay zekâ alıntısı garantisi vermez."),
        ("Mevcut sitelerimi nasıl taşırım?", "Taşıma akışlarıyla dosya ve veritabanlarını aktarın; uygulamanın çalışma zamanı, bağlantı bilgileri, posta ve SSL ayarlarını kontrol edin. Önce ayrı bir test ortamında geri yüklemeyi ve uygulamayı doğrulayın, sonra DNS geçişini yapın."),
        ("Güncellemeler nereden indirilir?", "Resmî kurulum paketleri GitHub sürümlerinde yayımlanır. Panelde Ayarlar → Güncellemeler üzerinden yeni sürümü kontrol edebilirsiniz. Güncellemeden önce yedek alın ve sürüm notlarını okuyun."),
    ],
    "en": [
        ("What is AdaPanel?", "AdaPanel is a hosting control panel you install on your own Windows Server. It manages IIS websites, email, databases, DNS, SSL and FTP in one interface. It does not provide a server or hosting service."),
        ("Who is AdaPanel for?", "AdaPanel is designed for developers, agencies and administrators hosting sites on Windows and IIS. Administrator, reseller and customer roles help separate resources and access."),
        ("What do I need to install it?", "You need administrator access to a Windows server, IIS and the components required by your applications. Take a backup before setup; Windows role installation may require a restart. Complete initial panel setup locally on the server."),
        ("Can I use a home server or a server behind NAT?", "Yes, but the local IIS address and the public IP used in DNS must be configured separately. Forward the required ports to the correct server. If another web service shares the public IP, plan ports 80/443 with a reverse proxy or a separate IP. Check CGNAT and provider port restrictions as well."),
        ("What do the SEO, AEO and GEO tools do?", "SEO covers technical search visibility; AEO focuses on answer systems, and GEO on generative AI systems understanding content. AdaPanel checks titles, canonical URLs, language, robots rules, structured data and readable text, then provides findings and suggested fixes. The audit does not rewrite your site and cannot guarantee rankings or AI citations."),
        ("How do I migrate existing sites?", "Use migration workflows to transfer files and databases, then check runtimes, connection settings, mail and SSL. Verify backup restoration and the application in a separate test environment before switching DNS."),
        ("Where can I get updates?", "Official installers are published in GitHub releases. Use Settings → Updates in the panel to check for a new version. Back up your data and read the release notes before updating."),
    ],
}

GUIDE = {
    "tr": [
        ("1. Sunucuyu hazırlayın", "Üretim için Windows Server ve uygulamanıza uygun IIS bileşenlerini hazırlayın. İşletim sistemi, çalışma zamanları ve posta/veritabanı bileşenlerinin uyumluluğunu birlikte kontrol edin. Mevcut sunucuda işlem yapıyorsanız dosya, veritabanı, posta ve panel ayarlarının yedeğini ayrı bir konumda saklayın."),
        ("2. Resmî paketi kurun", "GitHub sürümlerindeki AdaPanelSetup.exe dosyasını indirin ve yönetici olarak çalıştırın. Sürüm notlarını okuyun. Windows rol veya özellik kurulumu yeniden başlatma istiyorsa, bileşen kurulumuna devam etmeden önce sunucuyu yeniden başlatın."),
        ("3. İlk kurulumu sunucudan tamamlayın", "İlk panel kurulumu yalnız yerel erişimle tamamlanır. Kurulumun gösterdiği panel adresini ve güvenli giriş yolunu kullanın. Varsayılan HTTPS portu 8888’dir; giriş yolu kuruluma özeldir. Yönetici hesabını oluşturun ve kullanacağınız web, veritabanı ve posta bileşenlerini yapılandırın."),
        ("4. IIS ve uygulamayı doğrulayın", "IIS çalışmadan PHP ve phpMyAdmin yayınlanamaz. URL Rewrite kurulumu IIS CoreWebEngine veya W3SVC eksikliği bildiriyorsa önce IIS rolünü tamamlayın. Siteye uygun çalışma zamanını seçin; dosya izinleri, veritabanı bağlantısı, yönlendirmeler ve uygulama görevlerini test edin."),
        ("5. Genel IP, DNS ve SSL’i ayarlayın", "NAT kullanıyorsanız yerel IIS binding adresini DNS’te yayınlanacak genel IP ile karıştırmayın. DNS A kayıtları dışarıdan erişilebilen genel IP’ye gitmelidir. 80/443 yönlendirmesini ve güvenlik duvarını doğrulayın. Aynı IP’deki diğer hizmetlerle port çakışması varsa ters vekil veya ayrı IP planlayın. Alan adı doğru hedefe ulaştıktan sonra SSL’i doğrulayın."),
        ("6. Geri yükleme ve dış erişim testi yapın", "Bir yedeği ayrı bir test sitesine geri yükleyin; dosyaları, veritabanını, giriş ve kritik işlemleri kontrol edin. Dışarıdan görünüm ve SEO denetiminde ölçülen alan adının gerçek web alan adı olduğunu doğrulayın. Posta kullanıyorsanız MX, SPF, DKIM, DMARC, PTR ve sağlayıcı SMTP kısıtlarını ayrıca kontrol edin. Testlerden sonra DNS geçişini tamamlayın."),
    ],
    "en": [
        ("1. Prepare the server", "For production, prepare Windows Server and the IIS components your application needs. Check operating system, runtime, mail and database compatibility together. On an existing server, keep a separate backup of files, databases, mail and panel settings before making changes."),
        ("2. Install the official package", "Download AdaPanelSetup.exe from GitHub releases and run it as administrator. Read the release notes. If Windows asks for a restart after installing roles or features, restart the server before continuing component setup."),
        ("3. Complete initial setup locally", "Initial panel setup must be completed locally on the server. Use the panel address and secure login path shown by setup. The default HTTPS port is 8888; the login path is specific to the installation. Create the administrator account and configure your chosen web, database and mail components."),
        ("4. Verify IIS and the application", "PHP and phpMyAdmin cannot be published before IIS is available. If URL Rewrite reports missing IIS CoreWebEngine or W3SVC, complete IIS role installation first. Select the right runtime and test file permissions, database connections, redirects and application jobs."),
        ("5. Configure public IP, DNS and SSL", "Behind NAT, keep the local IIS binding address separate from the public IP published in DNS. DNS A records must point to the reachable public IP. Verify forwarding and firewall rules for ports 80/443. Plan a reverse proxy or a separate IP if other services share those ports. Verify SSL once the domain reaches the correct destination."),
        ("6. Test restoration and external access", "Restore a backup into a separate test site; verify files, databases, login and critical operations. Check that external visibility and SEO audits target the real web domain. For mail, also check MX, SPF, DKIM, DMARC, PTR and provider SMTP restrictions. Complete the DNS switch after testing."),
    ],
}


def metadata(lang, guide):
    path = PAGES[lang][int(guide)]
    title = ("AdaPanel Kurulum Rehberi | Windows Server, IIS ve NAT" if guide else "AdaPanel | Windows Hosting Kontrol Paneli") if lang == "tr" else ("AdaPanel Installation Guide | Windows Server, IIS & NAT" if guide else "AdaPanel | Windows Hosting Control Panel")
    description = ("AdaPanel kurulumu: Windows Server ve IIS hazırlığı, genel IP ve NAT ayarı, DNS, SSL ve yayından önce yedek geri yükleme kontrolü." if guide else "Windows Server ve IIS için AdaPanel: web, e-posta, DNS, SSL, veritabanı ve yedek yönetimi. Türkçe ve İngilizce panel, SEO/AEO/GEO denetimi.") if lang == "tr" else ("Install AdaPanel on Windows Server: prepare IIS, configure public IP and NAT, verify DNS and SSL, and test backup restoration before going live." if guide else "AdaPanel for Windows Server and IIS: manage websites, email, DNS, SSL, databases and backups. English and Turkish interface with SEO/AEO/GEO audits.")
    url = BASE + path
    head = '<meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">'
    head += f'<title>{escape(title)}</title><meta name="description" content="{escape(description, quote=True)}"><link rel="canonical" href="{url}">'
    for locale in ("tr", "en", "x-default"):
        head += f'<link rel="alternate" hreflang="{locale}" href="{BASE + PAGES["tr" if locale == "x-default" else locale][int(guide)]}">'
    for prop, value in {"og:title": title, "og:description": description, "og:type": "website", "og:url": url, "og:site_name": "AdaPanel", "og:locale": "tr_TR" if lang == "tr" else "en_US", "og:image": BASE + "/assets/social.png", "og:image:width": "1200", "og:image:height": "630", "og:image:alt": "AdaPanel — Windows hosting control panel"}.items():
        head += f'<meta property="{prop}" content="{escape(value, quote=True)}">'
    head += '<meta name="twitter:card" content="summary_large_image"><meta name="theme-color" content="#4f46e5"><link rel="icon" href="/assets/favicon.svg" type="image/svg+xml"><link rel="stylesheet" href="/assets/site.css">'
    graph = [
        {"@type": "Organization", "@id": BASE + "/#organization", "name": "AdaPanel", "url": BASE + "/", "logo": BASE + "/assets/favicon.svg", "sameAs": ["https://github.com/aliorala/adapanel"]},
        {"@type": "WebSite", "@id": BASE + "/#website", "url": BASE + "/", "name": "AdaPanel", "inLanguage": ["tr", "en"], "publisher": {"@id": BASE + "/#organization"}},
        {"@type": "WebPage", "@id": url + "#page", "url": url, "name": title, "description": description, "inLanguage": lang, "isPartOf": {"@id": BASE + "/#website"}},
    ]
    if not guide:
        graph.append({"@type": "SoftwareApplication", "@id": BASE + "/#software", "name": "AdaPanel", "url": BASE + "/", "applicationCategory": "DeveloperApplication", "operatingSystem": "Windows Server", "downloadUrl": RELEASE, "inLanguage": ["tr", "en"], "publisher": {"@id": BASE + "/#organization"}, "description": description})
        graph.append({"@type": "FAQPage", "@id": url + "#faq", "inLanguage": lang, "mainEntity": [{"@type": "Question", "name": q, "acceptedAnswer": {"@type": "Answer", "text": a}} for q, a in FAQ[lang]]})
    else:
        graph.append({"@type": "BreadcrumbList", "itemListElement": [{"@type": "ListItem", "position": 1, "name": "AdaPanel", "item": BASE + PAGES[lang][0]}, {"@type": "ListItem", "position": 2, "name": "Kurulum" if lang == "tr" else "Installation", "item": url}]})
    return head + '<script type="application/ld+json">' + json.dumps({"@context": "https://schema.org", "@graph": graph}, ensure_ascii=False).replace("<", "\\u003c") + "</script>"


def main():
    template = (ROOT / "scripts/website/index.template.html").read_text(encoding="utf-8-sig")
    css = re.search(r"<style>(.*?)</style>", template, re.S).group(1)
    css += """
    .reveal {opacity:1;transform:none} .lang-toggle a {padding:6px 9px;border-radius:7px;font-size:12px;font-weight:700} .lang-toggle a[aria-current] {background:var(--accent);color:white}
    .skip {position:absolute;top:-100px;left:16px;z-index:100;background:white;padding:14px} .skip:focus {top:8px}
    :focus-visible {outline:3px solid #4338ca;outline-offset:4px} .guide {max-width:820px;margin:auto} .guide p,.answer p {line-height:1.8;color:var(--muted)} .guide h2 {margin-top:2rem} .guide ul {padding-left:1.3rem;line-height:1.8} .guide a,.answer a {color:var(--accent);text-decoration:underline}
    .answer {padding:32px;border:1px solid var(--border);border-radius:18px;background:white} .answer h2 {font-size:clamp(24px,3vw,34px)} .section-link {display:inline-block;margin-top:20px;color:var(--accent);font-weight:600} .guide article section {padding:0}
    .mock-note {font-size:12px;color:var(--muted);margin:12px 0} .guide-hero {padding:70px 0 24px} .guide-hero h1 {font-size:clamp(30px,5vw,50px);line-height:1.15} .guide-section {padding:24px 0 70px} .guide .callout {border-left:4px solid var(--accent);padding:16px 20px;background:#eef2ff}
    @media(max-width:560px) {.nav-inner {gap:8px} .nav-links {gap:8px} .nav-links>.btn {display:none} .hero h1 {font-size:36px} .answer {padding:22px} .foot-inner {gap:24px} }
    """
    OUT.mkdir(parents=True, exist_ok=True)
    (OUT / "assets").mkdir(exist_ok=True)
    (OUT / "assets/site.css").write_text('\n'.join(line.rstrip() for line in css.strip().splitlines()) + '\n', encoding="utf-8")
    (OUT / "assets/favicon.svg").write_text('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64"><rect width="64" height="64" rx="16" fill="#4f46e5"/><text x="32" y="45" text-anchor="middle" font-family="Arial,sans-serif" font-size="40" font-weight="700" fill="white">A</text></svg>', encoding="utf-8")
    body = template[template.index("<body>"):]
    body = re.sub(r"<script>.*?</script>", "", body, flags=re.S)
    body = body.replace('<main>', '<main id="main">').replace('<span id="year"></span>', '2026')
    body = body.replace('<strong class="grad-text">∞</strong>', '<strong class="grad-text">3</strong>')
    body = body.replace('https://localhost:8890/panel', 'AdaPanel · IIS / PHP / ASP.NET')
    def revised(match):
        original = unescape(match.group(1))
        if original not in COPY: return match.group(0)
        tr, en = COPY[original]
        return 'data-tr="' + escape(tr, quote=True) + '" data-en="' + escape(en, quote=True) + '"'
    body = re.sub(r'data-tr="([^"]*)" data-en="[^"]*"', revised, body)
    for lang in PAGES:
        localizer = Localize(lang)
        localizer.feed(body)
        localized = "".join(localizer.output)
        home, guide_path = PAGES[lang]
        guide_label = "Kurulum rehberi" if lang == "tr" else "Installation guide"
        localized = re.sub(r'<span class="lang-toggle".*?</span>', '<span class="lang-toggle" role="group" aria-label="Language">LANG_LINKS</span>', localized, flags=re.S)
        localized = localized.replace('<body>', '<body><a class="skip" href="#main">' + ("İçeriğe geç" if lang == "tr" else "Skip to content") + '</a>')
        localized = localized.replace('<div class="brand"><span class="logo">A</span> AdaPanel</div>', f'<a class="brand" href="{home}" aria-label="AdaPanel"><span class="logo" aria-hidden="true">A</span> AdaPanel</a>')
        localized = localized.replace('href="#how"', f'href="{guide_path}"')
        localized = localized.replace('>Nasıl çalışır</a>', f'>{guide_label}</a>').replace('>How it works</a>', f'>{guide_label}</a>')
        faq_html = ''.join(f'<details class="q"><summary><span>{escape(q)}</span><span class="x" aria-hidden="true">+</span></summary><p class="a">{escape(a)}</p></details>' for q,a in FAQ[lang])
        localized = re.sub(r'<div class="faq reveal">.*?</div>', '<div class="faq reveal">' + faq_html + '</div>', localized, flags=re.S)
        answer_title = "Barındırma yönetiminden görünürlük denetimine" if lang == "tr" else "From hosting operations to visibility audits"
        answer_text = "Bir siteyi yayınlamak başlangıçtır. AdaPanel’in SEO, AEO ve GEO denetimi; arama motorlarının ve yanıt sistemlerinin okuyabileceği teknik sinyalleri aynı panelde inceler. Canonical, dil, robots, JSON-LD ve metin sorunlarını sayfa bazında, gerekçesi ve düzeltme önerisiyle görün. Arama botları ile yapay zekâ eğitim botlarının erişim kuralları ayrı gösterilir." if lang == "tr" else "Publishing is the first step. AdaPanel’s SEO, AEO and GEO audit checks the technical signals search engines and answer systems can read. Review canonical, language, robots, JSON-LD and text issues per page, with evidence and suggested fixes. Search crawlers and AI training crawlers have separate access summaries."
        answer_note = "Denetim, JavaScript çalıştırmadan dönen HTML’i ve sunucudaki erişilebilir verileri değerlendirir. İçeriğinizin kalitesi, dış bağlantılar ve gerçek arama performansı ayrıca izlenmelidir." if lang == "tr" else "The audit evaluates returned HTML without executing JavaScript and the data available on your server. Content quality, external links and real search performance need separate monitoring."
        extra = f'<section id="visibility"><div class="wrap answer"><h2>{answer_title}</h2><p>{answer_text}</p><p>{answer_note}</p><a href="{guide_path}">{guide_label} →</a></div></section>'
        localized = localized.replace('<section id="how">', extra + '<section id="how">')
        localized = localized.replace('<section id="features">', '<p class="mock-note wrap">' + ("Temsili panel görünümü; örnek veriler içerir." if lang == "tr" else "Illustrative panel preview with sample data.") + '</p><section id="features">')
        for is_guide in (False, True):
            html_body = localized
            links = ''.join(f'<a href="{PAGES[locale][int(is_guide)]}" hreflang="{locale}" lang="{locale}"' + (' aria-current="page"' if locale == lang else '') + f'>{locale.upper()}</a>' for locale in PAGES)
            html_body = html_body.replace('LANG_LINKS', links)
            if is_guide:
                title = "AdaPanel kurulum rehberi" if lang == "tr" else "AdaPanel installation guide"
                intro = "Windows Server ve IIS hazırlığından NAT, DNS ve geri yükleme testine: yayına geçmeden önce izlenecek adımlar." if lang == "tr" else "From Windows Server and IIS preparation to NAT, DNS and restoration testing: steps to complete before going live."
                sections = ''.join(f'<section><h2>{escape(h)}</h2><p>{escape(p)}</p></section>' for h,p in GUIDE[lang])
                content = f'<main id="main"><div class="wrap guide"><header class="guide-hero"><a href="{home}">AdaPanel</a><h1>{title}</h1><p>{intro}</p></header><article class="guide-section">{sections}<p class="callout">' + ("Yedek dosyasının varlığı yeterli değildir. Geri yüklenmiş uygulamanın çalıştığını doğrulayın ve eski sunucuyu geçiş tamamlanana kadar saklayın." if lang == "tr" else "A backup file alone is not proof of recovery. Verify the restored application works and keep the old server until the migration is complete.") + f'</p><a class="btn primary" href="{RELEASE}">' + ("Resmî sürümler ve kurulum paketi" if lang == "tr" else "Official releases and installer") + '</a></article></div></main>'
                html_body = re.sub(r'<main id="main">.*?</main>', content, html_body, flags=re.S)
                html_body = html_body.replace('href="#features"', f'href="{home}#features"').replace('href="#faq"', f'href="{home}#faq"')
            page = '<!doctype html>\n<html lang="' + lang + '"><head>' + metadata(lang, is_guide) + '</head>\n' + html_body
            target = OUT / PAGES[lang][int(is_guide)].strip('/') / "index.html"
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_text('\n'.join(line.rstrip() for line in page.splitlines()) + '\n', encoding="utf-8")
    (OUT / "robots.txt").write_text("User-agent: *\nAllow: /\n\nSitemap: https://adapanel.host/sitemap.xml\n", encoding="utf-8")
    urls = [BASE + path for pair in PAGES.values() for path in pair]
    (OUT / "sitemap.xml").write_text('<?xml version="1.0" encoding="UTF-8"?>\n<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">' + ''.join('<url><loc>' + url + '</loc></url>' for url in urls) + '</urlset>\n', encoding="utf-8")
    social = ROOT / "scripts/website/social.png"
    shutil.copyfile(social, OUT / "assets/social.png")
    print(f"Built 4 static pages and public assets: {OUT}")


if __name__ == "__main__":
    main()
