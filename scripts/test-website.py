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

    def test_four_indexable_pages_and_sitemap(self):
        self.assertEqual(set(self.pages), {'/', '/en/', '/kurulum/', '/en/installation/'})
        urls = {e.text for e in ET.parse(PUBLIC/'sitemap.xml').iter('{http://www.sitemaps.org/schemas/sitemap/0.9}loc')}
        self.assertEqual(urls, {BASE + path for path in self.pages})
        self.assertIn('Sitemap: ' + BASE + '/sitemap.xml', (PUBLIC/'robots.txt').read_text())

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
            self.assertEqual(len(page.scripts), 1)
            kind, body = page.scripts[0]
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

    def test_static_content_and_publish_isolation(self):
        for path,page in self.pages.items():
            self.assertNotIn('data-tr=',page.raw)
            self.assertNotIn('data-en=',page.raw)
            self.assertNotIn('localStorage',page.raw)
            self.assertNotIn('noindex',page.raw)
            self.assertNotIn('DNSSEC',page.raw)
            self.assertNotIn('Ücretsiz indir',page.raw)
            self.assertGreater(len(' '.join(page.text)), 2500)
        allowed = {'.html','.css','.svg','.png','.txt','.xml'}
        self.assertTrue(all(p.suffix in allowed for p in PUBLIC.rglob('*') if p.is_file()))
        self.assertFalse((PUBLIC/'web.config').exists())
        self.assertNotIn('website', (ROOT/'scripts/sync-frontend.ps1').read_text(encoding='utf-8-sig'))
        css = (PUBLIC/'assets/site.css').read_text()
        self.assertIn('.reveal {opacity:1;transform:none}',css)


if __name__ == '__main__': unittest.main(verbosity=2)
