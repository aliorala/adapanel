"""Validate the deployable website, independently of the panel build."""
import json
from pathlib import Path
from html.parser import HTMLParser
from urllib.parse import urlparse
import unittest
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
PUBLIC = ROOT / 'artifacts/website-public'
BASE = 'https://adapanel.host'


class Page(HTMLParser):
    def __init__(self, path):
        super().__init__()
        self.tags, self.text, self.scripts, self.current_script = [], [], [], None
        self.raw = path.read_text(encoding='utf-8')
        self.feed(self.raw)

    def handle_starttag(self, tag, attrs):
        attrs = dict(attrs)
        self.tags.append((tag, attrs))
        if tag == 'script':
            self.current_script = [attrs.get('type'), '']

    def handle_endtag(self, tag):
        if tag == 'script':
            self.scripts.append(self.current_script)
            self.current_script = None

    def handle_data(self, value):
        if self.current_script is not None: self.current_script[1] += value
        else: self.text.append(value)

    def select(self, tag, **attrs):
        return [a for t,a in self.tags if t == tag and all(a.get(k) == v for k,v in attrs.items())]


class WebsiteTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.pages = {'/' + str(p.parent.relative_to(PUBLIC)).replace('\\','/').strip('.').strip('/') + '/': Page(p) for p in PUBLIC.rglob('index.html')}
        cls.pages = {('/' if k == '//' else k):v for k,v in cls.pages.items()}

    def test_indexable_pages_and_sitemap(self):
        self.assertEqual(len(self.pages), 66)
        self.assertTrue({'/', '/en/', '/kurulum/', '/en/installation/', '/iis-site-yonetimi/', '/en/iis-site-management/', '/posta-sunucusu/', '/en/mail-server/', '/seo-aeo-geo/', '/en/seo-aeo-geo/'}.issubset(self.pages))
        urls = {e.text for e in ET.parse(PUBLIC/'sitemap.xml').iter('{http://www.sitemaps.org/schemas/sitemap/0.9}loc')}
        self.assertEqual(urls, {BASE + path for path in self.pages})
        self.assertIn('Sitemap: ' + BASE + '/sitemap.xml', (PUBLIC/'robots.txt').read_text())
        for entry in ET.parse(PUBLIC/'sitemap.xml').getroot():
            path = urlparse(entry.find('{http://www.sitemaps.org/schemas/sitemap/0.9}loc').text).path
            alternatives = {link.attrib['hreflang']:link.attrib['href'] for link in entry.findall('{http://www.w3.org/1999/xhtml}link')}
            self.assertEqual(alternatives, {a['hreflang']:a['href'] for a in self.pages[path].select('link', rel='alternate')})
            self.assertEqual(entry.find('{http://www.sitemaps.org/schemas/sitemap/0.9}lastmod').text, self.pages[path].select('time')[0]['datetime'])

    def test_content_dates_are_page_specific(self):
        changed = {'/', '/en/', '/adapanel-nedir/', '/en/what-is-adapanel/', '/windows-hosting-paneli-secimi/', '/en/windows-hosting-panel-selection/', '/windows-plesk-alternatifi/', '/en/plesk-alternative-windows/'}
        self.assertTrue(changed.issubset(self.pages))
        for path, page in self.pages.items():
            newest = {'/', '/en/', '/guncelleme-ve-surum-dogrulama/', '/en/updates-version-verification/'}
            expanded = json.loads((ROOT/'scripts/website/guides-expanded.json').read_text(encoding='utf-8'))
            newest.update(item[lang]['path'] for item in expanded.values() for lang in ('tr','en'))
            expected = '2026-10-29' if path in ('/', '/en/') else '2026-10-22' if path in newest else '2026-10-21' if path in changed else '2026-10-18'
            self.assertEqual(page.select('time')[0]['datetime'], expected)
            for item in json.loads(next(s[1] for s in page.scripts if s[0] == 'application/ld+json'))['@graph']:
                if 'dateModified' in item:
                    self.assertEqual(item['dateModified'], expected)

    def test_geo_adapter_profile_and_shared_answer(self):
        profile = json.loads((ROOT/'artifacts/geo-aeo/adapanel.profile.json').read_text(encoding='utf-8'))
        product = json.loads((ROOT/'scripts/website/guides-product.json').read_text(encoding='utf-8'))['product']
        for config in profile['configs']:
            lang = config['locale'][:2]
            page = self.pages['/' if lang == 'tr' else '/en/']
            self.assertEqual(config['shortAnswer'], product[lang]['intro'])
            self.assertIn(config['shortAnswer'], page.text)
            self.assertEqual(config['faq'][0]['answer'], config['shortAnswer'])
            self.assertEqual(config['title'], next(t for t in page.text if t == config['title']))
            self.assertEqual(config['description'], page.select('meta', name='description')[0]['content'])
            self.assertEqual(config['updatedAt'], '2026-10-29')
            self.assertEqual({e['url'] for e in config['publicUrls']}, {BASE+p for p in self.pages})
            for entry in config['publicUrls']:
                self.assertEqual(entry['updatedAt'], self.pages[urlparse(entry['url']).path].select('time')[0]['datetime'])
            self.assertEqual(config['training'], 'allow')
            self.assertFalse(config['experimentalLlmsTxt'])
        self.assertFalse((PUBLIC/'llms.txt').exists())
        for page in self.pages.values():
            ids = [a['id'] for _,a in page.tags if 'id' in a]
            self.assertEqual(len(ids), len(set(ids)))
            graph = json.loads(next(s[1] for s in page.scripts if s[0]=='application/ld+json'))['@graph']
            schema_ids = [item['@id'] for item in graph if '@id' in item]
            self.assertEqual(len(schema_ids), len(set(schema_ids)))

    def test_geo_adapter_rejects_invalid_metadata(self):
        import os, shutil, subprocess
        node = os.environ.get('ADAPANEL_NODE') or shutil.which('node')
        self.assertIsNotNone(node, 'ADAPANEL_NODE or Node.js 20+ PATH is required for build adapter tests')
        profile = json.loads((ROOT/'artifacts/geo-aeo/adapanel.profile.json').read_text(encoding='utf-8'))
        profile['configs'][0]['updatedAt'] = '2099-01-01'
        result = subprocess.run([node, str(ROOT/'scripts/geo-aeo/adapter.mjs')], input=json.dumps(profile['configs']), text=True, encoding='utf-8', capture_output=True)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('updatedAt', result.stderr)

    def test_robots_private_path_boundaries(self):
        import urllib.robotparser
        rules = urllib.robotparser.RobotFileParser()
        body = (PUBLIC/'robots.txt').read_text()
        # robotparser lacks the robots end-of-path '$' syntax; check real patterns.
        import re
        groups = body.split('User-agent: ')[1:]
        self.assertEqual({g.splitlines()[0] for g in groups}, {'*','OAI-SearchBot','PerplexityBot','GPTBot'})
        for group in groups:
            self.assertIn('Allow: /', group)
            patterns = [line.removeprefix('Disallow: ') for line in group.splitlines() if line.startswith('Disallow: ')]
            self.assertEqual(patterns, ['/admin$', '/admin?', '/admin/'])
            def blocked(path):
                return any(re.match(re.escape(p[:-1])+'$' if p.endswith('$') else re.escape(p), path) for p in patterns)
            for path in ('/admin', '/admin?x=1', '/admin/', '/admin/login'):
                self.assertTrue(blocked(path), path)
            for path in ('/', '/administrator', '/admin-tools', '/en/'):
                self.assertFalse(blocked(path), path)
        for source in (ROOT/'scripts/website/verification').glob('*'):
            if source.is_file(): self.assertEqual((PUBLIC/source.name).read_bytes(), source.read_bytes())

    def test_product_entity_and_evaluation_links(self):
        for lang, home, product, selection in [('tr', '/', '/adapanel-nedir/', '/windows-hosting-paneli-secimi/'), ('en', '/en/', '/en/what-is-adapanel/', '/en/windows-hosting-panel-selection/')]:
            for path in (home, product):
                graph = json.loads(next(s[1] for s in self.pages[path].scripts if s[0] == 'application/ld+json'))['@graph']
                software = next(item for item in graph if item['@type'] == 'SoftwareApplication')
                self.assertEqual(software['@id'], BASE + '/#software')
                self.assertEqual(software['operatingSystem'], 'Windows Server')
                self.assertEqual(software['sameAs'], ['https://github.com/aliorala/adapanel'])
                self.assertTrue(self.pages[path].select('a', href=selection))
            self.assertTrue(self.pages[selection].select('a', href=product))
            for path, page in self.pages.items():
                if page.select('html')[0]['lang'] == lang:
                    self.assertTrue(page.select('a', href=product), path)

    def test_metadata_and_reciprocal_locales(self):
        for path, page in self.pages.items():
            self.assertEqual(page.select('html')[0]['lang'], 'en' if path.startswith('/en/') else 'tr')
            self.assertEqual(len(page.select('h1')), 1)
            self.assertEqual(len(page.select('title')), 1)
            self.assertEqual(page.select('link', rel='canonical'), [{'rel':'canonical','href':BASE+path}])
            self.assertGreater(len(page.select('meta', name='description')[0]['content']), 70)
            alternates = page.select('link', rel='alternate')
            self.assertEqual({a['hreflang'] for a in alternates}, {'tr','en','x-default'})
            for alt in alternates:
                target = self.pages[urlparse(alt['href']).path]
                self.assertEqual(target.select('link', rel='alternate'), alternates)
            self.assertEqual(page.select('meta', property='og:url')[0]['content'], BASE+path)

    def test_local_links_and_assets_exist(self):
        for path, page in self.pages.items():
            ids = [a['id'] for _,a in page.tags if 'id' in a]
            self.assertEqual(len(ids),len(set(ids)))
            for tag,attrs in page.tags:
                value = attrs.get('href', attrs.get('src', ''))
                if not value or value.startswith(('https:', 'data:')): continue
                url = urlparse(value)
                target_path = url.path or path
                if target_path.endswith('/'):
                    self.assertIn(target_path, self.pages)
                    if url.fragment:
                        self.assertTrue(any(a.get('id') == url.fragment for _,a in self.pages[target_path].tags), value)
                else:
                    self.assertTrue((PUBLIC/target_path.lstrip('/')).is_file(), value)
        self.assertGreater((PUBLIC/'assets/social.png').stat().st_size, 1000)

    def test_structured_data_matches_visible_faq(self):
        for page in self.pages.values():
            structured = [s for s in page.scripts if s[0] == 'application/ld+json']
            self.assertEqual(len(structured), 1)
            kind, body = structured[0]
            self.assertEqual(kind, 'application/ld+json')
            data = json.loads(body)
            self.assertEqual(data['@context'], 'https://schema.org')
            visible = ' '.join(page.text)
            for item in data['@graph']:
                self.assertNotIn('aggregateRating', item)
                self.assertNotIn('offers', item)
                if item['@type'] == 'FAQPage':
                    self.assertEqual(len(item['mainEntity']), len(page.select('details')))
                    for question in item['mainEntity']:
                        self.assertIn(question['name'], visible)
                        self.assertIn(question['acceptedAnswer']['text'], visible)

    def test_answers_have_resolvable_links_and_sources(self):
        for path, page in self.pages.items():
            graph = json.loads(next(s[1] for s in page.scripts if s[0] == 'application/ld+json'))['@graph']
            webpage = next(item for item in graph if item['@type'] == 'WebPage')
            self.assertTrue(any(item.get('@id') == webpage['mainEntity']['@id'] for item in graph), path)
            for item in graph:
                if item['@type'] == 'TechArticle':
                    self.assertTrue(item['citation'])
                    for source in item['citation']:
                        self.assertTrue(page.select('a', href=source), (path, source))
                if item['@type'] == 'FAQPage':
                    self.assertEqual(webpage['hasPart']['@id'], item['@id'])
                    for question in item['mainEntity']:
                        fragment = urlparse(question['url']).fragment
                        self.assertTrue(page.select('details', id=fragment))
                        self.assertTrue(page.select('a', href='#' + fragment))
                        answer_id = urlparse(question['acceptedAnswer']['@id']).fragment
                        self.assertTrue(page.select('p', id=answer_id))
            self.assertTrue(page.select('script', src='/assets/answers.js'))

    def test_update_guides_are_linked_and_distinguish_runtime_updates(self):
        for path, home, marker in [('/guncelleme-ve-surum-dogrulama/', '/', 'PHP'), ('/en/updates-version-verification/', '/en/', 'PHP')]:
            self.assertTrue(self.pages[home].select('a', href=path))
            page = self.pages[path]
            self.assertIn(marker, ' '.join(page.text))
            self.assertTrue(page.select('a', href='https://github.com/aliorala/adapanel/releases/tag/0.8.118'))
            self.assertEqual(len(page.select('details')), 5)

    def test_static_content_and_publish_isolation(self):
        for path,page in self.pages.items():
            self.assertNotIn('data-tr=',page.raw)
            self.assertNotIn('data-en=',page.raw)
            self.assertNotIn('localStorage',page.raw)
            for directive in page.select('meta', name='robots'):
                self.assertNotIn('noindex', directive.get('content', '').lower())
            self.assertNotIn('Ücretsiz indir',page.raw)
            self.assertNotIn('0.8.95',page.raw)
            self.assertNotIn('public release is pending',page.raw)
            self.assertGreater(len(' '.join(page.text)), 2500)
        allowed = {'.html','.css','.svg','.png','.txt','.xml','.js'}
        self.assertTrue(all(p.suffix in allowed for p in PUBLIC.rglob('*') if p.is_file()))
        self.assertFalse((PUBLIC/'web.config').exists())
        self.assertNotIn('website', (ROOT/'scripts/sync-frontend.ps1').read_text(encoding='utf-8-sig'))
        css = (PUBLIC/'assets/site.css').read_text()
        self.assertIn('.reveal {opacity:1;transform:none}',css)

    def test_guides_are_discoverable_and_have_evidence(self):
        for path,page in self.pages.items():
            home = '/en/' if path.startswith('/en/') else '/'
            if path not in ('/','/en/','/kurulum/','/en/installation/'):
                self.assertTrue(self.pages[home].select('a', href=path), path)
                self.assertTrue(page.select('nav', **{'class':'toc'}))
                self.assertTrue(page.select('section', id='sources'))
                self.assertTrue(page.select('section', id='related'))
                self.assertTrue(page.select('time'))
                graph=json.loads(next(s[1] for s in page.scripts if s[0] == 'application/ld+json'))['@graph']
                self.assertTrue(any(item['@type']=='TechArticle' for item in graph))
                self.assertTrue(any(item['@type']=='BreadcrumbList' for item in graph))
        descriptions = [p.select('meta', name='description')[0]['content'] for p in self.pages.values()]
        self.assertEqual(len(descriptions),len(set(descriptions)))

    def test_expanded_guides_and_library(self):
        expanded = json.loads((ROOT/'scripts/website/guides-expanded.json').read_text(encoding='utf-8'))
        self.assertEqual(len(expanded), 12)
        answers = []
        for item in expanded.values():
            for lang in ('tr','en'):
                data = item[lang]
                self.assertEqual(len(data['sections']), 5)
                self.assertEqual(len(data['faq']), 4)
                self.assertGreaterEqual(len(data['sources']), 2)
                self.assertTrue(any(s.get('rows') for s in data['sections']))
                self.assertTrue(any(s.get('steps') for s in data['sections']))
                # Article body alone must contain substantive prose, not just nav.
                body = ' '.join([data['intro']] + [p for s in data['sections'] for p in s.get('paragraphs', [])] + [p for s in data['sections'] for p in s.get('steps', [])])
                self.assertGreater(len(body), 2000, data['path'])
                answers.extend(a for q,a in data['faq'])
                # New articles must have contextual inbound links beyond home.
                self.assertTrue(any(p.select('a', href=data['path']) for path,p in self.pages.items() if path not in ('/','/en/',data['path'])))
        self.assertEqual(len(answers), len(set(answers)))
        for home in ('/','/en/'):
            page = self.pages[home]
            self.assertTrue(page.select('input', id='guide-search'))
            self.assertTrue(page.select('select', id='guide-category'))
            self.assertIn('hidden', page.select('div', **{'class':'guide-filter'})[0])
            self.assertEqual(len(page.select('a', **{'class':'resource-card'})),31)
            self.assertTrue(page.select('script', src='/assets/guides.js'))
            graph = json.loads(next(s[1] for s in page.scripts if s[0]=='application/ld+json'))['@graph']
            listing = next(item for item in graph if item['@type']=='ItemList')
            self.assertEqual(listing['numberOfItems'],31)
            self.assertEqual([entry['position'] for entry in listing['itemListElement']],list(range(1,32)))
            self.assertEqual({urlparse(entry['url']).path for entry in listing['itemListElement']},{a['href'] for a in page.select('a', **{'class':'resource-card'})})


if __name__ == '__main__': unittest.main(verbosity=2)
