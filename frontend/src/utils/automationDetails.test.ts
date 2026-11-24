import { expect, it } from 'vitest';
import { automationDetails } from './automationDetails';
import en from '../i18n/en.json';
import tr from '../i18n/tr.json';

const translate = (catalog: typeof en) => (key: string, values: Record<string, string | number> = {}) =>
  catalog[key as keyof typeof catalog].replace(/\{(\w+)\}/g, (_, name: string) => String(values[name]));

it('localizes historical successful backup and certificate summaries', () => {
  expect(automationDetails('3 site, 4 veritabanı yedeklendi, 4 eski yedek silindi.\r\nmail: yedeklendi\r\npanel: yedeklendi', translate(en)))
    .toBe('Sites backed up: 3; databases: 4; expired backups deleted: 4.\nMail: backed up\nPanel: backed up');
  expect(automationDetails('1 sertifika yenilendi. Mail sertifikası (mail.example.com) güncellendi.', translate(en)))
    .toBe('Certificates renewed: 1. Mail certificate (mail.example.com) updated.');
});

it('localizes measured database totals and their archive locations without claiming remote upload success', () => {
  const details = '1 site, 4 veritabanı kopyası doğrulandı (site arşivlerinde: 2, ayrı dökümlerde: 2), 0 eski yedek silindi.\ndb postgresql/Foo: status=created_remote_failed; backup.remote_upload_failed (ftp) connection refused';
  expect(automationDetails(details, translate(en))).toBe('Complete site backups: 1; verified database copies: 4 (in site archives: 2; separate dumps: 2); expired backups deleted: 0.\ndb postgresql/Foo: status=created_remote_failed; backup.remote_upload_failed (ftp) connection refused');
  expect(automationDetails(details, translate(tr))).toBe('Tam site yedekleri: 1; doğrulanan veritabanı kopyaları: 4 (site arşivlerinde: 2; ayrı dökümlerde: 2); silinen eski yedekler: 0.\ndb postgresql/Foo: status=created_remote_failed; backup.remote_upload_failed (ftp) connection refused');
});

it('retains new summary prune failures verbatim rather than translating away the failure', () => {
  const failure = '1 site, 4 veritabanı kopyası doğrulandı (site arşivlerinde: 2, ayrı dökümlerde: 2), 0 eski yedek silindi, 1 uzak kopya silme denemesi basarisiz.\nFTP: 550 Permission denied';
  expect(automationDetails(failure, translate(en))).toBe(failure);
});

it('localizes update summaries in Turkish without losing component versions', () => {
  expect(automationDetails('0 update(s) installed, 1 pending: PHP 8.3.33 -> 8.3.35 | Version cannot be checked automatically (1): Docker', translate(tr)))
    .toBe('Yüklenen güncellemeler: 0; bekleyen: 1: PHP 8.3.33 -> 8.3.35 | Sürümü otomatik denetlenemeyen bileşenler (1): Docker');
});

it('localizes incomplete version checks and preserves the affected software names', () => {
  const details = 'Component version checks could not be completed. | Version cannot be checked automatically (1): Docker\nVersion checks failed: PHP, Adminer';
  expect(automationDetails(details, translate(tr))).toBe('Bileşen sürüm denetimleri tamamlanamadı. | Sürümü otomatik denetlenemeyen bileşenler (1): Docker\nSürüm denetimi başarısız: PHP, Adminer');
  expect(automationDetails(details, translate(en))).toContain('Version checks failed: PHP, Adminer');
});

it('retains unknown diagnostics and partial deletion failures without claiming success', () => {
  const failure = '3 site, 4 veritabanı yedeklendi, 0 eski yedek silindi, 1 yedegin uzak kopyasi SILINEMEDI.\nFTP: 550 Permission denied';
  expect(automationDetails(failure, translate(en))).toBe(failure);
  const unexpected = 'All checkable components are up to date. | Some future warning';
  expect(automationDetails(unexpected, translate(en))).toBe(unexpected);
});

it('localizes mail activation failures without hiding either the renewal count or the diagnosis', () => {
  const details = '2 sertifika yenilendi.\nMail certificate synchronization failed: mail.certificate_not_activated: fixture reload refused';
  expect(automationDetails(details, translate(en))).toBe('Certificates renewed: 2.\nMail certificate synchronization failed: ' + en['mail.certificate_not_activated'] + ': fixture reload refused');
  expect(automationDetails(details, translate(tr))).toBe('Yenilenen sertifikalar: 2.\nPosta sertifikası eşitlenemedi: ' + tr['mail.certificate_not_activated'] + ': fixture reload refused');
});

it('localizes a mail failure heading while preserving unknown provider diagnostics', () => {
  const details = 'Mail certificate synchronization failed: transport unavailable (fixture)';
  expect(automationDetails(details, translate(tr))).toBe('Posta sertifikası eşitlenemedi: transport unavailable (fixture)');
});

it('keeps a successful mail sync visible when renewal itself could not be measured', () => {
  const details = 'fixture win-acme unavailable\nMail sertifikası (mail.example.test) güncellendi.';
  expect(automationDetails(details, translate(en))).toBe('fixture win-acme unavailable\nMail certificate (mail.example.test) updated.');
});

it('localizes measured certificate verification counts without hiding a binding problem', () => {
  const details = '2 sertifika yenilendi.\nCertificate verification: verified=3; missing=1; unverified=2; binding-drift=4; bindings-measured=true.';
  expect(automationDetails(details, translate(en))).toBe('Certificates renewed: 2.\nCertificates verified: 3; missing from the store: 1; unverified: 2; incorrect HTTPS bindings: 4.');
  expect(automationDetails(details, translate(tr))).toBe('Yenilenen sertifikalar: 2.\nDoğrulanan sertifikalar: 3; depoda bulunmayan: 1; doğrulanamayan: 2; yanlış HTTPS bağlaması: 4.');
});

it('keeps unmeasured bindings explicit instead of interpreting zero as healthy', () => {
  const details = 'Certificate verification: verified=3; missing=0; unverified=0; binding-drift=0; bindings-measured=false.';
  expect(automationDetails(details, translate(en))).toBe('Certificates verified: 3; missing from the store: 0; unverified: 0; incorrect HTTPS bindings: —. HTTPS bindings could not be measured.');
  expect(automationDetails(details, translate(tr))).toBe('Doğrulanan sertifikalar: 3; depoda bulunmayan: 0; doğrulanamayan: 0; yanlış HTTPS bağlaması: —. HTTPS bağlamaları ölçülemedi.');
});
