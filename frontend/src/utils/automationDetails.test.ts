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

it('localizes update summaries in Turkish without losing component versions', () => {
  expect(automationDetails('0 update(s) installed, 1 pending: PHP 8.3.33 -> 8.3.35 | Version cannot be checked automatically (1): Docker', translate(tr)))
    .toBe('Yüklenen güncellemeler: 0; bekleyen: 1: PHP 8.3.33 -> 8.3.35 | Sürümü otomatik denetlenemeyen bileşenler (1): Docker');
});

it('retains unknown diagnostics and partial deletion failures without claiming success', () => {
  const failure = '3 site, 4 veritabanı yedeklendi, 0 eski yedek silindi, 1 yedegin uzak kopyasi SILINEMEDI.\nFTP: 550 Permission denied';
  expect(automationDetails(failure, translate(en))).toBe(failure);
  const unexpected = 'All checkable components are up to date. | Some future warning';
  expect(automationDetails(unexpected, translate(en))).toBe(unexpected);
});
