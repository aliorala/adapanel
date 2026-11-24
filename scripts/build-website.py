"""Build the separate adapanel.host static site. Never copied to the panel wwwroot.

Python 3 standard library only. Publish artifacts/website-public via FTP, not the
source template, this script, or the AdaPanel repository.
"""
from pathlib import Path
from html import escape, unescape
from html.parser import HTMLParser
from datetime import date
import json
import re
import shutil
import os
import subprocess

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / "artifacts/website-public"
BASE = "https://adapanel.host"
RELEASE = "https://github.com/aliorala/adapanel/releases/latest"
PAGES = {"tr": ("/", "/kurulum/"), "en": ("/en/", "/en/installation/")}
UPDATED = "2026-10-18"
HOME_UPDATED = "2026-10-29"
TOPICS = json.loads((ROOT / "scripts/website/guides.json").read_text(encoding="utf-8"))
for source in ("guides-operations.json", "guides-administration.json", "guides-product.json", "guides-alternatives.json", "guides-updates.json", "guides-expanded.json"):
    additions = json.loads((ROOT / "scripts/website" / source).read_text(encoding="utf-8"))
    assert not TOPICS.keys() & additions.keys(), "Duplicate guide topic"
    TOPICS.update(additions)
TOPICS['iis'].update(category='publish', related=['dns', 'ssl', 'files'])
TOPICS['mail'].update(category='operations', related=['dns', 'ssl', 'backup'])
TOPICS['visibility'].update(category='visibility', related=['iis', 'wordpress', 'ssl'])
# Contextual links connect broad guides to their deeper operational follow-ups.
for key, additions in {
    'web_runtime': ['aspnet_deploy', 'laravel_deploy', 'php_diagnostics'],
    'iis': ['iis_errors', 'bindings'], 'mail': ['smtp_delivery'],
    'ssl': ['bindings'], 'roles': ['isolation'], 'files': ['uploads'],
    'app_runtime': ['reverse_proxy'], 'migration': ['staging'],
    'backup': ['recovery_drill'], 'selection': ['capacity'],
    'updates': ['php_diagnostics'],
}.items():
    TOPICS[key]['related'] = list(dict.fromkeys(TOPICS[key]['related'] + additions))
CATEGORIES = {
    'product': ('AdaPanel’i tanıyın ve değerlendirin', 'Explore and evaluate AdaPanel'),
    'publish': ('Alan adı ve yayın', 'Domains and publication'),
    'apps': ('Uygulama ve veritabanı', 'Applications and databases'),
    'operations': ('İşletim ve güvenlik', 'Operations and security'),
    'visibility': ('Arama görünürlüğü', 'Search visibility'),
}


def modified(path):
    if path in ('/', '/en/'):
        return HOME_UPDATED
    for topic in TOPICS.values():
        if path in (topic['tr']['path'], topic['en']['path']):
            return topic.get('updated', UPDATED)
    return UPDATED


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
        ("Windows için Plesk alternatifi ararken AdaPanel’i değerlendirebilir miyim?", "Evet. AdaPanel, kendi Windows Server sunucunuzda IIS web sitelerini ve ilişkili hizmetleri yönetmek için değerlendirebileceğiniz bir hosting kontrol panelidir. ASP.NET/PHP uyumluluğunu, müşteri rollerini, posta teslimatını ve yedekten dönüşü kendi uygulamanızla test edin. Plesk’in bütün eklentilerinin veya yedek biçimlerinin birebir karşılandığını varsaymayın."),
        ("AdaPanel Linux üzerinde çalışır mı?", "AdaPanel’in sunucu yönetimi Windows Server ve Microsoft IIS için geliştirilmiştir. Linux sunucusuna kurulan bir kontrol paneli olarak sunulmaz. Sunucu işletim sistemi ile yönettiğiniz uygulamanın gereksinimlerini birlikte değerlendirin."),
        ("Kurulum için ne gerekir?", "Yönetici yetkisine sahip bir Windows sunucusu, IIS ve seçtiğiniz uygulamaların bileşenleri gerekir. Kurulum öncesinde yedek alın; Windows rol kurulumu yeniden başlatma gerektirebilir. İlk panel kurulumu sunucunun kendisinden tamamlanır."),
        ("Evdeki sunucu veya NAT arkasında kullanılabilir mi?", "Evet, ancak yerel IIS adresi ile internette yayınlanacak genel IP ayrı ayarlanmalıdır. Modemde gerekli portları doğru sunucuya yönlendirin. Aynı genel IP üzerinde başka bir web hizmeti varsa 80/443 paylaşımını ters vekil veya ayrı IP ile planlayın. CGNAT ve sağlayıcı port kısıtlamaları ayrıca değerlendirilmelidir."),
        ("SEO, AEO ve GEO araçları ne yapar?", "SEO arama motoru görünürlüğünün teknik temelidir; AEO yanıt sistemlerinin, GEO ise üretken yapay zekâ sistemlerinin içeriği anlamasına odaklanır. AdaPanel; başlık, canonical, dil, robots, yapılandırılmış veri ve okunabilir metin sinyallerini tarar. Bulgular düzeltme önerileriyle gösterilir. Bu denetim sitenizin içeriğini otomatik değiştirmez ve sıralama ya da yapay zekâ alıntısı garantisi vermez."),
        ("Mevcut sitelerimi nasıl taşırım?", "Taşıma akışlarıyla dosya ve veritabanlarını aktarın; uygulamanın çalışma zamanı, bağlantı bilgileri, posta ve SSL ayarlarını kontrol edin. Önce ayrı bir test ortamında geri yüklemeyi ve uygulamayı doğrulayın, sonra DNS geçişini yapın."),
        ("Güncellemeler nereden indirilir?", "Resmî kurulum paketleri GitHub sürümlerinde yayımlanır. Panelde Ayarlar → Güncellemeler üzerinden yeni sürümü kontrol edebilirsiniz. Güncellemeden önce yedek alın ve sürüm notlarını okuyun."),
    ],
    "en": [
        ("What is AdaPanel?", "AdaPanel is a hosting control panel you install on your own Windows Server. It manages IIS websites, email, databases, DNS, SSL and FTP in one interface. It does not provide a server or hosting service."),
        ("Who is AdaPanel for?", "AdaPanel is designed for developers, agencies and administrators hosting sites on Windows and IIS. Administrator, reseller and customer roles help separate resources and access."),
        ("Can I evaluate AdaPanel as a Plesk alternative for Windows?", "Yes. AdaPanel is a hosting control panel option for managing IIS websites and related services on your own Windows Server. Test ASP.NET/PHP compatibility, customer roles, email delivery and backup restoration with your application. Do not assume that every Plesk extension or backup format has an identical equivalent."),
        ("Does AdaPanel run on Linux?", "AdaPanel’s server management is built for Windows Server and Microsoft IIS. It is not offered as a control panel installed on Linux. Evaluate the server operating system together with your application requirements."),
        ("What do I need to install it?", "You need administrator access to a Windows server, IIS and the components required by your applications. Take a backup before setup; Windows role installation may require a restart. Complete initial panel setup locally on the server."),
        ("Can I use a home server or a server behind NAT?", "Yes, but the local IIS address and the public IP used in DNS must be configured separately. Forward the required ports to the correct server. If another web service shares the public IP, plan ports 80/443 with a reverse proxy or a separate IP. Check CGNAT and provider port restrictions as well."),
        ("What do the SEO, AEO and GEO tools do?", "SEO covers technical search visibility; AEO focuses on answer systems, and GEO on generative AI systems understanding content. AdaPanel checks titles, canonical URLs, language, robots rules, structured data and readable text, then provides findings and suggested fixes. The audit does not rewrite your site and cannot guarantee rankings or AI citations."),
        ("How do I migrate existing sites?", "Use migration workflows to transfer files and databases, then check runtimes, connection settings, mail and SSL. Verify backup restoration and the application in a separate test environment before switching DNS."),
        ("Where can I get updates?", "Official installers are published in GitHub releases. Use Settings → Updates in the panel to check for a new version. Back up your data and read the release notes before updating."),
    ],
}

# The visible home answer, product introduction and software description share facts.
for language in PAGES:
    FAQ[language][0] = (FAQ[language][0][0], TOPICS['product'][language]['intro'])

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


def faq_schema(lang, url, pairs):
    return {"@type": "FAQPage", "@id": url + "#faq", "inLanguage": lang,
            "isPartOf": {"@id": url + "#page"},
            "mainEntity": [{"@type": "Question", "@id": url + f"#question-{i}",
                            "url": url + f"#question-{i}", "name": q,
                            "acceptedAnswer": {"@type": "Answer", "@id": url + f"#answer-{i}", "text": a}}
                           for i, (q, a) in enumerate(pairs, 1)]}


def faq_content(lang, pairs):
    label = 'Bu yanıta bağlantı' if lang == 'tr' else 'Link to this answer'
    return ''.join(f'<details class="q" id="question-{i}"><summary><span>{escape(q)}</span></summary>'
                   f'<p class="a" id="answer-{i}">{escape(a)}</p><p class="answer-permalink">'
                   f'<a href="#question-{i}">{label}</a></p></details>'
                   for i, (q, a) in enumerate(pairs, 1))


def page_metadata(lang, guide, topic=None):
    path = PAGES[lang][int(guide)]
    title = ("AdaPanel Kurulum Rehberi | Windows Server, IIS ve NAT" if guide else "Windows Hosting Kontrol Paneli ve IIS Yönetimi | AdaPanel") if lang == "tr" else ("AdaPanel Installation Guide | Windows Server, IIS & NAT" if guide else "Windows Hosting Control Panel for IIS | AdaPanel")
    description = ("AdaPanel kurulumu: Windows Server ve IIS hazırlığı, genel IP ve NAT ayarı, DNS, SSL ve yayından önce yedek geri yükleme kontrolü." if guide else "Windows Server ve IIS için AdaPanel: web, e-posta, DNS, SSL, veritabanı ve yedek yönetimi. Türkçe ve İngilizce panel, SEO/AEO/GEO denetimi.") if lang == "tr" else ("Install AdaPanel on Windows Server: prepare IIS, configure public IP and NAT, verify DNS and SSL, and test backup restoration before going live." if guide else "AdaPanel for Windows Server and IIS: manage websites, email, DNS, SSL, databases and backups. English and Turkish interface with SEO/AEO/GEO audits.")
    if topic:
        path, title, description = (topic[lang][key] for key in ("path", "title", "description"))
    return path, title, description


def metadata(lang, guide, topic=None):
    path, title, description = page_metadata(lang, guide, topic)
    url = BASE + path
    head = '<meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">'
    head += f'<title>{escape(title)}</title><meta name="description" content="{escape(description, quote=True)}"><link rel="canonical" href="{url}">'
    for locale in ("tr", "en", "x-default"):
        language = "tr" if locale == "x-default" else locale
        alternate = topic[language]["path"] if topic else PAGES[language][int(guide)]
        head += f'<link rel="alternate" hreflang="{locale}" href="{BASE + alternate}">'
    for prop, value in {"og:title": title, "og:description": description, "og:type": "website", "og:url": url, "og:site_name": "AdaPanel", "og:locale": "tr_TR" if lang == "tr" else "en_US", "og:image": BASE + "/assets/social.png", "og:image:width": "1200", "og:image:height": "630", "og:image:alt": "AdaPanel — Windows hosting control panel"}.items():
        head += f'<meta property="{prop}" content="{escape(value, quote=True)}">'
    head += '<meta name="robots" content="index, follow, max-image-preview:large"><meta name="twitter:card" content="summary_large_image"><meta name="theme-color" content="#4f46e5"><link rel="icon" href="/assets/favicon.svg" type="image/svg+xml"><link rel="stylesheet" href="/assets/site.css?v=20261022-expanded">'
    graph = [
        {"@type": "Organization", "@id": BASE + "/#organization", "name": "AdaPanel", "url": BASE + "/", "logo": BASE + "/assets/favicon.svg", "sameAs": ["https://github.com/aliorala/adapanel"]},
        {"@type": "WebSite", "@id": BASE + "/#website", "url": BASE + "/", "name": "AdaPanel", "inLanguage": ["tr", "en"], "publisher": {"@id": BASE + "/#organization"}},
        {"@type": "WebPage", "@id": url + "#page", "url": url, "name": title, "description": description, "inLanguage": lang, "dateModified": modified(path), "isPartOf": {"@id": BASE + "/#website"}, "about": {"@id": BASE + "/#software"}},
    ]
    if not guide:
        graph.append(faq_schema(lang, url, FAQ[lang]))
        graph.append({"@type": "ItemList", "@id": url + "#guides", "name": "AdaPanel kullanım rehberleri" if lang == 'tr' else "AdaPanel practical guides", "numberOfItems": len(TOPICS), "itemListElement": [
            {"@type": "ListItem", "position": i, "name": item[lang]['heading'], "url": BASE + item[lang]['path']}
            for i, item in enumerate((item for category in CATEGORIES for item in TOPICS.values() if item['category'] == category), 1)]})
    else:
        label = topic[lang]["heading"] if topic else ("Kurulum" if lang == "tr" else "Installation")
        graph.append({"@type": "BreadcrumbList", "itemListElement": [{"@type": "ListItem", "position": 1, "name": "AdaPanel", "item": BASE + PAGES[lang][0]}, {"@type": "ListItem", "position": 2, "name": label, "item": url}]})
        graph.append({"@type": "TechArticle", "@id": url + "#article", "headline": label, "description": description, "inLanguage": lang, "dateModified": modified(path), "author": {"@id": BASE + "/#organization"}, "publisher": {"@id": BASE + "/#organization"}, "mainEntityOfPage": {"@id": url + "#page"}, "about": {"@id": BASE + "/#software"}, "citation": [source[1] for source in topic[lang]['sources']] if topic else [RELEASE]})
        if topic:
            graph.append(faq_schema(lang, url, topic[lang]['faq']))
    graph[2]['mainEntity'] = {"@id": url + "#article" if guide else BASE + "/#software"}
    if not guide or topic:
        graph[2]['hasPart'] = {"@id": url + "#faq"}
    if not guide or topic is TOPICS['product']:
        graph.append({"@type": "SoftwareApplication", "@id": BASE + "/#software", "name": "AdaPanel", "url": BASE + "/", "applicationCategory": "DeveloperApplication", "operatingSystem": "Windows Server", "downloadUrl": RELEASE, "inLanguage": ["tr", "en"], "publisher": {"@id": BASE + "/#organization"}, "description": TOPICS['product'][lang]['intro'], "sameAs": ["https://github.com/aliorala/adapanel"]})
    return head + '<script type="application/ld+json">' + json.dumps({"@context": "https://schema.org", "@graph": graph}, ensure_ascii=False).replace("<", "\\u003c") + "</script>"


def updated_line(lang, path):
    value = modified(path)
    parsed = date.fromisoformat(value)
    months = ('Ocak Şubat Mart Nisan Mayıs Haziran Temmuz Ağustos Eylül Ekim Kasım Aralık' if lang == 'tr' else 'January February March April May June July August September October November December').split()
    label = f'{parsed.day} {months[parsed.month - 1]} {parsed.year}'
    about = TOPICS['product'][lang]['path']
    return '<p class="updated">' + ('Hazırlayan: ' if lang == 'tr' else 'By: ') + f'<a href="{about}">AdaPanel</a> · ' + ("Güncellendi: " if lang == "tr" else "Updated: ") + f'<time datetime="{value}">{label}</time></p>'


def resources(lang):
    heading = "AdaPanel kullanım rehberleri" if lang == "tr" else "AdaPanel practical guides"
    intro = ("Alan adını bağlamaktan yedeği geri yüklemeye: ihtiyacınız olan işlemi seçin. Her rehber ön koşulları, uygulama sırasını ve sonucun nasıl doğrulanacağını açıklar." if lang == 'tr' else "From connecting a domain to restoring a backup: choose the task you need. Each guide covers prerequisites, the workflow and how to verify the result.")
    routes = [
        (('Panel seçiyorum', 'Choosing a panel'), ['product', 'plesk-alternative', 'selection', 'capacity']),
        (('Uygulama yayınlıyorum', 'Publishing an app'), ['aspnet_deploy', 'laravel_deploy', 'reverse_proxy', 'staging']),
        (('Bir sorunu çözüyorum', 'Solving a problem'), ['iis_errors', 'php_diagnostics', 'smtp_delivery', 'recovery_drill']),
    ]
    journeys = '<div class="guide-journeys">' + ''.join('<div class="guide-journey"><h3>' + labels[lang == 'en'] + '</h3><ol>' + ''.join(f'<li><a href="{TOPICS[key][lang]["path"]}">{escape(TOPICS[key][lang]["heading"])}</a></li>' for key in keys) + '</ol></div>' for labels,keys in routes) + '</div>'
    search = ('Rehberlerde ara' if lang == 'tr' else 'Search guides')
    all_label = 'Bütün konular' if lang == 'tr' else 'All topics'
    filter_ui = '<div class="guide-filter" hidden><div><label for="guide-search">' + search + '</label><input type="search" id="guide-search" autocomplete="off" placeholder="' + ('Ör. PHP, SSL, 502, Laravel' if lang == 'tr' else 'e.g. PHP, SSL, 502, Laravel') + '" aria-controls="guide-results"></div><div><label for="guide-category">' + ('Konu' if lang == 'tr' else 'Topic') + '</label><select id="guide-category" aria-controls="guide-results"><option value="">' + all_label + '</option>' + ''.join(f'<option value="{key}">{escape(labels[lang == "en"])}</option>' for key,labels in CATEGORIES.items()) + '</select></div><button type="button" id="guide-reset">' + ('Temizle' if lang == 'tr' else 'Clear filters') + '</button><p id="guide-count" role="status" aria-live="polite"></p></div>'
    navigation, groups = '', ''
    for category, labels in CATEGORIES.items():
        label = labels[lang == 'en']
        navigation += f'<a href="#guide-{category}">{escape(label)}</a>'
        cards = ''.join(f'<a class="resource-card" href="{item[lang]["path"]}"><h4>{escape(item[lang]["heading"])}</h4><p>{escape(item[lang]["description"])}</p><span>' + ("Rehberi oku →" if lang == "tr" else "Read guide →") + '</span></a>' for item in TOPICS.values() if item['category'] == category)
        groups += f'<div class="guide-group" id="guide-{category}" data-category="{category}"><h3>{escape(label)}</h3><div class="resource-grid">{cards}</div></div>'
    return f'<section id="guides"><div class="wrap"><h2>{heading}</h2><p>{intro}</p>{journeys}{filter_ui}<nav class="guide-categories" aria-label="{escape(heading)}">{navigation}</nav><div id="guide-results">{groups}</div></div></section><script src="/assets/guides.js" defer></script>'


def topic_content(lang, data):
    home = PAGES[lang][0]
    toc = ''.join(f'<li><a href="#{s["id"]}">{escape(s["heading"])}</a></li>' for s in data['sections'])
    toc += '<li><a href="#faq">' + ('Sorular ve kısa yanıtlar' if lang == 'tr' else 'Questions and short answers') + '</a></li><li><a href="#sources">' + ('Kaynaklar' if lang == 'tr' else 'Sources') + '</a></li>'
    content = f'<main id="main"><div class="wrap guide"><header class="guide-hero"><nav aria-label="Breadcrumb"><a href="{home}">AdaPanel</a> / <span>{escape(data["heading"])}</span></nav><h1>{escape(data["heading"])}</h1><p class="lead">{escape(data["intro"])}</p>{updated_line(lang, data["path"])}</header><article class="guide-section"><nav class="toc" aria-label="' + ('İçindekiler' if lang == 'tr' else 'On this page') + '"><h2>' + ('Bu rehberde' if lang == 'tr' else 'On this page') + f'</h2><ol>{toc}</ol></nav>'
    for section in data['sections']:
        content += f'<section id="{section["id"]}"><h2>{escape(section["heading"])}</h2>'
        content += ''.join(f'<p>{escape(p)}</p>' for p in section.get('paragraphs', []))
        if section.get('steps'):
            content += '<ol class="checklist">' + ''.join(f'<li>{escape(p)}</li>' for p in section['steps']) + '</ol>'
        if section.get('rows'):
            content += '<div class="table-scroll" tabindex="0" role="region" aria-label="' + escape(section['heading'], quote=True) + '"><table><caption>' + escape(section['heading']) + '</caption><thead><tr>' + ''.join(f'<th scope="col">{escape(v)}</th>' for v in section['columns']) + '</tr></thead><tbody>' + ''.join('<tr>' + ''.join(f'<td>{escape(v)}</td>' for v in row) + '</tr>' for row in section['rows']) + '</tbody></table></div>'
        if section.get('links'):
            content += '<ul>' + ''.join(f'<li><a href="{escape(url, quote=True)}">{escape(label)}</a></li>' for label, url in section['links']) + '</ul>'
        content += '</section>'
    content += '<section id="faq"><h2>' + ('Sık sorulan sorular' if lang == 'tr' else 'Frequently asked questions') + '</h2>'
    content += faq_content(lang, data['faq']) + '</section>'
    topic = next(item for item in TOPICS.values() if item[lang]['path'] == data['path'])
    content += '<section id="related"><h2>' + ('İlgili rehberler' if lang == 'tr' else 'Related guides') + '</h2><ul>'
    for key in topic['related']:
        related = TOPICS[key][lang]
        content += f'<li><a href="{related["path"]}">{escape(related["heading"])}</a></li>'
    content += '</ul></section>'
    content += '<section id="sources"><h2>' + ('Kaynaklar ve devamı' if lang == 'tr' else 'Sources and next steps') + '</h2><ul>' + ''.join(f'<li><a href="{escape(url, quote=True)}">{escape(label)}</a></li>' for label,url in data['sources']) + '</ul><p><a href="' + PAGES[lang][1] + '">' + ('AdaPanel kurulum rehberi' if lang == 'tr' else 'AdaPanel installation guide') + '</a> · <a href="' + home + '#guides">' + ('Diğer rehberler' if lang == 'tr' else 'More guides') + '</a></p></section>'
    return content + f'<p><a class="btn primary" href="{RELEASE}">' + ('AdaPanel sürümlerini incele' if lang == 'tr' else 'Explore AdaPanel releases') + '</a></p></article></div></main>'


def validate_geo_profile():
    """Validate current Python sources; Node is used only while building."""
    node = os.environ.get('ADAPANEL_NODE') or shutil.which('node')
    if not node:
        raise RuntimeError('GEO/AEO build requires Node.js 20+: set ADAPANEL_NODE or add node to PATH.')
    urls = [BASE + path for paths in PAGES.values() for path in paths]
    urls += [BASE + topic[lang]['path'] for topic in TOPICS.values() for lang in PAGES]
    configs = []
    for lang in PAGES:
        path, title, description = page_metadata(lang, False)
        configs.append(dict(factsVerified=True, verificationScope='Current local Python content sources; no live verification.',
            application=dict(name='AdaPanel', url=BASE + '/'),
            publisher=dict(type='Organization', name='AdaPanel', url=BASE + '/'),
            canonical=BASE + path, title=title, description=description,
            shortAnswer=TOPICS['product'][lang]['intro'], locale='tr-TR' if lang == 'tr' else 'en-US',
            updatedAt=modified(path), faq=[dict(question=q, answer=a) for q,a in FAQ[lang]],
            relatedLinks=[dict(label=TOPICS['product'][lang]['heading'], url=BASE + TOPICS['product'][lang]['path'])],
            privatePaths=['/admin/'], publicUrls=[dict(url=url, updatedAt=modified(url[len(BASE):])) for url in urls],
            training='allow', experimentalLlmsTxt=False))
    result = subprocess.run([node, str(ROOT/'scripts/geo-aeo/adapter.mjs')], input=json.dumps(configs),
                            text=True, encoding='utf-8', capture_output=True)
    if result.returncode:
        raise RuntimeError('GEO/AEO adapter validation failed (Node.js 20+ required): ' + result.stderr.strip())
    artifact = ROOT/'artifacts/geo-aeo'
    artifact.mkdir(parents=True, exist_ok=True)
    (artifact/'adapanel.profile.json').write_text(result.stdout, encoding='utf-8')


def main():
    validate_geo_profile()
    template = (ROOT / "scripts/website/index.template.html").read_text(encoding="utf-8-sig")
    css = re.search(r"<style>(.*?)</style>", template, re.S).group(1)
    css += """
    header.nav {background:rgba(255,255,255,.97);border-bottom-color:var(--border)}
    [id] {scroll-margin-top:100px} .trust,footer {color:var(--muted)} .guide .btn.primary {color:white;text-decoration:none;white-space:normal}
    .resource-grid {display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:20px} .resource-card {display:block;padding:26px;background:white;border:1px solid var(--border);border-radius:18px;color:var(--text)} .resource-card h3 {font-size:21px;line-height:1.35;margin:0 0 14px} .resource-card p {color:var(--muted)} .resource-card span {color:var(--accent);font-weight:650} .resource-card:hover {border-color:var(--accent)}
    .guide .lead {font-size:1.15rem} .updated {font-size:.85rem;color:var(--muted)} .toc {padding:20px 24px;background:#eef2ff;border-radius:16px} .toc h2 {margin-top:0} .toc ol,.checklist {padding-left:1.4rem} .checklist li {padding:7px 0;color:var(--muted)} .guide .q {margin-bottom:10px} .guide-hero nav {font-size:.85rem;color:var(--muted)} .guide-section a {overflow-wrap:anywhere}
    .table-scroll {overflow-x:auto;margin:24px 0;border:1px solid var(--border);border-radius:12px} table {width:100%;border-collapse:collapse;font-size:.95rem;background:white} caption {text-align:left;padding:14px;font-weight:600;background:#eef2ff} th,td {text-align:left;vertical-align:top;padding:14px;border-bottom:1px solid var(--border)} th {background:#f8fafc} .project-facts {display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:18px;margin:24px 0} .project-facts div {padding:16px;background:#f6f8fb;border-radius:12px} .project-facts dt {font-weight:700} .project-facts dd {margin:5px 0 0;color:var(--muted)}
    @media(max-width:780px) {.resource-grid {grid-template-columns:1fr} .nav-inner {flex-wrap:wrap} .nav-links {flex-wrap:wrap;justify-content:flex-end;gap:12px} .nav-links>a.mobile-guide {display:inline} }
    @media(max-width:560px) {.project-facts {grid-template-columns:1fr} .guide-hero {padding-top:36px} .table-scroll table {min-width:450px} .nav-links {gap:6px} .nav-links>a.mobile-guide {font-size:12px} .brand {font-size:1rem} .brand .logo {width:30px;height:30px} }
    @media(prefers-reduced-motion:reduce) {html {scroll-behavior:auto} *,*::before,*::after {transition:none!important;animation:none!important}}
    .reveal {opacity:1;transform:none} .lang-toggle a {padding:6px 9px;border-radius:7px;font-size:12px;font-weight:700} .lang-toggle a[aria-current] {background:var(--accent);color:white}
    .skip {position:absolute;top:-100px;left:16px;z-index:100;background:white;padding:14px} .skip:focus {top:8px}
    :focus-visible {outline:3px solid #4338ca;outline-offset:4px} .guide {max-width:820px;margin:auto} .guide p,.answer p {line-height:1.8;color:var(--muted)} .guide h2 {margin-top:2rem} .guide ul {padding-left:1.3rem;line-height:1.8} .guide a,.answer a {color:var(--accent);text-decoration:underline}
    .answer {padding:32px;border:1px solid var(--border);border-radius:18px;background:white} .answer h2 {font-size:clamp(24px,3vw,34px)} .section-link {display:inline-block;margin-top:20px;color:var(--accent);font-weight:600} .guide article section {padding:0}
    .panel-preview {margin:0} .mock-note {font-size:12px;color:var(--muted);max-width:1000px;margin:14px auto 0;text-align:center} .guide-hero {padding:70px 0 24px} .guide-hero h1 {font-size:clamp(30px,5vw,50px);line-height:1.15} .guide-section {padding:24px 0 70px} .guide .callout {border-left:4px solid var(--accent);padding:16px 20px;background:#eef2ff}
    @media(max-width:560px) {.nav-inner {gap:8px} .nav-links {gap:8px} .nav-links>.btn {display:none} .hero h1 {font-size:36px} .answer {padding:22px} .foot-inner {gap:24px} }
    """
    OUT.mkdir(parents=True, exist_ok=True)
    css += """
    .resource-card h4 {font-size:21px;line-height:1.35;margin:0 0 14px}
    .guide-group {margin-top:36px} .guide-group>h3 {font-size:25px;margin-bottom:18px}
    .guide-categories {display:flex;flex-wrap:wrap;gap:10px;margin-top:24px}
    .guide-categories a {padding:10px 16px;border:1px solid var(--border);border-radius:24px;background:white;color:var(--accent);font-weight:600}
    .answer-permalink {font-size:.85rem;padding:0 18px 14px;margin:0} .q:target {border-color:var(--accent)}
    [hidden] {display:none!important}
    .guide-journeys {display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:18px;margin:28px 0}
    .guide-journey {padding:24px;border:1px solid var(--border);border-radius:18px;background:#eef2ff}
    .guide-journey h3 {font-size:20px;margin-bottom:12px} .guide-journey ol {padding-left:20px}
    .guide-journey li {margin:10px 0;line-height:1.5} .guide-journey a {color:var(--accent);text-decoration:underline}
    .guide-filter {display:grid;grid-template-columns:2fr 1fr auto;gap:16px;align-items:end;padding:24px;background:white;border:1px solid var(--border);border-radius:18px}
    .guide-filter>div {min-width:0} .guide-filter label {display:block;font-weight:650;margin-bottom:8px}
    .guide-filter input,.guide-filter select,.guide-filter button {width:100%;min-height:46px;font:inherit;padding:10px 12px;border:1px solid var(--border);border-radius:8px;background:white;color:var(--text)}
    .guide-filter button {color:var(--accent);cursor:pointer;font-weight:650} .guide-filter p {grid-column:1/-1;margin:0;color:var(--muted)}
    @media(max-width:780px) {.guide-journeys,.guide-filter {grid-template-columns:1fr}}
    """
    (OUT / "assets").mkdir(exist_ok=True)
    shutil.copyfile(ROOT / 'scripts/website/answers.js', OUT / 'assets/answers.js')
    shutil.copyfile(ROOT / 'scripts/website/guides.js', OUT / 'assets/guides.js')
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
        localized = localized.replace('</body>', '<script src="/assets/answers.js" defer></script></body>')
        home, guide_path = PAGES[lang]
        guide_label = "Kurulum rehberi" if lang == "tr" else "Installation guide"
        localized = re.sub(r'<span class="lang-toggle".*?</span>', '<span class="lang-toggle" role="group" aria-label="Language">LANG_LINKS</span>', localized, flags=re.S)
        localized = localized.replace('<body>', '<body><a class="skip" href="#main">' + ("İçeriğe geç" if lang == "tr" else "Skip to content") + '</a>')
        localized = localized.replace('<div class="brand"><span class="logo">A</span> AdaPanel</div>', f'<a class="brand" href="{home}" aria-label="AdaPanel"><span class="logo" aria-hidden="true">A</span> AdaPanel</a>')
        localized = localized.replace('href="#how"', f'href="{guide_path}"')
        localized = localized.replace('>Nasıl çalışır</a>', f'>{guide_label}</a>').replace('>How it works</a>', f'>{guide_label}</a>')
        faq_html = faq_content(lang, FAQ[lang])
        localized = re.sub(r'<div class="faq reveal">.*?</div>', '<div class="faq reveal">' + faq_html + '</div>', localized, flags=re.S)
        answer_title = "Barındırma yönetiminden görünürlük denetimine" if lang == "tr" else "From hosting operations to visibility audits"
        answer_text = "Bir siteyi yayınlamak başlangıçtır. AdaPanel’in SEO, AEO ve GEO denetimi; arama motorlarının ve yanıt sistemlerinin okuyabileceği teknik sinyalleri aynı panelde inceler. Canonical, dil, robots, JSON-LD ve metin sorunlarını sayfa bazında, gerekçesi ve düzeltme önerisiyle görün. Arama botları ile yapay zekâ eğitim botlarının erişim kuralları ayrı gösterilir." if lang == "tr" else "Publishing is the first step. AdaPanel’s SEO, AEO and GEO audit checks the technical signals search engines and answer systems can read. Review canonical, language, robots, JSON-LD and text issues per page, with evidence and suggested fixes. Search crawlers and AI training crawlers have separate access summaries."
        answer_note = "Denetim, JavaScript çalıştırmadan dönen HTML’i ve sunucudaki erişilebilir verileri değerlendirir. İçeriğinizin kalitesi, dış bağlantılar ve gerçek arama performansı ayrıca izlenmelidir." if lang == "tr" else "The audit evaluates returned HTML without executing JavaScript and the data available on your server. Content quality, external links and real search performance need separate monitoring."
        visibility_path = TOPICS['visibility'][lang]['path']
        extra = f'<section id="visibility"><div class="wrap answer"><h2>{answer_title}</h2><p>{answer_text}</p><p>{answer_note}</p><a href="{visibility_path}">' + ('SEO/AEO/GEO denetimini tanıyın →' if lang == 'tr' else 'Explore the SEO/AEO/GEO audit →') + '</a></div></section>'
        localized = localized.replace('<section id="how">', extra + '<section id="how">')
        localized = localized.replace('<nav class="nav-links">', '<nav class="nav-links" aria-label="' + ('Ana gezinme' if lang == 'tr' else 'Main navigation') + '">')
        localized = localized.replace('href="#faq">', 'class="mobile-guide" href="#guides">').replace('>SSS</a>', '>Rehberler</a>').replace('>FAQ</a>', '>Guides</a>')
        project_text = ('<h2>AdaPanel hakkında</h2><p>AdaPanel, Windows ve IIS üzerinde kendi sitelerini yöneten geliştiriciler, ajanslar ve sunucu yöneticileri için hazırlanmış bir kontrol panelidir. Bu site ürünün tanıtımını ve kullanım rehberlerini sunar.</p><dl class="project-facts"><div><dt>Kurulum modeli</dt><dd>Kendi Windows sunucunuzda; sunucu hizmeti ayrı sağlanır.</dd></div><div><dt>Arayüz dilleri</dt><dd>Türkçe ve İngilizce.</dd></div><div><dt>Yönetim rolleri</dt><dd>Yönetici, bayi ve müşteri.</dd></div><div><dt>Resmî dağıtım</dt><dd>GitHub sürümlerindeki kurulum paketi ve sürüm notları.</dd></div></dl>' if lang == 'tr' else '<h2>About AdaPanel</h2><p>AdaPanel is a control panel for developers, agencies and administrators managing their own sites on Windows and IIS. This website provides product information and practical guides.</p><dl class="project-facts"><div><dt>Deployment</dt><dd>On your own Windows server; hosting is supplied separately.</dd></div><div><dt>Interface languages</dt><dd>English and Turkish.</dd></div><div><dt>Management roles</dt><dd>Administrator, reseller and customer.</dd></div><div><dt>Official distribution</dt><dd>Installer packages and release notes on GitHub Releases.</dd></div></dl>')
        localized = localized.replace('</main>', resources(lang) + '<section id="project"><div class="wrap answer">' + project_text + updated_line(lang, home) + f'<a href="{RELEASE}">' + ('Güncel sürüm ve sürüm notları' if lang == 'tr' else 'Latest release and release notes') + '</a></div></section></main>')
        product_path, selection_path = TOPICS['product'][lang]['path'], TOPICS['selection'][lang]['path']
        discovery = (f'<section id="explore"><div class="wrap answer"><h2>AdaPanel sizin projenize uygun mu?</h2><p>Ürünün kapsamını öğrenin; Windows ve IIS üzerinde kendi uygulamanızla değerlendirin.</p><p><a href="{product_path}">AdaPanel nedir? Özellikler ve kullanım alanları</a></p><p><a href="{selection_path}">Windows hosting paneli seçimi ve deneme kontrol listesi</a></p></div></section>' if lang == 'tr' else f'<section id="explore"><div class="wrap answer"><h2>Is AdaPanel right for your project?</h2><p>Explore the product and evaluate it with your own application on Windows and IIS.</p><p><a href="{product_path}">What is AdaPanel? Features and use cases</a></p><p><a href="{selection_path}">Choosing a Windows hosting panel: evaluation checklist</a></p></div></section>')
        localized = localized.replace('<section id="features">', discovery + '<section id="features">')
        for is_guide in (False, True):
            html_body = localized
            links = ''.join(f'<a href="{PAGES[locale][int(is_guide)]}" hreflang="{locale}" lang="{locale}"' + (' aria-current="page"' if locale == lang else '') + f'>{locale.upper()}</a>' for locale in PAGES)
            html_body = html_body.replace('LANG_LINKS', links)
            if is_guide:
                title = "AdaPanel kurulum rehberi" if lang == "tr" else "AdaPanel installation guide"
                intro = "Windows Server ve IIS hazırlığından NAT, DNS ve geri yükleme testine: yayına geçmeden önce izlenecek adımlar." if lang == "tr" else "From Windows Server and IIS preparation to NAT, DNS and restoration testing: steps to complete before going live."
                sections = ''.join(f'<section><h2>{escape(h)}</h2><p>{escape(p)}</p></section>' for h,p in GUIDE[lang])
                content = f'<main id="main"><div class="wrap guide"><header class="guide-hero"><a href="{home}">AdaPanel</a><h1>{title}</h1><p>{intro}</p>{updated_line(lang, guide_path)}</header><article class="guide-section">{sections}<p class="callout">' + ("Yedek dosyasının varlığı yeterli değildir. Geri yüklenmiş uygulamanın çalıştığını doğrulayın ve eski sunucuyu geçiş tamamlanana kadar saklayın." if lang == "tr" else "A backup file alone is not proof of recovery. Verify the restored application works and keep the old server until the migration is complete.") + f'</p><a class="btn primary" href="{RELEASE}">' + ("Resmî sürümler ve kurulum paketi" if lang == "tr" else "Official releases and installer") + f'</a><p><a href="{home}#guides">' + ('Tüm kullanım rehberleri' if lang == 'tr' else 'All practical guides') + '</a></p></article></div></main>'
                html_body = re.sub(r'<main id="main">.*?</main>', content, html_body, flags=re.S)
                html_body = html_body.replace('href="#features"', f'href="{home}#features"').replace('href="#faq"', f'href="{home}#faq"')
                html_body = html_body.replace('href="#guides"', f'href="{home}#guides"')
            page = '<!doctype html>\n<html lang="' + lang + '"><head>' + metadata(lang, is_guide) + '</head>\n' + html_body
            target = OUT / PAGES[lang][int(is_guide)].strip('/') / "index.html"
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_text('\n'.join(line.rstrip() for line in page.splitlines()) + '\n', encoding="utf-8")
        for topic in TOPICS.values():
            html_body = re.sub(r'<main id="main">.*?</main>', lambda _: topic_content(lang, topic[lang]), localized, flags=re.S)
            links = ''.join(f'<a href="{topic[locale]["path"]}" hreflang="{locale}" lang="{locale}"' + (' aria-current="page"' if locale == lang else '') + f'>{locale.upper()}</a>' for locale in PAGES)
            html_body = html_body.replace('LANG_LINKS', links)
            for anchor in ('features','faq','guides'):
                html_body = html_body.replace(f'href="#{anchor}"', f'href="{home}#{anchor}"') if anchor != 'faq' else html_body
            page = '<!doctype html>\n<html lang="' + lang + '"><head>' + metadata(lang, True, topic) + '</head>\n' + html_body
            target = OUT / topic[lang]['path'].strip('/') / 'index.html'
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_text('\n'.join(line.rstrip() for line in page.splitlines()) + '\n', encoding='utf-8')
    private_rules = 'Allow: /\nDisallow: /admin$\nDisallow: /admin?\nDisallow: /admin/\n'
    robots = '\n'.join('User-agent: ' + bot + '\n' + private_rules for bot in ('*', 'OAI-SearchBot', 'PerplexityBot', 'GPTBot'))
    (OUT / "robots.txt").write_text(robots + '\nSitemap: https://adapanel.host/sitemap.xml\n', encoding="utf-8")
    pairs = [{lang: PAGES[lang][i] for lang in PAGES} for i in (0,1)] + [{lang: topic[lang]['path'] for lang in PAGES} for topic in TOPICS.values()]
    sitemap = '<?xml version="1.0" encoding="UTF-8"?>\n<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9" xmlns:xhtml="http://www.w3.org/1999/xhtml">'
    for pair in pairs:
        alternates = ''.join(f'<xhtml:link rel="alternate" hreflang="{lang}" href="{BASE + path}"/>' for lang,path in {**pair,'x-default':pair['tr']}.items())
        for path in pair.values():
            sitemap += f'<url><loc>{BASE + path}</loc><lastmod>{modified(path)}</lastmod>{alternates}</url>'
    (OUT / "sitemap.xml").write_text(sitemap + '</urlset>\n', encoding="utf-8")
    social = ROOT / "scripts/website/social.png"
    shutil.copyfile(social, OUT / "assets/social.png")
    # Keep ownership verification files in every generated deployment package.
    for verification in (ROOT / "scripts/website/verification").glob("*"):
        if verification.is_file():
            shutil.copyfile(verification, OUT / verification.name)
    print(f"Built {len(pairs)*2} static pages and public assets: {OUT}")


if __name__ == "__main__":
    main()
